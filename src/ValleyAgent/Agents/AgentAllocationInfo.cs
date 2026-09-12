using System;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Agents;

/// <summary>
///     Stores allocation information for a single NPC Agent.
/// </summary>
public class AgentAllocationInfo
{
    public AgentAllocationInfo(string npcName, double conversationFrequency, double giftFrequency,
        double friendshipLevel, double priorityScore)
    {
        NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        ConversationFrequency = conversationFrequency;
        GiftFrequency = giftFrequency;
        FriendshipLevel = friendshipLevel;
        PriorityScore = priorityScore;
        CurrentState = AgentState.IDLE;
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    ///     The name of the NPC.
    /// </summary>
    public string NpcName { get; }

    /// <summary>
    ///     How often the player talks to this NPC.
    /// </summary>
    public double ConversationFrequency { get; set; }

    /// <summary>
    ///     How often the player gifts this NPC.
    /// </summary>
    public double GiftFrequency { get; set; }

    /// <summary>
    ///     Current friendship level with this NPC.
    /// </summary>
    public double FriendshipLevel { get; set; }

    /// <summary>
    ///     Computed priority score (higher = more likely to retain allocation).
    /// </summary>
    public double PriorityScore { get; set; }

    /// <summary>
    ///     Whether this allocation is a manual player override.
    /// </summary>
    public bool IsManuallyOverridden { get; set; }

    /// <summary>
    ///     The current AgentState of this NPC.
    /// </summary>
    public AgentState CurrentState { get; set; }

    /// <summary>
    ///     When this allocation was last created or updated.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    ///     最近一次玩家与该 NPC 互动的时间（对话/送礼/聊天栏路由时刷新）。
    ///     用于互动空闲淘汰判定。默认 default(DateTime) 表示从未互动。
    /// </summary>
    public DateTime LastPlayerInteractionTick { get; set; }

    /// <summary>
    ///     导演 allocate_agent 设置的豁免截止时间（可空）。
    ///     期间豁免互动空闲淘汰，对应 beat.windowEnd。
    /// </summary>
    public DateTime? KeepUntil { get; set; }

    /// <summary>
    ///     判定是否应因互动空闲被淘汰。
    ///     条件：距上次玩家互动超过阈值，且无 KeepUntil 豁免或 KeepUntil 已过期。
    /// </summary>
    /// <param name="now">当前 UTC 时间。</param>
    /// <param name="idleThresholdSeconds">互动空闲阈值秒数。</param>
    /// <returns>true 表示应淘汰。</returns>
    public bool ShouldEvict(DateTime now, int idleThresholdSeconds)
    {
        var idleDuration = now - LastPlayerInteractionTick;
        if (idleDuration.TotalSeconds <= idleThresholdSeconds)
        {
            return false;
        }

        if (KeepUntil.HasValue && now < KeepUntil.Value)
        {
            return false;
        }

        return true;
    }
}