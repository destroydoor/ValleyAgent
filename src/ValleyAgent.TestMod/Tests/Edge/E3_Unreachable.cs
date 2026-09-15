#nullable enable
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using ValleyAgent.Navigation;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     Tests that the MovementService handles unreachable targets gracefully
///     without crashing, hanging, or producing infinite loops.
/// </summary>
public class E3_Unreachable : V3TestBase
{
    private IValleyAgentApi? _api;
    private Vector2 _barrierCenter;
    private bool _moveRequested;
    private int _moveRequestTick;
    private NPC? _npc;
    private Vector2 _npcStartTile;
    private int _stuckDetections;

    public E3_Unreachable(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E3_Unreachable";
    }

    public override int TimeoutTicks
    {
        get => 1200; // ~20s
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
    }

    public override void Setup()
    {
        _api = ModEntry.API;
        if (_api == null)
        {
            Monitor.Log("[WARN] API null, skipping allocation but still warping.", LogLevel.Warn);
        }
        else
        {
            _ = _api.TryAllocateAgent("Haley");
        }

        _ = _api!.TryAllocateAgent("Haley");
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _npcStartTile = new Vector2(32, 30);

        // Create 5 stump barriers in a horizontal line east of NPC
        // NPC at {32,30}, barriers at {33,30} through {37,30}
        var loc = _npc.currentLocation;
        _barrierCenter = new Vector2(35, 30); // middle of barrier line
        var barrierTiles = new List<Vector2>
        {
            new(33, 30),
            new(34, 30),
            new(35, 30),
            new(36, 30),
            new(37, 30)
        };
        foreach (var tile in barrierTiles)
        {
            var clump = new ResourceClump(ResourceClump.stumpIndex, 2, 2, tile);
            loc.resourceClumps.Add(clump);
        }

        // 原 "ResourceClump barriers created" 断言已删（2026-09-14 死断言清理）：
        // 紧跟无条件 Add 循环断言 Count>0 属构造性恒真，checker 判死。
    }

    public override bool Update()
    {
        // Request move to barrier center on tick 60 (give NPC time to initialize)
        if (CurrentTick == 60 && _npc != null && !_moveRequested)
        {
            Monitor.Log($"[E3] Requesting MoveTo to unreachable {_barrierCenter}...", LogLevel.Info);
            _moveRequested = true;
            _moveRequestTick = CurrentTick;

            // 实际调用 MovementService 移动到不可达目标
            var movementService = ModEntry.MovementService;
            if (movementService != null)
            {
                _ = movementService.MoveTo(_npc, _barrierCenter, MovementMode.ShortRange, CurrentTick);
            }
            else
            {
                // 回退：直接设置 PathFindController
                _npc.Speed = 2;
                _npc.controller = new PathFindController(
                    _npc, _npc.currentLocation, new Point((int)_barrierCenter.X, (int)_barrierCenter.Y), 2);
            }
        }

        // Check every 100 ticks
        if (CurrentTick % 100 != 0)
        {
            return false;
        }

        if (_npc == null)
        {
            Assert("NPC exists", false);
            return true;
        }

        var loc = _npc.currentLocation;
        AssertEx("NPC currentLocation not null", loc != null,
            "不可达目标导致寻路失败清理路径把 NPC 从地图移除（despawn），currentLocation 变 null",
            $"loc={loc?.NameOrUniqueName ?? "null"}");

        // Detect stuck: NPC hasn't moved meaningfully
        var distFromStart = Vector2.Distance(_npc.Tile, _npcStartTile);
        if (distFromStart < 0.5f && CurrentTick > _moveRequestTick + 60)
        {
            _stuckDetections++;
            Monitor.Log($"[E3] tick {CurrentTick}: NPC appears stuck (dist from start={distFromStart:F1})",
                LogLevel.Info);
        }

        // Verify no crash (NPC still alive and has a location)
        AssertEx("NPC has not crashed", _npc.currentLocation != null,
            "寻路失败后的 NPC 清理/复活逻辑把 Haley 移出 characters（同 F2 Teardown 记录过的死亡链），NPC 被销毁",
            $"location={_npc.currentLocation?.NameOrUniqueName ?? "null"}");

        // Success: test completes after TimeoutTicks (we're observing behavior, not demanding success)
        if (CurrentTick >= TimeoutTicks)
        {
            Assert("Test completed without crash", _npc.currentLocation != null,
                $"stuckDetections={_stuckDetections}, final tile={_npc.Tile}");
            // Verify barrier was real: stuckDetections > 0 proves NPC couldn't reach target
            Assert("Barrier was real (NPC got stuck)", _stuckDetections > 0,
                $"stuckDetections={_stuckDetections}");
            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        // Clean up resource clump barriers
        if (_npc?.currentLocation is GameLocation loc)
        {
            loc.resourceClumps.Clear();
        }

        Monitor.Log($"[E3] Teardown: stuckDetections={_stuckDetections}", LogLevel.Info);
        SaveResults();
    }
}