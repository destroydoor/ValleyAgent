#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     HUD 显示正确。让 Haley 与史莱姆战斗受伤，
///     验证 NPC 血量下降，血条与 HUD 卡片在视觉上可见。
/// </summary>
[RegisteredTest(TestGroup.Experience, "HUD 显示正确", "experience")]
public class EXP010_HudDisplayCorrect : V3TestBase
{
    private IValleyAgentApi? _api;
    private int _initialHealth = -1;
    private NPC? _npc;

    public EXP010_HudDisplayCorrect(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP010_HudDisplayCorrect";
    }

    public override int TimeoutTicks
    {
        get => 4000;
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

        // 生成 1 个史莱姆
        _ = TestScenes.SpawnFightScene(Helper, Monitor, 1);

        // 记录初始血量
        _initialHealth = _api.GetNpcHealth("Haley");
        Monitor.Log($"[EXP010] Setup complete. NPC health={_initialHealth}.", LogLevel.Info);

        // 设置 FIGHT 决策
        _ = _api.TrySetAgentState("Haley", "FIGHT");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 240: 断言血量下降 + 截图 + 视觉断言
        if (CurrentTick == 240)
        {
            var currentHealth = _api.GetNpcHealth("Haley");
            var tookDamage = currentHealth >= 0 && _initialHealth >= 0 && currentHealth < _initialHealth;
            Assert("npc_took_damage", tookDamage,
                $"initial={_initialHealth} current={currentHealth}");

            CaptureScreenshot("hud_after_fight");
            AssertVisual("health_bar_visible", "",
                "从玩家视角观察：NPC 的血条是否在屏幕上可见且血量随战斗减少？描述任何可能影响战斗感知的问题（如血条缺失、未动态更新、显示错误等）");
            AssertVisual("hud_card_visible", "",
                "从玩家视角观察：HUD 卡片（NPC 状态/血量/状态机）是否在屏幕上可见？描述任何可能影响信息获取的问题（如卡片缺失、信息不更新、显示错位等）");
        }

        return CurrentTick >= 360;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP010] Teardown.", LogLevel.Info);
    }
}