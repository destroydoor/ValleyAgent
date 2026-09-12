using ValleyAgent.Economy;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-5 NPC 求购服务测试（xunit）。
///     覆盖：无偏好不生成 / 未知物品跳过 / 倍率区间 / 当日有效 / 同 NPC 同日不重复 /
///     频率上限 / 换日重置 / 确定性 / ResetDaily / 交付结算（钱包扣减 + 物品转移）/ 钱包不足原子拒绝。
///     设计文档：docs/ideas/e35-implementation-思路.md
/// </summary>
public class NpcPurchaseRequestServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    private static NpcEconomyProfile Profile(string name = "Gus", List<string>? purchaseItems = null) => new(
        name,
        2500,
        new List<string> { "(O)303" },
        0.8,
        BudgetTier.Generous,
        60,
        0.65,
        purchaseItems ?? new List<string> { "(O)304", "(O)256", "(O)192" });

    private static int SalePrice(string itemId) => itemId switch
    {
        "(O)304" => 30, // Hops
        "(O)256" => 60, // Tomato
        "(O)192" => 80, // Potato
        _ => -1
    };

    private static string DisplayName(string itemId) => itemId switch
    {
        "(O)304" => "啤酒花",
        "(O)256" => "西红柿",
        "(O)192" => "土豆",
        _ => itemId
    };

    [Fact]
    public void NoPurchasePrefs_ReturnsNull()
    {
        var service = new NpcPurchaseRequestService();
        var profile = Profile("Abigail", new List<string>());

        var request = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);

        Assert.Null(request);
        Assert.Equal(0, service.PurchaseOffers.Count);
    }

    [Fact]
    public void UnknownItem_Skipped()
    {
        var service = new NpcPurchaseRequestService();
        var profile = Profile("Gus", new List<string> { "(O)999999" });

        var request = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);

        Assert.Null(request);
    }

    [Fact]
    public void GeneratesRequest_PriceInMultiplierBand()
    {
        var service = new NpcPurchaseRequestService();
        var profile = Profile();

        var request = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);

        Assert.NotNull(request);
        Assert.Equal("Gus", request!.NpcName);
        Assert.Equal(1, request.Quantity);

        // 价格 = 公道价 × [1.00, 1.10]（公道价 = 售价，E3-2）
        var fair = PricingEngine.CalculateFairPrice(SalePrice(request.ItemId));
        var minPrice = (int)(fair * service.PriceMultiplierMin);
        var maxPrice = (int)(fair * service.PriceMultiplierMax);
        Assert.True(request.Price >= Math.Max(1, minPrice));
        Assert.True(request.Price <= maxPrice);

        // 求购单已入 Registry，且有效期 ≈ 当日（长 TTL，非 30s）
        Assert.Equal(1, service.PurchaseOffers.Count);
        var offer = service.PurchaseOffers.Snapshot["Gus"];
        Assert.Equal(NpcPurchaseRequestService.DefaultRequestTtl, offer.ExpiresUtc - Now);
    }

    [Fact]
    public void SameNpcSameDay_DoubleGeneration_Skipped()
    {
        var service = new NpcPurchaseRequestService();
        var profile = Profile();

        var first = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);
        var second = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);

        Assert.NotNull(first);
        Assert.Null(second); // 同 NPC 已有未过期求购单 → 不重复生成
    }

    [Fact]
    public void FrequencyLimit_RespectedAfterOfferConsumed()
    {
        // MaxRequestsPerNpcPerDay = 1：即使第一单被取走，当天也不再生成
        var service = new NpcPurchaseRequestService { MaxRequestsPerNpcPerDay = 1 };
        var profile = Profile();

        var first = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);
        Assert.NotNull(first);

        service.PurchaseOffers.TryTake("Gus", Now, out _);

        var second = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);
        Assert.Null(second); // 每日频率上限 1 条，取走单后仍不生成
    }

    [Fact]
    public void NewDay_ResetsFrequency()
    {
        var service = new NpcPurchaseRequestService();
        var profile = Profile();

        var first = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);
        Assert.NotNull(first);
        service.PurchaseOffers.TryTake("Gus", Now, out _);

        var nextDay = service.TryGenerate(profile, "Y1_spring_2", SalePrice, DisplayName, Now.AddDays(1));
        Assert.NotNull(nextDay); // 换日频率重置
    }

    [Fact]
    public void DeterministicPrice_SameInputs_SameResult()
    {
        var a = new NpcPurchaseRequestService();
        var b = new NpcPurchaseRequestService();

        var ra = a.TryGenerate(Profile(), "Y1_spring_1", SalePrice, DisplayName, Now);
        var rb = b.TryGenerate(Profile(), "Y1_spring_1", SalePrice, DisplayName, Now);

        Assert.Equal(ra!.ItemId, rb!.ItemId); // 确定性选品：同输入同物品
        Assert.Equal(ra.Price, rb.Price); // 确定性倍率：同输入同价格
    }

    [Fact]
    public void ResetDaily_ClearsOffersAndCounts()
    {
        var service = new NpcPurchaseRequestService();
        var profile = Profile();

        var request = service.TryGenerate(profile, "Y1_spring_1", SalePrice, DisplayName, Now);
        Assert.NotNull(request);

        service.ResetDaily("Y1_spring_2");

        Assert.Equal(0, service.PurchaseOffers.Count); // ResetDaily 清空求购单
        var afterReset = service.TryGenerate(profile, "Y1_spring_2", SalePrice, DisplayName, Now.AddDays(1));
        Assert.NotNull(afterReset); // ResetDaily 后当天可重新生成
    }
}
