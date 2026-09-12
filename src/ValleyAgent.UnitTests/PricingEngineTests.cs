using ValleyAgent.Economy;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-2 定价引擎单元测试。
///     验证：公道价 = 卖价（负数钳 0）；savvy → 浮动率单调递减（高精明区间窄）；
///     心理价区间镜像 spread；购买力上限三档 0.3/0.5/0.8；出价 = min(心理价上限, 预算上限)；
///     同一物品向不同精明度/预算档 NPC 兜售 → 不同出价。
///     设计文档：docs/ideas/e32-implementation-思路.md §2。
/// </summary>
public class PricingEngineTests
{
    private static NpcEconomyProfile Profile(double savvy, BudgetTier tier)
        => new(
            "TestNpc",
            0,
            new List<string>(),
            savvy,
            tier,
            0,
            0.5);

    [Fact]
    public void CalculateFairPrice_ReturnsSalePriceAsIs()
    {
        Assert.Equal(100, PricingEngine.CalculateFairPrice(100));
        Assert.Equal(0, PricingEngine.CalculateFairPrice(0));
    }

    [Fact]
    public void CalculateFairPrice_NegativeSalePrice_ClampsToZero() =>
        Assert.Equal(0, PricingEngine.CalculateFairPrice(-50));

    [Fact]
    public void CalculateSavvySpread_HigherSavvy_NarrowerSpread()
    {
        // spread = 0.5 − 0.45 × savvy（Max=0.5 / Min=0.05）
        Assert.Equal(0.5, PricingEngine.CalculateSavvySpread(0.0), 9);
        Assert.Equal(0.365, PricingEngine.CalculateSavvySpread(0.3), 9);
        Assert.Equal(0.14, PricingEngine.CalculateSavvySpread(0.8), 9);
        Assert.Equal(0.05, PricingEngine.CalculateSavvySpread(1.0), 9);

        // 单调递减：精明度越高浮动越窄
        Assert.True(PricingEngine.CalculateSavvySpread(0.2) > PricingEngine.CalculateSavvySpread(0.6));
    }

    [Fact]
    public void CalculateSavvySpread_OutOfRange_ClampedTo01()
    {
        Assert.Equal(0.05, PricingEngine.CalculateSavvySpread(1.5), 9);
        Assert.Equal(0.5, PricingEngine.CalculateSavvySpread(-0.3), 9);
    }

    [Fact]
    public void CalculatePsychologicalBounds_MirrorSpreadAroundFairPrice()
    {
        // 公道价 100、savvy 0.8 → spread 14% → 区间 [86, 114]
        Assert.Equal(86, PricingEngine.CalculatePsychologicalLow(100, 0.8));
        Assert.Equal(114, PricingEngine.CalculatePsychologicalHigh(100, 0.8));

        // 公道价 100、savvy 0.3 → spread 36.5% → 区间 [64, 137]（AwayFromZero 舍入）
        Assert.Equal(64, PricingEngine.CalculatePsychologicalLow(100, 0.3));
        Assert.Equal(137, PricingEngine.CalculatePsychologicalHigh(100, 0.3));
    }

    [Fact]
    public void CalculateNpcOfferPrice_DifferentSavvy_DifferentOffers()
    {
        // 同一物品公道价 100、钱包充足（1000），精明度不同 → 心理价上限不同 → 出价不同
        var lowSavvy = Profile(0.3, BudgetTier.Generous);
        var highSavvy = Profile(0.8, BudgetTier.Generous);

        Assert.NotEqual(
            PricingEngine.CalculateNpcOfferPrice(100, lowSavvy, 1000),
            PricingEngine.CalculateNpcOfferPrice(100, highSavvy, 1000));

        // 高精明出价更接近公道价：0.8 → 114，0.3 → 137
        Assert.Equal(114, PricingEngine.CalculateNpcOfferPrice(100, highSavvy, 1000));
        Assert.Equal(137, PricingEngine.CalculateNpcOfferPrice(100, lowSavvy, 1000));
    }

    [Fact]
    public void CalculateBudgetCap_ThreeTiers_RespectsRatios()
    {
        // 钱包 1000：Cautious 0.3 / Normal 0.5 / Generous 0.8
        Assert.Equal(300, PricingEngine.CalculateBudgetCap(Profile(0.5, BudgetTier.Cautious), 1000));
        Assert.Equal(500, PricingEngine.CalculateBudgetCap(Profile(0.5, BudgetTier.Normal), 1000));
        Assert.Equal(800, PricingEngine.CalculateBudgetCap(Profile(0.5, BudgetTier.Generous), 1000));
    }

    [Fact]
    public void CalculateBudgetCap_ZeroOrNegativeWallet_Zero()
    {
        Assert.Equal(0, PricingEngine.CalculateBudgetCap(Profile(0.5, BudgetTier.Normal), 0));
        Assert.Equal(0, PricingEngine.CalculateBudgetCap(Profile(0.5, BudgetTier.Normal), -100));
    }

    [Fact]
    public void CalculateNpcOfferPrice_ThinWallet_CappedByBudget()
    {
        // Clint 式：公道价 5000、savvy 0.7（心理价上限 5925）、钱包 80、Cautious → 预算 24
        var clint = Profile(0.7, BudgetTier.Cautious);
        Assert.Equal(24, PricingEngine.CalculateNpcOfferPrice(5000, clint, 80));
    }

    [Fact]
    public void CalculateNpcOfferPrice_RichWallet_BoundByPsychologicalPrice()
    {
        // 钱包厚但心理价低 → 出价被心理价上限卡住，不超心理价当冤大头
        var cautious = Profile(0.8, BudgetTier.Cautious); // 心理价上限 114
        Assert.Equal(114, PricingEngine.CalculateNpcOfferPrice(100, cautious, 1000));
    }

    [Fact]
    public void CalculateNpcOfferPrice_SameWalletDifferentTier_DifferentOffers()
    {
        // 需求 §1.4 验收：同一物品（公道价 500）、同一钱包 1000、savvy 0.1（心理价上限 728），
        // 只换预算档次 → Cautious 300 / Normal 500 / Generous 728
        Assert.Equal(300, PricingEngine.CalculateNpcOfferPrice(500, Profile(0.1, BudgetTier.Cautious), 1000));
        Assert.Equal(500, PricingEngine.CalculateNpcOfferPrice(500, Profile(0.1, BudgetTier.Normal), 1000));
        Assert.Equal(728, PricingEngine.CalculateNpcOfferPrice(500, Profile(0.1, BudgetTier.Generous), 1000));
    }

    [Fact]
    public void CalculatePsychologicalHigh_IsNpcSellPrice()
    {
        // NPC 卖给玩家时从区间上限开价（需求 §1.4），无购买力约束
        Assert.Equal(114, PricingEngine.CalculatePsychologicalHigh(100, 0.8));
        Assert.Equal(137, PricingEngine.CalculatePsychologicalHigh(100, 0.3));
    }
}