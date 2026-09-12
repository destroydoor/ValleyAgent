using System;
using System.Collections.Generic;
using ValleyAgent.Brain;
using ValleyAgent.Config;

namespace ValleyAgent.Save;

[Obsolete("Use SaveData instead.")]
public class AgentSaveData
{
    public List<AgentData> Agents { get; set; } = new();
}

[Obsolete("Use SaveData.Models.AgentStateData instead.")]
public class AgentData
{
    public string NpcName { get; set; } = string.Empty;
    public string CurrentState { get; set; } = "IDLE";
    public double PriorityScore { get; set; }
    public bool IsManuallyOverridden { get; set; }
    public int Health { get; set; } = -1;
    public List<string> Inventory { get; set; } = new();

    /// <summary>
    ///     E3-1 钱包余额。-1 为"未设置"哨兵：旧档反序列化缺此字段时保持 -1，
    ///     读档时不覆盖 NpcEconomyProfile 初始资金（见 docs/ideas/e31-implementation-思路.md §3）。
    /// </summary>
    public int Money { get; set; } = -1;

    public string Emotion { get; set; } = "Neutral";
    public string FarmerNickname { get; set; } = GameConstants.DefaultFarmerNickname;
    public List<string> ShortTermMemories { get; set; } = new();
    public List<MemoryEntryData> StructuredMemories { get; set; } = new();
    public List<MemoryEntryData> LongTermMemories { get; set; } = new();
    public List<string> EmotionHistory { get; set; } = new();
    public string LastDecisionState { get; set; } = string.Empty;
    public string LastDecisionReason { get; set; } = string.Empty;
}