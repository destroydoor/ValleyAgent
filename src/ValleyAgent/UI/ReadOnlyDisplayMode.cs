using System;
using StardewValley;

namespace ValleyAgent.UI;

/// <summary>
///     只读展示模式：CanTakeItems 恒为 false（验收"无法从界面直接拿走任何物品"的规则侧承诺）；
///     点选回调可空 —— E3-4a 尚未接线还价流程时点选不产生任何效果，菜单不崩。
///     E3-4b 将通过 <see cref="OnItemSelected" /> 挂接还价入口。
///     POCO，不依赖 Game1，契约可在无头单元测试中验证。
/// </summary>
public sealed class ReadOnlyDisplayMode : IInventoryDisplayMode
{
    private readonly Action<Item>? _onItemSelected;

    /// <summary>
    ///     创建只读展示模式。
    /// </summary>
    /// <param name="onItemSelected">玩家点选 NPC 物品时的回调（E3-4b 接还价；可空）。</param>
    public ReadOnlyDisplayMode(Action<Item>? onItemSelected = null)
    {
        _onItemSelected = onItemSelected;
    }

    /// <inheritdoc />
    public bool CanTakeItems
    {
        get => false;
    }

    /// <summary>
    ///     转发点选意图到构造时注入的回调；未注入回调时安全 no-op（纯展示）。
    /// </summary>
    public void OnItemSelected(Item item) => _onItemSelected?.Invoke(item);
}