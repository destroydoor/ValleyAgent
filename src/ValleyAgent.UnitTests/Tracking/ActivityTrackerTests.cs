using ValleyAgent.Tracking;
using Xunit;

namespace ValleyAgent.UnitTests.Tracking;

/// <summary>
///     <see cref="ActivityTracker" /> 单元测试。
///     覆盖 plan Task 11 要求的 10 项核心场景 + 参数校验 + 线程安全 + 里程碑冷却。
/// </summary>
public static class ActivityTrackerTests
{
    // ───────────────────────── 辅助方法 ─────────────────────────

    private static ActivityTracker CreateWithDate(string date)
    {
        var tracker = new ActivityTracker();
        tracker.SetDate(date);
        return tracker;
    }

    // ───────────────────────── SetDate ─────────────────────────

    [Fact]
    public static void SetDate_Initializes_Current_Date()
    {
        var tracker = CreateWithDate("2026-07-26");
        var current = tracker.GetCurrentDaily();
        Assert.Equal("2026-07-26", current.Date);
        Assert.Equal(0, current.FishingMinutes);
        Assert.Empty(current.LocationsVisited);
    }

    [Fact]
    public static void SetDate_Resets_Previous_Accumulators()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordFishing(5, 30);
        tracker.SetDate("2026-07-27");
        var current = tracker.GetCurrentDaily();
        Assert.Equal("2026-07-27", current.Date);
        Assert.Equal(0, current.FishCaught);
        Assert.Equal(0, current.FishingMinutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public static void SetDate_Rejects_Null_Or_Empty(string? badDate)
    {
        var tracker = new ActivityTracker();
        Assert.Throws<ArgumentException>(() => tracker.SetDate(badDate!));
    }

    // ───────────────────────── Record 方法 ─────────────────────────

    [Fact]
    public static void RecordFishing_Accumulates_Caught_And_Minutes()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordFishing(3, 15);
        tracker.RecordFishing(4, 20);
        var current = tracker.GetCurrentDaily();
        Assert.Equal(7, current.FishCaught);
        Assert.Equal(35, current.FishingMinutes);
    }

