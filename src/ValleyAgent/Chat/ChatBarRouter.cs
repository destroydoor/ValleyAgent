using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.AI;
using ValleyAgent.Config;
using ValleyAgent.Infrastructure;
using ValleyAgent.Patches;
using ValleyAgent.Services;
using ValleyAgent.Services.Schedule;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Chat;

/// <summary>
///     聊天栏玩家→NPC 路由的游戏侧编排器（E2-2）。
///     职责：构建在场 NPC 摘要 → 四层路由消歧 → 只对被路由命中的 NPC 发起一次 LLM
///     dialogue 请求 → 主线程把回复交给 ActiveSpeechRouter 渲染（气泡/聊天栏），
///     全程不强制打开对话框（§3.1）。
/// </summary>
public static class ChatBarRouter
{
    private static IMonitor? _monitor;
    private static AgentService? _agentService;
    private static IAgentServerProvider? _agentServerProvider;
    private static CommandExecutor? _commandExecutor;
    private static ModConfig? _config;

    // E5-2: 远程喊话调度器（玩家喊到不在场 NPC 时安排延迟回应）与 4 层确定性路由。
    private static ShoutReplyScheduler? _shoutReplyScheduler;
    private static MorningShoutRouter? _morningShoutRouter;
    private static NpcScheduleService? _scheduleService;

    // 最近交互时间（UTC），供路由第 4 层"30 秒内交互过"使用。
    private static readonly Dictionary<string, DateTime> _lastInteractionTimes = new(StringComparer.OrdinalIgnoreCase);

    // 每 NPC 在途请求守卫：同一 NPC 已有未回 LLM 请求时，后续消息不重复发起（防刷屏）。
    private static readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _inflightLock = new();

    private static readonly ConcurrentQueue<PendingChatReply> _pendingReplies = new();

    /// <summary>
    ///     E5-3 主动发言额度（由 EventHandlerInitializer 在 Initialize 时注入）。
    ///     用于话痨度计算：额度越低话痨度越低。
    /// </summary>
    public static ProactiveSpeechQuota? ProactiveQuota { get; set; }

