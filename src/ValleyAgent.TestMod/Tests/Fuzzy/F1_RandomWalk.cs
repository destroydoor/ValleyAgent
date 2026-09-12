#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using ValleyAgent.Navigation;
using xTile.Dimensions;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     Fuzz test: NPC performs random walks with random direction picks and distances.
///     Verifies: PathFindController lifecycle, no crashes, stuck detection works.
/// </summary>
public class F1_RandomWalk : V3TestBase
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private IValleyAgentApi? _api;
    private bool _controllerExistedOnce;
    private bool _hadError;
    private Vector2 _initialPos;
    private int _lastMoveTick;
    private Vector2 _lastPos;
    private Vector2 _lastTarget;
    private int _moveCount;
    private NPC? _npc;
    private int _stuckCount;

    public F1_RandomWalk(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public override string TestName
    {
        get => "F1_RandomWalk";
    }

    public override int TimeoutTicks
    {
        get => 1800;
    }

    public override TestGroup Group
    {
        get => TestGroup.Fuzzy;
    }

    public override void Setup()
    {
        _api = ModEntry.API;
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC not found");
            return;
        }

        _initialPos = _npc.Tile;
        _lastPos = _initialPos;
        _moveCount = 0;
        _stuckCount = 0;
        _controllerExistedOnce = false;
        _hadError = false;
        _lastMoveTick = -999;
        _lastTarget = Vector2.Zero;

        // Allocate agent if not already
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(Game1.player.Tile.X + 2, Game1.player.Tile.Y));
        _npc.Halt();

        // Validate spawn tile is walkable; correct if on known unwalkable tile {X:57, Y:18}
        var spawnTile = _npc.Tile;
        var location = _npc.currentLocation;
        var validTile = TestScenes.FindWalkableTileNear(location, spawnTile, spawnTile);
        _npc.setTileLocation(validTile);

        _ = _api?.TryAllocateAgent("Haley");
    }

    public override bool Update()
    {
        if (_npc == null)
        {
            return true;
        }

        var tick = CurrentTick;
        try
        {
            return UpdateCore(tick);
        }
        catch (InvalidOperationException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (NullReferenceException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (ArgumentException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (IndexOutOfRangeException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (KeyNotFoundException ex)
        {
            return HandleUpdateError(ex, tick);
        }
    }

    private bool HandleUpdateError(Exception ex, int tick)
    {
        _hadError = true;
        _monitor.Log($"[F1] Error at tick {tick}: {ex.Message}", LogLevel.Error);
        return tick >= TimeoutTicks;
    }

    private bool UpdateCore(int tick)
    {
        if (_npc == null)
        {
            return true;
        }

        // Every 120 ticks, pick a random direction and move 3-8 tiles
        if (tick - _lastMoveTick >= 120)
        {
            _lastMoveTick = tick;

            // Pick random direction: 0=up, 1=down, 2=left, 3=right
            var direction = RandomNumberGenerator.GetInt32(4);
            var distance = RandomNumberGenerator.GetInt32(3, 9); // 3-8 tiles

            var offset = direction switch
            {
                0 => new Vector2(0, -1), // up
                1 => new Vector2(0, 1), // down
                2 => new Vector2(-1, 0), // left
                _ => new Vector2(1, 0) // right
            };

            var target = _npc.Tile + offset * distance;

            // Clamp to map bounds
            var loc = _npc.currentLocation;
            if (loc?.Map?.Layers.Count > 0)
            {
                var layer = loc.Map.Layers[0];
                var maxX = layer.LayerWidth - 1;
                var maxY = layer.LayerHeight - 1;
                target.X = Math.Clamp(target.X, 0, maxX);
                target.Y = Math.Clamp(target.Y, 0, maxY);
            }

            _lastTarget = target;

            // Get MovementService from ModEntry
            var movementService = ModEntry.MovementService;
            if (movementService != null)
            {
                var result = movementService.MoveTo(_npc, target, MovementMode.ShortRange, tick);
                _monitor.Log(
                    $"[F1] Move #{_moveCount + 1}: dir={direction}, dist={distance}, target={target}, result={result}",
                    LogLevel.Info);

                if (result is MoveResult.Success or MoveResult.AlreadyMoving)
                {
                    _moveCount++;
                }
            }
            else
            {
                // Fallback: direct PathFindController creation
                _npc.Speed = 2;
                _npc.controller = new PathFindController(_npc, _npc.currentLocation,
                    new Point((int)target.X, (int)target.Y), 2);
                _moveCount++;
            }
        }

        // Check if controller exists at least once
        if (_npc.controller is not null)
        {
            _controllerExistedOnce = true;
        }

        // Stuck detection: if NPC hasn't moved significantly in 120 ticks
        var distFromLast = Vector2.Distance(_npc.Tile, _lastPos);
        if (distFromLast < 0.5f && tick - _lastMoveTick > 120)
        {
            _stuckCount++;
            _monitor.Log($"[F1] Stuck at tick {tick}, stuckCount={_stuckCount}", LogLevel.Info);
        }

        // Update last position periodically
        if (tick % 30 == 0)
        {
            _lastPos = _npc.Tile;
        }

        // End after 1800 ticks
        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        if (_npc == null)
        {
            return;
        }

        var finalPos = _npc.Tile;
        var posChanged = Vector2.Distance(finalPos, _initialPos) >= 1.0f;

        Assert("NPC_moved_at_least_once", posChanged,
            $"Initial: {_initialPos}, Final: {finalPos}");
        Assert("PathFindController_existed_at_some_point", _controllerExistedOnce,
            "Controller was observed at least once");
        Assert("Decision_queue_no_error", !_hadError,
            _hadError ? "Exception occurred during random walks" : "No exception during random walks");
        Assert("Stuck_count_less_than_3", _stuckCount < 3,
            $"stuckCount={_stuckCount}");
        Assert("Final_tile_walkable",
            _npc.currentLocation?.isTilePassable(
                new Location((int)finalPos.X * 64, (int)finalPos.Y * 64),
                Game1.viewport) ?? false,
            $"Final tile {finalPos} walkable check");

        _monitor.Log(
            $"[F1] Final: pos={finalPos}, moves={_moveCount}, stuck={_stuckCount}, controller={_controllerExistedOnce}",
            LogLevel.Info);
    }
}