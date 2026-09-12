using System.Collections.Generic;
using ValleyAgent.StateMachine;

namespace ValleyAgent.AI;

/// <summary>
///     Categories of events that can trigger an LLM decision evaluation.
///     当前实现已弃用：实际决策通过 EventHandlerInitializer._pendingDecisions 队列处理，
///     但 DecisionTriggerType 仍由 DecisionContextBuilder 用作决策触发元数据。
/// </summary>
public enum DecisionTriggerType
{
    /// <summary>
    ///     Periodic timer-based decision (default: every 30s / 1800 ticks).
    /// </summary>
    Timer = 0,

    /// <summary>
    ///     The player closed the dialogue box after a conversation with this NPC.
    /// </summary>
    DialogueEnd = 1,

    /// <summary>
    ///     A handler task completed (e.g., all crops harvested).
    /// </summary>
    TaskComplete = 2,

    /// <summary>
    ///     Combat ended — no more hostiles in detection range.
    /// </summary>
    FightEnd = 3,

    /// <summary>
    ///     The player gave a gift to this NPC.
    /// </summary>
    GiftReceived = 4,

    /// <summary>
    ///     High-priority emergency (e.g., hostile monster entered the agent's range).
    /// </summary>
    Emergency = 5
}

/// <summary>
///     Snapshot of the agent's current environmental and relationship context,
///     used by rule-based decision engine and debug commands.
///     Populated by <see cref="DecisionContextBuilder" /> and consumed by
///     <see cref="RuleBasedDecisionEngine" />.
/// </summary>
public class DecisionContext
{
    public string NpcName { get; set; } = string.Empty;
    public string NpcPersonality { get; set; } = string.Empty;
    public string NpcBiography { get; set; } = string.Empty;
    public string NpcTraits { get; set; } = string.Empty;
    public string NpcRelationships { get; set; } = string.Empty;
    public string FarmerNickname { get; set; } = string.Empty;
    public string CurrentLocation { get; set; } = string.Empty;
    public string CurrentSeason { get; set; } = string.Empty;
    public int CurrentDay { get; set; }
    public int CurrentTime { get; set; }
    public AgentState CurrentState { get; set; }
    public Dictionary<string, object> RelationshipMemory { get; set; } = new();
    public Dictionary<string, object> GameState { get; set; } = new();

    public string Weather { get; set; } = string.Empty;
    public string Festival { get; set; } = string.Empty;
    public int FriendshipPoints { get; set; }
    public int FriendshipHearts { get; set; }
    public string FriendshipStatus { get; set; } = string.Empty;
    public string NearbySummary { get; set; } = string.Empty;
    public string FormattedTime { get; set; } = string.Empty;
    public string BlockedStates { get; set; } = string.Empty;
}