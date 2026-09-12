using ValleyAgent.Chat;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E5-3 主动发言额度服务单元测试（纯逻辑，无游戏依赖）。
///     覆盖：额度内消费、每日上限强制、冷却窗口、会话豁免、豁免不刷新冷却、
///     被动回应不消费、日切重置、空名字防御。
///     设计文档：docs/ideas/quota-implementation-思路.md
/// </summary>
public static class ProactiveSpeechQuotaTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ProactiveSpeechQuota CreateQuota(
        ChatSessionRegistry? registry = null,
        int dailyLimit = 2,
        int cooldownMinutes = 30)
    {
        // 必须注入独立注册表，避免多测试共享静态 ChatSessionRegistry.Instance 导致计数串扰。
        registry ??= new ChatSessionRegistry();
        return new ProactiveSpeechQuota(registry) { DailyLimit = dailyLimit, CooldownMinutes = cooldownMinutes };
    }

    // ───────────────────────── 额度消费与上限 ─────────────────────────

    [Fact]
    public static void TryConsumeQuota_WithinLimit_ReturnsTrue()
    {
        var quota = CreateQuota(dailyLimit: 2);
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        Assert.Equal(1, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void TryConsumeQuota_BeyondDailyLimit_ReturnsFalse()
    {
        // 关闭冷却，纯测每日上限
        var quota = CreateQuota(dailyLimit: 2, cooldownMinutes: 0);
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddSeconds(1)));
        // 已用满 2 次 → 拒绝
        Assert.False(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddSeconds(2)));
        Assert.Equal(0, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void DailyLimit_ScopedByGameDate()
    {
        // 关闭冷却，纯测跨日额度独立
        var quota = CreateQuota(dailyLimit: 1, cooldownMinutes: 0);
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        // 另一天额度独立
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_2", T0.AddDays(1)));
        Assert.Equal(0, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
        Assert.Equal(0, quota.GetRemainingQuota("Haley", "Y1_spring_2"));
    }

    // ───────────────────────── 冷却窗口 ─────────────────────────

    [Fact]
    public static void TryConsumeQuota_WithinCooldownWindow_Rejects()
    {
        var quota = CreateQuota(cooldownMinutes: 30);
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        // 冷却内：即使当天额度没用完也拒绝
        Assert.False(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddMinutes(5)));
        Assert.Equal(1, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void TryConsumeQuota_AfterCooldownElapses_AllowsAgain()
    {
        var quota = CreateQuota(cooldownMinutes: 30);
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        Assert.False(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddMinutes(29)));
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddMinutes(31)));
        Assert.Equal(0, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void Cooldown_IsPerNpc()
    {
        var quota = CreateQuota(cooldownMinutes: 30);
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        // 其他 NPC 不受 Haley 的冷却影响
        Assert.True(quota.TryConsumeQuota("Abigail", "Y1_spring_1", T0.AddMinutes(1)));
    }

    // ───────────────────────── 会话豁免 ─────────────────────────

    [Fact]
    public static void TryConsumeQuota_InActiveSession_ExemptAndDoesNotConsume()
    {
        var registry = new ChatSessionRegistry();
        var quota = CreateQuota(registry, 2);
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);

        // 会话内：放行，但当日计数不变、剩余额度不变
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddSeconds(10)));
        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
        Assert.Equal(2, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void SessionExemptSpeech_DoesNotRefreshCooldown()
    {
        var registry = new ChatSessionRegistry();
        var quota = CreateQuota(registry, cooldownMinutes: 30);
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);

        // 会话内发言：豁免且不刷新冷却
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        // 会话 60s 超时后退出；此时会话内发言不应把冷却时间卡住 → 立即再主动发言应放行
        Assert.False(registry.IsInActiveSession("Haley", T0.AddSeconds(61)));
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddSeconds(61)));
        Assert.Equal(1, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void IsSessionExempt_ReflectsRegistrySession()
    {
        var registry = new ChatSessionRegistry();
        var quota = CreateQuota(registry);
        Assert.False(quota.IsSessionExempt("Haley", T0));
        registry.StartOrTouch("Haley", ChatSessionInitiator.ProactiveSpeech, T0);
        Assert.True(quota.IsSessionExempt("Haley", T0.AddSeconds(10)));
        Assert.False(quota.IsSessionExempt("Haley", T0.AddSeconds(61)));
    }

    // ───────────────────────── 被动回应 ─────────────────────────

    [Fact]
    public static void RecordPassiveResponse_NeverConsumesQuota()
    {
        var registry = new ChatSessionRegistry();
        var quota = CreateQuota(registry, 2);

        quota.RecordPassiveResponse("Haley", T0);
        quota.RecordPassiveResponse("Haley", T0.AddSeconds(1));
        quota.RecordPassiveResponse("Haley", T0.AddSeconds(2));

        // 被动回应不计数、不消耗额度、也不触发冷却
        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
        Assert.Equal(2, quota.GetRemainingQuota("Haley", "Y1_spring_1"));
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddSeconds(3)));
    }

    // ───────────────────────── 日切重置 ─────────────────────────

    [Fact]
    public static void ResetDaily_ClearsCountsAndCooldown()
    {
        var registry = new ChatSessionRegistry();
        var quota = CreateQuota(registry, 2, 30);

        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0));
        Assert.False(quota.TryConsumeQuota("Haley", "Y1_spring_1", T0.AddMinutes(5))); // 冷却内

        quota.ResetDaily("Y1_spring_2");

        // 计数回满
        Assert.Equal(2, quota.GetRemainingQuota("Haley", "Y1_spring_2"));
        // 冷却清空：新的一天立即可再次主动发言
        Assert.True(quota.TryConsumeQuota("Haley", "Y1_spring_2", T0.AddMinutes(6)));
    }

    // ───────────────────────── 边界 ─────────────────────────

    [Fact]
    public static void TryConsumeQuota_EmptyNpcName_ReturnsFalse()
    {
        var quota = CreateQuota();
        Assert.False(quota.TryConsumeQuota(" ", "Y1_spring_1", T0));
        Assert.False(quota.TryConsumeQuota("", "Y1_spring_1", T0));
    }

    [Fact]
    public static void Defaults_MatchConfigSpec()
    {
        // 未配置时默认值必须与 ModConfig 新字段一致（2 次 / 30 分钟）
        var quota = new ProactiveSpeechQuota();
        Assert.Equal(2, quota.DailyLimit);
        Assert.Equal(30, quota.CooldownMinutes);
    }
}