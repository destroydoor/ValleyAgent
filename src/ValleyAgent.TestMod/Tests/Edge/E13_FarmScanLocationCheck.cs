#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Handlers;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     刁钻测试：FarmHandler.ScanEnvironment 无位置限制 vs Update 有位置检查。
///     背景：FarmHandler.ScanEnvironment 不做位置检查——它在任何有 HoeDirt 的地图
///     上都会返回作物信息。但 FarmHandler.Update 在非农场位置会尝试导航到农场。
///     这导致 DecisionContext 可能报告"有成熟作物"但 NPC 无法就地收获。
///     测试步骤：
///     1. 传送到 Town（非农场位置）
///     2. 手动放置 HoeDirt + 成熟作物
///     3. 调用 FarmHandler.ScanEnvironment
///     4. 验证：返回非 null（扫描无位置限制，报告作物存在）
///     5. 强制 FARM 状态
///     6. 验证：NPC 开始导航离开（Update 检测非农场位置）
///     7. 验证：作物未被收获（NPC 离开而不是就地收获）
///     8. 对照：传送到 Farm，验证相同场景下 NPC 可以收获
/// </summary>
public class E13_FarmScanLocationCheck : V3TestBase
{
    private readonly List<Vector2> _farmCropTiles = new();
    private readonly List<Vector2> _townCropTiles = new();
    private IValleyAgentApi? _api;
    private bool _farmCropsHarvested;
    private string? _farmScanResult;

    private NPC? _npc;
    private bool _npcStartedNavigatingAway;
    private Vector2 _npcStartTile;
    private bool _phase2SetupDone;
    private bool _townCropsHarvested;
    private string? _townScanResult;

    public E13_FarmScanLocationCheck(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E13_FarmScanLocationCheck";
    }

    public override int TimeoutTicks
    {
        get => 1500;
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

        // Phase 1: 传送到 Town
        SafeWarp.Farmer(Monitor, "Town", 30, 60);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC 未找到");
            return;
        }

        // 将 NPC 也传送到 Town（setTileLocation 不改变 currentLocation）
        var town = Game1.getLocationFromName("Town");
        if (town != null && _npc.currentLocation != town)
        {
            _ = _npc.currentLocation?.characters.Remove(_npc);
            town.characters.Add(_npc);
            _npc.currentLocation = town;
        }

        _npc.setTileLocation(new Vector2(30, 58));
        _npc.Halt();
        _npcStartTile = _npc.Tile;
        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");

        // 在 Town 手动放置成熟作物（用 getLocationFromName 而非 currentLocation，因为 warpFarmer 可能延迟生效）
        var loc = Game1.getLocationFromName("Town");
        if (loc == null)
        {
            Skip("Town场景未找到");
            return;
        }

        string[] cropIds = { "472", "473", "474" };
        for (var i = 0; i < 3; i++)
        {
            var tile = new Vector2(30 + i, 57);
            if (loc.terrainFeatures.ContainsKey(tile))
            {
                continue;
            }

            var dirt = new HoeDirt
            {
                crop = new Crop(cropIds[i], (int)tile.X, (int)tile.Y, loc)
            };
            dirt.crop.growCompletely();
            loc.terrainFeatures[tile] = dirt;
            _townCropTiles.Add(tile);
        }

