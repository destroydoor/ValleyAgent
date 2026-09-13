using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ValleyAgent;
using ValleyAgent.Agents;
using ValleyAgent.Commands;
using ValleyAgent.Config;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Performance;
using ValleyAgent.Patches;
using ValleyAgent.Resilience;
using ValleyAgent.Services;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     PR2「从身份到身体」房客链路守卫（设计 docs/design/2026-09-13-agent-body-refactor.md §3.2，B1-B3）。
///     对话面：房客对任意在场村民可 AI 对话（patch/聊天栏三处身份门拆除，B1/B2）；
///     身体面：主机中继回包的身体类动作先 promote 再执行，FOLLOW 目标不丢、优先级持续刷新（B3）。
///     Harmony patch 静态方法依赖 Game1/DialogueBox 实例，无头环境不可驱动——
///     接线类断言用源码扫描（空白归一化 + 语义匹配，不缩进敏感）；
///     纯逻辑（身体类工具单一事实源、transport 存在性、对话计数）与中继主线程链路用真测试。
/// </summary>
[Collection("Game1Statics")]
public sealed class FarmhandBodyLinkTests : IDisposable
{
    private const string ModId = "dandm1.ValleyAgent";

    public void Dispose()
    {
        // 静态单例还原：transport 存在性是进程级状态，不还原会泄漏到同进程的其它测试。
        DialogueBoxInputPatch.SetDialogueTransport(null);
        NPCDialoguePatch.ClearAllConversations();
    }

