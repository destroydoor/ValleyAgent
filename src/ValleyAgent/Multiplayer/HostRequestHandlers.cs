using System;
using System.Collections.Concurrent;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.AI;
using ValleyAgent.Infrastructure;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Patches;
using ValleyAgent.Services;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Multiplayer;

/// <summary>
///     主机侧 3 条代理链路处理器（对话/送礼/交互）。
///     对话链路：HandleDialogueRequest 已实现——反序列化 WorldSnapshot、调用主机 IAgentServerProvider、
///     在主机侧执行 response.Actions（实体在主机权威）、通过 AgentSyncBroadcaster 回包给发起方 Farmhand。
///     送礼链路：HandleGiftRequest 通过 IGiftTransport（HostGiftTransport）评估，回包给 Farmhand。
///     交互链路：占位，后续 Task 填充。
/// </summary>
public class HostRequestHandlers
{
    private readonly AgentSyncBroadcaster _broadcaster;
    private readonly CommandExecutor? _commandExecutor;
    private readonly IGiftTransport _giftTransport;
    private readonly IMonitor _monitor;
    private readonly IAgentServerProvider _serverProvider;
    private readonly AgentService? _agentService;

    // await 续体在 ThreadPool 线程执行；所有 Game1/NetField 写入必须经此队列回主线程
    // （与 EventHandlerInitializer._pendingWsCommands 同一纪律，2026-08-23 审计 P0）。
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();

    // 静态排水方法无法触达实例 _monitor，构造时缓存一份（与 NPCGiftPatch.Monitor 同模式）。
    private static IMonitor? StaticMonitor { get; set; }

    /// <summary>后台线程投递主线程执行。供本类续体及 ServiceInitializer 的 provider 回调使用。</summary>
    internal static void EnqueueMainThread(Action action)
    {
        MainThreadActions.Enqueue(action);
        // 深度告警：泵每 tick 全量排水，稳态深度应≈0——爆表 = 主线程泵停摆的早期信号（2026-09-11 生产化仪器）
        QueueTelemetry.WarnIfDeep("host-request-mainthread", MainThreadActions.Count, StaticMonitor);
    }

