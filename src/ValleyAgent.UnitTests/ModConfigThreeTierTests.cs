using ValleyAgent.Config;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     三档容量配置（Min/Normal/Max）的迁移与 Validate 测试。
///     对应设计文档 §4.1 三档容量配置 + §4.5 配置迁移。
/// </summary>
public static class ModConfigThreeTierTests
{
    [Fact]
    public static void Validate_NormalWithinMinMax_NoChange()
    {
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 1, MaxAgentNpcs = 2 };
        var changed = config.Validate();
        Assert.False(changed);
        Assert.Equal(0, config.MinAgentNpcs);
        Assert.Equal(1, config.NormalAgentNpcs);
        Assert.Equal(2, config.MaxAgentNpcs);
    }

    [Fact]
    public static void Validate_NormalBelowMin_ClampedUp()
    {
        var config = new ModConfig { MinAgentNpcs = 2, NormalAgentNpcs = 0, MaxAgentNpcs = 3 };
        var changed = config.Validate();
        Assert.True(changed);
        Assert.Equal(2, config.NormalAgentNpcs);
    }

    [Fact]
    public static void Validate_NormalAboveMax_ClampedDown()
    {
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 5, MaxAgentNpcs = 2 };
        var changed = config.Validate();
        Assert.True(changed);
        Assert.Equal(2, config.NormalAgentNpcs);
    }

    [Fact]
    public static void Migrate_NormalDefaultsToMax_WhenZero()
    {
        // 旧存档无 Normal 字段 → 反序列化默认 0 → 迁移到 Max
        // (Max==1 触发 legacy 迁移到 2，所以 Normal 跟着变成 2)
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 0, MaxAgentNpcs = 1 };
        config.Validate(); // 内部调 MigrateLegacyFields
        Assert.Equal(2, config.NormalAgentNpcs);
    }

    [Fact]
    public static void Migrate_PreservesUserSetNormal_WhenNonZero()
    {
        // 用户显式设了 Normal，不应被迁移覆盖
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 1, MaxAgentNpcs = 2 };
        config.Validate();
        Assert.Equal(1, config.NormalAgentNpcs);
    }
}