    [Fact]
    public static void RecordFarming_Accumulates_Harvested_And_Minutes()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordFarming(10, 30);
        tracker.RecordFarming(5, 15);
        var current = tracker.GetCurrentDaily();
        Assert.Equal(15, current.CropsHarvested);
        Assert.Equal(45, current.FarmingMinutes);
    }

    [Fact]
    public static void RecordMining_Accumulates_Minutes_Ignoring_LevelsDescended()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordMining(30, 2);
        tracker.RecordMining(45); // levelsDescended 默认 0
        var current = tracker.GetCurrentDaily();
        Assert.Equal(75, current.MiningMinutes);
    }

    [Fact]
    public static void RecordForaging_Writes_ItemsForaged_And_Minutes()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordForaging(3, 10);
        tracker.RecordForaging(4, 15);
        var current = tracker.GetCurrentDaily();
        Assert.Equal(7, current.ItemsForaged);
        Assert.Equal(25, current.ForagingMinutes);
    }

    [Fact]
    public static void RecordCombat_Accumulates_Killed_And_Minutes()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordCombat(5, 10);
        tracker.RecordCombat(8, 20);
        var current = tracker.GetCurrentDaily();
        Assert.Equal(13, current.MonstersKilled);
        Assert.Equal(30, current.CombatMinutes);
    }

    [Fact]
    public static void RecordGift_Appends_Each_Gift_Without_Dedup()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordGift("Willy", "Oceanfish_6");
        tracker.RecordGift("Willy", "Oceanfish_6"); // 同 NPC 同物品，应保留
        tracker.RecordGift("Abigail", "emerald");
        var current = tracker.GetCurrentDaily();
        Assert.Equal(3, current.GiftsGiven.Count);
        Assert.Equal("Willy", current.GiftsGiven[0].To);
        Assert.Equal("Oceanfish_6", current.GiftsGiven[0].ItemId);
        Assert.Equal("Willy", current.GiftsGiven[1].To);
        Assert.Equal("Abigail", current.GiftsGiven[2].To);
    }

    [Fact]
    public static void RecordDialogue_Deduplicates_Same_Npc_Same_Day()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordDialogue("Willy");
        tracker.RecordDialogue("Willy"); // 同 NPC 同天去重
        tracker.RecordDialogue("Pierre");
        var current = tracker.GetCurrentDaily();
        Assert.Equal(new List<string> { "Willy", "Pierre" }, current.NpcsTalkedTo);
    }

    [Fact]
    public static void RecordLocationVisit_Deduplicates_Same_Location_Same_Day()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordLocationVisit("Beach");
        tracker.RecordLocationVisit("Beach"); // 同地点去重
        tracker.RecordLocationVisit("Farm");
        var current = tracker.GetCurrentDaily();
        Assert.Equal(new List<string> { "Beach", "Farm" }, current.LocationsVisited);
    }

    // ───────────────────────── 参数校验 ─────────────────────────

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public static void RecordFishing_Rejects_Negative(int caught, int minutes)
    {
        var tracker = CreateWithDate("2026-07-26");
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordFishing(caught, minutes));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public static void RecordMining_Rejects_Negative(int minutes, int levelsDescended)
    {
        var tracker = CreateWithDate("2026-07-26");
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordMining(minutes, levelsDescended));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public static void RecordGift_Rejects_Empty_Npc(string? badNpc)
    {
        var tracker = CreateWithDate("2026-07-26");
        Assert.Throws<ArgumentException>(() => tracker.RecordGift(badNpc!, "item"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public static void RecordDialogue_Rejects_Empty_Npc(string? badNpc)
    {
        var tracker = CreateWithDate("2026-07-26");
        Assert.Throws<ArgumentException>(() => tracker.RecordDialogue(badNpc!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public static void RecordLocationVisit_Rejects_Empty_Location(string? badLocation)
    {
        var tracker = CreateWithDate("2026-07-26");
        Assert.Throws<ArgumentException>(() => tracker.RecordLocationVisit(badLocation!));
    }

    // ───────────────────────── GetCurrentDaily 深拷贝 ─────────────────────────

    [Fact]
    public static void GetCurrentDaily_Returns_Deep_Copy()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordFishing(5, 30);
        tracker.RecordLocationVisit("Beach");
        var snapshot = tracker.GetCurrentDaily();
        snapshot.FishCaught = 999;
        snapshot.LocationsVisited.Add("Modified");
        // 内部状态不应被外部修改影响
        var current = tracker.GetCurrentDaily();
        Assert.Equal(5, current.FishCaught);
        Assert.Equal(new List<string> { "Beach" }, current.LocationsVisited);
    }

    // ───────────────────────── FinalizeDay ─────────────────────────

    [Fact]
    public static void FinalizeDay_Returns_Snapshot_And_Resets_Current()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordFishing(5, 30);
        tracker.RecordDialogue("Willy");
        var snapshot = tracker.FinalizeDay();
        Assert.Equal("2026-07-26", snapshot.Date);
        Assert.Equal(5, snapshot.FishCaught);
        Assert.Equal(30, snapshot.FishingMinutes);
        Assert.Equal(new List<string> { "Willy" }, snapshot.NpcsTalkedTo);

        // _current 被重置
        var current = tracker.GetCurrentDaily();
        Assert.Equal(string.Empty, current.Date);
        Assert.Equal(0, current.FishCaught);
        Assert.Empty(current.NpcsTalkedTo);
    }

    [Fact]
    public static void FinalizeDay_Stores_History_For_Milestone_Check()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordFishing(5, 35);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordFishing(6, 38);
        tracker.FinalizeDay();

        // _history 中应有 3 条记录
        var milestones = tracker.CheckMilestones();
        Assert.Contains(milestones, m => m.Type == MilestoneType.FishingStreak);
    }

    [Fact]
    public static void FinalizeDay_Returned_Snapshot_Is_Independent_From_Internal()
    {
        var tracker = CreateWithDate("2026-07-26");
        tracker.RecordGift("Willy", "Oceanfish_6");
        var snapshot = tracker.FinalizeDay();
        snapshot.GiftsGiven[0].To = "Modified";
        // 内部 _history 不应受影响（深拷贝）
        var milestones = tracker.CheckMilestones();
        Assert.DoesNotContain(milestones, m => m.Type == MilestoneType.SameGiftRepeated);
    }

    // ───────────────────────── 里程碑：连续活动 ─────────────────────────

    [Fact]
    public static void Milestone_FishingStreak_Triggers_After_3_Days()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordFishing(5, 35);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordFishing(6, 38);
        tracker.FinalizeDay();

        var milestones = tracker.CheckMilestones();
        var streak = Assert.Single(milestones);
        Assert.Equal(MilestoneType.FishingStreak, streak.Type);
        Assert.Contains("连续 3 天", streak.Description);
        Assert.Contains("30 分钟", streak.Description);
        Assert.False(string.IsNullOrEmpty(streak.DetectedAt));
    }

    [Fact]
    public static void Milestone_FishingStreak_Does_Not_Trigger_When_Below_Threshold()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordFishing(5, 20); // 不足 30 分钟
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordFishing(6, 35);
        tracker.FinalizeDay();

        // 中间一天不满足阈值，连续性中断
        var milestones = tracker.CheckMilestones();
        Assert.DoesNotContain(milestones, m => m.Type == MilestoneType.FishingStreak);
    }

    [Fact]
    public static void Milestone_FishingStreak_Does_Not_Trigger_With_Only_2_Days()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-25");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordFishing(6, 35);
        tracker.FinalizeDay();

        var milestones = tracker.CheckMilestones();
        Assert.DoesNotContain(milestones, m => m.Type == MilestoneType.FishingStreak);
    }

    [Fact]
    public static void Milestone_MiningStreak_Triggers_After_3_Days()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordMining(65);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordMining(80);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordMining(70);
        tracker.FinalizeDay();

        var milestones = tracker.CheckMilestones();
        Assert.Contains(milestones, m => m.Type == MilestoneType.MiningStreak);
    }

    [Fact]
    public static void Milestone_FarmingStreak_Triggers_After_3_Days()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordFarming(5, 35);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordFarming(6, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordFarming(4, 30);
        tracker.FinalizeDay();

        var milestones = tracker.CheckMilestones();
        Assert.Contains(milestones, m => m.Type == MilestoneType.FarmingStreak);
    }

    // ───────────────────────── 里程碑：送礼/对话 ─────────────────────────

    [Fact]
    public static void Milestone_SameGiftRepeated_Triggers_After_3_Times()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordGift("Willy", "Oceanfish_6");
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordGift("Willy", "Oceanfish_6");
        tracker.RecordGift("Pierre", "corn");
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordGift("Willy", "Oceanfish_6");
        tracker.FinalizeDay();

        var milestones = tracker.CheckMilestones();
        var gift = Assert.Single(milestones, m => m.Type == MilestoneType.SameGiftRepeated);
        Assert.Contains("Willy", gift.Description);
        Assert.Contains("Oceanfish_6", gift.Description);
        Assert.Contains("3 次", gift.Description);
    }

    [Fact]
    public static void Milestone_SameGiftRepeated_Does_Not_Trigger_Below_3_Times()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-25");
        tracker.RecordGift("Willy", "Oceanfish_6");
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordGift("Willy", "Oceanfish_6");
        tracker.FinalizeDay();

        // 累计 2 次，未达 SameGiftRepeatMinCount=3，不触发
        var milestones = tracker.CheckMilestones();
        Assert.DoesNotContain(milestones, m => m.Type == MilestoneType.SameGiftRepeated);
    }

    [Fact]
    public static void Milestone_DialogueCount_Triggers_After_10_Times()
    {
        var tracker = new ActivityTracker();
        // 每天 1 次对话，10 天达成
        for (var dayOffset = 16; dayOffset <= 25; dayOffset++)
        {
            var date = $"2026-07-{dayOffset:D2}";
            tracker.SetDate(date);
            tracker.RecordDialogue("Willy");
            tracker.FinalizeDay();
        }

        var milestones = tracker.CheckMilestones();
        var dialogue = Assert.Single(milestones, m => m.Type == MilestoneType.DialogueCount);
        Assert.Contains("Willy", dialogue.Description);
        Assert.Contains("10 次", dialogue.Description);
    }

    [Fact]
    public static void Milestone_DialogueCount_Does_Not_Trigger_Below_10_Times()
    {
        var tracker = new ActivityTracker();
        for (var dayOffset = 18; dayOffset <= 25; dayOffset++)
        {
            var date = $"2026-07-{dayOffset:D2}";
            tracker.SetDate(date);
            tracker.RecordDialogue("Willy");
            tracker.FinalizeDay();
        }

        // 累计 8 次
        var milestones = tracker.CheckMilestones();
        Assert.DoesNotContain(milestones, m => m.Type == MilestoneType.DialogueCount);
    }

    // ───────────────────────── 里程碑冷却 ─────────────────────────

    [Fact]
    public static void Milestone_Cooldown_Prevents_Same_Type_Within_7_Days()
    {
        var tracker = new ActivityTracker();
        tracker.SetDate("2026-07-24");
        tracker.RecordFishing(5, 35);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-25");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-26");
        tracker.RecordFishing(6, 38);
        tracker.FinalizeDay();

        // 第一次触发
        var firstRun = tracker.CheckMilestones();
        Assert.Contains(firstRun, m => m.Type == MilestoneType.FishingStreak);

        // 后续 3 天继续钓鱼
        tracker.SetDate("2026-07-27");
        tracker.RecordFishing(8, 45);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-28");
        tracker.RecordFishing(9, 50);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-29");
        tracker.RecordFishing(10, 60);
        tracker.FinalizeDay();

        // 7 天内同类型不应再次触发
        var secondRun = tracker.CheckMilestones();
        Assert.DoesNotContain(secondRun, m => m.Type == MilestoneType.FishingStreak);
    }

    [Fact]
    public static void Milestone_Cooldown_Expires_After_7_Days()
    {
        var tracker = new ActivityTracker();
        // Day 1-3: 触发
        tracker.SetDate("2026-07-01");
        tracker.RecordFishing(5, 35);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-02");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-03");
        tracker.RecordFishing(6, 38);
        tracker.FinalizeDay();

        var firstRun = tracker.CheckMilestones();
        Assert.Contains(firstRun, m => m.Type == MilestoneType.FishingStreak);

        // Day 4-9 中断，避开连续活动
        for (var day = 4; day <= 9; day++)
        {
            tracker.SetDate($"2026-07-{day:D2}");
            tracker.RecordFishing(0, 0); // 0 钓鱼
            tracker.FinalizeDay();
        }

        // Day 10-12: 再次连续 3 天，已过 7 天冷却
        tracker.SetDate("2026-07-10");
        tracker.RecordFishing(5, 35);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-11");
        tracker.RecordFishing(7, 40);
        tracker.FinalizeDay();

        tracker.SetDate("2026-07-12");
        tracker.RecordFishing(6, 38);
        tracker.FinalizeDay();

        // 第 3 天触发后历史已包含 7+ 天的钓鱼记录，最新 3 天连续，应再次触发
        // 但由于冷却判断按日期差 < 7 天，2026-07-03 vs 2026-07-12 = 9 天 > 7，应该触发
        var secondRun = tracker.CheckMilestones();
        Assert.Contains(secondRun, m => m.Type == MilestoneType.FishingStreak);
    }

    // ───────────────────────── 里程碑：综合 ─────────────────────────

    [Fact]
    public static void CheckMilestones_Returns_Empty_When_No_History()
    {
        var tracker = new ActivityTracker();
        var milestones = tracker.CheckMilestones();
        Assert.Empty(milestones);
    }

    [Fact]
    public static void CheckMilestones_Returns_Multiple_Types_When_All_Conditions_Met()
    {
        var tracker = new ActivityTracker();
        // 第一阶段：钓鱼连续 3 天 + 同 NPC 同礼物 3 次 + 同 NPC 对话累计 10 次
        for (var dayOffset = 1; dayOffset <= 10; dayOffset++)
        {
            var date = $"2026-07-{dayOffset:D2}";
            tracker.SetDate(date);
            tracker.RecordFishing(5, 35); // 钓鱼连续
            tracker.RecordDialogue("Willy");
            if (dayOffset <= 3)
            {
                tracker.RecordGift("Willy", "Oceanfish_6");
            }

            tracker.FinalizeDay();
        }

        var milestones = tracker.CheckMilestones();
        Assert.Contains(milestones, m => m.Type == MilestoneType.FishingStreak);
        Assert.Contains(milestones, m => m.Type == MilestoneType.SameGiftRepeated);
        Assert.Contains(milestones, m => m.Type == MilestoneType.DialogueCount);
    }

    // ───────────────────────── 线程安全 ─────────────────────────

    [Fact]
    public static void Concurrent_Record_Calls_Do_Not_Corrupt_State()
    {
        var tracker = CreateWithDate("2026-07-26");
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };

        Parallel.For(0L, 1000, options, i =>
        {
            // 确定性输入：用 i 的低位决定操作类型和数值，避免使用不安全的 Random。
            var op = (int)(i % 5);
            var value = (int)(i % 10);
            switch (op)
            {
                case 0:
                    tracker.RecordFishing(value, value);
                    break;
                case 1:
                    tracker.RecordFarming(value, value);
                    break;
                case 2:
                    tracker.RecordGift("Willy", "Oceanfish_6");
                    break;
                case 3:
                    tracker.RecordDialogue("Willy");
                    break;
                case 4:
                    tracker.RecordLocationVisit("Beach");
                    break;
            }
        });

        var current = tracker.GetCurrentDaily();
        Assert.Equal("2026-07-26", current.Date);
        // 1000 次操作不应抛异常；NpcsTalkedTo 和 LocationsVisited 应去重为单元素
        Assert.Equal(new List<string> { "Willy" }, current.NpcsTalkedTo);
        Assert.Equal(new List<string> { "Beach" }, current.LocationsVisited);
        // 礼物次数应等于 case 2 触发次数
        var expectedGiftCount = Enumerable.Range(0, 1000).Count(i => i % 5 == 2);
        Assert.Equal(expectedGiftCount, current.GiftsGiven.Count);
    }

    [Fact]
    public static void Concurrent_Finalize_And_Record_Do_Not_Throw()
    {
        var tracker = new ActivityTracker();
        var exceptions = new List<Exception>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        Parallel.For(0L, 50, options, day =>
        {
            try
            {
                var date = $"2026-07-{day + 1:D2}";
                tracker.SetDate(date);
                tracker.RecordFishing(5, 35);
                tracker.RecordGift("Willy", "Oceanfish_6");
                tracker.FinalizeDay();
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        });

        Assert.Empty(exceptions);
    }
}