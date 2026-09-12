#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     文本完整性视觉。在 Farm 上分配 Haley，玩家右键 NPC 打开对话、输入文本、按 Enter 发送，
///     截图验证对话文本完整显示（无截断、无乱码）。
/// </summary>
[RegisteredTest(TestGroup.Visual, "文本完整性视觉", "visual")]
public class VIS008_TextCompleteVisual : V3TestBase
{
    private const string PlayerInput = "你好，今天的天气怎么样？";

    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS008_TextCompleteVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS008_TextCompleteVisual";
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

        Monitor.Log($"[VIS008] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 玩家右键 NPC 打开对话
        if (CurrentTick == 60)
        {
            var npcTile = _npc.Tile;
            Input?.SimulateRightClick((int)npcTile.X, (int)npcTile.Y);
            Monitor.Log($"[VIS008] Player right-clicked NPC at tile ({npcTile.X},{npcTile.Y}).", LogLevel.Info);
        }

        // tick 90: 玩家在对话输入框输入文本
        if (CurrentTick == 90)
        {
            Input?.SimulateTextInput(PlayerInput);
            Monitor.Log("[VIS008] Player typed text into dialogue input.", LogLevel.Info);
        }

        // tick 120: 玩家按 Enter 发送
        if (CurrentTick == 120)
        {
            Input?.SimulateKeyPress(SButton.Enter);
            // 安全网：确保对话请求被排队
            _ = _api.TryGenerateDialogue("Haley", PlayerInput);
            Monitor.Log("[VIS008] Player pressed Enter; safety-net TryGenerateDialogue invoked.", LogLevel.Info);
        }

        // tick 180: 截图 + 视觉断言
        if (CurrentTick == 180)
        {
            CaptureScreenshot("dialogue_text");

            AssertTextVisible("dialogue_text_complete", "Haley的回复文本");
            AssertVisual("text_no_truncation", "",
                "从玩家视角观察：对话文本是否完整可读？描述任何可能影响阅读体验的问题（如文本截断、溢出、字号过小等）");
            AssertVisual("text_encoding_correct", "",
                "从玩家视角观察：文本编码是否正常？描述任何可能影响理解的显示问题（如乱码、方块字、缺失字符等）");
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
        Monitor.Log("[VIS008] Teardown.", LogLevel.Info);
    }
}