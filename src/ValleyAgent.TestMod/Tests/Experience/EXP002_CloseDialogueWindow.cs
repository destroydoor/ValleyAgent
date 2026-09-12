#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     关闭对话窗口。打开对话后按 ESC，验证菜单已关闭且视觉上对话消失。
/// </summary>
[RegisteredTest(TestGroup.Experience, "关闭对话窗口", "experience")]
public class EXP002_CloseDialogueWindow : V3TestBase
{
    private IValleyAgentApi? _api;
    // 2026-08-20 Phase 5：菜单打开/关闭轮询状态机
    private bool _menuOpenedChecked;
    private bool _menuClosedChecked;
    private bool _menuCloseVerified;
    private int _menuCloseWaitStart;
    private NPC? _npc;

    public EXP002_CloseDialogueWindow(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP002_CloseDialogueWindow";
    }

    public override int TimeoutTicks
    {
        get => 3000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Experience;
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

        Monitor.Log($"[EXP002] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 右键 NPC 打开对话
        if (CurrentTick == 60)
        {
            var tx = (int)_npc.Tile.X;
            var ty = (int)_npc.Tile.Y;
            if (Input != null)
            {
                Input.SimulateRightClick(tx, ty);
            }
            else
            {
                _ = _api.TryGenerateDialogue("Haley", "你好");
            }
        }

        // tick 120 起轮询等待菜单打开（真实 LLM 触发对话 5-15s；固定 tick 曾致 menu=null 失败）
        if (CurrentTick >= 120 && !_menuOpenedChecked && CurrentTick % 60 == 0)
        {
            if (Game1.activeClickableMenu != null || CurrentTick >= 1200)
            {
                _menuOpenedChecked = true;
                CaptureScreenshot("dialogue_before_close");
                AssertMenuOpen("menu_opened_before_close");
            }
        }

        // 菜单打开后（或超时）按 ESC 关闭：tick 1260 起每 60 tick 尝试一次
        if (_menuOpenedChecked && !_menuClosedChecked && CurrentTick >= 1260 && CurrentTick % 60 == 0)
        {
            if (Input != null)
            {
                Input.SimulateKeyPress(SButton.Escape);
                Monitor.Log("[EXP002] ESC pressed.", LogLevel.Info);
            }
            else
            {
                Game1.activeClickableMenu = null;
                Monitor.Log("[EXP002] Input null; force-closed menu.", LogLevel.Warn);
            }

            // 等待关闭生效：tick 1320 起检查菜单关闭
            _menuCloseWaitStart = CurrentTick;
            _menuClosedChecked = true;
        }

        // 关闭断言：ESC 后轮询菜单关闭（最多 600 tick）
        if (_menuClosedChecked && !_menuCloseVerified && CurrentTick >= _menuCloseWaitStart + 60 && CurrentTick % 60 == 0)
        {
            if (Game1.activeClickableMenu == null || CurrentTick >= _menuCloseWaitStart + 600)
            {
                _menuCloseVerified = true;
                CaptureScreenshot("dialogue_after_close");
                AssertMenuClosed("menu_closed");
                AssertVisual("dialogue_closed", "",
                    "从玩家视角观察：对话窗口是否已从屏幕上消失？描述任何可能影响体验的问题（如残留、卡顿、关闭不彻底等）");
            }
        }

        // 关闭轮询完成或兜底超时（tick 2000）才结束——否则断言永远不执行
        return CurrentTick >= 360 && (_menuCloseVerified || CurrentTick >= 2000);
    }

    public override void Teardown()
    {
        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP002] Teardown.", LogLevel.Info);
    }
}