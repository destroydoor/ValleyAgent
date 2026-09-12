using ValleyAgent.Tracking;
using Xunit;

namespace ValleyAgent.UnitTests.Tracking;

/// <summary>
///     阶段 3 PlayerActionTracker 单元测试（spec §2.7 / §2.9）。
///     纯分类（<see cref="PlayerActionTracker.Classify" />）无游戏依赖可直接测；
///     采样聚合经公共 API（Sample/StartDay/GetYesterdaySummary/GetTrendDescription）验证行为。
/// </summary>
public class PlayerActionTrackerTests
{
    // ───────────────────────── 纯分类 ─────────────────────────

    [Theory]
    [InlineData("Mine", null, PlayerActivity.Mine)]
    [InlineData("MineShaft", null, PlayerActivity.Mine)]
    [InlineData("UndergroundMine", null, PlayerActivity.Mine)]
    [InlineData("VolcanoDungeon", null, PlayerActivity.Mine)]
    [InlineData("SkullCave", null, PlayerActivity.Mine)]
    public void Classify_MineLocations_ReturnsMine(string location, string? held, PlayerActivity expected)
    {
        Assert.Equal(expected, PlayerActionTracker.Classify(location, held));
    }

    [Theory]
    [InlineData("Beach", "Fishing Rod")]
    [InlineData("Town", "Training Rod")]
    [InlineData("Mountain", "Iridium Rod")]
    public void Classify_FishingRodHeld_ReturnsFish(string location, string held)
    {
        Assert.Equal(PlayerActivity.Fish, PlayerActionTracker.Classify(location, held));
    }

    [Theory]
    [InlineData("Farm")]
    [InlineData("FarmHouse")]
    [InlineData("Greenhouse")]
    [InlineData("Coop")]
    public void Classify_FarmLocations_ReturnsFarm(string location)
    {
        Assert.Equal(PlayerActivity.Farm, PlayerActionTracker.Classify(location, null));
    }

    [Theory]
    [InlineData("Forest")]
    [InlineData("Woods")]
    [InlineData("Beach")]
    [InlineData("Mountain")]
    [InlineData("Desert")]
    public void Classify_ForageLocations_ReturnsForage(string location)
    {
        Assert.Equal(PlayerActivity.Forage, PlayerActionTracker.Classify(location, null));
    }

    [Theory]
    [InlineData("Town")]
    [InlineData("Saloon")]
    [InlineData("SeedShop")]
    [InlineData("Clinic")]
    public void Classify_SocialLocations_ReturnsSocial(string location)
    {
        Assert.Equal(PlayerActivity.Social, PlayerActionTracker.Classify(location, null));
    }

    [Fact]
    public void Classify_UnknownLocation_ReturnsOther()
    {
        Assert.Equal(PlayerActivity.Other, PlayerActionTracker.Classify("CustomModMap", null));
        Assert.Equal(PlayerActivity.Other, PlayerActionTracker.Classify(null, null));
    }

    [Fact]
    public void Classify_MineWinsOverFishingRod()
    {
        // 优先级：矿洞地图 > 手持钓鱼竿（在矿洞钓鱼仍算 Mine）
        Assert.Equal(PlayerActivity.Mine, PlayerActionTracker.Classify("Mine", "Fishing Rod"));
    }

    [Fact]
    public void Classify_FishingWinsOverForageAndSocial()
    {
        // 优先级：钓鱼竿 > 农场/野外/社交地图（在沙滩钓鱼算 Fish 而非 Forage）
        Assert.Equal(PlayerActivity.Fish, PlayerActionTracker.Classify("Beach", "Fishing Rod"));
        Assert.Equal(PlayerActivity.Fish, PlayerActionTracker.Classify("Town", "Fishing Rod"));
    }

    [Fact]
    public void Classify_CaseInsensitiveLocations()
    {
        Assert.Equal(PlayerActivity.Mine, PlayerActionTracker.Classify("mine", null));
        Assert.Equal(PlayerActivity.Farm, PlayerActionTracker.Classify("FARM", null));
    }

    // ───────────────────────── 采样与日聚合（公共 API 行为） ─────────────────────────

    [Fact]
    public void GetTodaySummary_NoSamples_Empty()
    {
        var tracker = new PlayerActionTracker();

        Assert.Equal("", tracker.GetTodaySummary());
        Assert.Equal("", tracker.GetYesterdaySummary());
    }

    [Fact]
    public void StartDay_FirstDay_NoAggregation_NoThrow()
    {
        var tracker = new PlayerActionTracker();

        tracker.StartDay("Y1_spring_1");

        Assert.Equal("Y1_spring_1", tracker.CurrentDateIso);
        Assert.Equal("", tracker.GetYesterdaySummary());
        Assert.Equal("", tracker.GetTrendDescription());
    }

    [Fact]
    public void StartDay_SameDayRepeated_Idempotent()
    {
        var tracker = new PlayerActionTracker();
        tracker.StartDay("Y1_spring_1");

        tracker.StartDay("Y1_spring_1");

        Assert.Equal("Y1_spring_1", tracker.CurrentDateIso);
        Assert.Equal("", tracker.GetYesterdaySummary()); // 同日重复不聚合
    }

    [Fact]
    public void StartDay_SecondDay_AggregatesYesterdaySummary()
    {
        var tracker = new PlayerActionTracker();
        tracker.StartDay("Y1_spring_1");
        tracker.RecordSample(PlayerActivity.Other);
        tracker.RecordSample(PlayerActivity.Other);

        tracker.StartDay("Y1_spring_2");

        Assert.Equal("Y1_spring_2", tracker.CurrentDateIso);
        // 昨日两个采样均归 Other → 摘要 "other 100%"
        Assert.Equal("other 100%", tracker.GetYesterdaySummary());
        Assert.NotEqual("", tracker.GetTrendDescription()); // 有昨日数据即有趋势
    }

    [Fact]
    public void StartDay_SecondDay_ResetsTodayCounters()
    {
        var tracker = new PlayerActionTracker();
        tracker.StartDay("Y1_spring_1");
        tracker.RecordSample(PlayerActivity.Other);
        tracker.RecordSample(PlayerActivity.Other);

        tracker.StartDay("Y1_spring_2");
        tracker.RecordSample(PlayerActivity.Other); // 今日 1 个采样

        Assert.Equal("other 100%", tracker.GetTodaySummary()); // 今日桶已重置，不是 3 个采样
        Assert.Equal("other 100%", tracker.GetYesterdaySummary());
    }

    [Fact]
    public void StartDay_NoSamplesYesterday_NoSummary()
    {
        var tracker = new PlayerActionTracker();
        tracker.StartDay("Y1_spring_1");
        // 昨日无采样（0 samples）

        tracker.StartDay("Y1_spring_2");

        Assert.Equal("", tracker.GetYesterdaySummary());
        Assert.Equal("", tracker.GetTrendDescription());
    }
}
