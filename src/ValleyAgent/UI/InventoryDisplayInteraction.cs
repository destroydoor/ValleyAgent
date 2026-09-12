namespace ValleyAgent.UI;

/// <summary>
///     展示模式下物品交互的纯逻辑裁决（不依赖 XNA / 游戏运行时，可脱离游戏单测）。
///     ReadOnlyInventoryMenu 的每次格子点击都调用 Decide，按结果决定放行转移 / 触发选中回调。
///     这是 E3-4 验收"无法从界面直接拿走任何物品、点选正确进入谈价"的规则侧实现：
///     被测代码 == 上线代码，守卫不是在测试里造的玩具。
/// </summary>
public static class InventoryDisplayInteraction
{
    /// <summary>点击命中的目标：NPC 背包格子 还是 玩家自己的背包格子。</summary>
    public enum Target
    {
        /// <summary>NPC 背包格（菜单上半区的 ItemsToGrabMenu）。</summary>
        NpcItems,

        /// <summary>玩家自己的背包格（菜单下半区）。</summary>
        PlayerInventory
    }

    /// <summary>
    ///     依据展示模式裁决一次点击：
    ///     <list type="bullet">
    ///         <item>NPC 物品 + 只读（CanTakeItems=false）→ 禁止转移 + 触发点选回调（进入谈价）。</item>
    ///         <item>NPC 物品 + 可拿（未来 Stealable）→ 放行转移 + 不触发点选（直接拿走）。</item>
    ///         <item>玩家背包 → 任何模式下都禁止转移（不允许把玩家物品塞进 NPC 背包）。</item>
    ///     </list>
    /// </summary>
    public static Decision Decide(IInventoryDisplayMode mode, Target target)
    {
        if (target == Target.NpcItems)
        {
            return mode.CanTakeItems
                ? new Decision(true, false)
                : new Decision(false, true);
        }

        return new Decision(false, false);
    }

    /// <summary>
    ///     一次点击的裁决结果。
    /// </summary>
    /// <param name="AllowTransfer">是否放行物品转移（true 时菜单把点击交给 base 执行真正的转移）。</param>
    /// <param name="FireSelection">是否触发展示模式的 OnItemSelected 点选回调（E3-4b 接还价）。</param>
    public readonly record struct Decision(bool AllowTransfer, bool FireSelection);
}