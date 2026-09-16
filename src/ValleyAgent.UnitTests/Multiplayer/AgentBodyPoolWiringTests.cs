using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     PR2 B4/B5 接线守卫（设计 docs/design/2026-09-13-agent-body-refactor.md §3.3/§3.4）。
///     Harmony patch / EventHandlerInitializer 接线依赖 Game1 实例，无头环境不可驱动——
///     接线类断言用源码扫描（空白归一化 + 语义匹配，不缩进敏感；范式与
///     ThinClientCapabilityMatrixTests / FarmhandBodyLinkTests 一致）。纯逻辑部分
///     （池表规则、DirectorTools 接缝）在 AgentAllocationManagerIdleReclaimTests / DirectorToolsTests 有真单测。
/// </summary>
public class AgentBodyPoolWiringTests
{
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

    // ───────────────────────── B5.1：对话结束释放 manual override ─────────────────────────

    [Fact]
    public void EndTopicConversation_ReleasesManualOverride_KeepUntilAware()
    {
        var body = Normalize(ExtractMethodBody(
            ReadSource("src", "ValleyAgent", "Patches", "NPCDialoguePatch.cs"),
            "EndTopicConversation(string npcName)"));

        // 释放前判定：HasAgent + manual 标记 + KeepUntil 未到期不释放（判定模式照抄 PromoteToAgent）。
        Assert.Contains("AgentService.HasAgent(npcName)", body);
        Assert.Contains("IsManuallyOverridden: true", body);
        Assert.Contains("!(allocation.KeepUntil.HasValue && DateTime.UtcNow < allocation.KeepUntil.Value)", body);
        Assert.Contains("ReleaseManualOverride(npcName)", body);

        // 次序：先恢复对话前状态，再释放 override（设计 §3.4 步骤 1：恢复 pre-state 之后）。
        var restore = body.IndexOf("TrySetAgentState(npcName", StringComparison.Ordinal);
        var release = body.IndexOf("ReleaseManualOverride(npcName)", StringComparison.Ordinal);
        Assert.True(restore >= 0 && release > restore,
            $"释放 override (idx={release}) 必须在恢复 pre-state (idx={restore}) 之后");
    }

    // ───────────────────────── B5.2/3：TimeChanged 周期驱动 + Manager 显式规则调用序 ─────────────────────────

    [Fact]
    public void TimeChanged_DrivesIdleReclaimEveryTick()
    {
        var src = ReadSource("src", "ValleyAgent", "Initialization", "EventHandlerInitializer.cs");
        // issue #24（2026-09-16）：OnTimeChanged 拆 wrapper（try/catch 守卫）+ OnTimeChangedCore，
        // 空闲回收编排移入 Core——断言跟随抽取，锁定"时间跳驱动回收"的契约不变。
        var timeChanged = Normalize(ExtractMethodBody(src, "private void OnTimeChangedCore(TimeChangedEventArgs e)"));

        // 每个时间跳（游戏内 10 分钟）调一次空闲回收编排；换日处的 ReevaluateAllocations 保留不动。
        Assert.Contains("RunIdleAllocationReclaim();", timeChanged);
        Assert.Contains("AllocationManager.ReevaluateAllocations();", Normalize(src));

        var reclaim = Normalize(ExtractMethodBody(src, "private void RunIdleAllocationReclaim()"));
        // 调用序（设计 §3.4）：先释放空闲 override，再按容量裁剪——顺序倒了被释放者错过了本轮淘汰窗口。
        var release = reclaim.IndexOf("ReleaseIdleManualOverrides(", StringComparison.Ordinal);
        var reevaluate = reclaim.IndexOf("ReevaluateAllocations();", StringComparison.Ordinal);
        Assert.True(release >= 0 && reevaluate > release,
            $"ReevaluateAllocations (idx={reevaluate}) 必须在 ReleaseIdleManualOverrides (idx={release}) 之后");

        // 活跃对话判定由调用方传委托（Manager 不得依赖 Patches 层）。
        Assert.Contains("DialogueBoxInputPatch.GetActiveAgentNpc()", reclaim);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", reclaim);

        // 空闲阈值常量：明显大于单轮对话间隔（房客中继对话主机无感知关闭，靠超时兜底）。
        Assert.Contains("IdleOverrideThreshold = TimeSpan.FromMinutes(10)", Normalize(src));
    }

