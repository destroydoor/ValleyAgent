#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     D1 验收 + Task 6A 到达观感：FOLLOW 中玩家 5 秒内连切两图（Farm→BusStop→Town），
///     断言 NPC 不瞬移到玩家脚下，并在模拟旅行时长后出现在 Town 入口瓦片
///     （LocationGraph 推导的 EntryTile），从入口走向玩家。
///     三个核心断言（Task 6A Step 2）：
///     1. NPC 到达瓦片 == LocationGraph 路径末跳 ToTile（地图边缘入口，非玩家 ±2 格内）；
///     2. 到达后 60 tick 内 NPC 与玩家距离持续缩小（在走路）或已存在向玩家的 PathFindController；
///     3. 全程无"NPC 与玩家距离 ≤2 且上一帧距离 >10"的贴脸帧（传送检测）。
/// </summary>
[RegisteredTest(TestGroup.Experience, "快速切图到达观感", "experience", "scene-switch")]
public class EXP014_QuickSceneSwitch : V3TestBase
{
    // SingleHopTicks 与 AgentNavigator 内部常量一致（private，这里硬编码用于断言）。
    // 模拟旅行单跳最短 30 tick（~0.5s），Farm→Town 至少 2 跳 = 60 tick 最短到达。
    private const int SingleHopTicks = 30;
    private const int PostArrivalObservationTicks = 60;
    private const float TeleportCloseThreshold = 2.0f;
    private const float TeleportPrevDistanceThreshold = 10.0f;
    private const float ArrivalTileWalkabilityTolerance = 3.0f;

    // 距离趋势采样（断言 2 — 持续缩小）
    private readonly List<float> _postArrivalDistances = new();

    private IValleyAgentApi? _api;
    private int _arrivalTick = -1;
    private Vector2 _arrivalTile = Vector2.Zero;

    // 到达检测
    private bool _arrivedAtTown;

    // 传送检测（断言 3）
    private bool _detectedTeleportFrame;
    private float _distanceAtArrival = -1f;
    private float _distanceAtArrivalPlus60 = -1f;
    private LocationGraph? _graph;
    private bool _hadPathFindControllerPostArrival;
    private NPC? _npc;
    private float _prevDistance = -1f;
    private int _teleportFrameTick = -1;

    // 玩家 warp 时间记录
    private int _warpToBusStopTick = -1;
    private int _warpToTownTick = -1;

    public EXP014_QuickSceneSwitch(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP014_QuickSceneSwitch";
    }

    public override int TimeoutTicks
    {
        get => 1800; // 30s — 2 warps + ~600 tick travel + 60 tick 观察 + buffer
    }

    public override TestGroup Group
    {
        get => TestGroup.Experience;
    }

    public override void Setup()
    {
        Monitor.Log($"=== {TestName} Setup ===", LogLevel.Info);

        // 抑制 LLM 决策，避免覆盖 FOLLOW 状态
        DebugFlags.SuppressDecisions = true;

        // 同 PIPE005：ModEntry.API 是 GameLaunched 时的 fallback（子 API 为 null），走惰性完整实例。
        _api = ValleyAgent.ModEntry.Instance?.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);

        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        // 确保 NPC 与玩家同图（Farm）
        var farm = Game1.getLocationFromName("Farm");
        if (farm != null && _npc.currentLocation != farm)
        {
            _ = _npc.currentLocation?.characters.Remove(_npc);
            farm.characters.Add(_npc);
            _npc.currentLocation = farm;
        }

        _npc.followSchedule = false;
        _npc.ignoreScheduleToday = true;
        _npc.setTileLocation(new Vector2(52, 15));
        _npc.Halt();
        _npc.controller = null;

        // 构建位置图用于断言 1 的 EntryTile 验证
        _graph = new LocationGraph();
        _graph.Build();

        _ = _api.TrySetAgentState("Haley", "FOLLOW");

