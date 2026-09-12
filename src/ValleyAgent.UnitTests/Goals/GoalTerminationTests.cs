using System.Collections.Generic;
using ValleyAgent.Goals;
using Xunit;

namespace ValleyAgent.UnitTests.Goals;

/// <summary>
///     五种 Goal 的终止条件单元测试（阶段 2，spec §3.5）。
///     用注入的背包物品计数快照 / 动作计数 / 怪物计数驱动各 Goal 的静态纯判定方法，
///     不依赖 Game1 / NPC 实例。
/// </summary>
public class GoalTerminationTests
{
    // ───────────────────────── chop_tree：Wood 获得量 ─────────────────────────

    [Fact]
    public void ChopTreeGoal_CollectedEnoughWood_IsComplete()
    {
        var collected = new Dictionary<string, int> { ["(O)388"] = 10 };

        Assert.True(ChopTreeGoal.IsTerminationMet(collected, 10));
    }

    [Fact]
    public void ChopTreeGoal_NotEnoughWood_NotComplete()
    {
        var collected = new Dictionary<string, int> { ["(O)388"] = 3 };

        Assert.False(ChopTreeGoal.IsTerminationMet(collected, 10));
    }

    [Fact]
    public void ChopTreeGoal_OnlyOtherItems_NotComplete()
    {
        var collected = new Dictionary<string, int> { ["(O)378"] = 99 };

        Assert.False(ChopTreeGoal.IsTerminationMet(collected, 1));
    }

    // ───────────────────────── mine：targetItemId 获得量 ─────────────────────────

    [Fact]
    public void MineGoal_CollectedTargetItem_IsComplete()
    {
        var collected = new Dictionary<string, int> { ["(O)390"] = 5 };

        Assert.True(MineGoal.IsTerminationMet(collected, "(O)390", 5));
    }

    [Fact]
    public void MineGoal_CustomTargetItem_CountsThatItem()
    {
        // LLM 指定铜矿石 (O)378：石头获得再多也不算达标
        var collected = new Dictionary<string, int> { ["(O)390"] = 20, ["(O)378"] = 2 };

        Assert.True(MineGoal.IsTerminationMet(collected, "(O)378", 2));
        Assert.False(MineGoal.IsTerminationMet(collected, "(O)378", 3));
    }

    // ───────────────────────── water_crops：动作完成计数 ─────────────────────────

    [Fact]
    public void WaterCropsGoal_ActionCountReached_IsComplete()
    {
        Assert.True(WaterCropsGoal.IsTerminationMet(5, 5));
        Assert.False(WaterCropsGoal.IsTerminationMet(4, 5));
    }

    // ───────────────────────── fight：击杀计数 / 区域清空 ─────────────────────────

    [Fact]
    public void FightGoal_KillsReached_IsComplete()
    {
        Assert.True(FightGoal.IsTerminationMet(baselineMonsters: 5, currentMonsters: 2, requiredKills: 3));
    }

    [Fact]
    public void FightGoal_AreaCleared_IsComplete()
    {
        Assert.True(FightGoal.IsTerminationMet(baselineMonsters: 3, currentMonsters: 0, requiredKills: 5));
    }

    [Fact]
    public void FightGoal_NotEnoughKills_NotComplete()
    {
        Assert.False(FightGoal.IsTerminationMet(baselineMonsters: 5, currentMonsters: 4, requiredKills: 3));
    }

    // ───────────────────────── forage：采集量 ─────────────────────────

    [Fact]
    public void ForageGoal_TotalCollected_IsComplete()
    {
        var collected = new Dictionary<string, int> { ["(O)16"] = 3, ["(O)20"] = 2 };

        Assert.True(ForageGoal.IsTerminationMet(collected, 5));
        Assert.False(ForageGoal.IsTerminationMet(collected, 6));
    }

    [Fact]
    public void ForageGoal_TargetItem_CountsOnlyThatItem()
    {
        var collected = new Dictionary<string, int> { ["(O)16"] = 1, ["(O)20"] = 9 };

        Assert.True(ForageGoal.IsTerminationMetForItem(collected, "(O)20", 9));
        Assert.False(ForageGoal.IsTerminationMetForItem(collected, "(O)16", 2));
    }

    // ───────────────────────── 卡死判定（GoalBase 纯逻辑） ─────────────────────────

    [Fact]
    public void GoalBase_IsStuck_AtThreshold_ReturnsTrue()
    {
        Assert.True(GoalBase.IsStuck(120, 120));
        Assert.False(GoalBase.IsStuck(119, 120));
        Assert.False(GoalBase.IsStuck(0, 120));
    }

    // ───────────────────────── 参数解析（CreateGoal 入口） ─────────────────────────

    [Fact]
    public void ParseParameters_QuantityAndTargetItemId()
    {
        var (quantity, targetItemId) = GoalBase.ParseParameters(
            new Dictionary<string, object> { ["quantity"] = 10, ["targetItemId"] = "(O)378" });

        Assert.Equal(10, quantity);
        Assert.Equal("(O)378", targetItemId);
    }

    [Fact]
    public void ParseParameters_MissingParams_Defaults()
    {
        var (quantity, targetItemId) = GoalBase.ParseParameters(null);

        Assert.Equal(1, quantity);
        Assert.Null(targetItemId);
    }

    [Fact]
    public void ParseParameters_InvalidQuantity_ClampedToOne()
    {
        var (quantity, _) = GoalBase.ParseParameters(
            new Dictionary<string, object> { ["quantity"] = -5 });

        Assert.Equal(1, quantity);
    }

    // ───────────────────────── GoalType 解析 ─────────────────────────

    [Theory]
    [InlineData("chop_tree", GoalType.ChopTree)]
    [InlineData("mine", GoalType.Mine)]
    [InlineData("water_crops", GoalType.WaterCrops)]
    [InlineData("fight", GoalType.Fight)]
    [InlineData("forage", GoalType.Forage)]
    [InlineData("CHOP_TREE", GoalType.ChopTree)] // 大小写不敏感
    public void ParseGoalType_KnownTypes(string wireType, GoalType expected)
    {
        Assert.Equal(expected, GoalBase.ParseGoalType(wireType));
    }

    [Fact]
    public void ParseGoalType_UnknownType_ReturnsNull()
    {
        Assert.Null(GoalBase.ParseGoalType("dance"));
        Assert.Null(GoalBase.ParseGoalType(""));
        Assert.Null(GoalBase.ParseGoalType(null!));
    }

    [Theory]
    [InlineData(GoalType.ChopTree, "chop_tree")]
    [InlineData(GoalType.Mine, "mine")]
    [InlineData(GoalType.WaterCrops, "water_crops")]
    [InlineData(GoalType.Fight, "fight")]
    [InlineData(GoalType.Forage, "forage")]
    public void GoalTypeToWire_RoundTrip(GoalType type, string wireType)
    {
        Assert.Equal(wireType, GoalBase.GoalTypeToWire(type));
    }
}
