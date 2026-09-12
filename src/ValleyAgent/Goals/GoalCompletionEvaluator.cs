using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleyAgent.Goals;

/// <summary>
///     Goal 完成判定的纯逻辑核心（spec §1.1：物品收集量模型，非 GoalVerifier 环境差量模型）。
///
///     <para>不依赖任何游戏类型 / Game1 状态，可在无游戏上下文的单测中直接驱动：
///     注入背包物品数快照（itemId → 数量）或动作/击杀计数，判定终止条件是否达成。</para>
/// </summary>
public static class GoalCompletionEvaluator
{
    /// <summary>
    ///     背包差量：baseline（Start 时快照）vs current（当前快照）→ 各 itemId 的净获得量。
    ///     只累计增加量，丢弃不出现或减少的 itemId（负值钳 0，防止 NPC 消耗物品导致误判）。
    /// </summary>
    public static Dictionary<string, int> ComputeCollectedCounts(
        IReadOnlyDictionary<string, int> baseline,
        IReadOnlyDictionary<string, int> current)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (baseline == null || current == null)
        {
            return result;
        }

        // 调用方传入的 baseline 可能使用默认（大小写敏感）比较器，这里统一为忽略大小写查找
        var baselineLookup = new Dictionary<string, int>(baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in current)
        {
            if (string.IsNullOrEmpty(kvp.Key))
            {
                continue;
            }

            baselineLookup.TryGetValue(kvp.Key, out var baselineCount);
            var delta = kvp.Value - baselineCount;
            if (delta > 0)
            {
                result[kvp.Key] = delta;
            }
        }

        return result;
    }

    /// <summary>指定物品获得量是否 ≥ requiredQty（chop_tree / mine / forage-targeted）。</summary>
    public static bool IsQuantityMet(IReadOnlyDictionary<string, int> collectedCounts, string itemId, int requiredQty)
    {
        if (requiredQty <= 0)
        {
            return true; // quantity 未指定或 0 → 视为任意获得即达标（调用方已做钳制，此处兜底）
        }

        return collectedCounts.TryGetValue(itemId, out var count) && count >= requiredQty;
    }

    /// <summary>所有物品获得量之和是否 ≥ requiredQty（forage 未指定 targetItemId 时）。</summary>
    public static bool IsTotalQuantityMet(IReadOnlyDictionary<string, int> collectedCounts, int requiredQty)
    {
        if (requiredQty <= 0)
        {
            return true;
        }

        return collectedCounts.Values.Sum() >= requiredQty;
    }

    /// <summary>动作完成计数是否 ≥ requiredQty（water_crops：浇地次数）。</summary>
    public static bool IsActionCountMet(int actionsCompleted, int requiredQty)
    {
        if (requiredQty <= 0)
        {
            return true;
        }

        return actionsCompleted >= requiredQty;
    }

    /// <summary>
    ///     击杀计数判定（fight）：击杀数 = 起始怪物数 - 当前怪物数（钳 0），
    ///     达成条件：击杀数 ≥ requiredKills 或 区域怪物清零（currentMonsters == 0）。
    /// </summary>
    public static bool IsKillCountMet(int baselineMonsters, int currentMonsters, int requiredKills)
    {
        var killed = Math.Max(0, baselineMonsters - currentMonsters);
        if (currentMonsters <= 0)
        {
            return true; // 区域清空即视为完成
        }

        return requiredKills > 0 && killed >= requiredKills;
    }

    /// <summary>计算击杀数（供 CollectedCount 展示与测试断言）。</summary>
    public static int ComputeKillCount(int baselineMonsters, int currentMonsters) =>
        Math.Max(0, baselineMonsters - currentMonsters);
}

/// <summary>
///     Goal 时间计算纯逻辑（游戏分钟）。Game1.timeOfDay 是 HHMM 整数（如 1400 = 14:00）。
///     全部静态纯函数，可在单测中直接驱动（不依赖 Game1）。
/// </summary>
public static class GoalTime
{
    /// <summary>HHMM → 当天分钟数（1400 → 840）。</summary>
    public static int TimeOfDayToMinutes(int timeOfDay)
    {
        var hours = timeOfDay / 100;
        var minutes = timeOfDay % 100;
        return (hours * 60) + minutes;
    }

    /// <summary>
    ///     起始分钟到当前分钟的游戏经过分钟数。跨天（now &lt; start）时按一天 1440 分钟回绕。
    ///     天数间隔 >1 天不做精确累计（Goal 生命周期以单日为限，超时由全局超时兜底）。
    /// </summary>
    public static int ElapsedMinutes(int startMinutes, int nowMinutes)
    {
        var elapsed = nowMinutes - startMinutes;
        return elapsed >= 0 ? elapsed : elapsed + 1440;
    }

    /// <summary>是否超时：经过分钟 ≥ 超时阈值（分钟）。</summary>
    public static bool IsTimedOut(int elapsedMinutes, int timeoutMinutes) => elapsedMinutes >= timeoutMinutes;
}
