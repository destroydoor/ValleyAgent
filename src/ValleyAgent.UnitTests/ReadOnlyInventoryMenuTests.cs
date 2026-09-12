using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.UI;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-4a 只读背包展示 UI 单测。
///     验证：只读模式永远不允许拿走（验收"无法从界面直接拿走任何物品"的规则侧）；
///     点选回调转发正确（验收"点选进入谈价"的钩子形状）；InventoryDisplayInteraction 纯策略
///     按展示模式裁决"是否放行转移 / 是否触发选中"；未来偷窃抽象（CanTakeItems=true）的放行路径；
///     菜单与接口的契约形状（防签名漂移，镜像 AgentInventoryWalletTests 的反射断言风格）。
///     设计文档：docs/ideas/e34-implementation-思路.md §5 / §7。
/// </summary>
public class ReadOnlyInventoryMenuTests
{
    // ── ReadOnlyDisplayMode ─────────────────────────────────────────────

    [Fact]
    public void ReadOnlyDisplayMode_CanTakeItems_IsFalse()
    {
        var mode = new ReadOnlyDisplayMode();

        Assert.False(mode.CanTakeItems);
    }

    [Fact]
    public void ReadOnlyDisplayMode_OnItemSelected_ForwardsToCallback()
    {
        var item = new FakeItem("test_sword");
        Item? received = null;
        var mode = new ReadOnlyDisplayMode(i => received = i);

        mode.OnItemSelected(item);

        Assert.Same(item, received);
    }

    [Fact]
    public void ReadOnlyDisplayMode_NoCallback_DoesNotThrow()
    {
        // E3-4a 尚未接线还价流程：点选任何物品都不能崩、更不能转移
        var mode = new ReadOnlyDisplayMode();

        mode.OnItemSelected(new FakeItem("test_sword"));
    }

    // ── InventoryDisplayInteraction（"不能拿走"守卫，纯逻辑）──────────────

    [Fact]
    public void Decide_ReadOnly_NpcItemClick_NoTransfer_FiresSelection()
    {
        var mode = new ReadOnlyDisplayMode();

        var decision = InventoryDisplayInteraction.Decide(
            mode, InventoryDisplayInteraction.Target.NpcItems);

        Assert.False(decision.AllowTransfer); // 验收：只读永远不允许拿走
        Assert.True(decision.FireSelection); // 点选 → OnItemSelected（E3-4b 接还价）
    }

    [Fact]
    public void Decide_ReadOnly_PlayerInventoryClick_NoTransfer_NoSelection()
    {
        var mode = new ReadOnlyDisplayMode();

        var decision = InventoryDisplayInteraction.Decide(
            mode, InventoryDisplayInteraction.Target.PlayerInventory);

        Assert.False(decision.AllowTransfer); // 不允许把玩家物品塞进 NPC 背包
        Assert.False(decision.FireSelection); // 玩家自己的背包不触发谈价
    }

    [Fact]
    public void Decide_Stealable_NpcItemClick_AllowsTransfer_NoSelection()
    {
        // 未来偷窃抽象：同一菜单 + 同一策略，CanTakeItems=true 时翻转裁决
        var mode = new StealableDisplayModeStub();

        var decision = InventoryDisplayInteraction.Decide(
            mode, InventoryDisplayInteraction.Target.NpcItems);

        Assert.True(decision.AllowTransfer); // 放行给菜单交给 base 执行真正的拿走
        Assert.False(decision.FireSelection); // 直接拿走，不进谈价
    }

    [Fact]
    public void Decide_Stealable_PlayerInventoryClick_NoTransfer()
    {
        var mode = new StealableDisplayModeStub();

        var decision = InventoryDisplayInteraction.Decide(
            mode, InventoryDisplayInteraction.Target.PlayerInventory);

        Assert.False(decision.AllowTransfer);
        Assert.False(decision.FireSelection);
    }

    // ── 契约形状（防签名漂移）─────────────────────────────────────────────

    [Fact]
    public void IInventoryDisplayMode_Contract_Shape()
    {
        var type = typeof(IInventoryDisplayMode);

        Assert.True(type.IsInterface);

        var canTake = type.GetProperty(
            nameof(IInventoryDisplayMode.CanTakeItems), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(canTake);
        Assert.Equal(typeof(bool), canTake!.PropertyType);
        Assert.True(canTake.CanRead);

        var onSelected = type.GetMethod(
            nameof(IInventoryDisplayMode.OnItemSelected), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(onSelected);
        Assert.Equal(typeof(void), onSelected!.ReturnType);
        var parameters = onSelected.GetParameters();
        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(Item), parameter.ParameterType);
    }

    [Fact]
    public void ReadOnlyInventoryMenu_Contract_ExtendsItemGrabMenu_WithExpectedCtor()
    {
        var type = typeof(ReadOnlyInventoryMenu);

        Assert.Equal(typeof(ItemGrabMenu), type.BaseType);

        var ctor = type.GetConstructor(
            new[] { typeof(IList<Item>), typeof(string), typeof(IInventoryDisplayMode) });
        Assert.NotNull(ctor);

        // 菜单不实例化：ItemGrabMenu 构造需要 Game1.mouseCursors 纹理（脱离游戏不可用），
        // 只做类型级契约断言；行为逻辑已全部下沉到 InventoryDisplayInteraction 并被上面覆盖。
    }

    // ── 测试替身 ─────────────────────────────────────────────────────────

    /// <summary>
    ///     最小 Item 替身：Item 基类构造 IL 实测只做 Object..ctor + 字段赋值（不碰 Game1），
    ///     覆写 7 个抽象成员（含 protected abstract GetOneNew）后即可脱离游戏实例化。
    /// </summary>
    private sealed class FakeItem : Item
    {
        public FakeItem(string displayName)
        {
            DisplayName = displayName;
        }

        public override string DisplayName { get; }

        public override string TypeDefinitionId
        {
            get => "(F)test";
        }

        public override string getDescription() => "fake item for unit tests";
        public override bool isPlaceable() => false;
        public override int maximumStackSize() => 1;
        protected override Item GetOneNew() => new FakeItem(DisplayName);

        public override void drawInMenu(
            SpriteBatch spriteBatch,
            Vector2 location,
            float scaleSize,
            float transparency,
            float layerDepth,
            StackDrawType drawStackNumber,
            Color color,
            bool drawShadow)
        {
        }
    }

    /// <summary>
    ///     未来偷窃模式替身：CanTakeItems=true，OnItemSelected 不应被触发。
    /// </summary>
    private sealed class StealableDisplayModeStub : IInventoryDisplayMode
    {
        public bool CanTakeItems
        {
            get => true;
        }

        public void OnItemSelected(Item item)
        {
        }
    }
}