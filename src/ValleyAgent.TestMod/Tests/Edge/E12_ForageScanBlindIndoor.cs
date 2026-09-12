#nullable enable
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Handlers;
using ValleyAgent.Utils;
using SObject = StardewValley.Object;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     刁钻测试：ForageHandler.ScanEnvironment 的 IsOutdoors 检查是硬编码的，
///     DebugFlags.ForceForageLocation 只影响 Update 中的导航逻辑，不影响扫描。
///     背景：ForageHandler.cs ScanEnvironment 检查 location.IsOutdoors，
///     如果不在室外，直接返回 null，不扫描任何采集物。
///     DebugFlags.ForceForageLocation 标志只影响 Update 方法中的位置检查，
///     不影响 ScanEnvironment。这意味着即使在室内手动放置了采集物，
///     且设置了 ForceForageLocation=true，ScanEnvironment 仍然返回 null。
///     测试步骤：
///     1. 传送到 FarmHouse（室内）
///     2. 手动放置采集物（SObject 带 IsSpawnedObject=true）
///     3. 设置 DebugFlags.ForceForageLocation = true
///     4. 调用 ForageHandler.ScanEnvironment
///     5. 验证：返回 null（盲区：标志不影响扫描）
///     6. 作为对照：传送到 Farm（室外），验证相同采集物可被扫描到
/// </summary>
public class E12_ForageScanBlindIndoor : V3TestBase
{
    private readonly List<Vector2> _indoorForageTiles = new();
    private readonly List<Vector2> _outdoorForageTiles = new();
    private IValleyAgentApi? _api;
    private GameLocation? _farmLocation;
    private string? _indoorScanResult;

    private NPC? _npc;
    private string? _outdoorScanResult;

    public E12_ForageScanBlindIndoor(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E12_ForageScanBlindIndoor";
    }

    public override int TimeoutTicks
    {
        get => 900;
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
    }

    public override void Setup()
    {
        DebugFlags.SuppressDecisions = true;

        _api = ModEntry.API;
        if (_api == null)
        {
            Skip("API不可用");
            return;
        }

        // 先获取 Farm 引用（用于后续的室外对照测试）
        _farmLocation = Game1.getLocationFromName("Farm");

        // 传送到 FarmHouse（室内场景）—— warpFarmer 是异步的，下一 tick 才生效
        SafeWarp.Farmer(Monitor, "FarmHouse", 5, 12);

        // 直接获取 FarmHouse Location 实例（不依赖异步的 Game1.currentLocation）
        var farmHouse = Game1.getLocationFromName("FarmHouse");
        if (farmHouse == null)
        {
            Skip("FarmHouse 未找到");
            return;
        }

        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC 未找到");
            return;
        }

        // 将 NPC 直接传送到 FarmHouse（warpCharacter 是同步的，立即生效）
        Game1.warpCharacter(_npc, farmHouse, new Vector2(7, 10));
        _ = _api.TryAllocateAgent("Haley");

        // 在 FarmHouse 中手动放置采集物（使用 farmHouse 而非 Game1.currentLocation）
        var forageIds = new[] { "(O)16", "(O)18", "(O)20" }; // Wild Horseradish, Daffodil, Leek
        for (var i = 0; i < 3; i++)
        {
            var tile = new Vector2(6 + i, 11);
            if (farmHouse.objects.ContainsKey(tile))
            {
                continue;
            }

            var forage = ItemRegistry.Create<SObject>(forageIds[i]);
            forage.IsSpawnedObject = true;
            forage.CanBeGrabbed = true;
            farmHouse.objects[tile] = forage;
            _indoorForageTiles.Add(tile);
        }

