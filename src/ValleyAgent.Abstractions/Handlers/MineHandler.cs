using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;
using ValleyAgent.StateMachine;
using ValleyAgent.Services;
using ValleyAgent.Utils;
using ValleyAgent.Inventory;

namespace ValleyAgent.Handlers
{
    /// <summary>
    /// Handles NPC mining behavior: find breakable stones/ore and mine them.
    /// If NPC is not at a mining location, navigates there first (AGENTS.md 3.1 autonomy).
    /// </summary>
    public class MineHandler : HandlerBase
    {
        private const int SwingThreshold = 36;
        private const int ScanRadius = 12;
        private const int DebugLogCooldownTicks = 60;
        private static readonly string[] s_mineLocations = { "UndergroundMine", "Mine", "VolcanoDungeon", "Mountain" };
        private readonly Dictionary<string, int> _lastDebugLogTick = new(StringComparer.OrdinalIgnoreCase);
        // 独立冷却追踪：记录镐子挥动结束 tick，用于 IsMining/DrawTool 判断
        private readonly Dictionary<string, int> _swingEndTick = new(StringComparer.OrdinalIgnoreCase);

        public MineHandler(IMonitor? monitor, IMovementService movementService, AgentNavigator? navigator = null)
            : base(monitor, movementService, navigator) { }

