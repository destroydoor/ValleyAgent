using System.Collections.Generic;
using ValleyAgent.Goals;
using Xunit;

namespace ValleyAgent.UnitTests.Goals;

/// <summary>
///     <see cref="GoalCompletionEvaluator" /> 纯逻辑单元测试（阶段 2 Goal 终止判定核心）。
///     不依赖任何游戏类型 / Game1 状态——注入背包物品数快照（itemId → 数量）。
///     覆盖：背包差量计算（基线 vs 当前）、单项达标、总量达标、动作计数、击杀计数。
/// </summary>
public class GoalCompletionEvaluatorTests
{
    // ───────────────────────── 背包差量计算 ─────────────────────────

    [Fact]
    public void ComputeCollectedCounts_NewItems_ReturnsDelta()
    {
        var baseline = new Dictionary<string, int> { ["(O)388"] = 3 };
        var current = new Dictionary<string, int> { ["(O)388"] = 8 };

        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(baseline, current);

        Assert.Equal(5, delta["(O)388"]);
    }

    [Fact]
    public void ComputeCollectedCounts_ItemsNotInBaseline_CountedAsFullAmount()
    {
        var baseline = new Dictionary<string, int>();
        var current = new Dictionary<string, int> { ["(O)388"] = 4, ["(O)378"] = 2 };

        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(baseline, current);

        Assert.Equal(4, delta["(O)388"]);
        Assert.Equal(2, delta["(O)378"]);
    }

    [Fact]
    public void ComputeCollectedCounts_DecreasedItems_ClampedToZero()
    {
        // NPC 消耗了物品（如生火用木头）→ 负增量不能计入获得量
        var baseline = new Dictionary<string, int> { ["(O)388"] = 10 };
        var current = new Dictionary<string, int> { ["(O)388"] = 3 };

        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(baseline, current);

        Assert.DoesNotContain("(O)388", delta.Keys);
    }

    [Fact]
    public void ComputeCollectedCounts_EmptyBaseline_EmptyResult()
    {
        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(
            new Dictionary<string, int>(), new Dictionary<string, int>());

        Assert.Empty(delta);
    }

    [Fact]
    public void ComputeCollectedCounts_ItemIdComparison_IsCaseInsensitive()
    {
        var baseline = new Dictionary<string, int> { ["(O)388"] = 1 };
        var current = new Dictionary<string, int> { ["(o)388"] = 4 };

        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(baseline, current);

        Assert.Equal(3, delta["(o)388"]);
    }

    // ───────────────────────── 单项达标（chop_tree / mine / forage-targeted） ─────────────────────────

    [Fact]
    public void IsQuantityMet_ReachedQty_ReturnsTrue()
    {
        var collected = new Dictionary<string, int> { ["(O)388"] = 10 };

        Assert.True(GoalCompletionEvaluator.IsQuantityMet(collected, "(O)388", 10));
    }

    [Fact]
    public void IsQuantityMet_BelowQty_ReturnsFalse()
    {
        var collected = new Dictionary<string, int> { ["(O)388"] = 9 };

        Assert.False(GoalCompletionEvaluator.IsQuantityMet(collected, "(O)388", 10));
    }

    [Fact]
    public void IsQuantityMet_MissingItem_ReturnsFalse()
    {
        var collected = new Dictionary<string, int> { ["(O)378"] = 99 };

        Assert.False(GoalCompletionEvaluator.IsQuantityMet(collected, "(O)388", 1));
    }

    [Fact]
    public void IsQuantityMet_ZeroQty_ReturnsTrue()
    {
        // quantity 未指定/0 → 任意获得即达标（兜底语义）
        Assert.True(GoalCompletionEvaluator.IsQuantityMet(
            new Dictionary<string, int> { ["(O)388"] = 1 }, "(O)388", 0));
    }

    // ───────────────────────── 总量达标（forage 未指定 targetItemId） ─────────────────────────

