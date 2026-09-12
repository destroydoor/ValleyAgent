using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.Utils;

namespace ValleyAgent.Goals;

/// <summary>
///     chop_tree Goal：砍树获得 Wood ≥ quantity（spec §3.2 终止条件）。
///
///     <para>注意：FarmHandler 的 forced-target 只支持 water/harvest（HoeDirt），结构上无法砍树
///     （FarmHandler.FindFarmingTarget 强制覆盖只处理 HoeDirt 瓦片）。因此本 Goal 自带砍树动作循环
///     （与 CommandExecutor.ExecuteChopTree 同源，但木头直接进 NPC 背包而非掉落地面），
///     移动仍走 IMovementService（唯一碰 npc.controller 的组件），不修改任何 Handler 内部。</para>
///
///     <para>终止判定 = 背包 Wood (O)388 获得量 ≥ quantity（物品收集量模型，spec §1.1）。</para>
/// </summary>
public class ChopTreeGoal : GoalBase
{
    /// <summary>Wood 的 QualifiedItemId。</summary>
    public const string WoodItemId = "(O)388";

    private const int ScanRadius = 12;

    /// <summary>砍树动作冷却（tick）：与 Handler 默认动作冷却（48 tick）一致，防止 60 次/秒连砍。</summary>
    private const int ChopCooldownTicks = 48;

    private readonly IMonitor? _monitor;
    private readonly IMovementService _movementService;
    private int _lastChopTick = int.MinValue;

    public ChopTreeGoal(
        string npcName,
        bool reportBack,
        string callId,
        int quantity,
        int timeoutGameMinutes,
        int stuckTickThreshold,
        IMonitor? monitor,
        IMovementService movementService)
        : base(npcName, GoalType.ChopTree, reportBack, callId, timeoutGameMinutes, stuckTickThreshold)
    {
        _monitor = monitor;
        _movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
        RequiredQuantity = Math.Max(1, quantity);
        TargetItemId = WoodItemId;
    }

    /// <summary>
    ///     纯终止判定（单测入口）：背包 Wood 获得量 ≥ requiredQty。
    ///     collectedCounts 为 <see cref="GoalCompletionEvaluator.ComputeCollectedCounts"/> 的差量结果。
    /// </summary>
    public static bool IsTerminationMet(IReadOnlyDictionary<string, int> collectedCounts, int requiredQty) =>
        GoalCompletionEvaluator.IsQuantityMet(collectedCounts, WoodItemId, requiredQty);

    /// <inheritdoc />
    protected override string DescribeProgressCore() => $"已获得 {CollectedCount}/{RequiredQuantity} 木头";

    /// <inheritdoc />
    protected override void OnStarted(AgentInstance agent, NPC npc)
    {
        // 基线快照已由 GoalBase.Start 捕获；无需额外初始化
    }

    /// <inheritdoc />
    protected override void TickCore(AgentInstance agent, NPC npc, int currentTick)
    {
        var location = npc.currentLocation;
        if (location == null)
        {
            // 决策分支日志（铁律：任何决策分支必须 log 结果和原因，不允许静默）
            _monitor?.Log($"[Goal] {npc.Name}: chop_tree tick skipped — npc.currentLocation is null", LogLevel.Debug);
            return;
        }

        // 资源耗尽：附近找不到可砍的树 → 失败
        var treeTile = FindNearestTree(location, npc.Tile);
        if (!treeTile.HasValue)
        {
            _monitor?.Log($"[Goal] {npc.Name}: chop_tree resource_exhausted — no tree within {ScanRadius} tiles of {npc.Tile} in {location.Name}", LogLevel.Debug);
            Fail("resource_exhausted");
            return;
        }

        var treeTilePoint = treeTile.Value;
        if (IsAdjacent(npc.TilePoint, treeTilePoint))
        {
            // 相邻：冷却中则原地等待（不重复 MoveTo），冷却结束才砍。
            // _lastChopTick 初始为 int.MinValue，currentTick - int.MinValue 会溢出为负 → 永远"冷却中"，
            // 导致树永远不砍（2026-08-09 游戏内实测：NPC 原地 120 tick 不动被误判 stuck）。
            // 必须显式判未砍过。
            if (_lastChopTick != int.MinValue && currentTick - _lastChopTick < ChopCooldownTicks)
            {
                _movementService.Stop(npc, "chop-goal-cooldown");
                return;
            }

            _movementService.Stop(npc, "chop-goal-adjacent");
            ChopTree(npc, agent, location, treeTilePoint, currentTick);
            return;
        }

        // 非相邻 → 寻路到树的相邻可走瓦片
        var approach = PathfindingUtility.GetApproachTile(
            treeTilePoint, (x, y) => TileWalkability.IsTileWalkable(location, x, y));
        if (_movementService.HandleStuck(npc, new Vector2(approach.X, approach.Y), currentTick))
        {
            return;
        }

        _ = _movementService.MoveTo(npc, approach, MovementMode.ShortRange, currentTick);
    }

    /// <inheritdoc />
    protected override bool EvaluateTermination(IReadOnlyDictionary<string, int> currentCounts) =>
        IsTerminationMet(
            GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts), RequiredQuantity);

    /// <inheritdoc />
    protected override void UpdateCollected(IReadOnlyDictionary<string, int> currentCounts)
    {
        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts);
        CollectedCount = delta.GetValueOrDefault(WoodItemId);
    }

    /// <summary>找最近的非树桩 Tree 瓦片（ScanRadius 内）。</summary>
    private static Point? FindNearestTree(GameLocation location, Vector2 npcTile)
    {
        Point? best = null;
        var bestDist = float.MaxValue;
        foreach (var tileV in location.terrainFeatures.Keys)
        {
            if (location.terrainFeatures[tileV] is not Tree tree || tree.stump.Value)
            {
                continue;
            }

            var dist = Vector2.Distance(npcTile, tileV);
            if (dist <= ScanRadius && dist < bestDist)
            {
                bestDist = dist;
                best = new Point((int)tileV.X, (int)tileV.Y);
            }
        }

        return best;
    }

    private static bool IsAdjacent(Point a, Point b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return dx + dy <= 1;
    }

    /// <summary>
    ///     砍树：移除 terrainFeature，产出 Wood 进 NPC 背包（满则掉落地面）。
    ///     与 ExecuteChopTree 同源但落袋而非掉落，保证背包差量可累计（spec §1.1）。
    /// </summary>
    private void ChopTree(NPC npc, AgentInstance agent, GameLocation location, Point treeTile, int currentTick)
    {
        var tileV = new Vector2(treeTile.X, treeTile.Y);
        if (!location.terrainFeatures.TryGetValue(tileV, out var feature) || feature is not Tree tree)
        {
            return;
        }

        if (tree.stump.Value)
        {
            return;
        }

        ToolAnimationHelper.PlayToolSwingAnimation(npc);
        location.terrainFeatures.Remove(tileV);
        location.playSound("axe");
        _lastChopTick = currentTick;

        var woodQty = tree.growthStage.Value >= 5 ? 5 : 1;
        var wood = ItemRegistry.Create(WoodItemId, woodQty, allowNull: true);
        if (wood == null)
        {
            return;
        }

        if (agent?.Inventory == null || !agent.Inventory.TryAdd(wood))
        {
            // 背包满 → 掉落地面（不阻塞目标，仍算获得一次产出）
            _ = Game1.createItemDebris(wood, tileV * 64f, npc.FacingDirection, location);
        }

        _monitor?.Log($"[Goal] {npc.Name} chopped tree at {treeTile} (+{woodQty} wood)", LogLevel.Debug);
    }
}
