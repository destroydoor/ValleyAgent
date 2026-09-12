using ValleyAgent.Patches;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     送礼/交易选择菜单纯逻辑测试。
///     IsGiftableHeldItem 依赖 Game1.objectData（运行时才加载），不进单测；
///     这里覆盖菜单弹出决策、交易意图文案、菜单提问文案与 ResponseKey 常量。
/// </summary>
public class GiftTradeMenuLogicTests
{
    [Fact]
    public void ShouldOfferGiftTradeMenu_HostWithGiftableItem_ReturnsTrue() =>
        Assert.True(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(false, true));

    [Fact]
    public void ShouldOfferGiftTradeMenu_HostEmptyHandOrTool_ReturnsFalse()
    {
        // 空手或手持武器/工具（不可赠送）→ 不弹菜单，直接开 AI 对话
        Assert.False(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(false, false));
    }

    [Fact]
    public void ShouldOfferGiftTradeMenu_ThinClient_AlwaysReturnsFalse()
    {
        // ThinClient 保持现状：即使手持可赠送物品也直接开对话，不弹菜单
        Assert.False(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(true, true));
        Assert.False(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(true, false));
    }

    [Fact]
    public void BuildTradeIntentMessage_IncludesDisplayNameAndStack()
    {
        var message = GiftTradeMenuLogic.BuildTradeIntentMessage("南瓜", 5);

        Assert.Contains("南瓜", message);
        Assert.Contains("×5", message);
        Assert.Equal("（我想卖给你 南瓜 ×5，你出个价吧）", message);
    }

    [Fact]
    public void BuildTradeIntentMessage_SingleItem_IncludesStackOfOne()
    {
        var message = GiftTradeMenuLogic.BuildTradeIntentMessage("钻石", 1);

        Assert.Equal("（我想卖给你 钻石 ×1，你出个价吧）", message);
    }

    [Fact]
    public void BuildMenuQuestion_IncludesNpcAndItemName()
    {
        var question = GiftTradeMenuLogic.BuildMenuQuestion("海莉", "南瓜");

        Assert.Contains("海莉", question);
        Assert.Contains("南瓜", question);
    }

    [Fact]
    public void ResponseKeys_AreDistinct()
    {
        // 残留回调兜底依赖三个 ResponseKey 互不相同（且不与常见原版 key 冲突）
        Assert.NotEqual(GiftTradeMenuLogic.ResponseKeyGift, GiftTradeMenuLogic.ResponseKeyTrade);
        Assert.NotEqual(GiftTradeMenuLogic.ResponseKeyGift, GiftTradeMenuLogic.ResponseKeyCancel);
        Assert.NotEqual(GiftTradeMenuLogic.ResponseKeyTrade, GiftTradeMenuLogic.ResponseKeyCancel);
    }
}