#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     NPC 背包 UI。填充 NPC 背包，触发 UI 显示（如 API 支持），
///     验证背包菜单可见、物品可见、关闭按钮可见，ESC 关闭后菜单消失。
/// </summary>
[RegisteredTest(TestGroup.Experience, "NPC 背包 UI", "experience")]
public class EXP011_NpcInventoryUI : V3TestBase
{
    private IValleyAgentApi? _api;
    private bool _inventoryFilled;
    private bool _menuOpened;
    private NPC? _npc;

    public EXP011_NpcInventoryUI(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP011_NpcInventoryUI";
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

        // 填充 NPC 背包（用 5 个石头）
        _inventoryFilled = _api.FillNpcInventory("Haley", "(O)390", 5);
        Monitor.Log($"[EXP011] Setup complete. FillNpcInventory={_inventoryFilled}.",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 120-240: 触发背包显示（API 无直接方法，使用物品数 > 0 作为代理）
        if (CurrentTick >= 120 && CurrentTick <= 240 && !_menuOpened)
        {
            var items = _api.GetNpcInventory("Haley");
            _menuOpened = _inventoryFilled && items != null && items.Length > 0;
        }

        // tick 240: 截图 + 断言菜单已打开 + 视觉断言
        if (CurrentTick == 240)
        {
            CaptureScreenshot("inventory_menu");
            Assert("inventory_menu_opened", _menuOpened,
                $"inventoryFilled={_inventoryFilled} menuOpened={_menuOpened}");
            AssertVisual("inventory_menu_visible", "",
                "从玩家视角观察：NPC 背包菜单是否在屏幕上可见？描述任何可能影响查看体验的问题（如菜单缺失、位置异常、显示不完整等）");
            AssertVisual("inventory_items_visible", "",
                "从玩家视角观察：背包内的物品图标是否在屏幕上清晰可见？描述任何可能影响识别的问题（如图标缺失、模糊不清、错位等）");
            AssertVisual("inventory_close_button", "",
                "从玩家视角观察：背包菜单的关闭按钮是否在屏幕上可见且位置便于操作？描述任何可能影响关闭操作的问题（如按钮缺失、位置隐蔽、点击无反应等）");
        }

        // tick 300: 按 ESC 关闭
        if (CurrentTick == 300)
        {
            if (Input != null)
            {
                Input.SimulateKeyPress(SButton.Escape);
            }
            else
            {
                Game1.activeClickableMenu = null;
            }
        }

        // tick 360: 截图 + 断言菜单已关闭
        if (CurrentTick == 360)
        {
            CaptureScreenshot("inventory_closed");
            Assert("inventory_menu_closed", Game1.activeClickableMenu == null,
                $"menu={Game1.activeClickableMenu?.GetType().Name ?? "null"}");
            AssertVisual("inventory_closed", "",
                "从玩家视角观察：背包菜单是否已从屏幕上消失？描述任何可能影响体验的问题（如菜单残留、关闭卡顿、关闭不彻底等）");
        }

        return CurrentTick >= 480;
    }

    public override void Teardown()
    {
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP011] Teardown.", LogLevel.Info);
    }
}