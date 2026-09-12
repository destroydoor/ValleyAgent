#nullable enable
using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     Tests that the state machine handles rapid indoor/outdoor map transitions
///     (warp into FarmHouse �?warp back out �?repeat) without crashing or
///     producing null-location errors.
/// </summary>
public class E2_DoorLoop : V3TestBase
{
    private const int TargetCycles = 5;
    private const int TicksPerCycle = 200;

    private IValleyAgentApi? _api;
    private int _cycleCount;
    private int _errors;
    private FarmHouse? _farmHouse;
    private Point _indoorTile; // inside FarmHouse tile
    private bool _inside;
    private NPC? _npc;
    private Point _outdoorTile; // just outside the door tile

    public E2_DoorLoop(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E2_DoorLoop";
    }

    public override int TimeoutTicks
    {
        get => 2400; // ~40s for 5 full cycles
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
            Skip("API not available");
            return;
        }

        _ = _api.TryAllocateAgent("Haley");
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();

        // 初始化 FarmHouse 引用和室内/室外瓦片
        _farmHouse = Utility.getHomeOfFarmer(Game1.player)
                     ?? Game1.getLocationFromName("FarmHouse") as FarmHouse;
        // 典型 FarmHouse 室内可用瓦片 — 使用 warpTarget 入口坐标
        _indoorTile = _farmHouse?.getEntryLocation() ?? default;
        // 户外位置在 FarmHouse 的 warp 入口附近
        var farm = Game1.getLocationFromName("Farm")!;
        // 查找从 Farm 到 FarmHouse 的 warp
        var doorWarp = farm.warps?.FirstOrDefault(w => w?.TargetName == "FarmHouse");
        _outdoorTile = doorWarp != null
            ? new Point(doorWarp.X, doorWarp.Y)
            : new Point(54, 12); // 默认 Farm → FarmHouse 入口
    }

    public override bool Update()
    {
        var cycleTick = CurrentTick % TicksPerCycle;

        // Toggle warp every TicksPerCycle/2 ticks
        if (cycleTick is 0 or TicksPerCycle / 2)
        {
            if (_npc == null || _farmHouse == null)
            {
                return false;
            }

            try
            {
                _inside = !_inside;

                if (_inside)
                {
                    // Warp NPC inside FarmHouse
                    if (_npc.currentLocation != _farmHouse)
                    {
                        Game1.warpCharacter(_npc, _farmHouse, new Vector2(_indoorTile.X, _indoorTile.Y));
                    }
                    else
                    {
                        _npc.setTileLocation(new Vector2(_indoorTile.X, _indoorTile.Y));
                    }

                    Monitor.Log($"[E2] Warp INSIDE at tick {CurrentTick}", LogLevel.Info);
                }
                else
                {
                    // Warp NPC back outside to Farm
                    var farm = Game1.getLocationFromName("Farm")!;
                    if (_npc.currentLocation != farm)
                    {
                        Game1.warpCharacter(_npc, farm, new Vector2(_outdoorTile.X, _outdoorTile.Y));
                    }
                    else
                    {
                        _npc.setTileLocation(new Vector2(_outdoorTile.X, _outdoorTile.Y));
                    }

                    _cycleCount++;
                    Monitor.Log($"[E2] Warp OUTSIDE at tick {CurrentTick}, cycle {_cycleCount}/{TargetCycles}",
                        LogLevel.Info);
                }

                // Verify no null location
                var loc = _npc.currentLocation;
                Assert("NPC.currentLocation not null after warp", loc != null,
                    $"loc={loc?.NameOrUniqueName ?? "null"}");
            }
            catch (InvalidOperationException ex)
            {
                _errors++;
                Monitor.Log($"[E2] Exception during warp: {ex.Message}", LogLevel.Error);
            }
            catch (NullReferenceException ex)
            {
                _errors++;
                Monitor.Log($"[E2] Exception during warp: {ex.Message}", LogLevel.Error);
            }
            catch (ArgumentException ex)
            {
                _errors++;
                Monitor.Log($"[E2] Exception during warp: {ex.Message}", LogLevel.Error);
            }
        }

        // Mid-cycle location check every 100 ticks
        if (CurrentTick % 100 == 0 && _npc != null)
        {
            var loc = _npc.currentLocation;
            Assert("Location check", loc != null && loc.NameOrUniqueName != null,
                $"loc={loc?.NameOrUniqueName ?? "null"}");

            // Verify position resets correctly
            if (!_inside)
            {
                var dist = Vector2.Distance(_npc.Tile, new Vector2(_outdoorTile.X, _outdoorTile.Y));
                Assert($"NPC position reset correctly (cycle {_cycleCount})", dist < 5f,
                    $"dist={dist:F1} from expected {_outdoorTile}");
            }
        }

        // Success after TargetCycles completed (NPC is outside after last cycle)
        if (_cycleCount >= TargetCycles && !_inside)
        {
            Assert("Completed all cycles", _cycleCount >= TargetCycles && _errors == 0,
                $"{_cycleCount} cycles, {_errors} errors");
            return true;
        }

        // Timeout
        if (CurrentTick >= TimeoutTicks)
        {
            Assert("Did not timeout", _cycleCount >= TargetCycles, $"cycles={_cycleCount}, errors={_errors}");
            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        Monitor.Log($"[E2] Teardown: {_cycleCount} cycles, {_errors} errors", LogLevel.Info);
        SaveResults();
    }
}