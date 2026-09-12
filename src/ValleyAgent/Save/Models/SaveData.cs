using System;
using System.Collections.Generic;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Save.Models;
// ============================================================
// Save Data Models - Game-agnostic, no SMAPI dependencies
// Version: 2.0.0
// ============================================================

/// <summary>
///     Root save data container for all persistent Agent state.
///     Game-agnostic: no SMAPI or Stardew Valley types.
/// </summary>
public class SaveData
{
    /// <summary>
    ///     Save data format version (e.g., "2.0.0").
    ///     Used for migration when deserializing older saves.
    /// </summary>
    public string Version { get; set; } = "2.0.0";

    /// <summary>
    ///     Integer schema version for the 3-tier memory system (T21).
    ///     Defaults to 1 for the current schema. Old saves without this field
    ///     are treated as version 0 and migrated to 1 on load.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    ///     Per-NPC agent state snapshots.
    ///     Key: NPC name.
    /// </summary>
    public Dictionary<string, AgentStateData> AgentStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Per-NPC memory snapshots (dialogue history, events).
    ///     Key: NPC name.
    /// </summary>
    public Dictionary<string, MemoryData> Memories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Per-NPC friendship change history.
    ///     Key: NPC name.
    /// </summary>
    public Dictionary<string, FriendshipHistoryData> FriendshipHistory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Agent allocation data (which NPCs are currently AI agents).
    /// </summary>
    public AllocationData Allocations { get; set; } = new();

    /// <summary>
    ///     Session-level statistics (LLM calls, tokens, decisions, etc.).
    /// </summary>
    public StatisticsData Statistics { get; set; } = new();
}

/// <summary>
///     Serializable snapshot of an agent's current state.
/// </summary>
public class AgentStateData
{
    /// <summary>The name of the NPC.</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>The current agent state enum value.</summary>
    public AgentState CurrentState { get; set; } = AgentState.IDLE;

    /// <summary>The current in-game location name (e.g., "Pelican Town").</summary>
    public string CurrentLocation { get; set; } = string.Empty;

    /// <summary>X position in the game world.</summary>
    public float PositionX { get; set; }

    /// <summary>Y position in the game world.</summary>
    public float PositionY { get; set; }

    /// <summary>Current target/action description, if any.</summary>
    public string CurrentTarget { get; set; } = "";

    public long StateStartTime { get; set; }

    /// <summary>Current health points (0-100). -1 means not set.</summary>
    public int Health { get; set; } = -1;

    /// <summary>Serialized inventory items: "ItemId:Stack" per slot.</summary>
    public List<string> Inventory { get; set; } = new();

    /// <summary>
    ///     E3-1 钱包余额。-1 为"未设置"哨兵：旧档/旧版本反序列化缺此字段时保持 -1，
    ///     读档时不覆盖 NpcEconomyProfile 初始资金（见 docs/ideas/e31-implementation-思路.md §3）。
    /// </summary>
    public int Money { get; set; } = -1;

    /// <summary>玩家昵称，NPC对玩家的称呼（默认"新来的农夫"，与 GameConstants.DefaultFarmerNickname 保持同步）。</summary>
    public string FarmerNickname { get; set; } = "新来的农夫";

    /// <summary>当前情绪枚举值。</summary>
    public string Emotion { get; set; } = "Neutral";

    /// <summary>当前目标描述。</summary>
    public string CurrentGoal { get; set; } = string.Empty;

    public EmotionSaveData EmotionData { get; set; } = new();
}

/// <summary>
///     Serializable snapshot of an NPC's memory.
///     Dialogue history is capped at 20 exchanges.
/// </summary>
public class MemoryData
{
    /// <summary>The name of the NPC.</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>Recent dialogue exchanges (max 20, oldest truncated).</summary>
    public List<DialogueExchangeData> DialogueHistory { get; set; } = new();

    /// <summary>Significant events remembered by this NPC.</summary>
    public List<EventRecordData> EventHistory { get; set; } = new();

    /// <summary>结构化短期记忆条目。</summary>
    public List<StructuredMemoryEntry> ShortTermMemories { get; set; } = new();

    public List<StructuredMemoryEntry> LongTermMemories { get; set; } = new();
}

/// <summary>
///     A recorded event in an NPC's memory.
/// </summary>
public class EventRecordData
{
    /// <summary>Type/category of the event.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Human-readable description of the event.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When the event occurred (real world time).</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Optional: in-game date key when the event occurred.</summary>
    public string DateKey { get; set; } = string.Empty;
}

/// <summary>
///     Serializable snapshot of friendship history for a single NPC.
/// </summary>
public class FriendshipHistoryData
{
    /// <summary>The name of the NPC.</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>Chronological list of friendship changes.</summary>
    public List<FriendshipChangeData> Changes { get; set; } = new();

    /// <summary>Current friendship points (0-2500).</summary>
    public int CurrentPoints { get; set; }
}

/// <summary>
///     A single recorded friendship change.
/// </summary>
public class FriendshipChangeData
{
    /// <summary>When the change occurred (real world time).</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>In-game date key when the change occurred.</summary>
    public string DateKey { get; set; } = string.Empty;

    /// <summary>Type of interaction that caused the change.</summary>
    public string InteractionType { get; set; } = string.Empty;

    /// <summary>The change amount applied.</summary>
    public int ChangeAmount { get; set; }

    /// <summary>Friendship points before the change.</summary>
    public int PointsBefore { get; set; }

    /// <summary>Friendship points after the change.</summary>
    public int PointsAfter { get; set; }

    /// <summary>Reason for the change.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Special event modifiers that were active.</summary>
    public string SpecialEvents { get; set; } = string.Empty;

    /// <summary>Whether diminishing returns were applied.</summary>
    public bool DiminishingReturnsApplied { get; set; }

    /// <summary>LLM confidence level for this evaluation.</summary>
    public double Confidence { get; set; }
}

/// <summary>
///     Serializable snapshot of agent allocations.
/// </summary>
public class AllocationData
{
    /// <summary>List of NPC names currently allocated as Agents.</summary>
    public List<string> AgentNpcNames { get; set; } = new();

    /// <summary>
    ///     Manual override flags per NPC.
    ///     Key: NPC name, Value: true if manually force-allocated.
    /// </summary>
    public Dictionary<string, bool> ManualOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     Serializable session statistics.
/// </summary>
public class StatisticsData
{
    /// <summary>When the current session started (real world time).</summary>
    public DateTime SessionStartTime { get; set; } = DateTime.UtcNow;

    /// <summary>Total LLM API calls made this session.</summary>
    public int TotalLlmCalls { get; set; }

    /// <summary>Total tokens consumed across all LLM calls.</summary>
    public int TotalTokensUsed { get; set; }

    /// <summary>Total AI decisions made (state transitions, actions).</summary>
    public int TotalDecisions { get; set; }

    /// <summary>Average LLM response time in milliseconds.</summary>
    public double AverageLlmResponseTime { get; set; }
}

public class StructuredMemoryEntry
{
    public string Text { get; set; } = "";
    public double Importance { get; set; }
    public string EntryType { get; set; } = "";
    public long Timestamp { get; set; }
}

public class EmotionSaveData
{
    public string Emotion { get; set; } = "Neutral";
    public float Intensity { get; set; } = 1.0f;
    public string Source { get; set; } = "";
}

public class FriendshipChangeRecord
{
    public long Timestamp { get; set; }
    public int Delta { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
}