    /// <summary>当前游戏日期 ISO（"Y1_spring_1"），供额度查询用。</summary>
    private static string GameDateIso
    {
        get
        {
            try
            {
                return $"{Game1.year}_" + (Game1.currentSeason ?? "spring") + "_" + Game1.dayOfMonth;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public static void Initialize(
        IMonitor monitor,
        AgentService? agentService,
        IAgentServerProvider? provider,
        CommandExecutor? commandExecutor,
        ModConfig? config,
        ProactiveSpeechQuota? proactiveSpeechQuota = null,
        NpcScheduleService? scheduleService = null)
    {
        _monitor = monitor;
        _agentService = agentService;
        _agentServerProvider = provider;
        _commandExecutor = commandExecutor;
        _config = config;

        if (config != null)
        {
            ChatSessionRegistry.Instance.SessionTimeoutSeconds = config.ChatSessionTimeoutSeconds;
        }

        // E5-2: 装配远程喊话调度器（默认礼貌回应生成器；LLM 接线在合并期序列化任务完成）
        _shoutReplyScheduler = new ShoutReplyScheduler(
            monitor,
            proactiveSpeechQuota,
            DefaultRemoteReply);
        _morningShoutRouter = new MorningShoutRouter();
        _scheduleService = scheduleService;
    }

    /// <summary>
    ///     E5-2: 默认远程回应文本（无 LLM 接线时的礼貌回应）。
    ///     合并期由 LLM 对话路径生成器替换。
    /// </summary>
    private static string? DefaultRemoteReply(string npcName, string playerShout)
    {
        _ = playerShout;
        var npc = Game1.getCharacterFromName(npcName);
        return npc == null ? null : "（听到你的喊话）我在这边。";
    }

    /// <summary>主线程每 tick 处理远程喊话到期回应（由 EventHandlerInitializer 调用）。</summary>
    public static void ProcessShoutReplies() => _shoutReplyScheduler?.ProcessDueReplies();

    /// <summary>
    ///     E5-2: 不在场远程喊话路由。4 层确定性路由命中目标 → 安排延迟回应；未命中 → 静默。
    /// </summary>
    private static bool TryRouteRemoteShout(string text)
    {
        if (_morningShoutRouter == null || _shoutReplyScheduler == null || _agentService == null)
        {
            return false;
        }

        var candidates = BuildShoutCandidates();
        if (candidates.Count == 0)
        {
            return false;
        }

        var target = _morningShoutRouter.Resolve(text, candidates);
        if (string.IsNullOrEmpty(target))
        {
            return false; // 全员未醒 / 4 层全空 → 沉默
        }

        // 会话簿记：喊话也算一次交互（不强制建会话，但刷新会话时间）
        ChatSessionRegistry.Instance.StartOrTouch(target, ChatSessionInitiator.PlayerChat);
        RecordInteraction(target);

        _shoutReplyScheduler.ScheduleReply(target, text);
        _monitor?.Log($"[Shout] '{Preview(text)}' → remote target {target}", LogLevel.Debug);
        return true;
    }

    /// <summary>装配全部 Agent NPC 的喊话候选（4 层路由输入）。</summary>
    private static List<MorningShoutRouter.ShoutCandidate> BuildShoutCandidates()
    {
        var result = new List<MorningShoutRouter.ShoutCandidate>();
        if (_agentService == null)
        {
            return result;
        }

        var now = DateTime.UtcNow;
        foreach (var agent in _agentService.GetAllAgents())
        {
            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc == null)
            {
                continue;
            }

            var isAwake = _scheduleService?.IsAwake(npc, Game1.timeOfDay) ?? true;
            var friendshipPoints = 0;
            if (Game1.player?.friendshipData != null
                && Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fd))
            {
                friendshipPoints = fd.Points;
            }

            var state = agent.StateMachine.CurrentStateFlag;
            result.Add(new MorningShoutRouter.ShoutCandidate(
                agent.NpcName,
                npc.displayName ?? agent.NpcName,
                isAwake,
                ChatSessionRegistry.Instance.IsInActiveSession(agent.NpcName, now),
                // E4-1 雇佣状态落地后在此并入 HIRED（当前仅 FOLLOW 视为"随行中"）
                state == AgentState.FOLLOW,
                friendshipPoints));
        }

        return result;
    }

    /// <summary>
    ///     聊天栏消息入口（由 ChatBoxInputPatch 在本地玩家提交普通聊天时调用，主线程）。
    ///     无在场 NPC / 内容非对话 → 不路由（静默）；否则路由命中 → 异步请求 LLM。
    /// </summary>
    public static void HandlePlayerChatMessage(string message)
    {
        try
        {
            if (_agentServerProvider == null)
            {
                return; // ThinClient / 未初始化：不路由
            }

            if (_config == null || !_config.Enabled || !_config.ChatBarRoutingEnabled)
            {
                return;
            }

            var text = message?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return;
            }

            if (GameEventGuard.IsEventOrFestivalActive)
            {
                return; // 事件/节日期间 NPC 处于脚本位置，不打断
            }

            if (Game1.player == null || Game1.currentLocation == null)
            {
                return;
            }

            // 沉默权预过滤（零 token）：纯标点/符号/表情不算对话。
            if (ChatRouteResolver.IsTrivialNonDialogue(text))
            {
                _monitor?.Log($"[ChatBar] Dropped trivial message: {text}");
                return;
            }

            var present = BuildPresence(Game1.currentLocation, Game1.player);
            if (present.Count == 0)
            {
                // E5-2: 不在场远程喊话——4 层确定性路由命中后安排延迟回应。
                // 普通聊天只路由在场 NPC（§3.1），远程喊话是本层唯一入口。
                if (TryRouteRemoteShout(text))
                {
                    return;
                }

                _monitor?.Log("[ChatBar] No villager present — message not routed (off-scene remote chat is E5).",
                    LogLevel.Debug);
                return; // 不在场 NPC 听不到普通聊天（§3.1）；远程喊话是 E5
            }

            var sessionNpc = ChatSessionRegistry.Instance.GetActiveSessionNpc();
            var options = new ChatRouteOptions
            {
                RecentInteractionWindow = TimeSpan.FromSeconds(_config.ChatRecentInteractionWindowSeconds),
                NearbyTiles = _config.ChatNearbyDistanceTiles,
                GroupResponseMax = _config.ChatGroupResponseMax,
                Talkativeness = GetTalkativeness
            };
            var route = ChatRouteResolver.Resolve(text, sessionNpc, present, options);
            if (route == null)
            {
                return;
            }

            _monitor?.Log(
                $"[ChatBar] '{text}' → {(route.IsGroup ? "group(" + string.Join(",", route.TargetNpcs) + ")" : route.TargetNpcs[0])}",
                LogLevel.Debug);

            foreach (var target in route.TargetNpcs)
            {
                // 会话簿记：路由命中即建/刷新会话（会话内来回不计主动额度）。
                ChatSessionRegistry.Instance.StartOrTouch(target, ChatSessionInitiator.PlayerChat);
                RecordInteraction(target);

                if (!TryReserveInflight(target))
                {
                    continue; // 该 NPC 已有在途请求
                }

                _ = SendDialogueAsync(target, text);
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[ChatBar] Route failed: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     主线程消费 LLM 回复（每 tick 由 EventHandlerInitializer.OnUpdateTicked 调用）。
    ///     回复经 ActiveSpeechRouter 渲染（≤8 格气泡 / 否则聊天栏），不打开对话框。
    /// </summary>
    public static void ProcessPendingReplies()
    {
        while (_pendingReplies.TryDequeue(out var reply))
        {
            // 死锁修复（2026-09-12）：逐条兜底。异常逃出 while 会让本 tick 剩余回复
            // 连同调用方下游的所有主线程泵一起被跳过（泵是串行责任链）。
            try
            {
                RenderReply(reply);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[ChatBar] Failed to render reply for {reply.NpcName}: {ex}", LogLevel.Error);
            }
        }
    }

    private static void RenderReply(PendingChatReply reply)
    {
        var npc = Game1.getCharacterFromName(reply.NpcName);
        if (npc == null)
        {
            return;
        }

        if (reply.IsFailure)
        {
            // §3.7 规则 3：失败也要灰色字，不要沉默（静默规则只保护正常流程）。
            Game1.chatBox?.addMessage($"*{reply.NpcName} 没有回应*", Color.Gray);
            return;
        }

        if (string.IsNullOrWhiteSpace(reply.Speech))
        {
            return; // LLM 行使沉默权：路由命中 ≠ 必须回
        }

        ActiveSpeechRouter.Route(npc, Game1.player, reply.Speech);
        _monitor?.Log($"[ChatBar] {reply.NpcName} → {Preview(reply.Speech)}", LogLevel.Debug);

        NPCDialoguePatch.IncrementConversationCount(reply.NpcName);

        try
        {
            DialogueBoxInputPatch.DispatchDialogueActions(npc, reply.Actions);
        }
        catch (Exception ex)
        {
            // 动作分发（含 PromoteToAgent）失败不得回灌渲染泵：台词已经说出去了。
            _monitor?.Log($"[ChatBar] Action dispatch failed for {reply.NpcName}: {ex}", LogLevel.Error);
        }
    }

    /// <summary>记录一次"与玩家交互"（聊天栏路由命中 / 对话框对话开始）。</summary>
    public static void RecordInteraction(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return;
        }

        _lastInteractionTimes[npcName] = DateTime.UtcNow;
    }

    /// <summary>返回标题/卸载时清空全部路由状态。</summary>
    public static void Reset()
    {
        _lastInteractionTimes.Clear();
        lock (_inflightLock)
        {
            _inflight.Clear();
        }

        while (_pendingReplies.TryDequeue(out _))
        {
        }

        ChatSessionRegistry.Instance.ClearAll();
    }

    /// <summary>
    ///     E2-2 话痨度确定性代理：0.5 + 好感/2500 × 0.5，截断到 [0, 1]。
    ///     E5-3 增强：结合主动发言额度——额度越低话痨度越低（NPC 快没配额时更沉默），
    ///     由 EventHandlerInitializer 注入 ProactiveQuota。
    /// </summary>
    private static double GetTalkativeness(string npcName)
    {
        var friendshipBase = 0.5;
        if (Game1.player?.friendshipData != null
            && Game1.player.friendshipData.TryGetValue(npcName, out var friendship))
        {
            friendshipBase = 0.5 + friendship.Points / 2500.0 * 0.5;
        }

        var quotaFactor = 1.0;
        if (ProactiveQuota != null)
        {
            var remaining = ProactiveQuota.GetRemainingQuota(npcName, GameDateIso);
            quotaFactor = remaining <= 0 ? 0.2 : 0.5 + 0.5 * (remaining / Math.Max(1, ProactiveQuota.DailyLimit));
        }

        return Math.Clamp(friendshipBase * quotaFactor, 0.0, 1.0);
    }

    private static List<ChatPresence> BuildPresence(GameLocation location, Farmer player)
    {
        var result = new List<ChatPresence>();
        if (location.characters == null)
        {
            return result;
        }

        foreach (var character in location.characters)
        {
            if (character is not NPC npc || !npc.IsVillager || npc.Tile == null)
            {
                continue;
            }

            var distance = (int)(Math.Abs(npc.Tile.X - player.Tile.X) + Math.Abs(npc.Tile.Y - player.Tile.Y));
            var isFollowing = IsFollowing(npc.Name);
            var lastInteraction = _lastInteractionTimes.TryGetValue(npc.Name, out var ts) ? ts : DateTime.MinValue;
            result.Add(new ChatPresence(npc.Name, npc.displayName, distance, isFollowing, lastInteraction));
        }

        return result;
    }

    private static bool IsFollowing(string npcName)
    {
        return _agentService != null
               && _agentService.TryGetAgent(npcName, out var agent)
               && agent != null
               && agent.StateMachine.CurrentStateFlag == AgentState.FOLLOW;
    }

    private static bool TryReserveInflight(string npcName)
    {
        lock (_inflightLock)
        {
            return _inflight.Add(npcName);
        }
    }

    private static void ReleaseInflight(string npcName)
    {
        lock (_inflightLock)
        {
            _ = _inflight.Remove(npcName);
        }
    }

    private static async Task SendDialogueAsync(string npcName, string text)
    {
        try
        {
            var worldSnapshot = WorldSnapshotBuilder.Build(npcName, DialogueBoxInputPatch.GetNpcState(npcName));
            var request = new DialogueRequest(
                "dialogue",
                Guid.NewGuid().ToString("N"),
                npcName,
                text,
                worldSnapshot,
                Game1.player.UniqueMultiplayerID.ToString());

            var response = await _agentServerProvider!.GenerateDialogueAsync(request).ConfigureAwait(false);
            _pendingReplies.Enqueue(new PendingChatReply(
                npcName,
                response.Speech ?? string.Empty,
                response.Actions ?? new List<ToolAction>(),
                false));
            // 深度告警：主线程泵停摆时聊天回复堆积的早期信号（2026-09-11 生产化仪器）
            QueueTelemetry.WarnIfDeep("chat-replies", _pendingReplies.Count, _monitor);
        }
        catch (TimeoutException)
        {
            // 异步聊天不阻塞玩家：超时记为失败（主线程渲染灰色系统消息）
            _pendingReplies.Enqueue(new PendingChatReply(npcName, string.Empty, new List<ToolAction>(), true));
            QueueTelemetry.WarnIfDeep("chat-replies", _pendingReplies.Count, _monitor);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[ChatBar] Dialogue request failed for {npcName}: {ex.Message}", LogLevel.Error);
            _pendingReplies.Enqueue(new PendingChatReply(npcName, string.Empty, new List<ToolAction>(), true));
            QueueTelemetry.WarnIfDeep("chat-replies", _pendingReplies.Count, _monitor);
        }
        finally
        {
            ReleaseInflight(npcName);
        }
    }

    private static string Preview(string text)
    {
        var t = text.Replace('\n', ' ').Trim();
        return t.Length > 80 ? t.Substring(0, 80) + "..." : t;
    }

    /// <summary>待主线程渲染的聊天栏回复。</summary>
    private sealed class PendingChatReply
    {
        public PendingChatReply(string npcName, string speech, IReadOnlyList<ToolAction> actions, bool isFailure)
        {
            NpcName = npcName;
            Speech = speech;
            Actions = actions;
            IsFailure = isFailure;
        }

        public string NpcName { get; }
        public string Speech { get; }
        public IReadOnlyList<ToolAction> Actions { get; }
        public bool IsFailure { get; }
    }
}