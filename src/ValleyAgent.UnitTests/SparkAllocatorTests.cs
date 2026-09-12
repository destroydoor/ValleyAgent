using ValleyAgent.Agents;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     SparkAllocator 单测：5% 邻近 NPC spark 激活的纯逻辑判定。
///     对应设计文档 §4.2.3 + §4.1 spark 概率切换。
///     用确定性 FNV-1a 哈希代替 Random，概率分档通过 ComputeProbability 直接验证。
/// </summary>
public static class SparkAllocatorTests
{
    // --- 逻辑门控测试（probabilityOverride=1.0 保证触发） ---

    [Fact]
    public static void TrySpark_FirstTimeForNpc_ProbabilityOne_ReturnsTrueAndRecordsDate()
    {
        var spark = new SparkAllocator();
        var hit = spark.TrySpark("Haley", "Y1_spring_1", 0, 0, 1, 2, 1.0);
        Assert.True(hit);
        // 同一 NPC 同一天不再触发
        var hit2 = spark.TrySpark("Haley", "Y1_spring_1", 0, 0, 1, 2, 1.0);
        Assert.False(hit2);
    }

    [Fact]
    public static void TrySpark_CurrentAtOrAboveNormal_ReturnsFalse()
    {
        var spark = new SparkAllocator();
        var hit = spark.TrySpark("Haley", "Y1_spring_1", 1, 0, 1, 2, 1.0);
        Assert.False(hit);
    }

    [Fact]
    public static void TrySpark_CurrentAboveNormal_ReturnsFalse()
    {
        var spark = new SparkAllocator();
        var hit = spark.TrySpark("Haley", "Y1_spring_1", 2, 0, 1, 2, 1.0);
        Assert.False(hit);
    }

    [Fact]
    public static void TrySpark_NewDay_ResetsRolledSet()
    {
        var spark = new SparkAllocator();
        spark.TrySpark("Haley", "Y1_spring_1", 0, 0, 1, 2, 1.0);
        // 第二天可以再次触发
        var hit = spark.TrySpark("Haley", "Y1_spring_2", 0, 0, 1, 2, 1.0);
        Assert.True(hit);
    }

    [Fact]
    public static void TrySpark_DifferentNpcSameDay_BothCanSpark()
    {
        var spark = new SparkAllocator();
        var hit1 = spark.TrySpark("Haley", "Y1_spring_1", 0, 0, 2, 3, 1.0);
        var hit2 = spark.TrySpark("Abigail", "Y1_spring_1", 0, 0, 2, 3, 1.0);
        Assert.True(hit1);
        Assert.True(hit2);
    }

    [Fact]
    public static void TrySpark_NullOrEmptyNpcName_ReturnsFalse()
    {
        var spark = new SparkAllocator();
        Assert.False(spark.TrySpark("", "Y1_spring_1", 0, 0, 1, 2, 1.0));
        Assert.False(spark.TrySpark("Haley", "", 0, 0, 1, 2, 1.0));
        Assert.False(spark.TrySpark("Haley", null!, 0, 0, 1, 2, 1.0));
    }

    // --- 概率分档直接测试（不依赖 hash 值） ---

    [Fact]
    public static void ComputeProbability_BelowMin_DoublesProbability() =>
        Assert.Equal(0.10, SparkAllocator.ComputeProbability(0, 1, 2, 0.05));

    [Fact]
    public static void ComputeProbability_AtMin_UsesBaseProbability() =>
        Assert.Equal(0.05, SparkAllocator.ComputeProbability(1, 1, 2, 0.05));

    [Fact]
    public static void ComputeProbability_AtOrAboveNormal_ReturnsZero()
    {
        Assert.Equal(0.0, SparkAllocator.ComputeProbability(2, 1, 2, 0.05));
        Assert.Equal(0.0, SparkAllocator.ComputeProbability(3, 1, 2, 0.05));
    }

    [Fact]
    public static void ComputeProbability_NullOverride_DefaultsToFivePercent()
    {
        Assert.Equal(0.05, SparkAllocator.ComputeProbability(1, 1, 2, null));
        Assert.Equal(0.10, SparkAllocator.ComputeProbability(0, 1, 2, null));
    }

    // --- StableRoll 行为测试 ---

    [Fact]
    public static void StableRoll_SameInput_ReturnsSameOutput()
    {
        var r1 = SparkAllocator.StableRoll("Haley", "Y1_spring_1");
        var r2 = SparkAllocator.StableRoll("Haley", "Y1_spring_1");
        Assert.Equal(r1, r2);
    }

    [Fact]
    public static void StableRoll_DifferentInput_ReturnsDifferentOutput()
    {
        var r1 = SparkAllocator.StableRoll("Haley", "Y1_spring_1");
        var r2 = SparkAllocator.StableRoll("Abigail", "Y1_spring_1");
        Assert.NotEqual(r1, r2);
    }

    [Fact]
    public static void StableRoll_ReturnsInRange()
    {
        for (var i = 0; i < 100; i++)
        {
            var r = SparkAllocator.StableRoll($"Npc{i}", $"Y1_spring_{i}");
            Assert.True(r >= 0.0 && r < 1.0, $"Roll {r} out of range for Npc{i}/Y1_spring_{i}");
        }
    }

    // --- 集成验证：BelowMin 翻倍概率确实影响 TrySpark 结果 ---

    [Fact]
    public static void TrySpark_BelowMin_DoubleProbabilityEnablesExtraSparks()
    {
        // 找两个 hash 在 [0.05, 0.10) 的 NPC：BelowMin 翻倍触发，非 BelowMin 不触发。
        // 验证概率分档确实影响 TrySpark 结果，而不只是 ComputeProbability 的返回值。
        string? belowMinOnlyNpc = null;
        for (var i = 0; i < 2000 && belowMinOnlyNpc == null; i++)
        {
            var npc = $"TestNpc{i}";
            var roll = SparkAllocator.StableRoll(npc, "Y1_spring_1");
            if (roll >= 0.05 && roll < 0.10)
            {
                belowMinOnlyNpc = npc;
            }
        }

        Assert.NotNull(belowMinOnlyNpc);

        var spark = new SparkAllocator();
        // 非 BelowMin（currentCount >= minAgents），prob = 0.05，roll >= 0.05 → 不触发
        Assert.False(spark.TrySpark(belowMinOnlyNpc, "Y1_spring_1",
            1, 1, 2, 3));

        // BelowMin（currentCount < minAgents），prob = 0.10，roll < 0.10 → 触发
        // 同一 NPC 同一天已掷过，换日期获取新 hash（不同 dateKey → 不同 roll）
        // 找一个日期使该 NPC 的 roll < 0.10
        string? sparkDate = null;
        for (var d = 2; d <= 100 && sparkDate == null; d++)
        {
            var date = $"Y1_spring_{d}";
            var roll = SparkAllocator.StableRoll(belowMinOnlyNpc, date);
            if (roll < 0.10)
            {
                sparkDate = date;
            }
        }

        Assert.NotNull(sparkDate);
        Assert.True(spark.TrySpark(belowMinOnlyNpc, sparkDate,
            0, 1, 2, 3));
    }
}