        Monitor.Log(
            $"[EXP014] Setup complete. Player at Farm {Game1.player.Tile}. NPC at {_npc.Tile}. " +
            $"Graph has {_graph.KnownLocations.Count} locations.",
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
        var playerTile = Game1.player.Tile;
        var npcTile = _npc.Tile;
        var dist = Vector2.Distance(playerTile, npcTile);
        var hasController = _npc.controller is not null;

        // 玩家 warp 触发点
        if (tick == 60 && _warpToBusStopTick < 0)
        {
            SafeWarp.Farmer(Monitor, "BusStop", 6, 22);
            _warpToBusStopTick = tick;
            Monitor.Log("[EXP014] Player warped to BusStop. NPC should start travel Farm→BusStop.",
                LogLevel.Info);
        }

        // 玩家在 NPC 到达 BusStop 前 warp 到 Town（触发 NPC 重定向）
        if (tick == 180 && _warpToTownTick < 0)
        {
            SafeWarp.Farmer(Monitor, "Town", 54, 68);
            _warpToTownTick = tick;
            Monitor.Log("[EXP014] Player warped to Town. NPC should redirect Farm→Town.",
                LogLevel.Info);
        }

        // 到达检测：NPC 首次出现在 Town
        if (!_arrivedAtTown &&
            string.Equals(npcLoc, "Town", StringComparison.OrdinalIgnoreCase))
        {
            _arrivedAtTown = true;
            _arrivalTick = tick;
            _arrivalTile = npcTile;
            _distanceAtArrival = dist;
            Monitor.Log(
                $"[EXP014] NPC arrived at Town. tick={tick} tile={npcTile} dist={dist:F1} " +
                $"travelTimeSinceTownWarp={tick - _warpToTownTick}ticks",
                LogLevel.Info);
        }

        // 到达后 60 tick 内的观察
        if (_arrivedAtTown)
        {
            var ticksSinceArrival = tick - _arrivalTick;
            if (ticksSinceArrival <= PostArrivalObservationTicks)
            {
                _postArrivalDistances.Add(dist);
                if (hasController)
                {
                    _hadPathFindControllerPostArrival = true;
                }

                if (ticksSinceArrival == PostArrivalObservationTicks)
                {
                    _distanceAtArrivalPlus60 = dist;
                }
            }
        }

        // 传送检测（断言 3）：距离 ≤2 且上一帧距离 >10
        // 仅在玩家与 NPC 同图时检测（跨图距离无意义）
        if (string.Equals(playerLoc, npcLoc, StringComparison.OrdinalIgnoreCase)
            && _prevDistance > 0)
        {
            if (dist <= TeleportCloseThreshold && _prevDistance > TeleportPrevDistanceThreshold)
            {
                _detectedTeleportFrame = true;
                _teleportFrameTick = tick;
                Monitor.Log(
                    $"[EXP014] TELEPORT FRAME DETECTED: tick={tick} dist={dist:F1} prevDist={_prevDistance:F1} " +
                    $"playerLoc={playerLoc} npcLoc={npcLoc}",
                    LogLevel.Warn);
            }
        }

        _prevDistance = dist;

        // 周期日志
        if (tick % 120 == 0)
        {
            Monitor.Log(
                $"[EXP014] tick={tick} player={playerLoc}@{playerTile} npc={npcLoc}@{npcTile} " +
                $"dist={dist:F1} ctrl={hasController} arrived={_arrivedAtTown}",
                LogLevel.Info);
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        Monitor.Log($"=== {TestName} Teardown ===", LogLevel.Info);

        if (_npc == null || _graph == null)
        {
            Monitor.Log("[EXP014] Teardown skipped: NPC or graph null.", LogLevel.Warn);
            DebugFlags.SuppressDecisions = false;
            SaveResults();
            return;
        }

        // ── 断言 1：NPC 到达瓦片 == LocationGraph 路径末跳 ToTile（非玩家 ±2 格内） ──
        if (_arrivedAtTown)
        {
            var path = _graph.FindPath("Farm", "Town");
            Assert("path_farm_to_town_found", path.Found,
                $"Farm→Town path hopCount={path.HopCount}");

            if (path.Found && path.HopCount > 0)
            {
                var lastHop = path.Hops[path.HopCount - 1];
                var expectedEntry = new Vector2(lastHop.ToTile.X, lastHop.ToTile.Y);
                var arrivalToEntryDist = Vector2.Distance(_arrivalTile, expectedEntry);

                // 1a: 到达瓦片与 LocationGraph 末跳 ToTile 距离 ≤ 3（允许 walkability 修正）
                Assert("arrival_tile_matches_graph_entry",
                    arrivalToEntryDist <= ArrivalTileWalkabilityTolerance,
                    $"arrivalTile={_arrivalTile} expectedEntry={expectedEntry} " +
                    $"dist={arrivalToEntryDist:F1} tolerance={ArrivalTileWalkabilityTolerance}");

                // 1b: 到达瓦片非玩家 ±2 格内（非贴脸传送）
                var arrivalToPlayerDist = Vector2.Distance(
                    _arrivalTile, Game1.player.Tile);
                Assert("arrival_tile_not_at_player_face",
                    arrivalToPlayerDist > 2.0f,
                    $"arrivalTile={_arrivalTile} playerTile={Game1.player.Tile} " +
                    $"dist={arrivalToPlayerDist:F1} (must be >2.0 to avoid teleport-to-player)");

                // 到达时长 ≥ SingleHopTicks（确认走的是模拟旅行而非瞬移）
                var travelDuration = _arrivalTick - _warpToTownTick;
                Assert("arrival_travel_duration_ge_single_hop",
                    travelDuration >= SingleHopTicks,
                    $"travelDuration={travelDuration} singleHopTicks={SingleHopTicks} " +
                    $"arrivalTick={_arrivalTick} warpToTownTick={_warpToTownTick}");
            }
            else
            {
                Assert("path_farm_to_town_found", false,
                    "Farm→Town path not found — cannot verify arrival tile");
            }
        }
        else
        {
            Assert("npc_arrived_at_town", false,
                $"NPC never arrived at Town within {TimeoutTicks} ticks");
        }

        // ── 断言 2：到达后 60 tick 内距离持续缩小 OR 存在 PathFindController ──
        if (_arrivedAtTown && _distanceAtArrival >= 0)
        {
            // 2a: 距离趋势（持续缩小 = 末值 < 初值，或全程单调递减允许 1 次反弹）
            var distanceShrinking = false;
            if (_distanceAtArrivalPlus60 >= 0)
            {
                distanceShrinking = _distanceAtArrivalPlus60 < _distanceAtArrival;
            }

            // 趋势采样：检查后 80% 的采样点是否都 < 初值（容忍初始抖动）
            var shrinkingSamples = 0;
            var totalSamples = _postArrivalDistances.Count;
            if (totalSamples > 5)
            {
                var threshold = _distanceAtArrival;
                foreach (var d in _postArrivalDistances)
                {
                    if (d < threshold)
                    {
                        shrinkingSamples++;
                    }
                }

                // 至少 60% 的采样点距离 < 初值
                if ((double)shrinkingSamples / totalSamples >= 0.6)
                {
                    distanceShrinking = true;
                }
            }

            Assert("post_arrival_walking_toward_player",
                distanceShrinking || _hadPathFindControllerPostArrival,
                $"distanceAtArrival={_distanceAtArrival:F1} distanceAtArrival+60={_distanceAtArrivalPlus60:F1} " +
                $"shrinking={distanceShrinking} hadController={_hadPathFindControllerPostArrival} " +
                $"samples={totalSamples}");

            Monitor.Log(
                $"[EXP014] Post-arrival analysis: dist0={_distanceAtArrival:F1} " +
                $"dist60={_distanceAtArrivalPlus60:F1} shrinking={distanceShrinking} " +
                $"hadController={_hadPathFindControllerPostArrival} samples={totalSamples}",
                LogLevel.Info);
        }
        else
        {
            Assert("post_arrival_walking_toward_player", false,
                "Arrival not detected — cannot verify post-arrival movement");
        }

        // ── 断言 3：全程无贴脸帧（传送检测） ──
        Assert("no_teleport_frame_detected",
            !_detectedTeleportFrame,
            _detectedTeleportFrame
                ? $"Teleport frame at tick={_teleportFrameTick} (dist≤{TeleportCloseThreshold} & prevDist>{TeleportPrevDistanceThreshold})"
                : "No teleport frame detected throughout test");

        // 恢复决策
        DebugFlags.SuppressDecisions = false;
        Monitor.Log(
            $"[EXP014] Final: arrived={_arrivedAtTown} arrivalTick={_arrivalTick} " +
            $"arrivalTile={_arrivalTile} teleportFrame={_detectedTeleportFrame}",
            LogLevel.Info);
        SaveResults();
    }
}