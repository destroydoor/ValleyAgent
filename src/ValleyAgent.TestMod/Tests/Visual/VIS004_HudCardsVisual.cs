#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     HUD 卡片视觉。分配 Haley 并设置 IDLE 状态，
///     截图验证 HUD 卡片（NPC 状态/决策/心情）显示正确。
/// </summary>
[RegisteredTest(TestGroup.Visual, "HUD 卡片视觉", "visual")]
public class VIS004_HudCardsVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS004_HudCardsVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS004_HudCardsVisual";
    }

    public override int TimeoutTicks
    {
        get => 600;
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

        // 设置 IDLE 状态，确保 HUD 有稳定数据可显示
        _ = _api.TrySetAgentState("Haley", "IDLE");
        Monitor.Log($"[VIS004] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 再次确认 IDLE 状态（让 HUD 数据稳定）
        if (CurrentTick == 60)
        {
            _ = _api.TrySetAgentState("Haley", "IDLE");
            Monitor.Log("[VIS004] Re-asserted IDLE state for HUD stability.", LogLevel.Info);
        }

        // tick 120: 截图 + 代码断言 + 视觉断言
        if (CurrentTick == 120)
        {
            CaptureScreenshot("hud_cards");

            var state = _api.GetAgentState("Haley");
            var emotion = _api.GetNpcEmotion("Haley");
            Assert("hud_visible", !string.IsNullOrEmpty(state),
                $"state={state} emotion={emotion}");

            AssertUIVisible("hud_card_visible", "NPC状态HUD卡片");
            AssertVisual("hud_card_content", "",
                "从玩家视角观察：HUD 卡片上是否显示了 NPC 名称、心情、状态？描述任何可能影响信息获取的问题（如信息缺失、显示错位、文字模糊等）");
        }

        return CurrentTick >= 240;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS004] Teardown.", LogLevel.Info);
    }
}