    // ───────────────────────── B5.4：常驻拆除订阅 + PromoteToAgent 收编 ─────────────────────────

    [Fact]
    public void OnAgentDeallocated_HasResidentTeardownSubscription()
    {
        var src = Normalize(ReadSource("src", "ValleyAgent", "Initialization", "EventHandlerInitializer.cs"));

        // 常驻订阅 + 对称反订阅（UnsubscribeEvents）。
        Assert.Contains("OnAgentDeallocated += OnAgentDeallocatedTeardown", src);
        Assert.Contains("OnAgentDeallocated -= OnAgentDeallocatedTeardown", src);
    }

    [Fact]
    public void ResidentTeardown_FullRitual_Idempotent()
    {
        var body = Normalize(ExtractMethodBody(
            ReadSource("src", "ValleyAgent", "Initialization", "EventHandlerInitializer.cs"),
            "OnAgentDeallocatedTeardown(object? sender, AgentAllocationEventArgs e)"));

        // 幂等守卫：agent 已不在直接返回（防重复副作用/重复弹提示）。
        var guard = body.IndexOf("TryGetAgent(e.NpcName, out var agent)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "拆除必须先做幂等守卫（agent 已不在直接返回）");

        // 拆除仪式：带 reason 的 evicted state_changed → RemoveAgent（降级休眠）→ 日程还原。
        var forceTransition = body.IndexOf("ForceTransition(AgentState.IDLE, true, reason: \"evicted\")", StringComparison.Ordinal);
        var removeAgent = body.IndexOf("RemoveAgent(e.NpcName)", StringComparison.Ordinal);
        Assert.True(forceTransition >= 0, "必须发带 reason=evicted 的 state_changed（TS 端感知被逐）");
        Assert.True(removeAgent > forceTransition,
            $"ForceTransition (idx={forceTransition}) 必须在 RemoveAgent (idx={removeAgent}) 之前——RemoveAgent 后实例已出活跃表");

        Assert.Contains("controller = null", body);
        Assert.Contains("Halt()", body);
        Assert.Contains("followSchedule = true", body);
        Assert.Contains("ignoreScheduleToday = false", body);

        // 玩家可见提示只对"被玩家手动挤掉"发；换日裁剪/空闲淘汰静默。
        var reasonGate = body.IndexOf("Replaced by manual override", StringComparison.Ordinal);
        var chatMessage = body.IndexOf("告别离开了", StringComparison.Ordinal);
        Assert.True(reasonGate >= 0 && chatMessage > reasonGate,
            "\"告别离开了\"必须被 reason == Replaced by manual override 门控");
    }

    [Fact]
    public void PromoteToAgent_DelegatesTeardownToResidentSubscription()
    {
        var body = Normalize(ExtractMethodBody(
            ReadSource("src", "ValleyAgent", "Patches", "DialogueBoxInputPatch.cs"),
            "PromoteToAgent(string npcName, string source = \"dialogue\")"));

        // 保留 replacedNpc 捕获与留痕日志……
        Assert.Contains("OnAgentDeallocated += OnDeallocated", body);
        Assert.Contains("deallocated — replaced by", body);

        // ……手工拆除块收编：ForceTransition/RemoveAgent/日程还原/告别提示改由常驻订阅统一做，
        // 保证 evicted state_changed 与聊天提示恰好各一次。
        Assert.DoesNotContain("RemoveAgent(replacedNpc)", body);
        Assert.DoesNotContain("告别离开了", body);
        Assert.DoesNotContain("followSchedule = true", body);

        // source 参数落日志：导演路径传 "director"，日志不误导。
        Assert.Contains("promoted to Agent via {source}", body);
    }

    // ───────────────────────── B4：DirectorTools 行为类建身体接线 ─────────────────────────

    [Fact]
    public void DirectorTools_DefaultEnsureBodyWiredToPromotion()
    {
        var src = Normalize(ReadSource("src", "ValleyAgent", "Commands", "DirectorTools.cs"));

        // 公共构造器默认接 PromoteToAgent（导演来源标注，日志不误导）。
        Assert.Contains("DialogueBoxInputPatch.PromoteToAgent(npcName, \"director\")", src);
        // 单一事实源：HasAgent 短路在 promote 之前。
        Assert.Contains("agentService.HasAgent(npcName)", src);

        var setPosition = ExtractMethodBody(src, "SetNpcPosition(Dictionary<string, object> args)");
        Assert.Contains("_ensureBody(npcName, \"set_npc_position\")", setPosition);
        // 分配失败按既有失败语义返回 AgentMissing，不抛异常。
        Assert.Contains("ActionResultReason.AgentMissing", setPosition);
    }

    [Fact]
    public void DirectorTools_SpawnBeatIsDataClass_NoBodyAllocation()
    {
        // spawn_beat / spawn_group_beat 归类依据：实现只写 BeatStore（消费方 WorldSnapshotBuilder /
        // DirectorContextBuilder 只读 beat 做 L3 prompt 注入，无消费活跃状态机的路径）→ 纯数据类，不建身体。
        var src = Normalize(ReadSource("src", "ValleyAgent", "Commands", "DirectorTools.cs"));
        var spawnBeat = ExtractMethodBody(src, "SpawnBeat(Dictionary<string, object> args)");
        var spawnGroupBeat = ExtractMethodBody(src, "SpawnGroupBeat(Dictionary<string, object> args)");

        Assert.Contains("_beatStore.Create(", spawnBeat);
        Assert.Contains("_beatStore.CreateGroup(", spawnGroupBeat);
        Assert.DoesNotContain("_ensureBody", spawnBeat);
        Assert.DoesNotContain("_ensureBody", spawnGroupBeat);
    }

    // ───────────────────────── B5.5：房客中继对话计数补遗 ─────────────────────────

    [Fact]
    public void HandleDialogueRequest_IncrementsConversationCountBeforeReading()
    {
        var src = Normalize(ReadSource("src", "ValleyAgent", "Multiplayer", "HostRequestHandlers.cs"));

        // 计数在主线程队列块内（与本地路径 SubmitInput 的主线程计数时机一致），
        // 且在 ApplyDialogueResponse 读取（UpdatePriority 刷 ConversationFrequency）之前——
        // 否则纯中继对话的 ConversationFrequency 恒为 0，空闲淘汰优先级比较失真。
        var enqueue = src.IndexOf("EnqueueMainThread(() =>", StringComparison.Ordinal);
        var increment = src.IndexOf("NPCDialoguePatch.IncrementConversationCount(msg.NpcName)", StringComparison.Ordinal);
        var apply = src.IndexOf("ApplyDialogueResponse(msg, response)", StringComparison.Ordinal);
        Assert.True(enqueue >= 0 && increment > enqueue,
            "中继对话计数必须在主线程队列块内（与本地路径计数时机一致）");
        Assert.True(apply > increment,
            $"ApplyDialogueResponse (idx={apply}) 必须在计数 (idx={increment}) 之后");
    }

    // ───────────────────────── B6：GMCM 文案（"AI 身体"表述） ─────────────────────────

    [Fact]
    public void Gmcm_AgentCountTiers_RenamedToBodyWording()
    {
        var src = ReadSource("src", "ValleyAgent", "Config", "GMCMIntegration.cs");

        Assert.Contains("最少 AI 身体数", src);
        Assert.Contains("平时 AI 身体数", src);
        Assert.Contains("AI 身体上限", src);
        // 旧"Agent NPC 数"文案全部退场。
        Assert.DoesNotContain("最小 Agent NPC 数", src);
        Assert.DoesNotContain("普通 Agent NPC 数", src);
        Assert.DoesNotContain("最大 Agent NPC 数", src);
    }
}
