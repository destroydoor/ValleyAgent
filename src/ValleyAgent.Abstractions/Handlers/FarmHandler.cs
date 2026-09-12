using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Navigation;
using ValleyAgent.StateMachine;
using ValleyAgent.Services;
using ValleyAgent.Utils;
using ValleyAgent.Inventory;

namespace ValleyAgent.Handlers
{
    /// <summary>
    /// Handles NPC farming behavior: water dry crops and harvest mature ones.
    /// If NPC is not on a farm map, navigates to one first (AGENTS.md 3.1 autonomy).
    /// </summary>
    public class FarmHandler : HandlerBase
    {
        private const int ScanRadius = 15;
        private const int DebugLogCooldownTicks = 60;
        private static readonly string[] s_farmLocations = { "Farm", "FarmHouse", "FarmCave" };
        private readonly Dictionary<string, int> _lastDebugLogTick = new(StringComparer.OrdinalIgnoreCase);

        // FarmHandler 使用基类 ForcedTargetInfo（含 Action 字段）

        public FarmHandler(IMonitor? monitor, IMovementService movementService, AgentNavigator? navigator = null)
            : base(monitor, movementService, navigator) { }

        /// <summary>
        /// Updates farming logic for the given NPC. Should be called every tick while in FARM state.
        /// </summary>
        public void Update(NPC npc, AgentInstance agent, int currentTick)
        {
            if (npc?.currentLocation == null)
            {
                return;
            }

            // ── Pre-task navigation: travel to a farm location (AGENTS.md 3.1 autonomy) ──
            if (!IsFarmLocation(npc.currentLocation))
            {
                if (_navigator != null && !_navigator.IsTravelling(npc.Name))
                {
                    var travelTarget = _navigator.NavigateToTaskLocation(
                        npc,
                        s_farmLocations,
                        currentTick,
                        out var startedTravel,
                        followsPlayer: false);
                    if (startedTravel)
                    {
                        _monitor?.Log($"[Farm] {npc.Name}: travelling to {travelTarget} for farming", LogLevel.Debug);
                    }
                }

                // 推进已存在的任务旅行（Departing/Travelling 阶段）。
                // 关键：FARM 状态必须自己推进旅行，因为 AgentNavigator.Update 只在 FOLLOW 状态被调用。
                // 若不调用 ProgressTravel，旅行会永远卡住，NPC 永远到不了农场。
                var stillTravelling = _navigator?.ProgressTravel(npc, currentTick) == true;

                // Halt NPC while waiting for travel or if navigator is unavailable
                _movementService.Stop(npc, "farm-pre-travel");

                // If still waiting for travel, don't exit — stay in FARM state
                if (stillTravelling)
                {
                    return;
                }

                // No navigator available or no path found — transition to IDLE
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            // We are on a farm — clear any travel state
            _navigator?.CancelTravel(npc.Name);

            var target = FindFarmingTarget(npc);
            if (target == null)
            {
                // scanRadius 内无目标：尝试在整个 location 内找最近作物并寻路过去。
                // 避免NPC刚跨图到达农场边缘时因距离作物>15格而立即退出FARM状态。
                var distantTarget = FindFarmingTargetInLocation(npc);
                if (distantTarget != null)
                {
                    var distantApproachTile = PathfindingUtility.GetApproachTile(
                        distantTarget.Tile,
                        (x, y) => TileWalkability.IsTileWalkable(npc.currentLocation, x, y));
                    var distantApproachVec = new Vector2(distantApproachTile.X, distantApproachTile.Y);
                    if (_movementService.HandleStuck(npc, distantApproachVec, currentTick))
                    {
                        return;
                    }
                    var distantMoveResult = _movementService.MoveTo(npc, distantApproachTile, MovementMode.LongRange, currentTick);
                    if (distantMoveResult == MoveResult.Frozen || distantMoveResult == MoveResult.NoPathFound)
                    {
                        _movementService.Stop(npc, "farm-distant-unreachable");
                        npc.doEmote(24);
                        _monitor?.Log($"[Farm] {npc.Name}: cannot reach distant farming target @ {distantTarget.Tile}. Exiting FARM.", LogLevel.Debug);
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                    }
                    return;
                }

                _movementService.Stop(npc, "farm-no-targets");
                npc.doEmote(24); // stretch — closing ritual
                _monitor?.Log($"[Farm] {npc.Name}: no farming targets in range (scanRadius={ScanRadius}), farm={npc.currentLocation.NameOrUniqueName}. Exiting FARM.", LogLevel.Debug);
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            // Log only once per second to avoid spam
            var shouldLog = !_lastDebugLogTick.TryGetValue(npc.Name, out var lastLog)
                || currentTick - lastLog >= DebugLogCooldownTicks;
            if (shouldLog)
            {
                _monitor?.Log($"[Farm] {npc.Name}: farming target @ {target.Tile} action={target.Action} dist={Math.Abs(npc.TilePoint.X - target.Tile.X) + Math.Abs(npc.TilePoint.Y - target.Tile.Y)} npcPos={npc.TilePoint}", LogLevel.Debug);
                _lastDebugLogTick[npc.Name] = currentTick;
            }

            if (IsAdjacent(npc.TilePoint, target.Tile))
            {
                _movementService.Stop(npc, "farm-adjacent");
                ResetObstacleClear(npc.Name);

                if (CanAct(npc.Name, currentTick))
                {
                    _monitor?.Log($"[Farm] {npc.Name}: performing {target.Action} at tile {target.Tile}", LogLevel.Debug);
                    PerformAction(npc, target, agent);
                    RecordAction(npc.Name, currentTick);

                    // If this was a forced one-shot target, clear it and exit
                    if (HasForcedTarget(npc.Name))
                    {
                        ClearForcedTarget(npc.Name);
                        _movementService.Stop(npc, "farm-forced-done");
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                    }
                }
                return;
            }

            // Find a walkable adjacent tile to approach from, since crop tiles themselves may not be walkable
            var approachTile = PathfindingUtility.GetApproachTile(
                target.Tile,
                (x, y) => TileWalkability.IsTileWalkable(npc.currentLocation, x, y));

            // Stuck detection — delegate to MovementService
            if (_movementService.HandleStuck(npc, new Vector2(approachTile.X, approachTile.Y), currentTick))
            {
                return;
            }

            // Pathfind to approach tile via MovementService (short-range mode = 5-tick cooldown)
            var moveResult = _movementService.MoveTo(npc, approachTile, MovementMode.ShortRange, currentTick);
            if (moveResult == MoveResult.Frozen)
            {
                _movementService.Stop(npc, "farm-frozen");
                ResetPathBlocked(npc.Name);
                agent.StateMachine.ForceTransition(AgentState.IDLE);
            }
            else if (moveResult == MoveResult.NoPathFound)
            {
                IncrementPathBlocked(npc.Name);
                if (IsPathBlockedExhausted(npc.Name))
                {
                    _monitor?.Log($"[Farm] {npc.Name}: path blocked {MaxPathBlockedRetries} times, giving up", LogLevel.Debug);
                    _movementService.Stop(npc, "farm-path-blocked-exhausted");
                    ResetPathBlocked(npc.Name);
                    ResetObstacleClear(npc.Name);
                    agent.StateMachine.ForceTransition(AgentState.IDLE);
                    return;
                }

                var obstacle = TileWalkability.FindNearestDestructibleObstacle(npc.currentLocation, npc.TilePoint, maxRadius: 5);
                if (obstacle.HasValue)
                {
                    var exhausted = RecordObstacleAttempt(npc.Name, obstacle.Value);
                    if (exhausted)
                    {
                        _monitor?.Log($"[Farm] {npc.Name}: obstacle at {obstacle.Value} not cleared after {MaxObstacleClearRetries} attempts, giving up target", LogLevel.Debug);
                        _movementService.Stop(npc, "farm-obstacle-clear-exhausted");
                        ResetPathBlocked(npc.Name);
                        ResetObstacleClear(npc.Name);
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                        return;
                    }

                    _monitor?.Log($"[Farm] {npc.Name}: path blocked, clearing obstacle at {obstacle.Value}", LogLevel.Debug);
                    var obstacleApproach = PathfindingUtility.GetApproachTile(
                        obstacle.Value,
                        (x, y) => TileWalkability.IsTileWalkable(npc.currentLocation, x, y));
                    var obstacleResult = _movementService.MoveTo(npc, obstacleApproach, MovementMode.ShortRange, currentTick);
                    if (obstacleResult == MoveResult.NoPathFound || obstacleResult == MoveResult.Frozen)
                    {
                        _monitor?.Log($"[Farm] {npc.Name}: cannot reach obstacle at {obstacle.Value}, giving up target", LogLevel.Debug);
                        _movementService.Stop(npc, "farm-obstacle-unreachable");
                        ResetPathBlocked(npc.Name);
                        ResetObstacleClear(npc.Name);
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                    }
                }
                else
                {
                    _movementService.Stop(npc, "farm-no-path-no-obstacle");
                    ResetPathBlocked(npc.Name);
                    ResetObstacleClear(npc.Name);
                    agent.StateMachine.ForceTransition(AgentState.IDLE);
                }
            }
            else
            {
                ResetPathBlocked(npc.Name);
            }
        }

        /// <summary>
        /// Set a forced farming target for one-shot command execution.
        /// </summary>
        public void SetForcedTarget(string npcName, Point tile, string action) => base.SetForcedTarget(npcName, tile, action: action);

        public new void ClearForcedTarget(string npcName) => base.ClearForcedTarget(npcName);

        /// <summary>Returns environment summary with counts, or null if none.</summary>
        public static string? ScanEnvironment(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null)
            {
                return null;
            }

            if (!IsFarmLocation(location))
            {
                return null;
            }

            var npcTile = npc.TilePoint;
            int matureCrops = 0, drySoil = 0;

            foreach (var tileV in location.terrainFeatures.Keys)
            {
                if (location.terrainFeatures[tileV] is not HoeDirt dirt || dirt.crop == null)
                {
                    continue;
                }

                Point tile = new((int)tileV.X, (int)tileV.Y);
                var dist = Math.Abs(npcTile.X - tile.X) + Math.Abs(npcTile.Y - tile.Y);
                if (dist > ScanRadius)
                {
                    continue;
                }

                var crop = dirt.crop;
                var isReady = crop.currentPhase.Value >= crop.phaseDays.Count - 1 || crop.fullyGrown.Value;
                if (isReady)
                {
                    matureCrops++;
                }
                else if (dirt.state.Value == 0)
                {
                    drySoil++;
                }
            }

            if (matureCrops == 0 && drySoil == 0)
            {
                return null;
            }

            var parts = new List<string>();
            if (matureCrops > 0)
            {
                parts.Add($"{matureCrops} mature crops");
            }

            if (drySoil > 0)
            {
                parts.Add($"{drySoil} dry soil patches");
            }

            return string.Join(", ", parts);
        }

        private static FarmingTarget? FindFarmingTargetStatic(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null || !IsFarmLocation(location))
            {
                return null;
            }
            var npcTile = npc.TilePoint;
            FarmingTarget? bestHarvest = null, bestWater = null;
            int bestHarvestDist = int.MaxValue, bestWaterDist = int.MaxValue;

            // 单次遍历同时收集收割和浇水目标
            foreach (var tileV in location.terrainFeatures.Keys)
            {
                if (location.terrainFeatures[tileV] is not HoeDirt dirt || dirt.crop == null)
                {
                    continue;
                }

                Point tile = new((int)tileV.X, (int)tileV.Y);
                var dist = Math.Abs(npcTile.X - tile.X) + Math.Abs(npcTile.Y - tile.Y);
                if (dist > ScanRadius)
                {
                    continue;
                }

                var crop = dirt.crop;
                var isReady = crop.currentPhase.Value >= crop.phaseDays.Count - 1 || crop.fullyGrown.Value;

                if (isReady && dist < bestHarvestDist)
                {
                    bestHarvestDist = dist;
                    bestHarvest = new FarmingTarget(tile, FarmingAction.Harvest, dirt, tileV);
                }
                else if (!isReady && dirt.state.Value == 0 && dist < bestWaterDist)
                {
                    bestWaterDist = dist;
                    bestWater = new FarmingTarget(tile, FarmingAction.Water, dirt, tileV);
                }
            }

            return bestHarvest ?? bestWater;
        }

