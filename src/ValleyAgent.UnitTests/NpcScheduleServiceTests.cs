using ValleyAgent.Services.Schedule;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E5-1 NpcScheduleService 作息表数据单元测试。
///     验证：已知 NPC 人设起床时间、起床时刻边界、未知 NPC 全局默认、夜间就寝、非法时刻、
///     自定义表注入、原版 schedule 最小 key 提取。
///     设计依据：docs/plan/2026-08-02-execution-plan.md E5-1；docs/ideas/2026-08-03-e51-schedule-data.md。
/// </summary>
public class NpcScheduleServiceTests
{
    [Fact]
    public void KnownNpcs_HavePersonaWakeTimes()
    {
        var svc = new NpcScheduleService();

        // 威利/玛妮 早起的渔夫与牧场主：6 点
        Assert.Equal(new StardewTime(600), svc.GetWakeUpTime("Willy"));
        Assert.Equal(new StardewTime(600), svc.GetWakeUpTime("Marnie"));
        // 海莉：需求 §3.4 明示 9 点前别指望
        Assert.Equal(new StardewTime(900), svc.GetWakeUpTime("Haley"));
        // 塞巴斯蒂安：夜猫子
        Assert.Equal(new StardewTime(1000), svc.GetWakeUpTime("Sebastian"));
    }

    [Fact]
    public void NpcNames_AreCaseInsensitive()
    {
        var svc = new NpcScheduleService();

        Assert.Equal(svc.GetWakeUpTime("Haley"), svc.GetWakeUpTime("haley"));
        Assert.Equal(svc.GetWakeUpTime("Haley"), svc.GetWakeUpTime("HALEY"));
        Assert.True(svc.IsAwake("hAlEy", 900));
    }

    [Fact]
    public void IsAwake_BoundaryAtAndAfterWakeThreshold()
    {
        var svc = new NpcScheduleService();

        // 海莉 9 点醒：恰在 900 已醒，900 之前未醒，900 之后醒
        Assert.True(svc.IsAwake("Haley", 900));
        Assert.False(svc.IsAwake("Haley", 859));
        Assert.True(svc.IsAwake("Haley", 901));
        // 清晨 6 点喊海莉 → 未醒（§3.4 剧本："才六点半。你最好有重要的事。"）
        Assert.False(svc.IsAwake("Haley", 600));
    }

    [Fact]
    public void IsAwake_EarlyRiser_AwakeAtSix()
    {
        var svc = new NpcScheduleService();

        Assert.True(svc.IsAwake("Willy", 600));
        Assert.True(svc.IsAwake("Marnie", 600));
    }

    [Fact]
    public void UnknownNpc_FallsBackToGlobalDefault()
    {
        var svc = new NpcScheduleService();

        // SVE / 模组新增 NPC 不在人设表 → 全局默认 600-2400
        Assert.Equal(NpcScheduleService.GlobalDefault, svc.GetSchedule("SVE_SomeNewNpc"));
        Assert.Equal(new StardewTime(600), svc.GetWakeUpTime("SVE_SomeNewNpc"));
        Assert.Equal(new StardewTime(2400), svc.GetBedtime("SVE_SomeNewNpc"));
        Assert.True(svc.IsAwake("SVE_SomeNewNpc", 700));
        Assert.False(svc.IsAwake("SVE_SomeNewNpc", 559));
    }

    [Fact]
    public void NightHours_AsleepAfterBedtime()
    {
        var svc = new NpcScheduleService();

        // 默认就寝 2400：2400 起入睡，2350（23:50，就寝前合法时刻）仍醒（半开区间 [wake, bed)）
        Assert.False(svc.IsAwake("Haley", 2400));
        Assert.True(svc.IsAwake("Haley", 2350));
        Assert.True(svc.IsAwake("Haley", 2300));
        // 夜猫子塞巴斯蒂安 2600 才睡：深夜仍醒，2600 入睡
        Assert.True(svc.IsAwake("Sebastian", 2500));
        Assert.False(svc.IsAwake("Sebastian", 2600));
        // 小孩 2100 睡
        Assert.True(svc.IsAwake("Jas", 2000));
        Assert.False(svc.IsAwake("Jas", 2100));
    }

    [Fact]
    public void InvalidTime_IsNotAwake_DoesNotThrow()
    {
        var svc = new NpcScheduleService();

        Assert.False(svc.IsAwake("Haley", 0));
        Assert.False(svc.IsAwake("Haley", 599));
        Assert.False(svc.IsAwake("Haley", 2601));
        Assert.False(svc.IsAwake("Haley", 660)); // 分钟 60 非法
    }

    [Fact]
    public void CustomTable_Injectable_UnknownFallsBackToGlobal()
    {
        var table = new Dictionary<string, NpcSchedule>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom"] = new(new StardewTime(700), new StardewTime(2000))
        };
        var svc = new NpcScheduleService(table);

        Assert.Equal(new StardewTime(700), svc.GetWakeUpTime("Custom"));
        Assert.Equal(new StardewTime(2000), svc.GetBedtime("Custom"));
        Assert.True(svc.IsAwake("Custom", 700));
        Assert.False(svc.IsAwake("Custom", 2000));
        // 不在注入表中 → 全局默认，而非内置人设表
        Assert.Equal(NpcScheduleService.GlobalDefault, svc.GetSchedule("Haley"));
    }

    [Fact]
    public void VanillaExtraction_MinScheduleKeyIsWakeUp()
    {
        var keys = new[] { 900, 600, 2200 };

        Assert.True(NpcScheduleService.TryExtractVanillaWakeUpTime(keys, out var wakeUpTime));
        Assert.Equal(new StardewTime(600), wakeUpTime);
    }

    [Fact]
    public void VanillaExtraction_EmptyOrNullKeys_Fails()
    {
        Assert.False(NpcScheduleService.TryExtractVanillaWakeUpTime(Array.Empty<int>(), out _));

        var nullKeys = (IEnumerable<int>?)null;
        Assert.False(NpcScheduleService.TryExtractVanillaWakeUpTime(nullKeys!, out _));
    }

    [Fact]
    public void StardewTime_Validation_ThrowsOnInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StardewTime(599));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StardewTime(2601));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StardewTime(660));
    }

    [Fact]
    public void StardewTime_ComparisonAndComponents()
    {
        var six = new StardewTime(600);
        var nineThirty = new StardewTime(930);

        Assert.True(nineThirty > six);
        Assert.True(six < nineThirty);
        Assert.True(six <= new StardewTime(600));
        Assert.Equal(6, six.Hours);
        Assert.Equal(0, six.Minutes);
        Assert.Equal(360, six.TotalMinutes);
        Assert.Equal(9, nineThirty.Hours);
        Assert.Equal(30, nineThirty.Minutes);
        Assert.Equal("9:30", nineThirty.ToString());
        Assert.Equal(new StardewTime(930), StardewTime.FromHoursMinutes(9, 30));
    }
}