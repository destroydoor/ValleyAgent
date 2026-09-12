#nullable enable
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     NPC 战斗动作可见。在 Farm 上生成 3 个史莱姆，FIGHT 决策，
///     录像验证 NPC 战斗动作可见，最终怪物应被击败。
/// </summary>
[RegisteredTest(TestGroup.Experience, "NPC 战斗动作可见", "experience")]
public class EXP006_NpcFightActionVisible : V3TestBase
{
    private IValleyAgentApi? _api;
    private int _initialMonsterCount;
    private NPC? _npc;

    public EXP006_NpcFightActionVisible(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP006_NpcFightActionVisible";
    }

    public override int TimeoutTicks
    {
        get => 6000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Experience;
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

        // 在玩家附近生成 3 个史莱姆
        var monsters = TestScenes.SpawnFightScene(Helper, Monitor);
        _initialMonsterCount = monsters.Count;
        Monitor.Log($"[EXP006] Spawned {_initialMonsterCount} slimes near NPC.", LogLevel.Info);

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
            Screenshot?.StartRecording(TestName, "fight_action", 3, 600);
            Monitor.Log("[EXP006] Recording started.", LogLevel.Info);
        }

        // 每 30 tick 截图
        if (CurrentTick > 120 && CurrentTick < 4000 && CurrentTick % 30 == 0)
        {
            CaptureScreenshot($"fight_tick_{CurrentTick}");
        }

        // tick 4000: 停止录像 + 断言怪物已击败 + 血条可见
        if (CurrentTick == 4000)
        {
            var recordingPath = Screenshot?.StopRecording();
            Monitor.Log($"[EXP006] Recording stopped. path={recordingPath ?? "(null)"}", LogLevel.Info);

            var remaining = CountRemainingMonsters();
            Assert("monsters_defeated", remaining == 0,
                $"initial={_initialMonsterCount} remaining={remaining}");

            AssertVisual("fight_action_visible", recordingPath ?? "",
                "从玩家视角观察：NPC 的战斗动作（攻击/移动）是否自然可见？描述任何可能影响战斗观感的问题（如动作僵硬、移动不连贯、攻击缺失等）");
            AssertVisual("fight_closing_ritual", recordingPath ?? "",
                "从玩家视角观察：战斗结束后的收尾动作是否自然可见？描述任何可能影响收尾体验的问题（如动作突兀、无过渡、突然消失等）");

            // 截图：用于血条可见性断言
            CaptureScreenshot("fight_health_bar");
            AssertVisual("health_bar_visible", "",
                "从玩家视角观察：战斗中 NPC 的血条是否在屏幕上可见？描述任何可能影响战斗感知的问题（如血条缺失、显示不稳定、位置异常等）");
        }

        return CurrentTick >= 4120;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP006] Teardown.", LogLevel.Info);
    }

    private static int CountRemainingMonsters()
    {
        var loc = Game1.player.currentLocation;
        if (loc == null)
        {
            return -1;
        }

        return loc.characters.OfType<Monster>().Count(m => m.Health > 0);
    }
}