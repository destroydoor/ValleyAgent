using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.AI;
using ValleyAgent.Services;

namespace ValleyAgent.Multiplayer;

/// <summary>
///     主机端 Agent 状态广播器。
///     定期将所有 Agent 的状态通过 SMAPI Mod 消息广播给 Farmhand 客户端。
///     Farmhand 端通过 AgentRemoteRenderer 接收并渲染。
/// </summary>
public class AgentSyncBroadcaster
{
    /// <summary>广播间隔（ticks）。60 ticks ≈ 1 秒。</summary>
    private const int BroadcastIntervalTicks = 60;

    private readonly AgentService _agentService;
    private readonly IModHelper _helper;
    private readonly string _modId;
    private readonly IMonitor _monitor;

    private int _lastBroadcastTick;

    public AgentSyncBroadcaster(AgentService agentService, IMonitor monitor, IModHelper helper, string modId)
    {
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
    }

    /// <summary>
    ///     每 tick 调用。判断是否需要广播，如果需要则构建消息并发送。
    ///     只在主机端且有 Farmhand 连接时才广播。
    /// </summary>
    public void Update(int currentTick)
    {
        if (!MultiplayerHelper.ShouldRunAgentLogic)
        {
            return;
        }

        // 程序化 hosting 从单机存档启动时 Context.IsMultiplayer 可能为 false，
        // 但只要有 farmhand 连接就需要广播。因此同时检测连接玩家数。
        var hasConnectedPlayers = _helper.Multiplayer.GetConnectedPlayers().Any();
        if (!MultiplayerHelper.IsMultiplayer && !hasConnectedPlayers)
        {
            return;
        }

        if (currentTick - _lastBroadcastTick < BroadcastIntervalTicks)
        {
            return;
        }

        _lastBroadcastTick = currentTick;
        BroadcastAgentStates();
    }

