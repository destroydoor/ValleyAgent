using System;
using System.Collections.Generic;

namespace ValleyAgent.Agents;

/// <summary>
///     玩家进入 NPC 附近时低概率激活该 NPC 为 Agent。
///     每日每 NPC 只掷一次（dateKey 隔离），换日自动隔离旧记录。
///     概率根据当前 Agent 数量分档（设计文档 §4.1）：
///     current &lt; Min → 双倍概率
///     Min ≤ current &lt; Normal → 基础概率
///     current ≥ Normal → 不触发
/// </summary>
public class SparkAllocator
{
    private readonly Dictionary<string, HashSet<string>> _rolledByDate
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     尝试 spark 激活。命中时调用方应 ForceAllocate。
    /// </summary>
    /// <param name="npcName">NPC 名。</param>
    /// <param name="dateKey">游戏日期 key（如 "Y1_spring_1"），换日变化。</param>
    /// <param name="currentAgentCount">当前 Agent 数量。</param>
    /// <param name="minAgents">MinAgentNpcs。</param>
    /// <param name="normalAgents">NormalAgentNpcs。</param>
    /// <param name="maxAgents">MaxAgentNpcs（当前未用，保留签名一致性）。</param>
    /// <param name="probabilityOverride">基础概率覆盖（测试用，默认 null 走 5%）。</param>
    /// <returns>true 表示命中 spark（调用方应 ForceAllocate）。</returns>
    public bool TrySpark(
        string npcName,
        string dateKey,
        int currentAgentCount,
        int minAgents,
        int normalAgents,
        int maxAgents,
        double? probabilityOverride = null)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(dateKey))
        {
            return false;
        }

        // current >= Normal → 停止 spark 主动激活
        if (currentAgentCount >= normalAgents)
        {
            return false;
        }

        // 每日每 NPC 只掷一次
        if (!_rolledByDate.TryGetValue(dateKey, out var rolledSet))
        {
            rolledSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _rolledByDate[dateKey] = rolledSet;
        }

        if (rolledSet.Contains(npcName))
        {
            return false;
        }

        _ = rolledSet.Add(npcName);

        var prob = ComputeProbability(currentAgentCount, minAgents, normalAgents, probabilityOverride);

        // 确定性 FNV-1a 哈希代替 Random（避免 CA5394，与 ShoutReplyScheduler.StableDelayMs 同一先例）。
        // 同 NPC+日期结果稳定，便于测试断言。
        var roll = StableRoll(npcName, dateKey);
        return roll < prob;
    }

    /// <summary>
    ///     换日清理（可选——dateKey 变化自动隔离旧记录，但调用此方法可释放内存）。
    /// </summary>
    /// <param name="currentDateKey">当前日期 key，非此 key 的记录被清除。</param>
    public void ResetDaily(string currentDateKey)
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in _rolledByDate)
        {
            if (!string.Equals(kvp.Key, currentDateKey, StringComparison.OrdinalIgnoreCase))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _ = _rolledByDate.Remove(key);
        }
    }

    /// <summary>
    ///     概率分档计算（设计文档 §4.1）：
    ///     current ≥ Normal → 0（停止 spark）
    ///     current &lt; Min → baseProb × 2
    ///     Min ≤ current &lt; Normal → baseProb
    /// </summary>
    internal static double ComputeProbability(int currentAgentCount, int minAgents, int normalAgents,
        double? probabilityOverride)
    {
        if (currentAgentCount >= normalAgents)
        {
            return 0.0;
        }

        var baseProb = probabilityOverride ?? 0.05;
        return currentAgentCount < minAgents ? baseProb * 2.0 : baseProb;
    }

    /// <summary>
    ///     FNV-1a 哈希 → [0, 1) 双精度。同 NPC+日期结果稳定，便于测试断言。
    ///     避免使用 Random 触发 CA5394（与 ShoutReplyScheduler.StableDelayMs 同一先例）。
    /// </summary>
    internal static double StableRoll(string npcName, string dateKey)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in npcName + "\u0001" + dateKey)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash / (double)uint.MaxValue;
    }
}