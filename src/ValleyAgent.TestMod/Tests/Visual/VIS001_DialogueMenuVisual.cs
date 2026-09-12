#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     对话菜单视觉外观。右键 Haley 触发对话，截图后断言对话菜单在视觉上
///     出现、位置合理、文字清晰可读。视觉断言延迟到 Kimi 离线分析。
/// </summary>
[RegisteredTest(TestGroup.Visual, "对话菜单视觉", "visual")]
public class VIS001_DialogueMenuVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS001_DialogueMenuVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS001_DialogueMenuVisual";
    }

    public override int TimeoutTicks
    {
        get => 3000;
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

        Monitor.Log($"[VIS001] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 右键 NPC 触发对话（若 Input 不可用则直接通过 API 请求）
        if (CurrentTick == 60)
        {
            var tx = (int)_npc.Tile.X;
            var ty = (int)_npc.Tile.Y;
            if (Input != null)
            {
                Input.SimulateRightClick(tx, ty);
                Monitor.Log($"[VIS001] Right-clicked NPC at tile ({tx},{ty}).", LogLevel.Info);
            }
            else
            {
                _ = _api.TryGenerateDialogue("Haley", "你好");
                Monitor.Log("[VIS001] Input null; called TryGenerateDialogue directly.",
                    LogLevel.Warn);
            }
        }

        // tick 120: 截图 + 代码断言 + 视觉断言
        if (CurrentTick == 120)
        {
            CaptureScreenshot("dialogue_menu");

            var found = _api.TryGetLastDialogue("Haley", out var response);
            Assert("dialogue_generated", found && !string.IsNullOrEmpty(response),
                $"found={found} len={response?.Length ?? 0}");

            AssertVisual("dialogue_menu_visible", "",
                "从玩家视角观察：画面中是否出现了对话菜单/聊天窗口？描述任何可能影响对话开启体验的问题（如窗口未弹出、弹出延迟、闪退等）");
            AssertVisual("dialogue_menu_position_correct", "",
                "从玩家视角观察：对话菜单的位置是否合理？描述任何可能影响操作的体验问题（如遮挡关键游戏元素、位置偏移、超出屏幕等）");
            AssertVisual("dialogue_menu_text_readable", "",
                "从玩家视角观察：对话菜单中的文字是否清晰可读？描述任何可能影响阅读体验的问题（如字体过小、模糊、颜色与背景对比度不足等）");
        }

        return CurrentTick >= 240;
    }

    public override void Teardown()
    {
        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS001] Teardown.", LogLevel.Info);
    }
}