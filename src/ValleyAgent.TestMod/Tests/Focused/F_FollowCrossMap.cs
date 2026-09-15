#nullable enable
using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Focused;

/// <summary>
///     Focused test: Verifies NPC can FOLLOW the player across maps with
///     entrance-to-exit realism (2026-08-02 user requirement):
///     - NPC walks to the exit before travelling (like a player), no mid-map vanish
///     - NPC arrives at the map entrance and walks to the player, no teleport-on-player
///     - NPC keeps following continuously after arrival
///     Phase timeline (600/2400/2400/2400 ticks):
///     0-600      Farm: player & NPC start together
///     600        player warps to BusStop (entry tile)
///     600-3000   NPC walks to Farm exit (~25 tiles) + travels + walks from BusStop entrance
///     assert @2800: player & NPC on BusStop, no teleport jump
///     3000       player warps to Town (entry tile)
///     3000-5400  NPC exits BusStop + travels + arrives Town entrance
///     assert @5200: player & NPC on Town, no teleport jump
///     5400       player warps back to Farm
///     5400-7800  NPC exits Town + travels (2 hops) + arrives Farm east entrance + walks to player (70,17)
///     assert @7500: player & NPC on Farm; @7700 final
/// </summary>
public class F_FollowCrossMap : V3TestBase
{
    private IValleyAgentApi? _api;

    // 同图传送检测：记录上一 tick 的 NPC 位置，同图内突变 > 40 格 = 非法瞬移
    private string _lastNpcMap = "";
    private Vector2 _lastNpcTile = Vector2.Zero;

    private NPC? _npc;
    private string _startLocation = "";
    private Vector2 _startNpcTile;

    public F_FollowCrossMap(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "F_FollowCrossMap";
    }

    public override int TimeoutTicks
    {
        get => 7800; // 130 seconds (walk-to-exit + travel + walk-from-entrance per phase)
    }

    public override TestGroup Group
    {
        get => TestGroup.Fuzzy;
    }

    /// <summary>
    ///     解析从 fromMap 到 toMap 的到达瓦片：优先读目标地图的实际 warp 数据
    ///     （TargetX/TargetY = 玩家/旅行 NPC 进图的位置），避免硬编码越界或踩水。
    /// </summary>
    private static Point GetArrivalTile(string fromMap, string toMap)
    {
        var from = Game1.getLocationFromName(fromMap);
        if (from?.warps != null)
        {
            foreach (var warp in from.warps)
            {
                if (string.Equals(warp.TargetName, toMap, StringComparison.OrdinalIgnoreCase))
                {
                    return new Point(warp.TargetX, warp.TargetY);
                }
            }
        }

        // Fallback: 无直接 warp 时用已知可走瓦片（Town→Farm 需经 BusStop，无直达 warp）
        return toMap.ToLowerInvariant() switch
        {
            "busstop" => new Point(10, 22),
            "town" => new Point(54, 30),
            "farm" => new Point(54, 15),
            _ => new Point(30, 30)
        };
    }

    /// <summary>
    ///     玩家 warp 落点：warp 到达瓦片本身就是反向 warp 的触发瓦片（SDV warp 双向），
    ///     玩家落地即被自动弹回源地图（2026-08-02 实测：BusStop (11,23) 弹回 Farm、
    ///     Town (0,54) 弹回 BusStop）。原先手工 +2 偏移，现统一走 WarpTargetGuard：
    ///     守卫会拒绝 warp 触发格并 BFS 验证入口可达性，自动修正到最近合法瓦片。
    /// </summary>
    private Point WarpLandingTile(string fromMap, string toMap)
    {
        var arrival = GetArrivalTile(fromMap, toMap);
        return WarpTargetGuard.ResolveLandingTile(Monitor, toMap, arrival.X, arrival.Y,
            $"{TestName}:{fromMap}->{toMap}");
    }

