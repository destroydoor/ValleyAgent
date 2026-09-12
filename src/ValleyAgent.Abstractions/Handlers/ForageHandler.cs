using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
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
    /// Handles NPC foraging behavior: find and collect spawned ground items and berry bushes.
    /// If NPC is not outdoors, navigates outdoors first (AGENTS.md 3.1 autonomy).
    /// </summary>
    public class ForageHandler : HandlerBase
    {
        private const int ScanRadius = 15;
        private const int DebugLogCooldownTicks = 60; // 1s at 60fps - throttle handler debug logs
        private static readonly string[] s_forageLocations = { "Farm", "Town", "Forest", "Mountain", "Beach", "BusStop", "Woods" };
        private readonly Dictionary<string, int> _lastDebugLogTick = new(StringComparer.OrdinalIgnoreCase);

        public ForageHandler(IMonitor? monitor, IMovementService movementService, AgentNavigator? navigator = null) : base(monitor, movementService, navigator) { }

        /// <summary>
        /// Updates foraging logic for the given NPC. Should be called every tick while in FORAGE state.
        /// </summary>
        public void Update(NPC npc, AgentInstance agent, int currentTick)
        {
            if (npc?.currentLocation == null)
            {
                return;
            }

            // Only forage outdoors or in caves (not inside buildings)
            if (!npc.currentLocation.IsOutdoors && npc.currentLocation is not StardewValley.Locations.MineShaft && !DebugFlags.ForceForageLocation)
            {
                // ── Pre-task navigation: travel outdoors (AGENTS.md 3.1 autonomy) ──
                if (_navigator != null && !_navigator.IsTravelling(npc.Name))
                {
                    var travelTarget = _navigator.NavigateToTaskLocation(
                        npc,
                        s_forageLocations,
                        currentTick,
                        out var startedTravel,
                        followsPlayer: false);
                    if (startedTravel)
                    {
                        _monitor?.Log($"[Forage] {npc.Name}: travelling to {travelTarget} for foraging", LogLevel.Debug);
                    }
                }

                // 推进已存在的任务旅行（Departing/Travelling 阶段）。
                // 关键：FORAGE 状态必须自己推进旅行，因为 AgentNavigator.Update 只在 FOLLOW 状态被调用。
                // 若不调用 ProgressTravel，旅行会永远卡住，NPC 永远到不了户外。
                var stillTravelling = _navigator?.ProgressTravel(npc, currentTick) == true;

                // Halt NPC while waiting for travel or if navigator is unavailable
                _movementService.Stop(npc, "forage-pre-travel");

                // If still waiting for travel, don't exit — stay in FORAGE state
                if (stillTravelling)
                {
                    return;
                }

                // No navigator available or no path found — transition to IDLE
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            // We are outdoors — clear any travel state
            _navigator?.CancelTravel(npc.Name);

            var target = FindForageTarget(npc);
            if (target == null)
            {
                _movementService.Stop(npc, "forage-no-targets");
                npc.doEmote(20);
                _monitor?.Log($"[Forage] {npc.Name}: no forageables in range (scanRadius={ScanRadius}), loc={npc.currentLocation.NameOrUniqueName}. Exiting FORAGE.", LogLevel.Debug);
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            // Log only once per second to avoid spam
            if (!_lastDebugLogTick.TryGetValue(npc.Name, out var lastLog) || currentTick - lastLog >= DebugLogCooldownTicks)
            {
                _monitor?.Log($"[Forage] {npc.Name}: forage target {target.Object.Name} @ {target.Tile} dist={Math.Abs(npc.TilePoint.X - target.Tile.X) + Math.Abs(npc.TilePoint.Y - target.Tile.Y)} npcPos={npc.TilePoint}", LogLevel.Debug);
                _lastDebugLogTick[npc.Name] = currentTick;
            }

            if (IsAdjacent(npc.TilePoint, target.Tile))
            {
                _movementService.Stop(npc, "forage-adjacent");

                if (CanAct(npc.Name, currentTick))
                {
                    PerformForage(npc, target, agent);
                    RecordAction(npc.Name, currentTick);

                    // If this was a forced one-shot target, clear it and exit
                    if (TryGetForcedTarget(npc.Name, out _))
                    {
                        ClearForcedTarget(npc.Name);
                        _movementService.Stop(npc, "forage-forced-done");
                        agent.StateMachine.ForceTransition(AgentState.IDLE);
                    }
                }
                return;
            }

            // Find a walkable adjacent tile to approach from, since forageable tiles themselves may not be walkable
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
                _movementService.Stop(npc, "forage-frozen");
                agent.StateMachine.ForceTransition(AgentState.IDLE);
            }
        }

        // ═══ Target Detection ═══════════════════════════════════════════════

        /// <summary>Returns environment summary with counts, or null if none.</summary>
        public static string? ScanEnvironment(NPC npc)
        {
            var location = npc.currentLocation;
            // 与 Update 方法的 IsOutdoors 检查保持一致：ForceForageLocation=true 时允许在室内/矿洞扫描
            if (location == null || (!location.IsOutdoors && location is not StardewValley.Locations.MineShaft && !DebugFlags.ForceForageLocation))
            {
                return null;
            }

            var npcTile = npc.TilePoint;
            var forageCount = 0;

            foreach (var pair in location.objects.Pairs.ToArray())
            {
                if (!IsForageableObject(pair.Value))
                {
                    continue;
                }

                Point tile = new((int)pair.Key.X, (int)pair.Key.Y);
                var dist = Math.Abs(npcTile.X - tile.X) + Math.Abs(npcTile.Y - tile.Y);
                if (dist <= ScanRadius)
                {
                    forageCount++;
                }
            }

            return forageCount > 0 ? $"{forageCount} forageable items" : null;
        }

        private static ForageTarget? FindForageTargetStatic(NPC npc)
        {
            var location = npc.currentLocation;
            var npcTile = npc.TilePoint;
            ForageTarget? best = null;
            var bestDist = int.MaxValue;

            // Scan spawned ground forageables (pick nearest)
            foreach (var tileV in location.objects.Keys.ToArray())
            {
                if (!location.objects.TryGetValue(tileV, out var obj))
                {
                    continue;
                }
                if (!IsForageableObject(obj))
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
                    best = new ForageTarget(tile, obj, tileV);
                }
            }

            return best;
        }

        /// <summary>Instance version: checks forced-target override first, then falls back to static scan.</summary>
        private ForageTarget? FindForageTarget(NPC npc)
        {
            var location = npc.currentLocation;

            // ─── Forced-target override (command-driven one-shot) ───────────
            if (TryGetForcedTarget(npc.Name, out var forcedTile))
            {
                var fv = new Vector2(forcedTile.X, forcedTile.Y);
                if (location.objects.TryGetValue(fv, out var obj) && IsForageableObject(obj))
                {
                    return new ForageTarget(forcedTile, obj, fv);
                }
                // Forced target is invalid — clear it and fall through
                ClearForcedTarget(npc.Name);
            }

            return FindForageTargetStatic(npc);
        }

        private static bool IsForageableObject(StardewValley.Object obj)
        {
            if (obj.questItem.Value)
            {
                return false;
            }

            // Exclude obvious non-forage categories
            if (obj.Category is StardewValley.Object.metalResources
                or StardewValley.Object.buildingResources
                or StardewValley.Object.GemCategory)
            {
                return false;
            }

            // Primary signal: standard forageables
            if (obj.CanBeGrabbed && obj.IsSpawnedObject)
            {
                return true;
            }

            // Fallback: check for forage-like names (test-spawned items, modded forageables)
            var lower = obj.DisplayName.ToLowerInvariant();
            var looksLikeForage = lower.Contains("horseradish") || lower.Contains("daffodil")
                || lower.Contains("leek") || lower.Contains("salmonberry")
                || lower.Contains("berry") || lower.Contains("mushroom")
                || lower.Contains("wild") || lower.Contains("root")
                || lower.Contains("coconut") || lower.Contains("cactus")
                || lower.Contains("onion") || lower.Contains("garlic")
                || lower.Contains("grape") || lower.Contains("plum")
                || lower.Contains("spice") || lower.Contains("crystal")
                || lower.Contains("clam") || lower.Contains("coral")
                || lower.Contains("sea urchin") || lower.Contains("rainbow")
                || lower.Contains("shell") || lower.Contains("geode")
                || lower.Contains("forage") || lower.Contains("spring")
                || lower.Contains("summer") || lower.Contains("fall")
                || lower.Contains("winter");

            var isSmallObject = !obj.bigCraftable.Value && !obj.DisplayName.Contains("Error", StringComparison.OrdinalIgnoreCase);
            return looksLikeForage && isSmallObject;
        }

        // ═══ Forage Execution ═══════════════════════════════════════════════

        private static void PerformForage(NPC npc, ForageTarget target, AgentInstance? agent)
        {
            FacePosition(npc, (target.TileV * 64f) + new Vector2(32f, 32f));
            ForageGroundObject(npc, target, agent?.Inventory);
        }

        private static void ForageGroundObject(NPC npc, ForageTarget target, AgentInventory? inventory)
        {
            var location = npc.currentLocation;
            var obj = target.Object;
            if (obj == null)
            {
                return;
            }

            var tile = target.TileV;
            if (!location.objects.ContainsKey(tile))
            {
                return;
            }

            ToolAnimationHelper.PlayPickupAnimation(npc);
            _ = location.objects.Remove(tile);
            location.playSound("pickUpItem");

            // Create the item and try to put it in the backpack
            var drop = new StardewValley.Object(obj.ItemId, 1);
            if (inventory != null && inventory.TryAdd(drop))
            {
            }
            else
            {
                // Backpack full or no inventory - drop on ground
                _ = Game1.createItemDebris(drop, tile * 64f, npc.FacingDirection, location);
            }
            npc.doEmote(20); // happy emote

            // Leaf rustle animation at the pickup location
            location.temporarySprites.Add(new TemporaryAnimatedSprite(
                "TileSheets\\animations",
                new Rectangle(0, 1085, 58, 58),
                60f,
                8,
                0,
                tile * 64f,
                flicker: false,
                flipped: npc.FacingDirection == 3,
                layerDepth: ((tile.Y * 64f) + 32f) / 10000f,
                alphaFade: 0f,
                Color.White,
                scale: 1f,
                scaleChange: 0f,
                rotation: 0f,
                rotationChange: 0f));
        }

        // ═══ Data Model ═════════════════════════════════════════════════════

        private class ForageTarget
        {
            public Point Tile { get; }
            public Vector2 TileV { get; }
            public StardewValley.Object Object { get; }

            public ForageTarget(Point tile, StardewValley.Object obj, Vector2 tileV)
            {
                Tile = tile;
                TileV = tileV;
                Object = obj;
            }
        }
    }
}