    // ───────────────────────── 源码扫描基建（范式与 ThinClientCapabilityMatrixTests 一致） ─────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null,
            $"从 {AppContext.BaseDirectory} 向上找不到 AGENTS.md，无法定位仓库根——源码审计测试依赖源码树在位");
        return dir!.FullName;
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>空白归一化：换行/缩进/多空格折叠为单空格，断言只关心 token 序列、不缩进敏感。</summary>
    private static string Normalize(string source) => Regex.Replace(source, @"\s+", " ");

    /// <summary>按大括号配对提取方法体（源码审计用，足够可靠：审计目标是本仓库自己的规整代码）。</summary>
    private static string ExtractMethodBody(string source, string signatureFragment)
    {
        var sig = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(sig >= 0, $"找不到方法签名片段: {signatureFragment}");
        var open = source.IndexOf('{', sig);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(open, i - open + 1);
                }
            }
        }

        throw new InvalidOperationException("大括号不配对，提取失败");
    }

    // ───────────────────────── B1：房客对话 patch 三处门 ─────────────────────────

    [Fact]
    public void NpcDialoguePatch_NonAgentBranches_ThinClientGateRemoved()
    {
        var src = Normalize(ReadSource("src", "ValleyAgent", "Patches", "NPCDialoguePatch.cs"));

        // 非 Agent AI 分支不得再以 !isThinClient 为门（F4 身份门槛拆除；房客与主机同节奏）。
        // spark 门（!isThinClient && OnSparkCandidate）是主机专属机制（F7），不在本断言射程内。
        Assert.False(Regex.IsMatch(src, @"isThinClient && \(Config\?\.EnableInfiniteDialogue"),
            "非 Agent 分支仍带 !isThinClient 门 → 房客对无身体村民只能拿原版台词（门槛未拆）");

        // 开关语义保留：EnableInfiniteDialogue + NonAgentAIChatEnabled 两个条件各出现两次
        // （EnableFirstClickVanilla=false 直进 AI 分支 + 原版先行分支）。
        Assert.Equal(2, Regex.Matches(src,
                @"\(Config\?\.EnableInfiniteDialogue \?\? true\) && \(Config\?\.NonAgentAIChatEnabled \?\? true\)")
            .Count);
        Assert.Contains("!(Config?.EnableFirstClickVanilla ?? true)", src);

        // 直进 AI 分支必须把 isThinClient 传给 OpenAgentDialogue（房客侧跳过本地 AgentService 状态保存），
        // 加上 Agent 分支的调用共两处。
        Assert.Equal(2, Regex.Matches(src, @"OpenAgentDialogue\(__instance, isThinClient\);").Count);

        // 原版先行分支仍要置 pending 标记：原版台词关闭后由 CloseDialoguePostfix 追加 AI 对话。
        Assert.Contains("_pendingVanillaDialogueNpc = __instance.Name;", src);
    }

    [Fact]
    public void CloseDialoguePostfix_GateAllowsDialogueTransport()
    {
        var body = Normalize(ExtractMethodBody(
            ReadSource("src", "ValleyAgent", "Patches", "NPCDialoguePatch.cs"),
            "CloseDialoguePostfix(DialogueBox __instance)"));

        // 第三处门：房客侧 AgentServerProvider 恒 null，通道存在性由 transport 判定——
        // 缺这条放行，房客播完原版台词后 AI 输入框永不弹出（比现状更差的静默无响应）。
        Assert.Contains("AgentServerProvider == null && !DialogueBoxInputPatch.HasDialogueTransport", body);

        // 放行后必须完成两件事：开 AI 占位对话框 + 标记活跃对话（输入框与提交管道依赖该标记）。
        Assert.Contains("Game1.drawDialogue(speaker);", body);
        Assert.Contains("DialogueBoxInputPatch.SetActiveAgentNpc(speaker.Name);", body);
    }

    // ───────────────────────── B2：聊天栏房客候选 ─────────────────────────

    [Fact]
    public void ChatBarRouter_BuildPresence_NoFarmhandBroadcastFilter()
    {
        var body = Normalize(ExtractMethodBody(
            ReadSource("src", "ValleyAgent", "Chat", "ChatBarRouter.cs"),
            "BuildPresence(GameLocation location"));

        // 身份过滤拆除：对话不需要身体，全部在场村民都是候选（广播名单只用于远程状态渲染）。
        Assert.DoesNotContain("IsFarmhand", body);
        Assert.DoesNotContain("GetRemoteState", body);

        // 基础守卫不回退：仍按村民/在场判定收敛候选。
        Assert.Contains("IsVillager", body);
    }

    // ───────────────────────── B3：主机中继 promote 语义 ─────────────────────────

    [Fact]
    public void HostRequestHandlers_RelayActions_UseSharedPromotionGate()
    {
        var src = Normalize(ReadSource("src", "ValleyAgent", "Multiplayer", "HostRequestHandlers.cs"));

        // 单一事实源：promote 判定与身体类清单复用 DialogueBoxInputPatch，不复制清单。
        Assert.Contains("DialogueBoxInputPatch.IsPromotionTriggerTool(", src);
        Assert.Contains("DialogueBoxInputPatch.PromoteToAgent(", src);
        Assert.DoesNotContain("\"set_state\"", src);
        Assert.DoesNotContain("\"follow\"", src);

        // speak 不丢广播：中继循环不得引入 DispatchDialogueActions 的 speak/wait 跳过。
        Assert.DoesNotContain("tool is \"speak\" or \"wait\"", src);

        // 顺序约束：LastDialoguePlayerId 必须写在 promote 之后（本轮刚建的身体才能记到 FOLLOW 目标）。
        var promote = src.IndexOf("DialogueBoxInputPatch.PromoteToAgent(", StringComparison.Ordinal);
        var followTarget = src.IndexOf("LastDialoguePlayerId = msg.PlayerId.ToString();", StringComparison.Ordinal);
        Assert.True(promote >= 0 && followTarget > promote,
            $"LastDialoguePlayerId 赋值 (idx={followTarget}) 必须在 promote 调用 (idx={promote}) 之后，"
            + "否则本次回复刚建的身体记不到 FOLLOW 目标");

        // 优先级刷新：调 UpdatePriority，对话次数用 GetConversationCount，hearts 取发起玩家（非 Game1.player）。
        Assert.Contains("UpdatePriority(", src);
        Assert.Contains("NPCDialoguePatch.GetConversationCount(", src);
        Assert.True(Regex.Matches(src, @"Game1\.GetPlayer\(msg\.PlayerId\)").Count >= 2,
            "优先级刷新应按发起玩家（Game1.GetPlayer(msg.PlayerId)）取 hearts 基线");
        Assert.DoesNotContain("Game1.player", src);
    }

    // ───────────────────────── 真单测：DialogueBoxInputPatch / NPCDialoguePatch 只读入口 ─────────────────────────

    [Theory]
    [InlineData("set_state", true)]
    [InlineData("follow", true)]
    [InlineData("move_to", true)]
    [InlineData("harvest", true)]
    [InlineData("water", true)]
    [InlineData("attack", true)]
    [InlineData("mine", true)]
    [InlineData("forage", true)]
    [InlineData("eat_food", true)]
    [InlineData("drop_item", true)]
    [InlineData("use_item", true)]
    [InlineData("SET_STATE", true)] // 清单按 OrdinalIgnoreCase 构建，大小写差异不得漏判
    [InlineData("speak", false)]
    [InlineData("wait", false)]
    [InlineData("emote", false)]
    [InlineData("give_item", false)]
    [InlineData("set", false)]
    [InlineData("", false)]
    public void IsPromotionTriggerTool_OnlyBodyToolsMatch(string tool, bool expected)
    {
        Assert.Equal(expected, DialogueBoxInputPatch.IsPromotionTriggerTool(tool));
    }

    [Fact]
    public void IsPromotionTriggerTool_NullTool_ReturnsFalse()
    {
        Assert.False(DialogueBoxInputPatch.IsPromotionTriggerTool(null!));
    }

    [Fact]
    public void HasDialogueTransport_FollowsSetDialogueTransport()
    {
        DialogueBoxInputPatch.SetDialogueTransport(null);
        Assert.False(DialogueBoxInputPatch.HasDialogueTransport);

        DialogueBoxInputPatch.SetDialogueTransport(new FarmhandDialogueTransport(
            new RecordingModHelper(new RecordingMultiplayerService()), new RecordingMonitor(),
            ModId, TimeSpan.FromMilliseconds(100)));
        Assert.True(DialogueBoxInputPatch.HasDialogueTransport);
        // Dispose 统一还原为 null（主机形态语义）。
    }

    [Fact]
    public void ConversationCount_PerNpcAndCaseInsensitive()
    {
        NPCDialoguePatch.IncrementConversationCount("Haley");
        NPCDialoguePatch.IncrementConversationCount("haley");
        NPCDialoguePatch.IncrementConversationCount("Sebastian");

        // 中继优先级刷新的输入：按 NPC 分桶 + OrdinalIgnoreCase + 未对话者恒 0。
        Assert.Equal(2, NPCDialoguePatch.GetConversationCount("Haley"));
        Assert.Equal(1, NPCDialoguePatch.GetConversationCount("Sebastian"));
        Assert.Equal(0, NPCDialoguePatch.GetConversationCount("Nobody"));
    }

    // ───────────────────────── 行为测试：主机中继回包链路 ─────────────────────────

    /// <summary>
    ///     双用途 provider 桩：GenerateDialogueAsync 回放编程好的响应，
    ///     SendMessageAsync 捕获 action_result JSON（CommandExecutor 的 C3 反馈环出口），
    ///     用"最后一条 action_result 是哪个工具"证明动作循环走到了队尾（中途未被打断）。
    /// </summary>
    private sealed class RecordingRelayProvider : IAgentServerProvider
    {
        private readonly Func<DialogueRequest, DialogueResponse> _responder;
        private readonly List<string> _sent = new();

        public RecordingRelayProvider(Func<DialogueRequest, DialogueResponse> responder) => _responder = responder;

        // 空访问器事件：桩不触发连接状态（自定义访问器无 CS0067）。
        public event Action? OnConnected { add { } remove { } }
        public event Action? OnDisconnected { add { } remove { } }

        public IReadOnlyList<string> SentJson => _sent.ToList();

        public Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default)
            => Task.FromResult(_responder(request));

        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task SendMessageAsync(string jsonMessage, CancellationToken ct = default)
        {
            lock (_sent)
            {
                _sent.Add(jsonMessage);
            }

            return Task.CompletedTask;
        }
    }

    private static DialogueRequestMessage DialogueMsg(long playerId, string npc, string input) =>
        new()
        {
            NpcName = npc,
            PlayerMessage = input,
            PlayerId = playerId,
            WorldSnapshotJson = System.Text.Json.JsonSerializer.Serialize(TestSnapshots.Minimal())
        };

    [Fact]
    public void RelayDialogueResponse_NoAgentService_AllActionsReachExecutor_BroadcastCompletes()
    {
        // 房客中继的最保守形态（旧部署 _agentService 为 null）：身体类动作不得让回包链路中断，
        // speak 仍按既有语义走 ExecuteAction（NoOp 不回发），后续动作照常分发到队尾。
        using var scope = new Game1TestScope(hostMode: true);
        _ = scope;
        var monitor = new RecordingMonitor();
        var multiplayer = new RecordingMultiplayerService();
        var provider = new RecordingRelayProvider(_ => new DialogueResponse(
            "好，我去看看",
            new List<ToolAction>
            {
                new("speak", new Dictionary<string, object>(), "call-speak"),
                new("set_state", new Dictionary<string, object> { ["state"] = "FOLLOW" }, "call-state"),
                new("emote", new Dictionary<string, object> { ["emote_id"] = "happy" }, "call-emote"),
            }));
        var broadcaster = new AgentSyncBroadcaster(
            new AgentService(new ModConfig(), new AgentAllocationManager(), new TokenBudgetManager(100_000),
                new PerformanceMonitor(), new CacheManager(), new CircuitBreaker()),
            monitor, new RecordingModHelper(multiplayer), ModId);
        var executor = new CommandExecutor(monitor, null!, new CommandRegistry(monitor), provider);
        var handlers = new HostRequestHandlers(monitor, provider, broadcaster, new StubHostGiftTransport(),
            commandExecutor: executor, agentService: null);
        using var pump = new MainThreadPump();

        handlers.HandleDialogueRequest(DialogueMsg(111222333, "Abigail", "去看看"));

        Assert.True(
            Poll.Until(() => monitor.CountContaining("HandleDialogueRequest completed") >= 1, TimeSpan.FromSeconds(15)),
            "回包未完成（动作循环可能中断）："
            + string.Join(" | ", monitor.Snapshot().TakeLast(6).Select(e => $"{e.Level}:{e.Message}")));

        // 动作循环走到队尾：最后一条 action_result 是列表末尾的 emote（speak 是 NoOp 不回发）。
        var lastResult = provider.SentJson.LastOrDefault();
        Assert.NotNull(lastResult);
        Assert.Contains("\"tool\":\"emote\"", lastResult);
        Assert.Contains("\"success\":false", lastResult);

        // 回包照常广播给发起房客（含 actionsJson 透传）。
        var responses = multiplayer.OfType(MessageTypes.DialogueResponse);
        var response = Assert.Single(responses);
        var actionsJson = ((DialogueResponseMessage)response.Message).ActionsJson ?? "";
        Assert.Contains("emote", actionsJson);
        Assert.Contains("set_state", actionsJson);
    }

    [Fact]
    public void RelayDialogueResponse_BodyToolOnNonexistentNpc_NoGhostBody_NoBreak()
    {
        // NPC 对象不存在（房客请求了无效名字）时不建身体：promote 门以 getCharacterFromName 为前置，
        // 防止为幽灵 NPC 占用身体名额；动作照常尝试执行并安全失败，回包不中断。
        using var scope = new Game1TestScope(hostMode: true);
        _ = scope;
        var monitor = new RecordingMonitor();
        var multiplayer = new RecordingMultiplayerService();
        var provider = new RecordingRelayProvider(_ => new DialogueResponse(
            "（沉默）",
            new List<ToolAction>
            {
                new("set_state", new Dictionary<string, object> { ["state"] = "FOLLOW" }, "call-state"),
            }));
        var manager = new AgentAllocationManager();
        var agentService = new AgentService(new ModConfig(), manager, new TokenBudgetManager(100_000),
            new PerformanceMonitor(), new CacheManager(), new CircuitBreaker());
        var broadcaster = new AgentSyncBroadcaster(agentService, monitor, new RecordingModHelper(multiplayer), ModId);
        var executor = new CommandExecutor(monitor, agentService, new CommandRegistry(monitor), provider);
        var handlers = new HostRequestHandlers(monitor, provider, broadcaster, new StubHostGiftTransport(),
            commandExecutor: executor, agentService: agentService);
        using var pump = new MainThreadPump();

        handlers.HandleDialogueRequest(DialogueMsg(111222333, "NonExistentNpc_98765", "跟我走"));

        Assert.True(
            Poll.Until(() => monitor.CountContaining("HandleDialogueRequest completed") >= 1, TimeSpan.FromSeconds(15)),
            "回包未完成："
            + string.Join(" | ", monitor.Snapshot().TakeLast(6).Select(e => $"{e.Level}:{e.Message}")));

        // 幽灵 NPC 不占身体名额；动作仍被尝试（action_result 安全失败）。
        Assert.Empty(manager.GetAllAllocatedAgents());
        Assert.False(agentService.HasAgent("NonExistentNpc_98765"));
        var lastResult = provider.SentJson.LastOrDefault();
        Assert.NotNull(lastResult);
        Assert.Contains("\"tool\":\"set_state\"", lastResult);
        Assert.Contains("\"success\":false", lastResult);
        Assert.DoesNotContain("promotion to Agent failed",
            string.Join("\n", monitor.Snapshot().Select(e => e.Message)));
    }
}