    [Fact]
    public void IsTotalQuantityMet_SumReachedQty_ReturnsTrue()
    {
        var collected = new Dictionary<string, int> { ["(O)388"] = 3, ["(O)378"] = 2 };

        Assert.True(GoalCompletionEvaluator.IsTotalQuantityMet(collected, 5));
        Assert.False(GoalCompletionEvaluator.IsTotalQuantityMet(collected, 6));
    }

    [Fact]
    public void IsTotalQuantityMet_Empty_ReturnsFalse()
    {
        Assert.False(GoalCompletionEvaluator.IsTotalQuantityMet(
            new Dictionary<string, int>(), 1));
    }

    // ───────────────────────── 动作计数（water_crops） ─────────────────────────

    [Fact]
    public void IsActionCountMet_ReachedQty_ReturnsTrue()
    {
        Assert.True(GoalCompletionEvaluator.IsActionCountMet(5, 5));
        Assert.False(GoalCompletionEvaluator.IsActionCountMet(4, 5));
    }

    // ───────────────────────── 击杀计数（fight） ─────────────────────────

    [Fact]
    public void IsKillCountMet_KillsReachedQty_ReturnsTrue()
    {
        // 起始 5 只怪物，剩 2 只 → 击杀 3 ≥ 3
        Assert.True(GoalCompletionEvaluator.IsKillCountMet(5, 2, 3));
    }

    [Fact]
    public void IsKillCountMet_AreaCleared_ReturnsTrue()
    {
        // 区域怪物清零 → 视为完成（即使未达 quantity）
        Assert.True(GoalCompletionEvaluator.IsKillCountMet(5, 0, 3));
    }

    [Fact]
    public void IsKillCountMet_NotEnoughKills_ReturnsFalse()
    {
        // 击杀 1 < 3
        Assert.False(GoalCompletionEvaluator.IsKillCountMet(5, 4, 3));
    }

    [Fact]
    public void ComputeKillCount_NeverNegative()
    {
        // 怪物数量增长（新刷怪）→ 击杀数钳 0
        Assert.Equal(0, GoalCompletionEvaluator.ComputeKillCount(2, 5));
        Assert.Equal(3, GoalCompletionEvaluator.ComputeKillCount(5, 2));
    }
}

/// <summary>
///     <see cref="GoalTime" /> 纯逻辑单元测试（游戏分钟换算 / 超时判定）。
/// </summary>
public class GoalTimeTests
{
    [Theory]
    [InlineData(600, 360)] // 06:00
    [InlineData(1400, 840)] // 14:00
    [InlineData(1230, 750)] // 12:30
    [InlineData(0, 0)] // 午夜 00:00
    public void TimeOfDayToMinutes_ConvertsHhmm(int timeOfDay, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, GoalTime.TimeOfDayToMinutes(timeOfDay));
    }

    [Fact]
    public void ElapsedMinutes_SameDay_SimpleDifference()
    {
        // 06:00 → 07:00 = 60 分钟
        Assert.Equal(60, GoalTime.ElapsedMinutes(
            GoalTime.TimeOfDayToMinutes(600), GoalTime.TimeOfDayToMinutes(700)));
    }

    [Fact]
    public void ElapsedMinutes_CrossMidnight_WrapsBy1440()
    {
        // 20:00 (1200) → 次日 06:00 (360)：1200 + 1440 - 360 = 2280... 反向：
        // 从 1400 (840) 到 0600 (360) 跨天 → 360 - 840 = -480 + 1440 = 960 分钟（16 小时）
        Assert.Equal(960, GoalTime.ElapsedMinutes(840, 360));
    }

    [Fact]
    public void IsTimedOut_AtThreshold_ReturnsTrue()
    {
        Assert.True(GoalTime.IsTimedOut(240, 240));
        Assert.False(GoalTime.IsTimedOut(239, 240));
    }
}
