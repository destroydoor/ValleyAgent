using System;
using System.Collections.Generic;
using StardewValley;
using StardewValley.Menus;

namespace ValleyAgent.UI;

/// <summary>
///     只读 NPC 背包展示菜单：复用原版箱子/背包界面（ItemGrabMenu）。
///     能看、能点选（点选回调走 IInventoryDisplayMode.OnItemSelected，E3-4b 接还价），
///     但无论鼠标左右键都无法从界面拿走任何物品，也无法把玩家物品塞进 NPC 背包。
///     本版本 ItemGrabMenu 没有 canGrabItem 类开关、也没有 grabItemFromChest 可覆写
///     （拿取逻辑内联在 receiveLeftClick/receiveRightClick），因此只读守卫 = 覆写这两个
///     入口拦截两个格子区域（详见 docs/ideas/e34-implementation-思路.md §2.2 / §3）。
/// </summary>
public sealed class ReadOnlyInventoryMenu : ItemGrabMenu
{
    private readonly IInventoryDisplayMode _displayMode;

    /// <summary>
    ///     以只读方式展示 NPC 背包。
    /// </summary>
    /// <param name="npcItems">NPC 背包物品快照（可含 null 空槽，来自 AgentInventory.GetAllItems）。</param>
    /// <param name="title">菜单标题（如 "Abigail 的背包"）。</param>
    /// <param name="displayMode">展示模式；只读展示应传入 ReadOnlyDisplayMode。</param>
    public ReadOnlyInventoryMenu(IList<Item> npcItems, string title, IInventoryDisplayMode displayMode)
        : base(npcItems,
            false,
            true,
            static _ => false,
            null,
            title,
            null,
            false,
            true,
            true,
            true,
            false,
            0,
            null,
            -1,
            null)
    {
        _displayMode = displayMode ?? throw new ArgumentNullException(nameof(displayMode));

        // 第 3 层防线：防御性消灭任何可能原地改写 NPC 背包的按钮（整理排序 / 补满堆叠 / 调色）。
        // 即使某版本无视 showOrganizeButton=false / source=source_none 也创建了按钮，
        // 置空后点击无对象可点，从根上杜绝"整理/合并堆叠"这类对 NPC 物品的就地变更。
        organizeButton = null;
        fillStacksButton = null;
        colorPickerToggleButton = null;
        specialButton = null;
    }

    /// <inheritdoc />
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (!HandleReadOnlyClick(x, y))
        {
            base.receiveLeftClick(x, y, playSound);
        }
    }

    /// <inheritdoc />
    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        if (!HandleReadOnlyClick(x, y))
        {
            base.receiveRightClick(x, y, playSound);
        }
    }

    /// <summary>
    ///     只读裁决（第 1 层防线）：实际调用 InventoryDisplayInteraction 纯策略，保证被测代码 == 上线代码。
    ///     NPC 物品格命中 → 按策略触发点选回调；策略禁止转移时吞掉点击（base 会尝试拿走）。
    ///     玩家背包格命中 → 任何展示模式下都吞掉（不允许把玩家物品塞进 NPC 背包）。
    ///     返回 true 表示本次点击已被本菜单吞掉，调用方不应再交给 base。
    /// </summary>
    private bool HandleReadOnlyClick(int x, int y)
    {
        if (ItemsToGrabMenu != null && ItemsToGrabMenu.isWithinBounds(x, y))
        {
            var decision = InventoryDisplayInteraction.Decide(
                _displayMode, InventoryDisplayInteraction.Target.NpcItems);

            if (decision.FireSelection && ItemsToGrabMenu.getItemAt(x, y) is { } npcItem)
            {
                _displayMode.OnItemSelected(npcItem);
            }

            return !decision.AllowTransfer;
        }

        if (inventory != null && inventory.isWithinBounds(x, y))
        {
            var decision = InventoryDisplayInteraction.Decide(
                _displayMode, InventoryDisplayInteraction.Target.PlayerInventory);
            return !decision.AllowTransfer;
        }

        return false;
    }
}