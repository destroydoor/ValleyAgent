#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     战斗血条视觉。在 Farm 上生成 1 个史莱姆并设置 FIGHT 状态，
///     录像验证 NPC 头顶血条 UI 可见且数值与代码侧 NPC.health 一致。
/// </summary>
[RegisteredTest(TestGroup.Visual, "血条视觉", "visual")]
public class VIS003_HealthBarVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS003_HealthBarVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS003_HealthBarVisual";
    }

    public override int TimeoutTicks
    {
        get => 1200;
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

        // 在玩家附近生成 1 个史莱姆
        _ = TestScenes.SpawnFightScene(Helper, Monitor, 1);
        Monitor.Log("[VIS003] Spawned 1 slime near NPC.", LogLevel.Info);

        // 设置 FIGHT 决策
        _ = _api.TrySetAgentState("Haley", "FIGHT");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 开始录像
        if (CurrentTick == 60)
        {
            Screenshot?.StartRecording(TestName, "health_bar", 3, 600);
            Monitor.Log("[VIS003] Recording started.", LogLevel.Info);
        }

        // tick 600: 停止录像 + 代码断言 + 视觉断言
        if (CurrentTick == 600)
        {
            var recordingPath = Screenshot?.StopRecording();
            Monitor.Log($"[VIS003] Recording stopped. path={recordingPath ?? "(null)"}",
                LogLevel.Info);

            var health = _api.GetNpcHealth("Haley");
            Assert("npc_has_health", health > 0, $"health={health}");

            AssertVisual("health_bar_visible", recordingPath ?? "",
                "从玩家视角观察：NPC 头顶是否在视频中出现了血条？描述任何可能影响战斗感知的问题（如血条缺失、位置偏移、显示不稳定等）");
            AssertVisual("health_bar_value_correct", recordingPath ?? "",
                "从玩家视角观察：血条数值是否与 NPC 实际血量一致？描述任何可能影响信息准确性的问题（如数值不同步、血条未更新、显示错误等）");
        }

        return CurrentTick >= 720;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS003] Teardown.", LogLevel.Info);
    }
}