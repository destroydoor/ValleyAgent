using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Handlers;
using ValleyAgent.Services;

namespace ValleyAgent.Goals;

/// <summary>
///     mine Goal：开采矿石/石头，背包 targetItemId 获得量 ≥ quantity（spec §3.2 终止条件）。
///
///     <para>委托模式（spec §1.2 / design doc §7.1）：每 tick 调 <see cref="MineHandler.Update"/>，
///     Handler 自动扫描最近可开采石头、寻路、开采并落袋（MineObject → AgentInventory.TryAdd），
///     目标耗尽时自退出 IDLE，由 GoalExecutor 的恢复守卫重新拉回 EXECUTING_GOAL 继续。</para>
///
///     <para>终止判定 = 背包差量（物品收集量模型，spec §1.1）。targetItemId 默认 (O)390 Stone，
///     LLM 可传 (O)378 铜矿石等；MineHandler 产出宝石/矿物时按其实际 ItemId 计数。</para>
/// </summary>
public class MineGoal : GoalBase
{
    /// <summary>默认目标物品：Stone（MineHandler 开采普通石头的产出）。</summary>
    public const string DefaultTargetItemId = "(O)390";

    private readonly MineHandler _mineHandler;

    public MineGoal(
        string npcName,
        bool reportBack,
        string callId,
        int quantity,
        string? targetItemId,
        int timeoutGameMinutes,
        int stuckTickThreshold,
        MineHandler mineHandler)
        : base(npcName, GoalType.Mine, reportBack, callId, timeoutGameMinutes, stuckTickThreshold)
    {
        _mineHandler = mineHandler ?? throw new ArgumentNullException(nameof(mineHandler));
        RequiredQuantity = Math.Max(1, quantity);
        TargetItemId = string.IsNullOrWhiteSpace(targetItemId) ? DefaultTargetItemId : targetItemId.Trim();
    }

    /// <summary>
    ///     纯终止判定（单测入口）：背包 targetItemId 获得量 ≥ requiredQty。
    ///     collectedCounts 为背包差量结果。
    /// </summary>
    public static bool IsTerminationMet(
        IReadOnlyDictionary<string, int> collectedCounts, string targetItemId, int requiredQty) =>
        GoalCompletionEvaluator.IsQuantityMet(collectedCounts, targetItemId, requiredQty);

    /// <inheritdoc />
    protected override string DescribeProgressCore() => $"已获得 {CollectedCount}/{RequiredQuantity} {TargetItemId}";

    /// <inheritdoc />
    protected override void OnStarted(AgentInstance agent, NPC npc)
    {
        // 基线快照已由 GoalBase.Start 捕获；Handler 自动扫描，无需预设目标
    }

    /// <inheritdoc />
    protected override void TickCore(AgentInstance agent, NPC npc, int currentTick) =>
        _mineHandler.Update(npc, agent, currentTick);

    /// <inheritdoc />
    protected override bool EvaluateTermination(IReadOnlyDictionary<string, int> currentCounts) =>
        IsTerminationMet(
            GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts),
            TargetItemId,
            RequiredQuantity);

    /// <inheritdoc />
    protected override void UpdateCollected(IReadOnlyDictionary<string, int> currentCounts)
    {
        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts);
        CollectedCount = delta.GetValueOrDefault(TargetItemId);
    }
}
