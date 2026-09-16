using ValleyAgent.Config;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     多 Provider 模式新增字段的迁移与 Validate 钳位测试。
///     对应实施计划 Task 3：ModConfig 扩展 + 迁移。
/// </summary>
public static class ConfigMigrationTests
{
    [Fact]
    public static void Migrate_DialogueCooldownMs_5000_to_Seconds_5()
    {
        var config = new ModConfig();
        config.DialogueCooldownMs = 5000;
        config.DialogueCooldownSeconds = 3; // 默认值

        config.MigrateLegacyFields();

        Assert.Equal(5, config.DialogueCooldownSeconds);
    }

    [Fact]
    public static void Migrate_DialogueCooldownMs_Default_NoMigration()
    {
        var config = new ModConfig();
        config.DialogueCooldownMs = 3000; // 默认值
        config.DialogueCooldownSeconds = 3;

        config.MigrateLegacyFields();

        Assert.Equal(3, config.DialogueCooldownSeconds);
    }

    [Fact]
    public static void Migrate_GiftCooldownMs_60000_to_Seconds_60()
    {
        var config = new ModConfig();
        config.GiftCooldownMs = 60000;
        config.GiftCooldownSeconds = 30; // 默认值

        config.MigrateLegacyFields();

        Assert.Equal(60, config.GiftCooldownSeconds);
    }

    [Fact]
    public static void Validate_ProbabilityClamp_BelowZero()
    {
        var config = new ModConfig();
        config.ProactiveSpeechProbability = -0.5f;

        config.Validate();

        Assert.Equal(0.3f, config.ProactiveSpeechProbability);
    }

    [Fact]
    public static void Validate_ProbabilityClamp_AboveOne()
    {
        var config = new ModConfig();
        config.ProactiveGiftProbability = 1.5f;

        config.Validate();

        Assert.Equal(0.1f, config.ProactiveGiftProbability);
    }

    [Fact]
    public static void Validate_CooldownSecondsClamp_BelowMin()
    {
        var config = new ModConfig();
        config.DialogueCooldownSeconds = 0;

        config.Validate();

        Assert.Equal(3, config.DialogueCooldownSeconds);
    }

    [Fact]
    public static void Validate_CooldownSecondsClamp_AboveMax()
    {
        var config = new ModConfig();
        config.GiftCooldownSeconds = 9999;

        config.Validate();

        Assert.Equal(30, config.GiftCooldownSeconds);
    }

    [Fact]
    public static void Defaults_NewFieldsCorrect()
    {
        var config = new ModConfig();

        Assert.False(config.MultiProviderEnabled);
        Assert.True(config.EnableTrade);
        Assert.True(config.EnableHire);
        // EnableDirector 断言已随 issue #17 摘除（2026-09-16）：该开关零行为消费，
        // 配置项与 --disable-director 透传一并撤除。
        Assert.True(config.EnableProactiveSpeech);
        Assert.True(config.EnableInfiniteDialogue);
        Assert.True(config.EnableProtagonistMapping);
        Assert.Equal(0.3f, config.ProactiveSpeechProbability);
        Assert.Equal(3, config.DialogueCooldownSeconds);
        Assert.Equal(30, config.GiftCooldownSeconds);
        Assert.Equal("minimax", config.DirectorPrimaryProvider);
        Assert.Equal("MiniMax-M3", config.DirectorPrimaryModel);
    }
}