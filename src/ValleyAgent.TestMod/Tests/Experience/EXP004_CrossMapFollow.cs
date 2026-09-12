#nullable enable
using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     跨地图跟随。玩家从 Farm → BusStop → Town，验证 Haley 始终跟随。
/// </summary>
[RegisteredTest(TestGroup.Experience, "跨地图跟随", "experience")]
public class EXP004_CrossMapFollow : V3TestBase
{
    // SingleHopTicks 与 AgentNavigator 内部常量一致（private，这里硬编码用于断言）。
    // 模拟旅行单跳最短 30 tick；跨图到达时长必须 ≥ SingleHopTicks 才证明走的是模拟旅行而非瞬移。
    private const int SingleHopTicks = 30;

    private IValleyAgentApi? _api;
    private int _arrivedAtBusStopTick = -1;
    private int _arrivedAtTownTick = -1;
    private NPC? _npc;
    private int _warpToBusStopTick = -1;
    private int _warpToTownTick = -1;

    public EXP004_CrossMapFollow(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP004_CrossMapFollow";
    }

    public override int TimeoutTicks
    {
        get => 15000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Experience;
    }

    public override void Setup()
    {
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
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        // 确保 NPC 与玩家同图
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

        _ = _api.TrySetAgentState("Haley", "FOLLOW");
        Monitor.Log($"[EXP004] Setup complete. NPC at {_npc.Tile} on {_npc.currentLocation?.NameOrUniqueName}.",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var npcLoc = _npc.currentLocation?.NameOrUniqueName ?? "";
        var playerLoc = Game1.player.currentLocation?.NameOrUniqueName ?? "";

        // 追踪 NPC 到达时间（首次出现在目标地图的 tick）
        if (_arrivedAtBusStopTick < 0
            && string.Equals(npcLoc, "BusStop", StringComparison.OrdinalIgnoreCase))
        {
            _arrivedAtBusStopTick = CurrentTick;
        }

        if (_arrivedAtTownTick < 0
            && string.Equals(npcLoc, "Town", StringComparison.OrdinalIgnoreCase))
        {
            _arrivedAtTownTick = CurrentTick;
        }

        // tick 120: 在 Farm 上截图 + 断言
        if (CurrentTick == 120)
        {
            CaptureScreenshot("npc_on_farm");
            Assert("npc_on_farm",
                string.Equals(npcLoc, "Farm", StringComparison.OrdinalIgnoreCase),
                $"npcLoc={npcLoc}");
            AssertVisual("npc_visible_on_farm", "",
                "从玩家视角观察：NPC 是否在 Farm 地图上可见？描述任何可能影响感知的问题（如模型缺失、闪烁、位置异常等）");
        }

        // tick 240: 玩家 warp 到 BusStop
        if (CurrentTick == 240)
        {
            SafeWarp.Farmer(Monitor, "BusStop", 6, 22);
            _warpToBusStopTick = CurrentTick;
            Monitor.Log("[EXP004] Player warped to BusStop.", LogLevel.Info);
        }

        // tick 600: 断言 NPC 跟到 BusStop
        if (CurrentTick == 600)
        {
            CaptureScreenshot("npc_on_busstop");
            Assert("npc_followed_to_busstop",
                string.Equals(npcLoc, "BusStop", StringComparison.OrdinalIgnoreCase),
                $"npcLoc={npcLoc} playerLoc={playerLoc}");
            AssertVisual("npc_followed_to_busstop", "",
                "从玩家视角观察：NPC 是否跟随玩家到达 BusStop？描述任何可能影响跟随体验的问题（如跟丢、卡住、延迟过大等）");

            // 新增断言：到达时长 ≥ SingleHopTicks（证明走模拟旅行而非瞬移）
            if (_warpToBusStopTick >= 0 && _arrivedAtBusStopTick >= 0)
            {
                var travelDuration = _arrivedAtBusStopTick - _warpToBusStopTick;
                Assert("busstop_arrival_duration_ge_single_hop",
                    travelDuration >= SingleHopTicks,
                    $"travelDuration={travelDuration} singleHopTicks={SingleHopTicks} " +
                    $"warpTick={_warpToBusStopTick} arrivalTick={_arrivedAtBusStopTick}");
            }
            else
            {
                Assert("busstop_arrival_duration_ge_single_hop", false,
                    $"warpToBusStopTick={_warpToBusStopTick} arrivedAtBusStopTick={_arrivedAtBusStopTick} " +
                    "(timing not captured)");
            }
        }

        // tick 720: 玩家 warp 到 Town
        if (CurrentTick == 720)
        {
            SafeWarp.Farmer(Monitor, "Town", 54, 68);
            _warpToTownTick = CurrentTick;
            Monitor.Log("[EXP004] Player warped to Town.", LogLevel.Info);
        }

        // tick 1200: 断言 NPC 跟到 Town
        if (CurrentTick == 1200)
        {
            CaptureScreenshot("npc_on_town");
            Assert("npc_followed_to_town",
                string.Equals(npcLoc, "Town", StringComparison.OrdinalIgnoreCase),
                $"npcLoc={npcLoc} playerLoc={playerLoc}");
            AssertVisual("npc_followed_to_town", "",
                "从玩家视角观察：NPC 是否跟随玩家到达 Town？描述任何可能影响跟随体验的问题（如跟丢、卡住、延迟过大等）");

            // 新增断言：到达时长 ≥ SingleHopTicks
            if (_warpToTownTick >= 0 && _arrivedAtTownTick >= 0)
            {
                var travelDuration = _arrivedAtTownTick - _warpToTownTick;
                Assert("town_arrival_duration_ge_single_hop",
                    travelDuration >= SingleHopTicks,
                    $"travelDuration={travelDuration} singleHopTicks={SingleHopTicks} " +
                    $"warpTick={_warpToTownTick} arrivalTick={_arrivedAtTownTick}");
            }
            else
            {
                Assert("town_arrival_duration_ge_single_hop", false,
                    $"warpToTownTick={_warpToTownTick} arrivedAtTownTick={_arrivedAtTownTick} " +
                    "(timing not captured)");
            }
        }

        return CurrentTick >= 1320;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP004] Teardown.", LogLevel.Info);
    }
}