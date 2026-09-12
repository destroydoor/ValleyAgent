#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     战斗动画视觉。在 Farm 上生成 2 个史莱姆，FIGHT 决策，
///     录像验证 NPC 挥剑动作与怪物受击效果可见。
/// </summary>
[RegisteredTest(TestGroup.Visual, "战斗动画视觉", "visual")]
public class VIS002_FightAnimationVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS002_FightAnimationVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS002_FightAnimationVisual";
    }

    public override int TimeoutTicks
    {
        get => 6000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Visual;
    }

    public override void Setup()
    {
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

        _npc.setTileLocation(new Vector2(54, 16));
        _npc.Halt();
        _npc.controller = null;

        // 在玩家附近生成 2 个史莱姆
        _ = TestScenes.SpawnFightScene(Helper, Monitor, 2);
        Monitor.Log("[VIS002] Spawned 2 slimes near NPC.", LogLevel.Info);

        // 设置 FIGHT 决策
        _ = _api.TrySetAgentState("Haley", "FIGHT");
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
            Screenshot?.StartRecording(TestName, "fight_animation", 3, 600);
            Monitor.Log("[VIS002] Recording started.", LogLevel.Info);
        }

        // 每 30 tick 截图（用于视觉分析的静态帧）
        if (CurrentTick > 120 && CurrentTick < 4000 && CurrentTick % 30 == 0)
        {
            CaptureScreenshot($"fight_tick_{CurrentTick}");
        }

        // tick 4000: 停止录像 + 代码断言 + 视觉断言
        if (CurrentTick == 4000)
        {
            var recordingPath = Screenshot?.StopRecording();
            Monitor.Log($"[VIS002] Recording stopped. path={recordingPath ?? "(null)"}",
                LogLevel.Info);

            var state = _api.GetAgentState("Haley");
            Assert("fight_engaged", state == "FIGHT", $"state={state}");

            AssertVisual("fight_swing_visible", recordingPath ?? "",
                "从玩家视角观察：NPC 是否在视频中挥剑攻击怪物？描述任何可能影响战斗观感的问题（如动作僵硬、挥剑缺失、动作不连贯等）");
            AssertVisual("fight_hit_effect", recordingPath ?? "",
                "从玩家视角观察：怪物是否在视频中显示受击效果？描述任何可能影响反馈感的问题（如命中无特效、反馈不明显、特效缺失等）");
        }

        return CurrentTick >= 4120;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS002] Teardown.", LogLevel.Info);
    }
}