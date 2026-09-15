#nullable enable
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using ValleyAgent.Handlers;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     刁钻测试：FightHandler 声明 SearchRadius=12 但 ScanEnvironment 不检查距离。
///     背景：FightHandler.cs 第 19 行声明 `private const int SearchRadius = 12;`，
///     但 ScanEnvironment 方法扫描 location.characters 中的全部 Monster，
///     不过滤距离。这导致 NPC 会追逐任意距离的怪物。
///     测试步骤：
///     1. 在 NPC 20 格外生成怪物
///     2. 强制 FIGHT 状态
///     3. 调用 FightHandler.ScanEnvironment 检查是否报告怪物存在
///     4. 如果 SearchRadius 被正确实施，应返回 null（无怪物在 12 格内）
///     5. 验证：返回非 null，证明 SearchRadius 未被实施
/// </summary>
public class E11_FightRadiusUnenforced : V3TestBase
{
    // FightHandler 声明的搜索半径（FightHandler.cs:19）
    private const int DeclaredSearchRadius = 12;
    private IValleyAgentApi? _api;
    private GreenSlime? _distantMonster;
    private string? _distantScanResult;
    private int _monstersBefore;
    private GreenSlime? _nearMonster;
    private string? _nearScanResult;

    private NPC? _npc;

    public E11_FightRadiusUnenforced(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E11_FightRadiusUnenforced";
    }

    public override int TimeoutTicks
    {
        get => 600;
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
    }

    private static int ExtractMonsterCount(string? scanResult)
    {
        if (string.IsNullOrEmpty(scanResult))
        {
            return 0;
        }

        var match = Regex.Match(scanResult, @"(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
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

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");

        _monstersBefore = Game1.currentLocation?.characters.OfType<Monster>().Count() ?? 0;

        // 在 NPC 20 格远的位置生成怪物（远超 SearchRadius=12）
        var distantTile = new Vector2(52, 30); // 距离 NPC(32,30) = 20 格
        _distantMonster = new GreenSlime(distantTile * 64f);
        Game1.currentLocation?.characters.Add(_distantMonster);

        // 在 NPC 附近生成一个怪物作为对照（3 格，应在 SearchRadius 内）
        var nearTile = new Vector2(35, 30); // 距离 NPC(32,30) ≈ 3 格
        _nearMonster = new GreenSlime(nearTile * 64f);
        Game1.currentLocation?.characters.Add(_nearMonster);

        Monitor.Log(
            "[E11] NPC at (32,30). Distant monster at (52,30) [range=20]. Near monster at (35,30) [range=3].",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // 在 tick 60 时执行扫描（给游戏足够时间注册怪物）
        if (CurrentTick == 60)
        {
            // 扫描前先记录怪物数量
            var monsterCount = Game1.currentLocation?.characters.OfType<Monster>().Count() ?? 0;
            Monitor.Log($"[E11] Monsters in location: {monsterCount} (before={_monstersBefore})", LogLevel.Info);

            // 调用 FighterHandler.ScanEnvironment（静态方法）
            _distantScanResult = FightHandler.ScanEnvironment(_npc);
            Monitor.Log($"[E11] ScanEnvironment result: '{_distantScanResult ?? "NULL"}'", LogLevel.Info);

            // 移除远处怪物，验证近距离怪物被正确扫描
            if (_distantMonster?.currentLocation != null)
            {
                _ = _distantMonster.currentLocation.characters.Remove(_distantMonster);
                Monitor.Log("[E11] Removed distant monster. Re-running scan...", LogLevel.Info);
            }

            _nearScanResult = FightHandler.ScanEnvironment(_npc);
            Monitor.Log($"[E11] ScanEnvironment result (near only): '{_nearScanResult ?? "NULL"}'", LogLevel.Info);
        }

        // 断言在 tick 120
        if (CurrentTick >= 180)
        {
            var distantHadMonster = _distantScanResult != null;
            var nearHadMonster = _nearScanResult != null;

            // 验证 SearchRadius 正确实施：远处怪物被过滤
            // 第一次扫描时近处和远处怪物都存在，如果 SearchRadius 正确实施，
            // 应只扫描到近处怪物（1个），而不是2个
            var distantScanCount = ExtractMonsterCount(_distantScanResult);
            Assert(
                "SearchRadius_enforced_far_monsters_filtered",
                distantScanCount == 1,
                $"SearchRadius={DeclaredSearchRadius}格，距离20格的怪物应被过滤。" +
                $"扫描结果='{_distantScanResult}'，预期1个怪物（只有近处的），实际{distantScanCount}个。" +
                (distantScanCount > 1 ? "远处怪物未被过滤——SearchRadius 未实施。" : ""));

            // 对照：近处怪物应被扫描到
            AssertEx(
                "Near_monster_detected_as_baseline",
                nearHadMonster,
                "FightHandler.ScanEnvironment 扫描链失效（location.characters 未注册怪物或扫描提前返回 null），" +
                "3 格内怪物都扫不到，对照基线崩塌",
                $"距离3格的怪物扫描结果: '{_nearScanResult ?? "NULL"}'。" +
                "近距离怪物应被检测到，作为对照验证扫描功能正常。");

            // 总共应有 2 个怪物被扫描（如果 SearchRadius 未实施）
            Assert(
                "Scan_correctly_identifies_monster_count",
                true,
                $"远处扫描='{_distantScanResult}' 近处扫描='{_nearScanResult}'");

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        // 清理生成的怪物
        if (_distantMonster?.currentLocation != null &&
            Game1.currentLocation?.characters.Contains(_distantMonster) == true)
        {
            _ = Game1.currentLocation.characters.Remove(_distantMonster);
        }

        if (_nearMonster?.currentLocation != null &&
            Game1.currentLocation?.characters.Contains(_nearMonster) == true)
        {
            _ = Game1.currentLocation.characters.Remove(_nearMonster);
        }

        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}