        private FarmingTarget? FindFarmingTarget(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null || !IsFarmLocation(location))
            {
                return null;
            }

            // ─── Forced-target override (command-driven one-shot) ───────────
            if (TryGetForcedTargetInfo(npc.Name, out var forced))
            {
                var fv = new Vector2(forced.Tile.X, forced.Tile.Y);
                if (location.terrainFeatures.TryGetValue(fv, out var feature) && feature is HoeDirt dirt)
                {
                    if (forced.Action == "water")
                    {
                        if (dirt.state.Value == 0)
                        {
                            return new FarmingTarget(forced.Tile, FarmingAction.Water, dirt, fv);
                        }
                    }
                    else if (forced.Action == "harvest" && dirt.crop != null)
                    {
                        var isReady = dirt.crop.currentPhase.Value >= dirt.crop.phaseDays.Count - 1
                            || dirt.crop.fullyGrown.Value;
                        if (isReady)
                        {
                            return new FarmingTarget(forced.Tile, FarmingAction.Harvest, dirt, fv);
                        }
                    }
                }
                ClearForcedTarget(npc.Name);
            }

            return FindFarmingTargetStatic(npc);
        }

        /// <summary>
        /// 在整个 location 内查找最近的 farming 目标（无 scanRadius 限制）。
        /// 用于 NPC 距离作物较远（>scanRadius）时寻路靠近，避免立即退出 FARM 状态。
        /// </summary>
        private static FarmingTarget? FindFarmingTargetInLocation(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null || !IsFarmLocation(location))
            {
                return null;
            }
            var npcTile = npc.TilePoint;
            FarmingTarget? bestHarvest = null, bestWater = null;
            int bestHarvestDist = int.MaxValue, bestWaterDist = int.MaxValue;