        /// <summary>
        /// Updates mining logic for the given NPC. Should be called every tick while in MINE state.
        /// </summary>
        public void Update(NPC npc, AgentInstance agent, int currentTick)
        {
            if (npc?.currentLocation == null || agent == null)
            {
                return;
            }

            // ── Check if there are mining targets nearby first ──
            // If rocks are nearby (e.g., spawned by test mod), mine them regardless of location
            var nearbyTarget = FindMiningTarget(npc);

            if (nearbyTarget == null && !IsMiningLocation(npc.currentLocation) && !DebugFlags.ForceMiningLocation)
            {
                // ── Pre-task navigation: travel to a mining location (AGENTS.md 3.1 autonomy) ──
                if (_navigator != null && !_navigator.IsTravelling(npc.Name))
                {
                    var travelTarget = _navigator.NavigateToTaskLocation(
                        npc,
                        s_mineLocations,
                        currentTick,
                        out var startedTravel,
                        followsPlayer: false);
                    if (startedTravel)
                    {
                        _monitor?.Log($"[Mine] {npc.Name}: travelling to {travelTarget} for mining", LogLevel.Debug);
                    }
                }

                // 推进已存在的任务旅行（Departing/Travelling 阶段）。
                // 关键：MINE 状态必须自己推进旅行，因为 AgentNavigator.Update 只在 FOLLOW 状态被调用。
                // 若不调用 ProgressTravel，旅行会永远卡住，NPC 永远到不了矿区。
                var stillTravelling = _navigator?.ProgressTravel(npc, currentTick) == true;

                // Halt NPC while waiting for travel or if navigator is unavailable
                _movementService.Stop(npc, "mine-pre-travel");

                // If still waiting for travel, don't exit — stay in MINE state
                if (stillTravelling)
                {
                    return;
                }

                // No navigator available or no path found — transition to IDLE
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            // We are at a mining location — clear any travel state
            _navigator?.CancelTravel(npc.Name);

            var target = FindMiningTarget(npc);
            if (target == null)
            {
                _movementService.Stop(npc, "mine-no-targets");
                npc.doEmote(20);
                _monitor?.Log($"[Mine] {npc.Name}: no mining targets in range (scanRadius={ScanRadius}), loc={npc.currentLocation.NameOrUniqueName}. Exiting MINE.", LogLevel.Debug);
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            // Log only once per second to avoid spam
            if (!_lastDebugLogTick.TryGetValue(npc.Name, out var lastLog) || currentTick - lastLog >= DebugLogCooldownTicks)
            {
                _monitor?.Log($"[Mine] {npc.Name}: mining target {target.Object.Name} @ {target.Tile} dist={Math.Abs(npc.TilePoint.X - target.Tile.X) + Math.Abs(npc.TilePoint.Y - target.Tile.Y)} npcPos={npc.TilePoint}", LogLevel.Debug);
                _lastDebugLogTick[npc.Name] = currentTick;
            }

            if (IsAdjacent(npc.TilePoint, target.Tile))
            {
                _movementService.Stop(npc, "mine-adjacent");
                ResetObstacleClear(npc.Name);

                if (CanAct(npc.Name, currentTick))
                {
                    MineObject(npc, target, agent.Inventory);
                    RecordAction(npc.Name, currentTick);

                    // If this was a forced one-shot target, clear it and exit
                    if (HasForcedTarget(npc.Name))
                    {
                        ClearForcedTarget(npc.Name);
                        _movementService.Stop(npc, "mine-forced-done");
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                    }
                }
                return;
            }

            // Find a walkable adjacent tile to approach from, since stone tiles themselves are not walkable
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
                _movementService.Stop(npc, "mine-frozen");
                ResetPathBlocked(npc.Name);
                agent.StateMachine.ForceTransition(AgentState.IDLE);
            }
            else if (moveResult == MoveResult.NoPathFound)
            {
                IncrementPathBlocked(npc.Name);
                if (IsPathBlockedExhausted(npc.Name))
                {
                    _monitor?.Log($"[Mine] {npc.Name}: path blocked {MaxPathBlockedRetries} times, giving up", LogLevel.Debug);
                    _movementService.Stop(npc, "mine-path-blocked-exhausted");
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
                        _monitor?.Log($"[Mine] {npc.Name}: obstacle at {obstacle.Value} not cleared after {MaxObstacleClearRetries} attempts, giving up target", LogLevel.Debug);
                        _movementService.Stop(npc, "mine-obstacle-clear-exhausted");
                        ResetPathBlocked(npc.Name);
                        ResetObstacleClear(npc.Name);
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                        return;
                    }

                    _monitor?.Log($"[Mine] {npc.Name}: path blocked, clearing obstacle at {obstacle.Value}", LogLevel.Debug);
                    var obstacleApproach = PathfindingUtility.GetApproachTile(
                        obstacle.Value,
                        (x, y) => TileWalkability.IsTileWalkable(npc.currentLocation, x, y));
                    var obstacleResult = _movementService.MoveTo(npc, obstacleApproach, MovementMode.ShortRange, currentTick);
                    if (obstacleResult == MoveResult.NoPathFound || obstacleResult == MoveResult.Frozen)
                    {
                        _monitor?.Log($"[Mine] {npc.Name}: cannot reach obstacle at {obstacle.Value}, giving up target", LogLevel.Debug);
                        _movementService.Stop(npc, "mine-obstacle-unreachable");
                        ResetPathBlocked(npc.Name);
                        ResetObstacleClear(npc.Name);
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                    }
                }
                else
                {
                    _movementService.Stop(npc, "mine-no-path-no-obstacle");
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

        public override void Cleanup(string npcName)
        {
            base.Cleanup(npcName);
            _ = _swingEndTick.Remove(npcName);
            _ = _lastDebugLogTick.Remove(npcName);
        }

        private static bool IsMiningLocation(GameLocation location)
        {
            var name = location.NameOrUniqueName;
            return name.Contains("Mine", StringComparison.OrdinalIgnoreCase)
                || name.Contains("UndergroundMine", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Volcano", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Quarry", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Mountain", StringComparison.OrdinalIgnoreCase)
                || location is StardewValley.Locations.MineShaft;
        }

        /// <summary>Returns environment summary with counts, or null if none.</summary>
        public static string? ScanEnvironment(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null || (!IsMiningLocation(location) && !DebugFlags.ForceMiningLocation))
            {
                return null;
            }

            var npcTile = npc.TilePoint;
            var stoneCount = 0;

            foreach (var pair in location.objects.Pairs)
            {
                if (!IsBreakableStone(pair.Value))
                {
                    continue;
                }

                Point tile = new((int)pair.Key.X, (int)pair.Key.Y);
                var dist = Math.Abs(npcTile.X - tile.X) + Math.Abs(npcTile.Y - tile.Y);
                if (dist <= ScanRadius)
                {
                    stoneCount++;
                }
            }

            return stoneCount > 0 ? $"{stoneCount} breakable stones" : null;
        }

        public void SetForcedTarget(string npcName, Point tile) => base.SetForcedTarget(npcName, tile);
        public new void ClearForcedTarget(string npcName) => base.ClearForcedTarget(npcName);
        public new bool HasForcedTarget(string npcName) => base.HasForcedTarget(npcName);

        private static MiningTarget? FindMiningTargetStatic(NPC npc)
        {
            var location = npc.currentLocation;
            var npcTile = npc.TilePoint;
            MiningTarget? best = null;
            var bestDist = int.MaxValue;

            foreach (var tileV in location.objects.Keys)
            {
                var obj = location.objects[tileV];
                if (!IsBreakableStone(obj))
                {
                    continue;
                }

                Point tile = new((int)tileV.X, (int)tileV.Y);
                var dist = Math.Abs(npcTile.X - tile.X) + Math.Abs(npcTile.Y - tile.Y);
                if (dist > ScanRadius)
                {
                    continue;
                }

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = new MiningTarget(tile, obj, tileV);
                }
            }

            return best;
        }

        /// <summary>Instance version: checks forced-target override first, then falls back to static scan.</summary>
        private MiningTarget? FindMiningTarget(NPC npc)
        {
            var location = npc.currentLocation;

            // ─── Forced-target override (command-driven one-shot) ───────────
            if (TryGetForcedTarget(npc.Name, out var forcedTile))
            {
                var fv = new Vector2(forcedTile.X, forcedTile.Y);
                if (location.objects.TryGetValue(fv, out var obj) && IsBreakableStone(obj))
                {
                    return new MiningTarget(forcedTile, obj, fv);
                }

                ClearForcedTarget(npc.Name);
            }

            return FindMiningTargetStatic(npc);
        }

        private static bool IsBreakableStone(StardewValley.Object obj)
        {
            if (obj.IsBreakableStone())
            {
                return true;
            }

            var itemId = obj.ItemId ?? string.Empty;
            var qid = obj.QualifiedItemId ?? string.Empty;

            // Plain stone (spawned by TestMod or naturally)
            if (qid == "(O)343" || itemId == "343")
            {
                return true;
            }

            // Additional ore nodes that might not be caught by IsBreakableStone
            if (qid is "(O)75" or "(O)76" or "(O)77" ||
                itemId is "75" or "76" or "77")
            {
                return true;
            }

            // 注意：GemCategory 和 Minerals 是可拾取物品，不需要镐子破坏
            // 它们应通过 ForageHandler 的拾取逻辑处理，而非 MineHandler 的开采逻辑

            return false;
        }

        private void MineObject(NPC npc, MiningTarget target, AgentInventory? inventory)
        {
            var location = npc.currentLocation;
            var obj = target.Object;
            var tile = target.TileV;

            ToolAnimationHelper.PlayToolSwingAnimation(npc);
            FacePosition(npc, (tile * 64f) + new Vector2(32f, 32f));
            location.playSound("hammer");
            AnimatePickaxeSwing(npc);

            // 记录镐子挥动结束 tick，用于 IsMining/DrawTool
            _swingEndTick[npc.Name] = Game1.ticks + SwingThreshold;

            if (location.objects.ContainsKey(tile))
            {
                _ = location.objects.Remove(tile);

                // Determine drop
                StardewValley.Object drop;
                if (obj.Category == StardewValley.Object.GemCategory || obj.Type == "Minerals")
                {
                    drop = new StardewValley.Object(obj.ItemId, 1);
                }
                else
                {
                    drop = new StardewValley.Object("(O)390", 1); // Stone
                }

                // Try to put into backpack
                if (!(inventory != null && inventory.TryAdd(drop)))
                {
                    _ = Game1.createItemDebris(drop, tile * 64f, npc.FacingDirection, location);
                }

                npc.shake(300);
                _monitor?.Log($"{npc.Name} mined {obj.DisplayName} at {tile}", LogLevel.Trace);
            }
        }

        private static void AnimatePickaxeSwing(NPC npc)
        {
            var location = npc.currentLocation;
            var layerDepth = (npc.GetBoundingBox().Bottom + 2) / 10000f;
            var sourceRect = new Rectangle(80, 32, 16, 16); // Pickaxe from tool sheet

            var offset = npc.FacingDirection switch
            {
                0 => new Vector2(0, -48),
                1 => new Vector2(32, -16),
                2 => new Vector2(0, 16),
                3 => new Vector2(-32, -16),
                _ => Vector2.Zero
            };

            location.temporarySprites.Add(new TemporaryAnimatedSprite(
                "TileSheets\\tools",
                sourceRect,
                80f,
                1,
                0,
                npc.Position + offset,
                flicker: false,
                flipped: npc.FacingDirection == 3,
                layerDepth: layerDepth,
                alphaFade: 0.02f,
                Color.White,
                scale: 3f,
                scaleChange: 0f,
                rotation: npc.FacingDirection == 1 ? (float)Math.PI / 4 : npc.FacingDirection == 3 ? -(float)Math.PI / 4 : 0f,
                rotationChange: 0f));
        }
        private class MiningTarget
        {
            public Point Tile { get; }
            public Vector2 TileV { get; }
            public StardewValley.Object Object { get; }

            public MiningTarget(Point tile, StardewValley.Object obj, Vector2 tileV)
            {
                Tile = tile;
                TileV = tileV;
                Object = obj;
            }
        }

        // ─── Tool Drawing ──────────────────────────────────────────────────

        public bool IsMining(NPC npc) => _swingEndTick.TryGetValue(npc.Name, out var endTick) && Game1.ticks < endTick;

        public void DrawTool(SpriteBatch b, NPC npc)
        {
            if (!IsMining(npc))
            {
                return;
            }

            var texture = Game1.content.Load<Texture2D>("TileSheets\\tools");
            var src = new Rectangle(80, 32, 16, 16); // Pickaxe
            var pos = npc.getLocalPosition(Game1.viewport);
            var layer = npc.StandingPixel.Y / 10000f;

            switch (npc.FacingDirection)
            {
                case 0:
                    b.Draw(texture, pos + new Vector2(16, -32), src, Color.White, 0, Vector2.Zero, 3f, SpriteEffects.None, layer);
                    break;
                case 1:
                    b.Draw(texture, pos + new Vector2(48, -16), src, Color.White, (float)Math.PI / 4, Vector2.Zero, 3f, SpriteEffects.None, layer);
                    break;
                case 2:
                    b.Draw(texture, pos + new Vector2(16, 16), src, Color.White, 0, Vector2.Zero, 3f, SpriteEffects.None, layer);
                    break;
                case 3:
                    b.Draw(texture, pos + new Vector2(-16, -16), src, Color.White, -(float)Math.PI / 4, Vector2.Zero, 3f, SpriteEffects.FlipHorizontally, layer);
                    break;
                default:
                    break;
            }
        }
    }
}
