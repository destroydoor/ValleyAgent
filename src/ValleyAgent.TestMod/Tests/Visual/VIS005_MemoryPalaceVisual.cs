#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Visual;

/// <summary>
///     记忆宫殿视觉（玩家视角）。分配 Haley 并通过 API 生成对话历史，
///     然后从玩家视角尝试通过常见热键（P、M）访问记忆宫殿功能。
///     本测试不直接实例化内部菜单类型——若玩家无路径访问该功能，
///     这本身就是一个值得报告的体验问题。截图后交由 Kimi 视觉分析
///     判断玩家能否感知到记忆宫殿入口/界面。
/// </summary>
[RegisteredTest(TestGroup.Visual, "记忆宫殿视觉", "visual")]
public class VIS005_MemoryPalaceVisual : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;

    public VIS005_MemoryPalaceVisual(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "VIS005_MemoryPalaceVisual";
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

        // 通过 API 生成若干对话历史，为记忆宫殿提供数据
        _api.DialogueCooldownMs = 0;
        ClearDialogueCooldown(_api, "Haley");
        _ = _api.TryGenerateDialogue("Haley", "你好，今天天气真不错。");
        _ = _api.TryGenerateDialogue("Haley", "我喜欢在农场附近散步。");

        Monitor.Log($"[VIS005] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 模拟玩家按下常见热键 P（可能是记忆宫殿/物品栏入口）
        if (CurrentTick == 60)
        {
            Input?.SimulateKeyPress(SButton.P);
            Monitor.Log("[VIS005] Simulated P key press (tick 60).", LogLevel.Info);
        }

        // tick 75: 模拟玩家按下常见热键 M（可能是地图/记忆宫殿入口），
        // 与上一次按键间隔若干 tick 以观察界面响应
        if (CurrentTick == 75)
        {
            Input?.SimulateKeyPress(SButton.M);
            Monitor.Log("[VIS005] Simulated M key press (tick 75).", LogLevel.Info);
        }

        // tick 120: 截图 + 玩家视角视觉断言
        if (CurrentTick == 120)
        {
            CaptureScreenshot("memory_palace");

            AssertUIVisible("memory_palace_access",
                "记忆宫殿功能入口或菜单界面（玩家视角）");
            AssertVisual("memory_palace_player_experience", "",
                "从玩家视角观察：记忆宫殿功能是否对玩家可见且可访问？" +
                "描述任何体验问题（如功能缺失、入口不明显、UI混乱等）");
        }

        return CurrentTick >= 240;
    }

    public override void Teardown()
    {
        // 关闭任何可能打开的菜单，避免影响后续测试
        if (Game1.activeClickableMenu != null)
        {
            Game1.activeClickableMenu = null;
        }

        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[VIS005] Teardown.", LogLevel.Info);
    }
}