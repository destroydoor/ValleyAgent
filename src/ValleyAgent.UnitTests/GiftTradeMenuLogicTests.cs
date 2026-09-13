using ValleyAgent.Patches;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     送礼/交易选择菜单纯逻辑测试。
///     IsGiftableHeldItem 依赖 Game1.objectData（运行时才加载），不进单测；
///     这里覆盖菜单弹出决策、交易意图文案、菜单提问文案与 ResponseKey 常量。
///
///     M3 多玩家化（2026-09-13）：菜单判定与运行时模式解耦——房客与主机同规则。
///     送礼分支走 IGiftTransport、交易分支走 IDialogueTransport，两条管道房客侧都已接通，
///     原先"房客不弹菜单"的限制（README 已知限制第 3 条）已解除。
/// </summary>
public class GiftTradeMenuLogicTests
{
    [Fact]
    public void ShouldOfferGiftTradeMenu_GiftableItem_ReturnsTrue() =>
        Assert.True(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(true));

    [Fact]
    public void ShouldOfferGiftTradeMenu_EmptyHandOrTool_ReturnsFalse()
    {
        // 空手或手持武器/工具（不可赠送）→ 不弹菜单，直接开 AI 对话
        Assert.False(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(false));
    }

    [Fact]
    public void ShouldOfferGiftTradeMenu_NoModeParameter_FarmhandNotFilteredByMode()
    {
        // M3：判定签名里不再有 isThinClient —— 房客与主机共用同一条规则。
        // 用一个真实断言钉住"没有模式过滤"这件事：签名只剩 isGiftableHeldItem 一个形参。
        var method = typeof(GiftTradeMenuLogic).GetMethod(nameof(GiftTradeMenuLogic.ShouldOfferGiftTradeMenu));
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal("isGiftableHeldItem", parameters[0].Name);
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