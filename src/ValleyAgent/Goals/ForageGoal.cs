using System;
using System.Collections.Generic;
using StardewValley;
using ValleyAgent.Handlers;
using ValleyAgent.Services;

namespace ValleyAgent.Goals;

/// <summary>
///     forage Goal：采集物品，背包获得量 ≥ quantity（spec §3.2 终止条件）。
///
///     <para>委托模式：每 tick 调 <see cref="ForageHandler.Update"/>（自动扫描最近可拾取物、寻路、
///     拾取落袋，ForageGroundObject → AgentInventory.TryAdd），目标耗尽时自退出 IDLE，
///     由 GoalExecutor 的恢复守卫拉回 EXECUTING_GOAL 继续。</para>
///
///     <para>终止判定 = 背包差量（物品收集量模型，spec §1.1）：
///     未传 targetItemId 时按全部获得量之和 ≥ quantity；传了则按指定物品 ≥ quantity。</para>
/// </summary>
public class ForageGoal : GoalBase
{
    private readonly ForageHandler _forageHandler;

    public ForageGoal(
        string npcName,
        bool reportBack,
        string callId,
        int quantity,
        string? targetItemId,
        int timeoutGameMinutes,
        int stuckTickThreshold,
        ForageHandler forageHandler)
        : base(npcName, GoalType.Forage, reportBack, callId, timeoutGameMinutes, stuckTickThreshold)
    {
        _forageHandler = forageHandler ?? throw new ArgumentNullException(nameof(forageHandler));
        RequiredQuantity = Math.Max(1, quantity);
        TargetItemId = string.IsNullOrWhiteSpace(targetItemId) ? string.Empty : targetItemId.Trim();
    }

    /// <summary>纯终止判定（单测入口）：全部物品获得量之和 ≥ requiredQty。</summary>
    public static bool IsTerminationMet(IReadOnlyDictionary<string, int> collectedCounts, int requiredQty) =>
        GoalCompletionEvaluator.IsTotalQuantityMet(collectedCounts, requiredQty);

    /// <summary>纯终止判定（单测入口）：指定物品获得量 ≥ requiredQty。</summary>
    public static bool IsTerminationMetForItem(
        IReadOnlyDictionary<string, int> collectedCounts, string targetItemId, int requiredQty) =>
        GoalCompletionEvaluator.IsQuantityMet(collectedCounts, targetItemId, requiredQty);

    /// <inheritdoc />
    protected override string DescribeProgressCore() =>
        string.IsNullOrEmpty(TargetItemId)
            ? $"已采集 {CollectedCount}/{RequiredQuantity} 件物品"
            : $"已采集 {CollectedCount}/{RequiredQuantity} {TargetItemId}";

    /// <inheritdoc />
    protected override void OnStarted(AgentInstance agent, NPC npc)
    {
        // 基线快照已由 GoalBase.Start 捕获；Handler 自动扫描，无需预设目标
    }

    /// <inheritdoc />
    protected override void TickCore(AgentInstance agent, NPC npc, int currentTick) =>
        _forageHandler.Update(npc, agent, currentTick);

    /// <inheritdoc />
    protected override bool EvaluateTermination(IReadOnlyDictionary<string, int> currentCounts)
    {
        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts);
        return string.IsNullOrEmpty(TargetItemId)
            ? IsTerminationMet(delta, RequiredQuantity)
            : IsTerminationMetForItem(delta, TargetItemId, RequiredQuantity);
    }

    /// <inheritdoc />
    protected override void UpdateCollected(IReadOnlyDictionary<string, int> currentCounts)
    {
        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts);
        CollectedCount = string.IsNullOrEmpty(TargetItemId)
            ? Sum(delta)
            : delta.GetValueOrDefault(TargetItemId);
    }

    private static int Sum(IReadOnlyDictionary<string, int> counts)
    {
        var total = 0;
        foreach (var kvp in counts)
        {
            total += kvp.Value;
        }

        return total;
    }
}