        Monitor.Log(
            $"[E12] Placed {_indoorForageTiles.Count} forageables in FarmHouse (indoor={!farmHouse.IsOutdoors}). NPC loc={_npc.currentLocation?.NameOrUniqueName}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // Phase 1 (tick 60): 在室内执行 ScanEnvironment
        if (tick == 60)
        {
            var loc = Game1.currentLocation;
            Monitor.Log(
                $"[E12] Phase 1: Indoor scan | IsOutdoors={loc?.IsOutdoors} | ForceForage={DebugFlags.ForceForageLocation}",
                LogLevel.Info);

            // 设置 ForceForageLocation 标志（但它不影响 ScanEnvironment）
            DebugFlags.ForceForageLocation = true;

            // 执行扫描
            _indoorScanResult = ForageHandler.ScanEnvironment(_npc);
            Monitor.Log(
                $"[E12] Indoor ScanEnvironment result: '{_indoorScanResult ?? "NULL"}' | objects in location: {loc?.objects.Count()}",
                LogLevel.Info);

            // 计数：室内应该有几个采集物
            var indoorForageCount = 0;
            if (loc != null)
            {
                foreach (var key in loc.objects.Keys)
                {
                    if (loc.objects[key]?.IsSpawnedObject == true)
                    {
                        indoorForageCount++;
                    }
                }
            }

            Monitor.Log($"[E12] Indoor IsSpawnedObject count: {indoorForageCount}", LogLevel.Info);
        }

        // Phase 2 (tick 180-200): 传送到 Farm（室外）做对照
        if (tick >= 180 && tick < 200 && _farmLocation != null && _npc != null)
        {
            // 传送到 Farm
            SafeWarp.Farmer(Monitor, "Farm", 54, 15);
            // 使用 warpCharacter 同步传送 NPC（setTileLocation 只改坐标，不改 currentLocation）
            Game1.warpCharacter(_npc, _farmLocation, new Vector2(54, 17));
            _npc.Halt();

            var loc = Game1.currentLocation;
            Monitor.Log(
                $"[E12] Phase 2: Warped to Farm | IsOutdoors={loc?.IsOutdoors} | NPC loc={_npc.currentLocation?.NameOrUniqueName}",
                LogLevel.Info);
        }

        // Phase 2 完成 (tick 220): 清除 ForceForageLocation，确保 Phase 2 室外扫描走正常逻辑
        if (tick == 220)
        {
            DebugFlags.ForceForageLocation = false;
            Monitor.Log("[E12] Phase 2: Cleared ForceForageLocation for outdoor baseline scan", LogLevel.Info);
        }

        // Phase 2 继续 (tick 300): 在室外放采集物并扫描
        if (tick == 300 && _farmLocation != null)
        {
            // 直接获取 Farm Location 实例（不依赖异步的 Game1.currentLocation）
            var farmLoc = _farmLocation;
            // 放置采集物
            var forageIds = new[] { "(O)16", "(O)18", "(O)20" };
            for (var i = 0; i < 3; i++)
            {
                var tile = new Vector2(53 + i, 17);
                if (farmLoc.objects.ContainsKey(tile))
                {
                    continue;
                }

                var forage = ItemRegistry.Create<SObject>(forageIds[i]);
                forage.IsSpawnedObject = true;
                forage.CanBeGrabbed = true;
                farmLoc.objects[tile] = forage;
                _outdoorForageTiles.Add(tile);
            }

            _outdoorScanResult = _npc != null ? ForageHandler.ScanEnvironment(_npc) : null;
            Monitor.Log(
                $"[E12] Outdoor ScanEnvironment result: '{_outdoorScanResult ?? "NULL"}' | objects={farmLoc.objects.Count()} | NPC loc={_npc?.currentLocation?.NameOrUniqueName} | ForceForage={DebugFlags.ForceForageLocation}",
                LogLevel.Info);
        }

        // 断言在 tick 420
        if (tick >= 420)
        {
            var indoorBlind = string.IsNullOrEmpty(_indoorScanResult);
            var outdoorSees = !string.IsNullOrEmpty(_outdoorScanResult);

            // 核心漏洞：ForceForageLocation 标志不影响 ScanEnvironment
            Assert(
                "ForceForageLocation_flag_affects_ScanEnvironment",
                !indoorBlind,
                $"ForceForageLocation=true 但室内扫描结果='{_indoorScanResult ?? "NULL"}'。" +
                "如果 ForceForageLocation 标志正确传递到 ScanEnvironment，室内应能扫描到采集物。");

            // 对照：室外扫描正常
            Assert(
                "Outdoor_scan_baseline_works",
                outdoorSees,
                $"室外扫描结果='{_outdoorScanResult ?? "NULL"}'。" +
                "室外扫描作为对照验证 ForageHandler 功能正常。");

            // 诊断信息
            Assert(
                "Scan_divergence_documented",
                true,
                $"室内(ForceForage=true)='{_indoorScanResult}' vs 室外='{_outdoorScanResult}'。" +
                "差异表明 ScanEnvironment 的 IsOutdoors 检查独立于 DebugFlags.ForceForageLocation。");

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        // 清理室内采集物
        var farmHouse = Game1.getLocationFromName("FarmHouse");
        if (farmHouse != null)
        {
            foreach (var tile in _indoorForageTiles)
            {
                if (farmHouse.objects.ContainsKey(tile))
                {
                    _ = farmHouse.objects.Remove(tile);
                }
            }
        }

        // 清理室外采集物
        var farm = Game1.getLocationFromName("Farm");
        if (farm != null)
        {
            foreach (var tile in _outdoorForageTiles)
            {
                if (farm.objects.ContainsKey(tile))
                {
                    _ = farm.objects.Remove(tile);
                }
            }
        }

        DebugFlags.ForceForageLocation = false;
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}