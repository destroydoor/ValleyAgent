#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     矿洞挖掘动画视觉。传送到 Mine 场景、分配 Haley、生成 2 个矿石并设置 MINE 状态，
///     录像验证 NPC 挥镐动作与矿石碎裂效果可见。
/// </summary>
[RegisteredTest(TestGroup.Visual, "挖矿动画视觉", "visual")]
public class VIS006_MineAnimationVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS006_MineAnimationVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS006_MineAnimationVisual";
    }

    public override int TimeoutTicks
    {
        get => 4000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Visual;
    }

    public override void Setup()
    {
        DebugFlags.SuppressDecisions = true;
        // ForceMiningLocation 让 MineHandler 在任何位置都视为合法采矿点，
        // 避免 NPC 因为 "不在采矿位置" 而拒绝执行 MINE 行为。
        DebugFlags.ForceMiningLocation = true;

        // 同 PIPE005：ModEntry.API 是 GameLaunched 时的 fallback（子 API 为 null），走惰性完整实例。
        _api = ValleyAgent.ModEntry.Instance?.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        SafeWarp.Farmer(Monitor, "Mine", 10, 10);
        _ = _api.TryAllocateAgent("Haley");
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(10, 11));
        _npc.Halt();
        _npc.controller = null;

        // 在 NPC 附近生成 2 个矿石
        _ = TestScenes.SpawnMineScene(Helper, Monitor, 2);
        Monitor.Log("[VIS006] Spawned 2 rocks near NPC.", LogLevel.Info);

        // 设置 MINE 决策
        _ = _api.TrySetAgentState("Haley", "MINE");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 120: 开始录像
        if (CurrentTick == 120)
        {
            Screenshot?.StartRecording(TestName, "mine_animation", 3, 600);
            Monitor.Log("[VIS006] Recording started.", LogLevel.Info);
        }

        // tick 3000: 停止录像 + 代码断言 + 视觉断言
        if (CurrentTick == 3000)
        {
            var recordingPath = Screenshot?.StopRecording();
            Monitor.Log($"[VIS006] Recording stopped. path={recordingPath ?? "(null)"}",
                LogLevel.Info);

            var state = _api.GetAgentState("Haley");
            Assert("mine_state_set", state == "MINE", $"state={state}");

            AssertVisual("pickaxe_swing_visible", recordingPath ?? "",
                "从玩家视角观察：NPC 是否在视频中挥镐挖掘矿石？描述任何可能影响挖矿观感的问题（如动作僵硬、挥镐缺失、动作不连贯等）");
            AssertVisual("rock_break_effect", recordingPath ?? "",
                "从玩家视角观察：矿石是否在视频中显示了碎裂效果？描述任何可能影响反馈感的问题（如碎裂特效缺失、反馈不明显、矿石消失突兀等）");
        }

        return CurrentTick >= 3120;
    }

    public override void Teardown()
    {
        DebugFlags.ForceMiningLocation = false;
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS006] Teardown.", LogLevel.Info);
    }
}