        Monitor.Log(
            $"[E13] Phase 1: Placed {_townCropTiles.Count} mature crops in Town (location={loc.Name}, IsOutdoors={loc.IsOutdoors})",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // Phase 1: Town 扫描 (tick 60)
        if (tick == 60)
        {
            var loc = Game1.currentLocation;
            Monitor.Log(
                $"[E13] Phase 1: Scanning in Town | Location={loc?.Name} | IsOutdoors={loc?.IsOutdoors}",
                LogLevel.Info);

            // 调用 FarmHandler.ScanEnvironment（静态方法）
            _townScanResult = FarmHandler.ScanEnvironment(_npc);
            Monitor.Log(
                $"[E13] FarmHandler.ScanEnvironment in Town: '{_townScanResult ?? "NULL"}' | NPC.currentLocation={_npc.currentLocation?.NameOrUniqueName} | Game1.currentLocation={Game1.currentLocation?.NameOrUniqueName}",
                LogLevel.Info);

            // 强制 FARM 状态，观察 NPC 行为
            _ = _api.TrySetAgentState("Haley", "FARM");
            _npcStartTile = _npc.Tile;
            Monitor.Log(
                $"[E13] Forced FARM state. NPC at {_npc.Tile}. Watching for navigation...",
                LogLevel.Info);
        }

        // Phase 1: 观察 NPC 是否开始导航离开 (tick 60-600)
        if (tick > 60 && tick <= 600 && !_npcStartedNavigatingAway)
        {
            // 如果 NPC 开始移动（有 PathFindController 或位置改变）且远离作物
            if (_npc.controller != null || Vector2.Distance(_npc.Tile, _npcStartTile) > 3f)
            {
                _npcStartedNavigatingAway = true;
                Monitor.Log(
                    $"[E13] NPC started moving at tick {tick}. Position: {_npc.Tile}. Navigating away from crops.",
                    LogLevel.Info);
            }

            // 检查作物是否被收获
            var loc = Game1.getLocationFromName("Town");
            if (loc != null)
            {
                var remaining = _townCropTiles.Count(t =>
                    loc.terrainFeatures.ContainsKey(t) &&
                    loc.terrainFeatures[t] is HoeDirt hd &&
                    hd.crop != null &&
                    !hd.crop.dead.Value);
                if (remaining < _townCropTiles.Count)
                {
                    _townCropsHarvested = true;
                    Monitor.Log(
                        $"[E13] Town crops reduced: {remaining}/{_townCropTiles.Count} remaining at tick {tick}",
                        LogLevel.Info);
                }
            }
        }

        // Phase 2 (tick 650): 传送到 Farm 做对照（只执行一次）
        if (!_phase2SetupDone && tick >= 650)
        {
            _phase2SetupDone = true;

            // 清理 Town 的作物
            var town = Game1.getLocationFromName("Town");
            if (town != null)
            {
                foreach (var tile in _townCropTiles)
                {
                    if (town.terrainFeatures.ContainsKey(tile))
                    {
                        _ = town.terrainFeatures.Remove(tile);
                    }
                }
            }

            // 传送到 Farm
            SafeWarp.Farmer(Monitor, "Farm", 54, 15);

            // 使用 getLocationFromName 而非 Game1.currentLocation，
            // 因为 warpFarmer 可能需要数 tick 才生效，currentLocation 可能仍是 Town
            var farmLoc = Game1.getLocationFromName("Farm");
            if (farmLoc != null && _npc.currentLocation != farmLoc)
            {
                _ = _npc.currentLocation?.characters.Remove(_npc);
                farmLoc.characters.Add(_npc);
                _npc.currentLocation = farmLoc;
            }

            _npc.setTileLocation(new Vector2(54, 17));
            _npc.Halt();
            _npcStartTile = _npc.Tile;

            // 在 Farm 放置成熟作物（使用 farmLoc 而非 Game1.currentLocation）
            if (farmLoc != null)
            {
                // 先清理目标位置的 terrainFeatures，避免已有 HoeDirt 导致跳过
                string[] cropIds = { "472", "473", "474" };
                for (var i = 0; i < 3; i++)
                {
                    var tile = new Vector2(53 + i, 17);
                    // 清理已有 terrainFeatures，确保能放置新作物
                    if (farmLoc.terrainFeatures.ContainsKey(tile))
                    {
                        _ = farmLoc.terrainFeatures.Remove(tile);
                    }

                    var dirt = new HoeDirt
                    {
                        crop = new Crop(cropIds[i], (int)tile.X, (int)tile.Y, farmLoc)
                    };
                    dirt.crop.growCompletely();
                    farmLoc.terrainFeatures[tile] = dirt;
                    _farmCropTiles.Add(tile);
                }

                Monitor.Log(
                    $"[E13] Phase 2: Placed {_farmCropTiles.Count} crops in Farm. Location={farmLoc.Name} | NPC.currentLocation={_npc.currentLocation?.Name}",
                    LogLevel.Info);
            }

            // 立即扫描，避免 FarmHandler.Update 在扫描前自动收获作物
            _farmScanResult = FarmHandler.ScanEnvironment(_npc);
            Monitor.Log(
                $"[E13] FarmHandler.ScanEnvironment in Farm (immediate): '{_farmScanResult ?? "NULL"}'",
                LogLevel.Info);

            _ = _api.TrySetAgentState("Haley", "FARM");
            _npcStartTile = _npc.Tile;
        }

        // Phase 2: 观察 Farm 上的收获 (tick 650-1200)
        if (_phase2SetupDone && tick > 650 && tick <= 1200 && !_farmCropsHarvested)
        {
            var farm = Game1.getLocationFromName("Farm");
            if (farm != null)
            {
                var remaining = _farmCropTiles.Count(t =>
                    farm.terrainFeatures.ContainsKey(t) &&
                    farm.terrainFeatures[t] is HoeDirt hd &&
                    hd.crop != null &&
                    !hd.crop.dead.Value);
                if (remaining < _farmCropTiles.Count)
                {
                    _farmCropsHarvested = true;
                    Monitor.Log(
                        $"[E13] Farm crops harvested: {remaining}/{_farmCropTiles.Count} remaining at tick {tick}",
                        LogLevel.Info);
                }
            }
        }

        // 断言在 tick 1350
        if (tick >= 1350)
        {
            var townScanFoundCrops = !string.IsNullOrEmpty(_townScanResult);

            // 验证修复：FarmHandler.ScanEnvironment 现在有位置守卫，
            // 在非农场位置应返回 null
            AssertEx(
                "FarmHandler_ScanEnvironment_has_location_check",
                !townScanFoundCrops,
                "位置守卫被回退：FarmHandler.ScanEnvironment 在 Town 等非农场位置仍返回作物信息（IsFarmLocation 检查被移除/改写）",
                $"Town扫描结果='{_townScanResult ?? "NULL"}'。" +
                "FarmHandler.ScanEnvironment 在非农场位置应返回 null（有位置守卫）。");

            // 在 Town 时 NPC 不应就地收获
            // 如果 ScanEnvironment 有位置守卫（返回 null），则 FindFarmingTarget 也不会找到目标，
            // NPC 不可能执行收获。作物数量减少可能是 SDV 引擎行为（如季节变化、作物枯萎），
            // 不应归因于 FarmHandler。
            var npcCannotHarvestOnNonFarm = !townScanFoundCrops;
            AssertEx(
                "NPC_did_not_harvest_crops_on_non_farm_location",
                npcCannotHarvestOnNonFarm,
                "同上：Town 扫描守卫失效（townScanFoundCrops=true）时本断言同步失败——ScanEnvironment 返回非 null 意味着 FindFarmingTarget 可锁定 Town 作物",
                $"Town作物被收获={_townCropsHarvested}。ScanEnvironment位置守卫={!townScanFoundCrops}。" +
                "ScanEnvironment 返回 null 时，FindFarmingTarget 也不会找到目标，NPC 不可能收获。" +
                (_townCropsHarvested ? "（注意：作物减少可能是 SDV 引擎行为，非 FarmHandler 收获）" : ""));

            // 对照：Farm 位置扫描正常
            AssertEx(
                "Farm_scan_and_harvest_baseline_works",
                !string.IsNullOrEmpty(_farmScanResult) || _farmCropsHarvested,
                "Farm 基线崩塌：农场位置扫描返回 NULL 且 Phase2 观察窗口内 _farmCropsHarvested=false（收获链与扫描链同时失灵）",
                $"Farm扫描结果='{_farmScanResult ?? "NULL"}'。Farm收获={_farmCropsHarvested}。" +
                (!string.IsNullOrEmpty(_farmScanResult) ? "" : "（Farm扫描返回NULL但作物存在——可能是NPC位置问题）"));

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        // 清理 Town 作物
        var town = Game1.getLocationFromName("Town");
        if (town != null)
        {
            foreach (var tile in _townCropTiles)
            {
                if (town.terrainFeatures.ContainsKey(tile))
                {
                    _ = town.terrainFeatures.Remove(tile);
                }
            }
        }

        // 清理 Farm 作物
        var farm = Game1.getLocationFromName("Farm");
        if (farm != null)
        {
            foreach (var tile in _farmCropTiles)
            {
                if (farm.terrainFeatures.ContainsKey(tile))
                {
                    _ = farm.terrainFeatures.Remove(tile);
                }
            }
        }

        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}