            foreach (var tileV in location.terrainFeatures.Keys)
            {
                if (location.terrainFeatures[tileV] is not HoeDirt dirt || dirt.crop == null)
                {
                    continue;
                }

                Point tile = new((int)tileV.X, (int)tileV.Y);
                var dist = Math.Abs(npcTile.X - tile.X) + Math.Abs(npcTile.Y - tile.Y);

                var crop = dirt.crop;
                var isReady = crop.currentPhase.Value >= crop.phaseDays.Count - 1 || crop.fullyGrown.Value;

                if (isReady && dist < bestHarvestDist)
                {
                    bestHarvestDist = dist;
                    bestHarvest = new FarmingTarget(tile, FarmingAction.Harvest, dirt, tileV);
                }
                else if (!isReady && dirt.state.Value == 0 && dist < bestWaterDist)
                {
                    bestWaterDist = dist;
                    bestWater = new FarmingTarget(tile, FarmingAction.Water, dirt, tileV);
                }
            }

            return bestHarvest ?? bestWater;
        }

        private static void PerformAction(NPC npc, FarmingTarget target, AgentInstance? agent)
        {
            switch (target.Action)
            {
                case FarmingAction.Water:
                    WaterCrop(npc, target);
                    break;
                case FarmingAction.Harvest:
                    HarvestCrop(npc, target, agent?.Inventory);
                    break;
                default:
                    break;
            }
        }

        private static void WaterCrop(NPC npc, FarmingTarget target)
        {
            ToolAnimationHelper.PlayWateringAnimation(npc);
            target.Dirt.state.Value = 1;
            npc.currentLocation.playSound("wateringCan");

            var tilePos = target.TileV * 64f;
            npc.currentLocation.temporarySprites.Add(new TemporaryAnimatedSprite(
                "TileSheets\\animations",
                new Rectangle(0, 192, 64, 64),
                100f,
                4,
                0,
                tilePos,
                flicker: false,
                flipped: false,
                layerDepth: (tilePos.Y + 32f) / 10000f,
                alphaFade: 0f,
                Color.White,
                scale: 1f,
                scaleChange: 0f,
                rotation: 0f,
                rotationChange: 0f));

            FacePosition(npc, tilePos + new Vector2(32f, 32f));
        }

        private static void HarvestCrop(NPC npc, FarmingTarget target, AgentInventory? inventory)
        {
            if (target.Dirt.crop == null)
            {
                return;
            }

            ToolAnimationHelper.PlayToolSwingAnimation(npc);

            // 捕获收获物索引（harvest() 可能 null 化 crop）
            var harvestIndex = target.Dirt.crop?.indexOfHarvest.Value ?? "";

            // 不使用原版 crop.harvest()，因为它会把收获物给玩家
            // 改为手动收获：直接从 crop 数据获取收获物，放入 NPC 背包
            var harvested = false;
            if (!string.IsNullOrEmpty(harvestIndex))
            {
                var product = new StardewValley.Object(harvestIndex, 1);
                if (inventory != null && inventory.TryAdd(product))
                {
                    harvested = true;
                }
                else
                {
                    // 背包满或无背包 - 掉落到地面
                    _ = Game1.createItemDebris(product, target.TileV * 64f, npc.FacingDirection, npc.currentLocation);
                    harvested = true;
                }
            }

            if (harvested)
            {
                npc.currentLocation.playSound("harvest");

                // 移除非再生作物
                if (target.Dirt.crop != null)
                {
                    target.Dirt.crop = null;
                }
            }

            FacePosition(npc, (target.TileV * 64f) + new Vector2(32f, 32f));
        }

        private enum FarmingAction { Water, Harvest }

        /// <summary>
        /// Checks whether the given location is considered a farm where crops can grow.
        /// All farm types (Standard, Riverland, Forest, Hilltop, Wilderness, FourCorners, Beach, Meadowlands)
        /// are Farm subclasses, plus the Greenhouse indoors.
        /// FarmHouse and FarmCave are NOT farming locations (no crops grow there).
        /// Also accepts any outdoor location that has HoeDirt terrain features (test maps, mod farms).
        /// </summary>
        private static bool IsFarmLocation(GameLocation location)
        {
            if (location == null)
            {
                return false;
            }

            // Standard farm maps
            if (location is Farm)
            {
                return true;
            }

            var name = location.NameOrUniqueName;

            // FarmHouse 和 FarmCave 不是 farming location（作物不会在这里生长），
            // 必须排除，否则 NPC 在 FarmHouse 时会被误判为已在农场而立即退出 FARM 状态。
            if (name.Equals("FarmHouse", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FarmCave", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name.Contains("Farm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Greenhouse
            if (name.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 已知非农场户外位置排除
            if (name.Equals("Town", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Forest", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Mountain", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Beach", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Desert", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 其他户外位置有 HoeDirt 视为可 farming（测试地图、mod 农场）
            return location.IsOutdoors && location.terrainFeatures?.Values.OfType<HoeDirt>().Any() == true;
        }

        private class FarmingTarget
        {
            public Point Tile { get; }
            public Vector2 TileV { get; }
            public FarmingAction Action { get; }
            public HoeDirt Dirt { get; }

            public FarmingTarget(Point tile, FarmingAction action, HoeDirt dirt, Vector2 tileV)
            {
                Tile = tile;
                TileV = tileV;
                Action = action;
                Dirt = dirt;
            }
        }
    }
}
