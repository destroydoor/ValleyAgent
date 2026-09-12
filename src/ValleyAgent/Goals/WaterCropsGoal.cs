using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Handlers;
using ValleyAgent.Services;

namespace ValleyAgent.Goals;

/// <summary>
///     water_crops Goal：浇灌 dry 田地，动作完成计数 ≥ quantity（spec §3.2 终止条件：
///     "目标作物全部浇完（动作完成计数）"）。
///
///     <para>委托模式：每 tick 由 Goal 找到最近的 dry HoeDirt，<see cref="FarmHandler.SetForcedTarget"/>
///     (tile, "water") 强制 Handler 浇灌该瓦片后自退出 IDLE，恢复守卫拉回 EXECUTING_GOAL 后
///     继续下一个 dry 瓦片。浇灌次数通过 dry 瓦片数差量（dryBefore - dryAfter）累计——
///     产出物不是物品，动作计数是唯一可靠信号（spec §3.2 明确 water_crops 用动作计数）。</para>
/// </summary>
public class WaterCropsGoal : GoalBase
{
    private const int ScanRadius = 15;
    private readonly FarmHandler _farmHandler;

    public WaterCropsGoal(
        string npcName,
        bool reportBack,
        string callId,
        int quantity,
        int timeoutGameMinutes,
        int stuckTickThreshold,
        FarmHandler farmHandler)
        : base(npcName, GoalType.WaterCrops, reportBack, callId, timeoutGameMinutes, stuckTickThreshold)
    {
        _farmHandler = farmHandler ?? throw new ArgumentNullException(nameof(farmHandler));
        RequiredQuantity = Math.Max(1, quantity);
    }

    /// <summary>纯终止判定（单测入口）：动作完成计数 ≥ requiredQty。</summary>
    public static bool IsTerminationMet(int actionsCompleted, int requiredQty) =>
        GoalCompletionEvaluator.IsActionCountMet(actionsCompleted, requiredQty);

    /// <inheritdoc />
    protected override string DescribeProgressCore() => $"已浇灌 {CollectedCount}/{RequiredQuantity} 块田地";

    /// <inheritdoc />
    protected override void OnStarted(AgentInstance agent, NPC npc)
    {
        // 基线快照已由 GoalBase.Start 捕获；动作计数从 0 开始
    }

    /// <inheritdoc />
    protected override void TickCore(AgentInstance agent, NPC npc, int currentTick)
    {
        var location = npc.currentLocation;
        if (location == null)
        {
            return;
        }

        var dryTile = FindNearestDryTile(location, npc.Tile);
        if (dryTile == null)
        {
            // 无 dry 田地：若动作计数已达标则由外层判定 Complete；否则资源耗尽
            if (!IsTerminationMet(ActionsCompleted, RequiredQuantity))
            {
                Fail("resource_exhausted");
            }

            return;
        }

        var dryBefore = CountDryTiles(location);
        _farmHandler.SetForcedTarget(npc.Name, dryTile.Value, "water");
        _farmHandler.Update(npc, agent, currentTick);

        // 浇灌计数：dry 瓦片减少量（每浇一块 -1，钳 0 防抖动）
        var dryAfter = CountDryTiles(location);
        ActionsCompleted += Math.Max(0, dryBefore - dryAfter);
        CollectedCount = ActionsCompleted;
    }

    /// <inheritdoc />
    protected override bool EvaluateTermination(IReadOnlyDictionary<string, int> currentCounts) =>
        IsTerminationMet(ActionsCompleted, RequiredQuantity);

    /// <inheritdoc />
    protected override void UpdateCollected(IReadOnlyDictionary<string, int> currentCounts)
    {
        // water_crops 无物品产出：CollectedCount = 动作计数
        CollectedCount = ActionsCompleted;
    }

    /// <summary>找最近 dry 的 HoeDirt 瓦片（未浇水，state==0，与 FarmHandler 的 water 判定一致）。</summary>
    private static Point? FindNearestDryTile(GameLocation location, Vector2 npcTile)
    {
        Point? best = null;
        var bestDist = float.MaxValue;
        foreach (var tileV in location.terrainFeatures.Keys)
        {
            if (location.terrainFeatures[tileV] is not HoeDirt dirt || dirt.state.Value != 0)
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

    /// <summary>统计 location 内 dry（未浇水）的 HoeDirt 数量。</summary>
    private static int CountDryTiles(GameLocation location)
    {
        var count = 0;
        foreach (var feature in location.terrainFeatures.Values)
        {
            if (feature is HoeDirt dirt && dirt.state.Value == 0)
            {
                count++;
            }
        }

        return count;
    }
}
