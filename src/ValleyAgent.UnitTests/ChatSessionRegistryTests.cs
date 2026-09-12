using ValleyAgent.Chat;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E2-2 会话模式注册表单元测试（纯逻辑，无游戏依赖）。
///     覆盖：StartOrTouch 创建/刷新、60s 超时、GetActiveSessionNpc 选最近触碰、
///     会话内发言豁免主动额度、超时后额度恢复计数。
///     设计文档：docs/ideas/2026-08-03-e22-chat-bar-routing.md
/// </summary>
public static class ChatSessionRegistryTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ChatSessionRegistry CreateRegistry(double timeoutSeconds = 60)
        => new() { SessionTimeoutSeconds = timeoutSeconds };

    // ───────────────────────── 会话生命周期 ─────────────────────────

    [Fact]
    public static void StartOrTouch_CreatesSessionWithExchangeCountOne()
    {
        var registry = CreateRegistry();
        var session = registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        Assert.Equal("Haley", session.NpcName);
        Assert.Equal(ChatSessionInitiator.PlayerChat, session.Initiator);
        Assert.Equal(1, session.ExchangeCount);
        Assert.True(registry.IsInActiveSession("Haley", T0));
        Assert.Equal("Haley", registry.GetActiveSessionNpc(T0));
    }

    [Fact]
    public static void StartOrTouch_RefreshesExistingSession_IncrementsExchangeCount()
    {
        var registry = CreateRegistry();
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        var session = registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0.AddSeconds(5));
        Assert.Equal(2, session.ExchangeCount);
        Assert.Equal(2, registry.GetSessionExchangeCount("Haley"));
    }

    [Fact]
    public static void Session_ExpiresAfterTimeout_NoLongerActive()
    {
        var registry = CreateRegistry(60);
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        Assert.True(registry.IsInActiveSession("Haley", T0.AddSeconds(59)));
        Assert.False(registry.IsInActiveSession("Haley", T0.AddSeconds(61)));
        Assert.Null(registry.GetActiveSessionNpc(T0.AddSeconds(61)));
    }

    [Fact]
    public static void Session_TouchExtendsTimeoutWindow()
    {
        var registry = CreateRegistry(60);
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        // 第 59 秒玩家又回了一句 → 窗口从 59s 重新起算
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0.AddSeconds(59));
        Assert.True(registry.IsInActiveSession("Haley", T0.AddSeconds(59 + 59)));
        Assert.False(registry.IsInActiveSession("Haley", T0.AddSeconds(59 + 61)));
    }

    [Fact]
    public static void GetActiveSessionNpc_ReturnsMostRecentlyTouched()
    {
        var registry = CreateRegistry();
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        registry.StartOrTouch("Abigail", ChatSessionInitiator.PlayerChat, T0.AddSeconds(3));
        // 会话切换：最近触碰的 Abigail 是"当前会话"
        Assert.Equal("Abigail", registry.GetActiveSessionNpc(T0.AddSeconds(10)));
        // Haley 会话仍有效（未过期），只是不是最近
        Assert.True(registry.IsInActiveSession("Haley", T0.AddSeconds(10)));
    }

    [Fact]
    public static void GetActiveSessionNpc_ExpiredSessionsIgnored()
    {
        var registry = CreateRegistry(60);
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        registry.StartOrTouch("Abigail", ChatSessionInitiator.PlayerChat, T0.AddSeconds(120));
        Assert.Equal("Abigail", registry.GetActiveSessionNpc(T0.AddSeconds(130)));
    }

    [Fact]
    public static void EndSession_RemovesSession()
    {
        var registry = CreateRegistry();
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        registry.EndSession("Haley");
        Assert.False(registry.IsInActiveSession("Haley", T0.AddSeconds(1)));
        Assert.Equal(0, registry.GetSessionExchangeCount("Haley"));
    }

    [Fact]
    public static void ClearAll_RemovesEverything()
    {
        var registry = CreateRegistry();
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0);
        registry.ClearAll();
        Assert.False(registry.IsInActiveSession("Haley", T0));
        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
    }

    // ───────────────────────── 会话豁免主动额度（E5-3 钩子） ─────────────────────────

    [Fact]
    public static void RecordNpcSpeech_InSession_ExemptFromDailyQuota()
    {
        var registry = CreateRegistry();
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);

        // 会话内 NPC 发言：豁免，不计每日主动额度（ExchangeCount 只随玩家回合 StartOrTouch 增长）
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0.AddSeconds(10));
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0.AddSeconds(20));

        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
        Assert.Equal(1, registry.GetSessionExchangeCount("Haley"));
    }

    [Fact]
    public static void RecordNpcSpeech_OutsideSession_CountsTowardDailyQuota()
    {
        var registry = CreateRegistry();
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0);
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0.AddSeconds(1));
        Assert.Equal(2, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void RecordNpcSpeech_AfterTimeout_QuotaCountingResumes()
    {
        var registry = CreateRegistry(60);
        registry.StartOrTouch("Haley", ChatSessionInitiator.PlayerChat, T0);

        // 会话内 → 豁免
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0.AddSeconds(30));
        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));

        // 玩家沉默 60s → 会话超时退出 → 额度恢复计数
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0.AddSeconds(120));
        Assert.Equal(1, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void RecordNpcSpeech_PassiveResponse_NeverCounted()
    {
        var registry = CreateRegistry();
        registry.RecordNpcSpeech("Haley", false, "Y1_spring_1", T0);
        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
    }

    [Fact]
    public static void DailyCount_ScopedByGameDate()
    {
        var registry = CreateRegistry();
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0);
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_2", T0.AddDays(1));
        Assert.Equal(1, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
        Assert.Equal(1, registry.GetDailyProactiveCount("Haley", "Y1_spring_2"));
    }

    [Fact]
    public static void ClearDailyCounts_ResetsQuota()
    {
        var registry = CreateRegistry();
        registry.RecordNpcSpeech("Haley", true, "Y1_spring_1", T0);
        registry.ClearDailyCounts();
        Assert.Equal(0, registry.GetDailyProactiveCount("Haley", "Y1_spring_1"));
    }

    // ───────────────────────── 边界 ─────────────────────────

    [Fact]
    public static void EmptyNpcName_ThrowsOnStartOrTouch()
    {
        var registry = CreateRegistry();
        Assert.Throws<ArgumentException>(() => registry.StartOrTouch(" ", ChatSessionInitiator.PlayerChat, T0));
    }

    [Fact]
    public static void IsInActiveSession_EmptyName_ReturnsFalse()
    {
        var registry = CreateRegistry();
        Assert.False(registry.IsInActiveSession("", T0));
    }

    [Fact]
    public static void ProactiveSpeechInitiatedSession_Recorded()
    {
        var registry = CreateRegistry();
        var session = registry.StartOrTouch("Sebastian", ChatSessionInitiator.ProactiveSpeech, T0);
        Assert.Equal(ChatSessionInitiator.ProactiveSpeech, session.Initiator);
        Assert.True(registry.IsInActiveSession("Sebastian", T0.AddSeconds(10)));
    }
}