    /// <summary>每 tick 由 EventHandlerInitializer.OnUpdateTicked 在主线程调用。</summary>
    public static void ProcessMainThreadActions()
    {
        while (MainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 队列排水无外部兜底，吞异常保帧，但必须留痕（可观测性铁律）
                StaticMonitor?.Log($"[HostRequestHandlers] main-thread action failed: {ex}", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     构造函数。注入主机端 LLM 服务器连接、广播器、送礼传输层与命令执行器。
    ///     commandExecutor 为 C3 修复新增：主机侧执行 response.Actions 所需。实体在主机权威，farmhand 不执行 actions。
    ///     speak/emote 等视觉动作通过 CommandExecutor 已有的 BroadcastNpcAction 自然广播到客机。
    ///     允许 null 以兼容旧调用方（无 actions 执行能力时仅回退到序列化 actionsJson）。
    ///     agentService 为 2026-08-16 联机新增：记录 LastDialoguePlayerId（FOLLOW 目标解析）。
    /// </summary>
    public HostRequestHandlers(IMonitor monitor, IAgentServerProvider serverProvider, AgentSyncBroadcaster broadcaster,
        IGiftTransport giftTransport, CommandExecutor? commandExecutor = null, AgentService? agentService = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _serverProvider = serverProvider ?? throw new ArgumentNullException(nameof(serverProvider));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _giftTransport = giftTransport ?? throw new ArgumentNullException(nameof(giftTransport));
        _commandExecutor = commandExecutor;
        _agentService = agentService;
        StaticMonitor = _monitor;
    }

    /// <summary>
    ///     处理 Farmhand 发来的对话请求。
    ///     反序列化 worldSnapshot → 调用主机 LLM 服务器 → 在主机侧执行 response.Actions → 通过 broadcaster 回包给 Farmhand。
    ///     async void 模式：调用方 MultiplayerEventRouter 不 await，需在最外层 try/catch 兜底。
    /// </summary>
    public async void HandleDialogueRequest(DialogueRequestMessage msg)
    {
        _monitor.Log(
            $"[HostRequestHandlers] HandleDialogueRequest started for {msg.NpcName} from player {msg.PlayerId}",
            LogLevel.Debug);
        try
        {
            // 1. 反序列化 worldSnapshot；失败时主机本地重建
            WorldSnapshot? worldSnapshot = null;
            if (!string.IsNullOrEmpty(msg.WorldSnapshotJson))
            {
                try
                {
                    worldSnapshot = JsonSerializer.Deserialize<WorldSnapshot>(msg.WorldSnapshotJson);
                }
                catch (Exception ex)
                {
                    _monitor.Log(
                        $"[HostRequestHandlers] Failed to deserialize WorldSnapshot for {msg.NpcName}, will rebuild on host: {ex}",
                        LogLevel.Warn);
                }
            }

            if (worldSnapshot == null)
            {
                worldSnapshot = WorldSnapshotBuilder.Build(msg.NpcName);
            }

            // 2. 构造 DialogueRequest 调用主机 TS 服务器
            var request = new DialogueRequest(
                "dialogue",
                Guid.NewGuid().ToString("N"),
                msg.NpcName,
                msg.PlayerMessage,
                worldSnapshot,
                // 2026-08-16 联机：发起玩家 ID 贯穿到 TS（账本校验/执行按此玩家解析）。
                msg.PlayerId.ToString()
            );

            var response = await _serverProvider.GenerateDialogueAsync(request).ConfigureAwait(false);

            // 2026-08-23 审计 P0：await 续体跑在 ThreadPool 线程，而 ApplyDialogueResponse 全程
            // 写 NetField（friendshipData）/NPC 状态机——与 execute_adjust 同类的主线程纪律问题。
            // 整体投递回主线程执行；异常兜底随迁，不再依赖外层 catch。
            EnqueueMainThread(() =>
            {
                try
                {
                    // B5.5 补遗（B3 已知盲区）：房客中继对话在主机侧补记对话计数。
                    // 本地路径 SubmitInput 在发送前主线程计数，中继路径此前无人计数 →
                    // ApplyDialogueResponse 刷出的 ConversationFrequency 对纯中继恒为 0，
                    // 空闲淘汰的优先级比较失真。放在主线程队列块内、读数（UpdatePriority）之前，
                    // 与本地路径的计数时机一致（计数器本身是 ConcurrentDictionary，线程安全）。
                    NPCDialoguePatch.IncrementConversationCount(msg.NpcName);

                    ApplyDialogueResponse(msg, response);
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[HostRequestHandlers] HandleDialogueRequest failed for {msg.NpcName}: {ex}",
                        LogLevel.Error);
                    SafeSendFallback(msg.NpcName, msg.PlayerId, "（主机处理失败）", msg.RequestId);
                }
            });
        }
        catch (Exception ex)
        {
            // pre-await 阶段（快照反序列化/请求构造）失败仍在调用线程，直接兜底回包
            _monitor.Log($"[HostRequestHandlers] HandleDialogueRequest failed for {msg.NpcName}: {ex}", LogLevel.Error);
            SafeSendFallback(msg.NpcName, msg.PlayerId, "（主机处理失败）", msg.RequestId);
        }
    }

    /// <summary>
    ///     主线程应用对话响应：好感度 → 动作分发（身体类先 promote）→ Brain 记录发起玩家
    ///     → 优先级指标刷新 → 回包 Farmhand。
    ///     只能经 MainThreadActions 调用（写 friendshipData NetInt 与 NPC 状态机）。
    /// </summary>
    private void ApplyDialogueResponse(DialogueRequestMessage msg, DialogueResponse response)
    {
        // 房客对话好感度（2026-08-16 联机：此前被整个丢弃）。主机权威应用：
        // delta 加到发起玩家（Game1.GetPlayer 覆盖 MasterPlayer/在线/离线 farmhand）的 friendshipData。
        // 2026-08-23 审计 P1：原 `is > 0` 把负 delta 静默丢弃——LLM 表达不满时房客好感不降，
        // 与主机本地路径语义不对称；改 Clamp 到 [0,2500]，正负一致生效。
        if (response.FriendshipDelta is { } delta && delta != 0
            && Game1.GetPlayer(msg.PlayerId) is { } targetPlayer)
        {
            if (!targetPlayer.friendshipData.TryGetValue(msg.NpcName, out var fd))
            {
                fd = new StardewValley.Friendship { Points = 0 };
                targetPlayer.friendshipData[msg.NpcName] = fd;
            }

            fd.Points = Math.Clamp(fd.Points + delta, 0, 2500);
            _monitor.Log(
                $"[HostRequestHandlers] {msg.NpcName} {(delta > 0 ? "+" : "")}{delta} friendship → player {msg.PlayerId} ({fd.Points})",
                LogLevel.Info);
        }

        // C3 修复：在主机侧执行 response.Actions（实体在主机权威）。
        // farmhand 不执行 actions（_commandExecutor 为 null），仅通过 broadcaster 接收视觉广播。
        // speak/emote 等视觉动作通过 CommandExecutor 内部已有的 BroadcastNpcAction 自然广播到客机。
        // 这避免了 farmhand 玩家"说挖矿但 NPC 不动"的"说到做不到"问题。
        // 房客聊到的村民可能没有身体——回复里的身体类动作先按本地 DispatchDialogueActions
        // 同款 promote 语义建身体（设计 §3.2.4）；失败只跳过该动作，不中断整个回包。
        if (_commandExecutor != null && response.Actions != null && response.Actions.Count > 0)
        {
            // Game1 访问在主线程队列里执行（本方法只经 EnqueueMainThread 调用）。
            // getCharacterFromName 的 try/catch 与 CommandExecutor.ExecuteEmote 同模式：
            // 游戏状态未就绪时抛 NRE，此时取不到 NPC 就不建身体（防幽灵身体占名额）。
            NPC? relayNpc;
            try
            {
                relayNpc = Game1.getCharacterFromName(msg.NpcName);
            }
            catch (NullReferenceException ex)
            {
                _monitor.Log(
                    $"[HostRequestHandlers] game state not initialized for NPC '{msg.NpcName}': {ex}",
                    LogLevel.Warn);
                relayNpc = null;
            }

            foreach (var action in response.Actions)
            {
                try
                {
                    if (relayNpc != null
                        && _agentService != null
                        && !_agentService.HasAgent(msg.NpcName)
                        && DialogueBoxInputPatch.IsPromotionTriggerTool(action.Tool ?? ""))
                    {
                        if (!DialogueBoxInputPatch.PromoteToAgent(msg.NpcName))
                        {
                            _monitor.Log(
                                $"[HostRequestHandlers] {msg.NpcName}: action '{action.Tool}' skipped — promotion to Agent failed",
                                LogLevel.Warn);
                            continue;
                        }
                    }

                    _commandExecutor.ExecuteAction(action, msg.NpcName);
                }
                catch (Exception ex)
                {
                    _monitor.Log(
                        $"[HostRequestHandlers] Action '{action.Tool}' failed for {msg.NpcName}: {ex}",
                        LogLevel.Warn);
                }
            }
        }

        // 记录对话发起玩家到 NPC Brain（FOLLOW 等行为的目标玩家解析，AgentNavigator 消费）。
        // 必须在动作分发（含 promote 建身体）之后：本轮回复刚建的身体才能记到 FOLLOW 目标。
        if (_agentService != null && _agentService.TryGetBrain(msg.NpcName, out var brain) && brain?.Brain != null)
        {
            brain.Brain.LastDialoguePlayerId = msg.PlayerId.ToString();
        }

        // 优先级指标刷新（设计 §3.2.4 约束③）：正在被房客对话的身体持续刷新活跃度指标，
        // 保证空闲淘汰（ReevaluateAllocations）不会裁掉活跃对话者。
        // hearts 取发起玩家的 friendshipData——房客对话的好感基线在发起方，不能用主机本地玩家。
        if (_agentService != null && _agentService.HasAgent(msg.NpcName))
        {
            var requester = Game1.GetPlayer(msg.PlayerId);
            double hearts = 0;
            if (requester?.friendshipData != null
                && requester.friendshipData.TryGetValue(msg.NpcName, out var priorityFriendship))
            {
                hearts = priorityFriendship.Points / 250.0;
            }

            _ = _agentService.AllocationManager.UpdatePriority(
                msg.NpcName, NPCDialoguePatch.GetConversationCount(msg.NpcName), 0, hearts);
        }

        // 序列化 actions（保留传递给 farmhand 用于参考/日志，farmhand 端不会重复执行）
        string? actionsJson = null;
        if (response.Actions != null && response.Actions.Count > 0)
        {
            try
            {
                actionsJson = JsonSerializer.Serialize(response.Actions);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[HostRequestHandlers] Failed to serialize actions for {msg.NpcName}: {ex}",
                    LogLevel.Warn);
            }
        }

        _monitor.Log(
            $"[HostRequestHandlers] Sending dialogue response to player {msg.PlayerId} for {msg.NpcName}: {response.Speech ?? "(no speech)"}",
            LogLevel.Debug);
        _broadcaster.SendDialogueResponse(
            msg.NpcName,
            response.Speech ?? "",
            response.Emotion ?? "",
            null,
            msg.PlayerId,
            actionsJson,
            response.Fallback == true,
            // 2026-09-09：回填房客请求 requestId，房客端精确配对（旧版房客忽略此字段，行为不变）
            msg.RequestId,
            // 2026-09-13 R2：透传 TS 降级原因（busy/llm_error/billing/unavailable），房客端诊断留痕
            response.FallbackReason);
        _monitor.Log($"[HostRequestHandlers] HandleDialogueRequest completed for {msg.NpcName}", LogLevel.Debug);
    }

    /// <summary>给发起方 Farmhand 回兜底文本，失败只留痕不抛（async void 兜底路径）。
    ///     requestId 原样回填（无上下文时 null，房客退回 FIFO 匹配）。</summary>
    private void SafeSendFallback(string npcName, long playerId, string text, string? requestId = null)
    {
        try
        {
            _monitor.Log($"[HostRequestHandlers] Sending fallback response to player {playerId} for {npcName}",
                LogLevel.Debug);
            _broadcaster.SendDialogueResponse(npcName, text, "", null, playerId, requestId: requestId);
        }
        catch (Exception fallbackEx)
        {
            _monitor.Log($"[HostRequestHandlers] Fallback response also failed for {npcName}: {fallbackEx}",
                LogLevel.Error);
        }
    }

    /// <summary>
    ///     处理 Farmhand 发来的礼物请求。
    ///     调用 IGiftTransport（HostGiftTransport）评估礼物 → 通过 broadcaster.SendGiftResponse 回包给 Farmhand。
    ///     async void 模式：调用方 MultiplayerEventRouter 不 await，需在最外层 try/catch 兜底。
    /// </summary>
    public async void HandleGiftRequest(GiftRequestMessage msg)
    {
        try
        {
            // 2026-08-16 联机：求购单检查补位（主机权威，此前房客送礼绕过检查）。
            // 命中 NPC 当日求购单 → 拒绝交接并提示走对话议价（对齐单机 NPCGiftPatch 行为；
            // 物品已在 farmhand 本地消耗——完整修复需 farmhand 端同步求购单，记入已知限制）。
            var offer = NPCGiftPatch.PurchaseOffers?.Snapshot.TryGetValue(msg.NpcName, out var existing) == true
                ? existing
                : null;
            if (offer != null && !offer.IsExpired(DateTime.UtcNow)
                && offer.ItemId.Equals(msg.ItemId, StringComparison.OrdinalIgnoreCase))
            {
                _monitor.Log(
                    $"[HostRequestHandlers] {msg.NpcName}: farmhand offered {msg.ItemId} — purchase request hit, gift hand-over refused (settle via dialogue)",
                    LogLevel.Info);
                _broadcaster.SendGiftResponse(
                    msg.NpcName,
                    $"{msg.NpcName} 想收购 {offer.ItemName}（出价 {offer.AgreedPrice}g）——跟她聊聊价格吧",
                    "",
                    0,
                    msg.PlayerId,
                    msg.RequestId);
                return;
            }

            // 2026-08-23 审计 P1：把发起玩家 ID 传给评估层——好感基线必须取送礼玩家本人
            // （此前固定读主机 friendshipData，递减收益按错误亲密度计算）。
            // HostGiftTransport 当前无内部 await（同步完成）；仍统一经主线程队列回包，
            // 防止评估层将来变真异步时静默退回后台线程发 ModMessage。
            var response = await _giftTransport
                .SendAsync(msg.NpcName, msg.ItemId, msg.Quantity, msg.PlayerId).ConfigureAwait(false);

            EnqueueMainThread(() =>
                _broadcaster.SendGiftResponse(
                    msg.NpcName,
                    response.Reaction ?? "",
                    response.Emotion ?? "",
                    response.FriendshipDelta,
                    msg.PlayerId,
                    msg.RequestId));
        }
        catch (Exception ex)
        {
            _monitor.Log($"[HostRequestHandlers] HandleGiftRequest failed for {msg.NpcName}: {ex}", LogLevel.Error);
            try
            {
                _broadcaster.SendGiftResponse(
                    msg.NpcName,
                    "（主机处理礼物失败）",
                    "",
                    0,
                    msg.PlayerId,
                    msg.RequestId);
            }
            catch (Exception fallbackEx)
            {
                _monitor.Log(
                    $"[HostRequestHandlers] Fallback gift response also failed for {msg.NpcName}: {fallbackEx}",
                    LogLevel.Error);
            }
        }
    }

    /// <summary>处理 Farmhand 发来的交互请求（右键点击 NPC）。占位实现，后续 Task 填充。</summary>
    public void HandleInteractionRequest(InteractionRequestMessage msg) =>
        _monitor.Log($"[HostRequestHandlers] HandleInteractionRequest placeholder for {msg.NpcName}");
}