    public override void Setup()
    {
        Monitor.Log($"=== {TestName} Setup ===", LogLevel.Info);

        // 抑制 LLM 决策，避免 Mock/LLM 随机覆盖 FOLLOW 状态
        DebugFlags.SuppressDecisions = true;

        // 必须用 ValleyAgent.ModEntry.Instance.API（容器构建的真实 API）：
        // ModEntry.API 从 ModRegistry 获取的可能是 GameLaunched 时的 fallback
        // ValleyAgentApi(null,null,null)（_actionApi=null），TrySetAgentState 等会静默返回 false，
        // 导致 FOLLOW 从未真正进入（跨图跟随测试全部失败）。与 IntegrationTestBase.TryCommonSetup 一致。
        _api = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        if (_api == null)
        {
            Skip("API null");
            return;
        }

        // 强制清理真实事件残留：前序测试（F6_SceneSwitch）在 Mine warp 中间态超时，
        // 残留 Game1.eventUp=true → SDV warp 完成逻辑（Game1.cs:6239 if(!eventUp)）
        // 跳过玩家 Position 设置 → 玩家 Tile 恒 (-99,-99)，NPC 跟随目标失效，
        // 且所有基于玩家 Tile 的 dist 断言失败（全套回归时 dist=164.3/209.7 恒定）。
        // 只 Override GameEventGuard 守卫不够——必须清真实 eventUp + currentEvent。
        // 清理统一走 EventCleanup（只置空字段，不碰 onEventFinished 的 NRE 坑）。
        _ = EventCleanup.ClearActiveEvents(Monitor, $"{TestName} setup");

        // Ensure player is on Farm (70,17) — Farm 东侧开阔区（y15/y17 通道确认可走、连通）。
        // 注意：不可选农田中心（如 (30,30)）——农田周围有 NPCBarrier 树篱 + 开局 debris，
        // 玩家 warp 进去但 NPC 物理上无法穿越（真实游戏玩家也需先清理农场），
        // 导致"从入口进入"永远失败（2026-08-02 实测 bfsReachable=False 确认硬隔离）。
        // SafeWarp.Farmer 会 BFS 复核该落点，不可达时自动修正并记录。
        _ = SafeWarp.Farmer(Monitor, "Farm", 70, 17, TestName);

        // 强制无事件/节日状态（GameEventGuard 测试接口）：前序测试（F6 场景切换进 Mine 等）
        // 可能遗留半成品事件（onEventFinished 清理会抛 NullReference → 事件无法结束），
        // 导致 GameEventGuard.IsEventOrFestivalActive 恒 true → AgentNavigator 跨图旅行被阻断
        // （全套回归时 F_FollowCrossMap 失败根因）。Override 优先于真实状态，测试期间
        // 保证旅行不被事件守卫拦截。Teardown 必须调用 ResetGuardOverrides() 恢复。
        EventCleanup.SuppressEventGuards();

        // Find and allocate NPC
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");

        // 确保 NPC 与玩家在同一地图（setTileLocation 不改变 currentLocation）
        var farm = Game1.getLocationFromName("Farm");
        if (farm != null && _npc.currentLocation != farm)
        {
            _ = _npc.currentLocation?.characters.Remove(_npc);
            farm.characters.Add(_npc);
            _npc.currentLocation = farm;
        }

        // 抑制原版日程，防止游戏将 NPC 拉回 Mine 等位置
        _npc.followSchedule = false;
        _npc.ignoreScheduleToday = true;

        // Position NPC near player on Farm（东侧开阔区，避开 Cabin/农田 debris）
        _npc.setTileLocation(new Vector2(68, 17));
        _npc.Halt();
        _npc.controller = null;

        _startNpcTile = _npc.Tile;
        _startLocation = _npc.currentLocation?.NameOrUniqueName ?? "";
        _lastNpcTile = _npc.Tile;
        _lastNpcMap = _startLocation;

        // Set NPC to FOLLOW state
        _ = _api.TrySetAgentState("Haley", "FOLLOW");

        Monitor.Log(
            $"Player at Farm {Game1.player.Tile}. NPC at {_npc.Tile}. Target: Cross-map follow (walk-to-exit + walk-from-entrance).",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;
        var playerLoc = Game1.player.currentLocation?.NameOrUniqueName ?? "";
        var npcLoc = _npc.currentLocation?.NameOrUniqueName ?? "";
        var dist = Vector2.Distance(Game1.player.Tile, _npc.Tile);
        var hasController = _npc.controller is not null;
        var isSameMap = string.Equals(playerLoc, npcLoc, StringComparison.OrdinalIgnoreCase);

        // ── 同图传送检测：同图内位置突变 > 40 格 = 非法瞬移（传送贴脸） ──
        if (tick > 60 && string.Equals(_lastNpcMap, npcLoc, StringComparison.OrdinalIgnoreCase) &&
            _lastNpcTile != Vector2.Zero)
        {
            var jump = Vector2.Distance(_lastNpcTile, _npc.Tile);
            if (jump > 40f)
            {
                Assert("npc_no_teleport_jump", false,
                    $"tick={tick} jump={jump:F1} {_lastNpcTile} -> {_npc.Tile} (illegal same-map teleport)");
            }
        }

        _lastNpcTile = _npc.Tile;
        _lastNpcMap = npcLoc;

        // Log every 300 ticks
        if (tick % 300 == 0)
        {
            Monitor.Log(
                $"[F] tick={tick} player={playerLoc} npc={npcLoc} dist={dist:F1} ctrl={hasController} sameMap={isSameMap}",
                LogLevel.Info);
        }

        // ── Phase warps（落点经 WarpLandingTile → WarpTargetGuard 验证：
        //    避开反向 warp 触发格 + BFS 入口可达，SafeWarp 同步强制就位） ──
        if (tick == 600)
        {
            var tile = WarpLandingTile("Farm", "BusStop");
            _ = SafeWarp.Farmer(Monitor, "BusStop", tile.X, tile.Y, $"{TestName} phase2"); // 玩家走向 BusStop
            Monitor.Log($"[F] Phase 2: Player warped to BusStop @({tile.X},{tile.Y}). NPC walks to exit then follows.",
                LogLevel.Info);
        }

        if (tick == 3000)
        {
            var tile = WarpLandingTile("BusStop", "Town");
            _ = SafeWarp.Farmer(Monitor, "Town", tile.X, tile.Y, $"{TestName} phase3"); // 玩家走向 Town
            Monitor.Log($"[F] Phase 3: Player warped to Town @({tile.X},{tile.Y}). NPC should follow.", LogLevel.Info);
        }

        if (tick == 5400)
        {
            _ = SafeWarp.Farmer(Monitor, "Farm", 70, 17, $"{TestName} phase4"); // 玩家走回 Farm（东侧开阔区，NPC 可达）
            Monitor.Log("[F] Phase 4: Player warped back to Farm. NPC should follow.", LogLevel.Info);
        }

        // ── 分阶段地图期望断言（非空洞：每阶段末尾验证 NPC 真正跨图跟随） ──
        if (tick == 2800)
        {
            // Phase 2 end: player warped @600（~675 完成），NPC 走到 Farm 出口（~25格≈13s）+
            // 旅行 180 tick + 从 BusStop 入口走向玩家
            AssertEx("player_on_busstop", string.Equals(playerLoc, "BusStop", StringComparison.OrdinalIgnoreCase),
                "SafeWarp.Farmer 落点守卫失败或剧情事件把玩家拉走（WarpTargetGuard 拒绝落点/事件重定向），tick=2800 时玩家不在 BusStop",
                $"tick={tick} player={playerLoc} (expected BusStop)");
            Assert("npc_on_busstop", string.Equals(npcLoc, "BusStop", StringComparison.OrdinalIgnoreCase),
                $"tick={tick} npc={npcLoc} (expected BusStop)");
            Assert("npc_within_50_tiles_of_player", dist < 50f, $"tick={tick} dist={dist:F1}");
        }
        else if (tick == 5200)
        {
            // Phase 3 end: player warped @3000（~3075 完成），NPC 从 BusStop 出口出发 + 旅行 + 到达 Town
            Assert("player_on_town", string.Equals(playerLoc, "Town", StringComparison.OrdinalIgnoreCase),
                $"tick={tick} player={playerLoc} (expected Town)");
            Assert("npc_on_town", string.Equals(npcLoc, "Town", StringComparison.OrdinalIgnoreCase),
                $"tick={tick} npc={npcLoc} (expected Town)");
            Assert("npc_within_50_tiles_of_player", dist < 50f, $"tick={tick} dist={dist:F1}");
        }
        else if (tick == 7500)
        {
            // Phase 4 end: player warped @5400（~5475 完成），NPC 从 Town 出口出发 + 2 hop 旅行 +
            // 到达 Farm 东入口（~77,15 落点修正）+ 走向玩家（70,17，~8 格）
            Assert("player_on_farm", string.Equals(playerLoc, "Farm", StringComparison.OrdinalIgnoreCase),
                $"tick={tick} player={playerLoc} (expected Farm)");
            Assert("npc_on_farm", string.Equals(npcLoc, "Farm", StringComparison.OrdinalIgnoreCase),
                $"tick={tick} npc={npcLoc} (expected Farm)");
            Assert("npc_within_50_tiles_of_player", dist < 50f, $"tick={tick} dist={dist:F1}");
        }
        else if (tick == 7700)
        {
            // 最终稳态：玩家与 NPC 都在 Farm，且 NPC 已跟到玩家附近
            AssertEx("final_same_map", isSameMap,
                "FOLLOW 跨图跟随断裂（FOLLOW 缺陷族）：玩家已回到 Farm 但 NPC 仍滞留在 Town/BusStop 或旅行中途被丢下",
                $"tick={tick} player={playerLoc} npc={npcLoc}");
            Assert("final_near_player", dist < 50f, $"tick={tick} dist={dist:F1}");
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        Assert("NPC_exists_at_end", _npc != null);
        Monitor.Log(
            $"[F] Final: NPC at {_npc?.currentLocation?.NameOrUniqueName}, player at {Game1.player.currentLocation?.NameOrUniqueName}",
            LogLevel.Info);
        // 恢复决策
        DebugFlags.SuppressDecisions = false;
        // 恢复事件守卫（Setup 强制了无事件 Override，避免影响后续测试）
        EventCleanup.ResetGuardOverrides();
        SaveResults();
    }
}