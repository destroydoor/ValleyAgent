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
///     刁钻测试：MineHandler.ScanEnvironment 只在采矿位置扫描，
///     但 DebugFlags.ForceMiningLocation 标志只影响 Update 不传达到 ScanEnvironment。
///     背景：MineHandler.cs ScanEnvironment 检查 IsMiningLocation，
///     非采矿位置直接返回 null。ForceMiningLocation 标志只影响 Update 导航逻辑。
///     这是与 E12(Forage) 同根的对称漏洞。
///     测试步骤：
///     1. 在 Farm（非采矿位置）放置可破坏石头
///     2. 设置 DebugFlags.ForceMiningLocation = true
///     3. 调用 MineHandler.ScanEnvironment
///     4. 验证：返回 null（盲区）
///     5. 传送到 Mountain（采矿位置），放置石头
///     6. 验证：可正常扫描到石头
/// </summary>
public class E14_MineScanLocationCheck : V3TestBase
{
    private readonly List<Vector2> _farmRocks = new();
    private readonly List<Vector2> _mountainRocks = new();
    private IValleyAgentApi? _api;
    private string? _farmScanResult;
    private string? _mountainScanResult;

    private NPC? _npc;

    public E14_MineScanLocationCheck(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E14_MineScanLocationCheck";
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

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC 未找到");
            return;
        }

        _npc.setTileLocation(new Vector2(54, 17));
        _npc.Halt();
        _ = _api.TryAllocateAgent("Haley");

        // 在 Farm 放置可破坏石头
        var loc = Game1.currentLocation;
        if (loc == null)
        {
            Skip("当前场景为null");
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            var tile = new Vector2(53 + i, 17);
            if (loc.objects.ContainsKey(tile))
            {
                continue;
            }

            loc.objects[tile] = ItemRegistry.Create<SObject>("(O)343");
            _farmRocks.Add(tile);
        }

        Monitor.Log(
            $"[E14] Phase 1: Placed {_farmRocks.Count} rocks in Farm. Location={loc.Name}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // Phase 1: Farm 扫描 (tick 60)
        if (tick == 60)
        {
            DebugFlags.ForceMiningLocation = true;
            _farmScanResult = MineHandler.ScanEnvironment(_npc);
            Monitor.Log(
                $"[E14] MineHandler.ScanEnvironment in Farm (ForceMining=true): '{_farmScanResult ?? "NULL"}'",
                LogLevel.Info);
        }

        // Phase 2 (tick 80): 传送到 Mountain 做对照
        if (tick == 80)
        {
            // 清理 Farm 石头
            var farm = Game1.getLocationFromName("Farm");
            if (farm != null)
            {
                foreach (var tile in _farmRocks)
                {
                    if (farm.objects.ContainsKey(tile))
                    {
                        _ = farm.objects.Remove(tile);
                    }
                }
            }

            SafeWarp.Farmer(Monitor, "Mountain", 40, 10);
            _npc.setTileLocation(new Vector2(40, 12));
            _npc.Halt();

            Monitor.Log("[E14] Phase 2: Warped to Mountain", LogLevel.Info);
        }

        // Phase 2: Mountain 扫描 (tick 200)
        if (tick == 200)
        {
            var loc = Game1.currentLocation;
            if (loc == null)
            {
                return true;
            }

            for (var i = 0; i < 3; i++)
            {
                var tile = new Vector2(39 + i, 12);
                if (loc.objects.ContainsKey(tile))
                {
                    continue;
                }

                loc.objects[tile] = ItemRegistry.Create<SObject>("(O)343");
                _mountainRocks.Add(tile);
            }

            Monitor.Log(
                $"[E14] Placed {_mountainRocks.Count} rocks in Mountain. Location={loc.Name}",
                LogLevel.Info);

            _mountainScanResult = MineHandler.ScanEnvironment(_npc);
            Monitor.Log(
                $"[E14] MineHandler.ScanEnvironment in Mountain: '{_mountainScanResult ?? "NULL"}'",
                LogLevel.Info);
        }

        // 断言在 tick 500
        if (tick >= 500)
        {
            var farmBlind = string.IsNullOrEmpty(_farmScanResult);
            var mountainSees = !string.IsNullOrEmpty(_mountainScanResult);

            // 核心漏洞：ForceMiningLocation 不影响 ScanEnvironment
            AssertEx(
                "ForceMiningLocation_flag_affects_ScanEnvironment",
                !farmBlind,
                "ForceMiningLocation 标志未传达到 MineHandler.ScanEnvironment（IsMiningLocation 硬守卫压过标志），Farm 上放置的石头扫描结果为 NULL",
                $"ForceMiningLocation=true 但 Farm 扫描结果='{_farmScanResult ?? "NULL"}'。" +
                "如果标志正确传递到 ScanEnvironment，Farm 上应能扫描到石头。");

            // 对照
            AssertEx(
                "Mountain_scan_baseline_works",
                mountainSees,
                "采矿基线崩塌：Mountain（合法采矿位置）扫描返回 NULL——MineHandler 位置判定或石头注册失效",
                $"Mountain扫描结果='{_mountainScanResult ?? "NULL"}'");

            Assert(
                "Scan_divergence_documented",
                true,
                $"Farm(ForceMine=true)='{_farmScanResult}' vs Mountain='{_mountainScanResult}'");

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        var farm = Game1.getLocationFromName("Farm");
        if (farm != null)
        {
            foreach (var tile in _farmRocks)
            {
                if (farm.objects.ContainsKey(tile))
                {
                    _ = farm.objects.Remove(tile);
                }
            }
        }

        var mountain = Game1.getLocationFromName("Mountain");
        if (mountain != null)
        {
            foreach (var tile in _mountainRocks)
            {
                if (mountain.objects.ContainsKey(tile))
                {
                    _ = mountain.objects.Remove(tile);
                }
            }
        }

        DebugFlags.ForceMiningLocation = false;
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}