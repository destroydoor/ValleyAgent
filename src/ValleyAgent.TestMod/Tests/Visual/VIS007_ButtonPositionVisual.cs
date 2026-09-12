#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     按钮位置视觉。在 Farm 上右键 Haley 触发对话，
///     截图验证对话菜单中的"发送"和"关闭"按钮位置合理且互不重叠。
/// </summary>
[RegisteredTest(TestGroup.Visual, "按钮位置视觉", "visual")]
public class VIS007_ButtonPositionVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS007_ButtonPositionVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS007_ButtonPositionVisual";
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

        _api.DialogueCooldownMs = 0;
        ClearDialogueCooldown(_api, "Haley");

        Monitor.Log($"[VIS007] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 右键 NPC 触发对话
        if (CurrentTick == 60)
        {
            var tx = (int)_npc.Tile.X;
            var ty = (int)_npc.Tile.Y;
            if (Input != null)
            {
                Input.SimulateRightClick(tx, ty);
                Monitor.Log($"[VIS007] Right-clicked NPC at tile ({tx},{ty}).", LogLevel.Info);
            }
            else
            {
                _ = _api.TryGenerateDialogue("Haley", "你好");
                Monitor.Log("[VIS007] Input null; called TryGenerateDialogue directly.",
                    LogLevel.Warn);
            }
        }

        // tick 180: 截图 + 视觉断言
        if (CurrentTick == 180)
        {
            CaptureScreenshot("buttons_in_dialogue");

            AssertButtonPosition("send_button_position", "发送按钮");
            AssertButtonPosition("close_button_position", "关闭按钮");
            AssertVisual("buttons_no_overlap", "",
                "从玩家视角观察：对话菜单中各按钮是否互不重叠？描述任何可能影响点击操作的体验问题（如按钮重叠、点击区域过小、误触等）");
        }

        return CurrentTick >= 300;
    }

    public override void Teardown()
    {
        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS007] Teardown.", LogLevel.Info);
    }
}