    /// <summary>
    ///     广播所有 Agent 的状态给 Farmhand。
    /// </summary>
    private void BroadcastAgentStates()
    {
        try
        {
            var agents = _agentService.GetAllAgents();
            if (agents.Count == 0)
            {
                return;
            }

            var message = new AgentStateMessage
            {
                Agents = agents.Select(a => BuildSnapshot(a)).ToList()
            };

            _helper.Multiplayer.SendMessage(message, MessageTypes.AgentState, new[] { _modId });
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[Multiplayer] Failed to broadcast agent states: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     Farmhand 加入时发送完整状态同步。
    /// </summary>
    public void SendFullSync(long targetPlayerId)
    {
        if (!MultiplayerHelper.ShouldRunAgentLogic)
        {
            return;
        }

        try
        {
            var agents = _agentService.GetAllAgents();
            var message = new FullSyncMessage
            {
                Agents = agents.Select(a => BuildFullState(a)).ToList(),
                HostPlayerId = Game1.player?.UniqueMultiplayerID ?? 0
            };

            _helper.Multiplayer.SendMessage(message, MessageTypes.FullSync,
                new[] { _modId }, new[] { targetPlayerId });

            _monitor.Log($"[Multiplayer] Sent full sync to player {targetPlayerId} ({agents.Count} agents)",
                LogLevel.Debug);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[Multiplayer] Failed to send full sync: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     发送对话响应给 Farmhand。
    ///     actionsJson 为可选的 ToolAction 列表 JSON 序列化结果（spec 4.2 多动作）。向后兼容：默认 null。
    ///     fallback 为 TS 规则引擎降级标记（BUSY 等），farmhand 端据此走灰色系统提示（2026-08-23 审计补齐）。
    ///     requestId 为房客请求关联 ID 的回填（2026-09-09）：farmhand 端据此精确配对 pending；
    ///     null（旧调用点/兜底无上下文时）farmhand 退回 FIFO 匹配。
    ///     fallbackReason 为降级原因（busy/llm_error/billing/unavailable，2026-09-13 R2），透传自 TS
    ///     DialogueResponse.FallbackReason；null = 旧客户端/本地兜底无降级上下文。
    /// </summary>
    public void SendDialogueResponse(string npcName, string text, string emotion, string? action, long targetPlayerId,
        string? actionsJson = null, bool fallback = false, string? requestId = null, string? fallbackReason = null)
    {
        if (!MultiplayerHelper.ShouldRunAgentLogic || !MultiplayerHelper.IsMultiplayer)
        {
            return;
        }

        try
        {
            var message = new DialogueResponseMessage
            {
                NpcName = npcName,
                Text = text,
                Emotion = emotion,
                Action = action,
                ActionsJson = actionsJson,
                TargetPlayerId = targetPlayerId,
                Fallback = fallback,
                RequestId = requestId,
                FallbackReason = fallbackReason
            };

            _helper.Multiplayer.SendMessage(message, MessageTypes.DialogueResponse,
                new[] { _modId }, new[] { targetPlayerId });
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[Multiplayer] Failed to send dialogue response: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     发送礼物评估结果给 Farmhand。
    ///     requestId 为房客请求关联 ID 的回填（2026-09-09），语义同 SendDialogueResponse。
    /// </summary>
    public void SendGiftResponse(string npcName, string responseText, string emotion, int friendshipChange,
        long targetPlayerId, string? requestId = null)
    {
        if (!MultiplayerHelper.ShouldRunAgentLogic || !MultiplayerHelper.IsMultiplayer)
        {
            return;
        }

        try
        {
            var message = new GiftResponseMessage
            {
                NpcName = npcName,
                ResponseText = responseText,
                Emotion = emotion,
                FriendshipChange = friendshipChange,
                TargetPlayerId = targetPlayerId,
                RequestId = requestId
            };

            _helper.Multiplayer.SendMessage(message, MessageTypes.GiftResponse,
                new[] { _modId }, new[] { targetPlayerId });
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[Multiplayer] Failed to send gift response: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     立即广播指定 NPC 的当前状态快照（绕过 60 tick 节流）。
    ///     用于状态转换、血量变化等重要事件的即时同步。
    /// </summary>
    /// <param name="npcName">NPC 名称。</param>
    public void BroadcastImmediateState(string npcName)
    {
        if (!MultiplayerHelper.ShouldRunAgentLogic || !MultiplayerHelper.IsMultiplayer)
        {
            return;
        }

        try
        {
            if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
            {
                _monitor.Log($"[Multiplayer] BroadcastImmediateState: agent '{npcName}' not found");
                return;
            }

            var snapshot = BuildSnapshot(agent);
            var message = new AgentStateMessage
            {
                Agents = new List<AgentStateSnapshot> { snapshot }
            };

            _helper.Multiplayer.SendMessage(message, MessageTypes.AgentState, new[] { _modId });
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[Multiplayer] Failed to broadcast immediate state for '{npcName}': {ex}",
                LogLevel.Error);
        }
    }

    /// <summary>
    ///     广播 NPC 动作（emote/说话）给所有 Farmhand。
    /// </summary>
    public void BroadcastNpcAction(string npcName, string actionType, int emoteId = 0, string text = "",
        int durationMs = 0)
    {
        if (!MultiplayerHelper.ShouldRunAgentLogic || !MultiplayerHelper.IsMultiplayer)
        {
            return;
        }

        try
        {
            var message = new NpcActionMessage
            {
                NpcName = npcName,
                ActionType = actionType,
                EmoteId = emoteId,
                Text = text,
                DurationMs = durationMs
            };

            _helper.Multiplayer.SendMessage(message, MessageTypes.NpcAction, new[] { _modId });
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[Multiplayer] Failed to broadcast NPC action: {ex}", LogLevel.Warn);
        }
    }

    private static AgentStateSnapshot BuildSnapshot(AgentInstance agent)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        return new AgentStateSnapshot
        {
            NpcName = agent.NpcName,
            State = agent.StateMachine?.CurrentStateFlag.ToString() ?? "IDLE",
            Health = agent.Health?.Health ?? RuleDecisionContext.DefaultHealth,
            MaxHealth = agent.Health?.MaxHealth ?? RuleDecisionContext.DefaultHealth,
            Emotion = agent.Brain?.Emotion.ToString() ?? "Neutral",
            Location = npc?.currentLocation?.NameOrUniqueName ?? "",
            PosX = npc?.Position.X ?? 0,
            PosY = npc?.Position.Y ?? 0,
            IsDead = agent.Health?.IsDead ?? false,
            FacingDirection = npc?.FacingDirection ?? 0,
            IsMoving = npc?.isMoving() ?? false
        };
    }

    private static AgentFullState BuildFullState(AgentInstance agent)
    {
        var snapshot = BuildSnapshot(agent);
        var npc = Game1.getCharacterFromName(agent.NpcName);

        var friendship = 0;
        if (npc != null)
        {
            foreach (var farmer in Game1.getAllFarmers())
            {
                if (farmer.friendshipData.TryGetValue(agent.NpcName, out var fd) && fd.Points > friendship)
                {
                    friendship = fd.Points;
                }
            }
        }

        return new AgentFullState
        {
            NpcName = snapshot.NpcName,
            State = snapshot.State,
            Health = snapshot.Health,
            MaxHealth = snapshot.MaxHealth,
            Emotion = snapshot.Emotion,
            Location = snapshot.Location,
            PosX = snapshot.PosX,
            PosY = snapshot.PosY,
            IsDead = snapshot.IsDead,
            Friendship = friendship,
            RecentMemory = agent.Brain?.GetRecentMemories(5).Select(m => m.Text).ToList() ?? new List<string>(),
            FarmerNickname = agent.Brain?.FarmerNickname ?? "新来的农夫"
        };
    }
}