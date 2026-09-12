using StardewValley;

namespace ValleyAgent.Patches;

/// <summary>
///     "持物品右键 Agent NPC → 送礼/交易选择菜单"的判定与文案纯逻辑。
///     游戏相关部分（Item 类型判定）只做薄封装，决策与文案均可单测。
/// </summary>
public static class GiftTradeMenuLogic
{
    /// <summary>选择菜单"送礼"选项的 ResponseKey。</summary>
    public const string ResponseKeyGift = "Gift";

    /// <summary>选择菜单"交易"选项的 ResponseKey。</summary>
    public const string ResponseKeyTrade = "Trade";

    /// <summary>选择菜单"取消"选项的 ResponseKey。</summary>
    public const string ResponseKeyCancel = "Cancel";

    /// <summary>
    ///     手持物品右键 Agent NPC 时是否弹出 送礼/交易 选择菜单。
    ///     仅 Host 模式支持：ThinClient 维持现状（手持物品也直接开 AI 对话），
    ///     因为 ThinClient 的送礼走 IGiftTransport 代理管道，菜单注入交易意图依赖本地对话状态。
    /// </summary>
    public static bool ShouldOfferGiftTradeMenu(bool isThinClient, bool isGiftableHeldItem)
        => !isThinClient && isGiftableHeldItem;

    /// <summary>
    ///     玩家手持物是否可赠送：必须是 StardewValley.Object 且原版判定 canBeGivenAsGift()
    ///     （非 bigCraftable / Furniture / Wallpaper，且 objectData 未标记不可赠送）。
    ///     武器/工具等非 Object 手持物返回 false，视为空手。
    ///     注：依赖 Game1.objectData 已加载，只能在游戏运行中调用，不进单测。
    /// </summary>
    public static bool IsGiftableHeldItem(Item? heldItem)
        => heldItem is Object obj && obj.canBeGivenAsGift();

    /// <summary>
    ///     交易意图注入消息：以玩家口吻发给 LLM，让 NPC 表态要不要、开价。
    ///     之后玩家在聊天里阶梯还价，谈妥生成 pending offer，玩家再献物品时命中 E3-3 结算。
    /// </summary>
    public static string BuildTradeIntentMessage(string itemDisplayName, int stack)
        => $"（我想卖给你 {itemDisplayName} ×{stack}，你出个价吧）";

    /// <summary>选择菜单提问文案。</summary>
    public static string BuildMenuQuestion(string npcDisplayName, string itemDisplayName)
        => $"{npcDisplayName} 注意到了你手里的 {itemDisplayName}。";
}