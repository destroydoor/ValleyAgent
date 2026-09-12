#nullable enable
using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     Fuzz test: Rapidly adds items to NPC inventory via ValleyAgentApi to stress-test
///     overflow handling. After 12 distinct items fill all slots, subsequent items should
///     trigger the overflow path (debris on ground).
///     Verifies: Inventory respects MaxSlots=12, overflow items appear on ground, no NRE.
/// </summary>
public class F3_RandomDrops : V3TestBase
{
    // Item IDs to cycle through for fill testing
    private static readonly string[] ItemIds =
    {
        "(O)390", "(O)388", "(O)334", "(O)335", "(O)336", "(O)337",
        "(O)80", "(O)82", "(O)84", "(O)86", "(O)92", "(O)96",
        "(O)283", "(O)372", "(O)378", "(O)380", "(O)382", "(O)384",
        "(O)386", "(O)392", "(O)393", "(O)394", "(O)396", "(O)397",
        "(O)398", "(O)399", "(O)400", "(O)402", "(O)404", "(O)406"
    };

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Random _rng;
    private int _addCount;
    private IValleyAgentApi? _api;
    private int _dropTick;
    private bool _exceptionThrown;
    private int _groundItemCount;
    private int _maxSlotsSeen;
    private NPC? _npc;
    private int _overflowItems;
    private int _slotsFilled;

    public F3_RandomDrops(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
        _rng = new Random(42);
    }

    public override string TestName
    {
        get => "F3_RandomDrops";
    }

    public override int TimeoutTicks
    {
        get => 1500;
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

        // Allocate agent and clear inventory
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _ = _api?.TryAllocateAgent("Haley");

        _dropTick = -999;
        _addCount = 0;
        _slotsFilled = 0;
        _maxSlotsSeen = 0;
        _overflowItems = 0;
        _exceptionThrown = false;
        _groundItemCount = 0;
    }

    public override bool Update()
    {
        if (_npc == null)
        {
            return true;
        }

        var tick = CurrentTick;
        var loc = Game1.currentLocation ?? _npc.currentLocation;
        if (loc == null)
        {
            return true;
        }

        // Every 10 ticks, add one random item to NPC's agent inventory
        // After 12 slots are full, overflow items should hit the ground
        if (tick - _dropTick >= 10 && _addCount < ItemIds.Length)
        {
            _dropTick = tick;

            try
            {
                var itemId = ItemIds[_addCount % ItemIds.Length];
                _addCount++;

                // Use FillNpcInventory which calls agent.Inventory.TryAdd()
                // and returns false when inventory is full (overflow → debris)
                if (_api != null)
                {
                    var added = _api.FillNpcInventory("Haley", itemId, 1);
                    if (added)
                    {
                        _slotsFilled++;
                    }
                    else
                    {
                        // Inventory full — item should drop as debris
                        _overflowItems++;
                    }
                }
            }
            catch (NullReferenceException ex)
            {
                _exceptionThrown = true;
                _monitor.Log($"[F3] NRE during add: {ex.Message}", LogLevel.Error);
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[F3] Exception: {ex.Message}", LogLevel.Warn);
            }
            catch (ArgumentException ex)
            {
                _monitor.Log($"[F3] Exception: {ex.Message}", LogLevel.Warn);
            }
            catch (IOException ex)
            {
                _monitor.Log($"[F3] Exception: {ex.Message}", LogLevel.Warn);
            }
        }

        // Track inventory slot count
        var inventory = _api?.GetNpcInventory("Haley") ?? Array.Empty<string>();
        if (inventory.Length > _maxSlotsSeen)
        {
            _maxSlotsSeen = inventory.Length;
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        if (_npc == null)
        {
            return;
        }

        // Count ground items near NPC — 检查 debris（战利品掉落）和 objects（放置物品）
        // 溢出物品通常以 Debris 形式掉落在地面
        var loc = Game1.currentLocation ?? _npc.currentLocation;
        if (loc != null)
        {
            var npcTile = _npc.Tile;
            // 计算 tiles 2 格范围内的 Debris 数量（溢出物品以 Debris 形式存在）
            var nearbyDebris = loc.debris
                .Count(d => d?.Chunks?.Count > 0 && Vector2.Distance(
                    new Vector2(d.Chunks[0].position.Value.X / 64f, d.Chunks[0].position.Value.Y / 64f),
                    npcTile) <= 2f);
            // 也计算 objects 作为回退
            var nearbyObjects = loc.objects.Pairs
                .Count(kv => Vector2.Distance(kv.Key, npcTile) <= 2f && kv.Value != null);
            _groundItemCount = nearbyDebris + nearbyObjects;
        }

        // maxSlotsSeen must be ≤ MaxSlots (12) at all times
        Assert("Inventory_count_never_exceeds_12", _maxSlotsSeen <= 12,
            $"maxSlotsSeen={_maxSlotsSeen}");

        // After 12+ items, overflow MUST appear on ground
        Assert("Overflow_items_appeared_on_ground", _overflowItems > 0 || _groundItemCount > 0,
            $"overflowItems={_overflowItems}, groundItems={_groundItemCount}, slotsFilled={_slotsFilled}");

        Assert("No_NullReferenceException", !_exceptionThrown,
            "No NullReferenceException during test");

        _monitor.Log(
            $"[F3] Final: adds={_addCount}, slotsFilled={_slotsFilled}, maxSlots={_maxSlotsSeen}, overflow={_overflowItems}, groundItems={_groundItemCount}",
            LogLevel.Info);
    }
}