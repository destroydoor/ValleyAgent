using StardewValley;

namespace ValleyAgent.UI;

/// <summary>
///     展示模式：决定 NPC 背包界面（ReadOnlyInventoryMenu）对玩家交互的响应。
///     本轮只实现 ReadOnlyDisplayMode；未来偷窃功能实现 CanTakeItems=true 的模式，
///     复用同一个菜单与 InventoryDisplayInteraction 策略管线（执行计划 E3-4"预留展示模式抽象"）。
/// </summary>
public interface IInventoryDisplayMode
{
    /// <summary>
    ///     是否允许玩家直接拿走物品。
    ///     只读展示 = false（验收：无法从界面直接拿走任何物品）；未来偷窃模式 = true。
    /// </summary>
    public bool CanTakeItems { get; }

    /// <summary>
    ///     玩家点选 NPC 物品时的回调（传入被点选物品的引用）。
    ///     E3-4b 将把回调接到还价流程（2026-08-15 步骤 2 后由 TS 对话流承担）。
    /// </summary>
    public void OnItemSelected(Item item);
}