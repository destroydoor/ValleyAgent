using ValleyAgent.Resilience;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     <see cref="CircuitBreaker" /> 单元测试。
///     验证 CLOSED → OPEN → HALF_OPEN → CLOSED 状态机正确性，
///     对应设计文档 P1 "连续失败熔断"（docs/design/2026-08-01-npc-feedback-architecture.md）。
///     CircuitBreaker 是 game-agnostic 纯逻辑组件（无 SMAPI/StardewValley 依赖），
///     完全可单测。
/// </summary>
public static class CircuitBreakerTests
{
    // ───────────────────────── 辅助 ─────────────────────────

    private static CircuitBreaker CreateBreaker(int threshold = 3, int cooldownSeconds = 2, int halfOpenMaxCalls = 1)
    {
        return new CircuitBreaker(new CircuitBreakerConfig(
            threshold,
            10.0,
            60,
            cooldownSeconds,
            halfOpenMaxCalls,
            3));
    }

    // ───────────────────────── CLOSED 状态 ─────────────────────────

    [Fact]
    public static void Constructor_DefaultsToClosedState()
    {
        var cb = new CircuitBreaker();
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        Assert.True(cb.CanExecute);
        Assert.Equal(0, cb.ConsecutiveFailures);
    }

    [Fact]
    public static void ClosedState_CanExecuteAlwaysTrue()
    {
        var cb = CreateBreaker();
        for (var i = 0; i < 10; i++)
        {
            Assert.True(cb.CanExecute);
        }
    }

    [Fact]
    public static void RecordSuccess_InClosed_ResetsFailureCount()
    {
        var cb = CreateBreaker(3);
        cb.RecordFailure("error1");
        cb.RecordFailure("error2");
        Assert.Equal(2, cb.ConsecutiveFailures);

        cb.RecordSuccess();
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    [Fact]
    public static void TryAcquireExecution_InClosed_AlwaysReturnsTrue()
    {
        var cb = CreateBreaker();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(cb.TryAcquireExecution());
        }

        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    // ───────────────────────── CLOSED → OPEN 转换 ─────────────────────────

    [Fact]
    public static void RecordFailure_BelowThreshold_StaysClosed()
    {
        var cb = CreateBreaker(3);
        cb.RecordFailure("error1");
        cb.RecordFailure("error2");
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        Assert.Equal(2, cb.ConsecutiveFailures);
        Assert.True(cb.CanExecute);
    }

    [Fact]
    public static void RecordFailure_AtThreshold_TransitionsToOpen()
    {
        var cb = CreateBreaker(3);
        cb.RecordFailure("error1");
        cb.RecordFailure("error2");
        cb.RecordFailure("error3");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
        Assert.False(cb.CanExecute);
    }

    [Fact]
    public static void RecordFailure_ExceedsThreshold_TransitionsToOpenAtExactThreshold()
    {
        var cb = CreateBreaker(5);
        for (var i = 0; i < 4; i++)
        {
            cb.RecordFailure($"error{i}");
            Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        }

        cb.RecordFailure("error4");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    [Fact]
    public static void RecordFailure_SuccessInBetween_PreventsOpenTransition()
    {
        var cb = CreateBreaker(3);
        cb.RecordFailure("error1");
        cb.RecordFailure("error2");
        cb.RecordSuccess(); // 重置失败计数
        cb.RecordFailure("error1");
        cb.RecordFailure("error2");
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        Assert.Equal(2, cb.ConsecutiveFailures);
    }

    [Fact]
    public static void TransitionToOpen_FiresOnStateChangedAndOnFallbackActivated()
    {
        var cb = CreateBreaker(2);
        var stateChanges = 0;
        var fallbacks = 0;
        cb.OnStateChanged += (_, _) => stateChanges++;
        cb.OnFallbackActivated += (_, _) => fallbacks++;

        cb.RecordFailure("e1");
        Assert.Equal(0, stateChanges); // 还没到阈值
        Assert.Equal(0, fallbacks);

        cb.RecordFailure("e2");
        Assert.Equal(1, stateChanges); // CLOSED → OPEN
        Assert.Equal(1, fallbacks);
    }

    // ───────────────────────── OPEN 状态 ─────────────────────────

    [Fact]
    public static void OpenState_CanExecuteFalse()
    {
        var cb = CreateBreaker(1);
        cb.RecordFailure("open it");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
        Assert.False(cb.CanExecute);
    }

    [Fact]
    public static void OpenState_TryAcquireExecutionReturnsFalseDuringCooldown()
    {
        var cb = CreateBreaker(1, 60);
        cb.RecordFailure("open it");
        Assert.False(cb.TryAcquireExecution());
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    [Fact]
    public static void OpenState_RecordFailureNoOp()
    {
        var cb = CreateBreaker(1);
        cb.RecordFailure("open it");
        var failuresAtOpen = cb.ConsecutiveFailures;

        cb.RecordFailure("another failure");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
        // OPEN 状态下 RecordFailure 是 no-op，失败计数不变
        Assert.Equal(failuresAtOpen, cb.ConsecutiveFailures);
    }

    [Fact]
    public static void OpenState_RecordSuccessNoOp()
    {
        var cb = CreateBreaker(1);
        cb.RecordFailure("open it");

        cb.RecordSuccess();
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    [Fact]
    public static void OpenState_OpenedAtTimestampRecorded()
    {
        var cb = CreateBreaker(1);
        var before = DateTime.UtcNow;
        cb.RecordFailure("open it");
        var after = DateTime.UtcNow;

        Assert.True(cb.OpenedAt >= before && cb.OpenedAt <= after);
    }

    // ───────────────────────── OPEN → HALF_OPEN 转换 ─────────────────────────

    [Fact]
    public static void TryAcquireExecution_AfterCooldown_TransitionsToHalfOpen()
    {
        var cb = CreateBreaker(1, 1);
        cb.RecordFailure("open it");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);

        Thread.Sleep(1100); // 等待冷却期过
        var acquired = cb.TryAcquireExecution();
        Assert.True(acquired);
        Assert.Equal(CircuitBreakerState.HALF_OPEN, cb.CurrentState);
    }

    [Fact]
    public static void TryAcquireExecution_BeforeCooldown_StaysOpen()
    {
        var cb = CreateBreaker(1, 60);
        cb.RecordFailure("open it");

        var acquired = cb.TryAcquireExecution();
        Assert.False(acquired);
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    // ───────────────────────── HALF_OPEN 状态 ─────────────────────────

    [Fact]
    public static void HalfOpen_SuccessTransitionsToClosed()
    {
        var cb = CreateBreaker(1, 1);
        cb.RecordFailure("open it");
        Thread.Sleep(1100);
        cb.TryAcquireExecution(); // → HALF_OPEN

        cb.RecordSuccess();
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.True(cb.CanExecute);
    }

    [Fact]
    public static void HalfOpen_FailureTransitionsBackToOpen()
    {
        var cb = CreateBreaker(1, 1);
        cb.RecordFailure("open it");
        Thread.Sleep(1100);
        cb.TryAcquireExecution(); // → HALF_OPEN

        cb.RecordFailure("half-open test failed");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
        // 失败计数重置到阈值（防止再次 cooldown 后立即 HALF_OPEN）
        Assert.Equal(cb.Config.ConsecutiveFailureThreshold, cb.ConsecutiveFailures);
    }

    [Fact]
    public static void HalfOpen_CallLimitEnforced()
    {
        var cb = CreateBreaker(1, 1, 2);
        cb.RecordFailure("open it");
        Thread.Sleep(1100);

        // 第一次调用允许
        Assert.True(cb.TryAcquireExecution());
        // 第二次调用允许（HalfOpenMaxCalls=2）
        Assert.True(cb.TryAcquireExecution());
        // 第三次调用拒绝
        Assert.False(cb.TryAcquireExecution());
        Assert.Equal(CircuitBreakerState.HALF_OPEN, cb.CurrentState);
    }

    [Fact]
    public static void HalfOpen_FireFallbackEventOnFailure()
    {
        var cb = CreateBreaker(1, 1);
        cb.RecordFailure("open it");
        Thread.Sleep(1100);
        cb.TryAcquireExecution(); // → HALF_OPEN

        var fallbacks = 0;
        cb.OnFallbackActivated += (_, _) => fallbacks++;

        cb.RecordFailure("half-open failed");
        Assert.Equal(1, fallbacks);
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    // ───────────────────────── 慢响应触发 OPEN ─────────────────────────

    [Fact]
    public static void RecordResponseTime_SlowAverageTriggersOpen()
    {
        var cb = CreateBreaker(10, 60);
        // MinResponseTimeSamples=3, SlowResponseThresholdSeconds=10
        cb.RecordResponseTime(TimeSpan.FromSeconds(15));
        cb.RecordResponseTime(TimeSpan.FromSeconds(16));
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState); // 还没到最小样本数

        cb.RecordResponseTime(TimeSpan.FromSeconds(14));
        // 平均 (15+16+14)/3 = 15 > 10 → OPEN
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    [Fact]
    public static void RecordResponseTime_FastAverageStaysClosed()
    {
        var cb = CreateBreaker(10);
        cb.RecordResponseTime(TimeSpan.FromSeconds(1));
        cb.RecordResponseTime(TimeSpan.FromSeconds(2));
        cb.RecordResponseTime(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    [Fact]
    public static void RecordResponseTime_BelowMinSamplesStaysClosed()
    {
        var cb = CreateBreaker(10);
        // MinResponseTimeSamples=3，只记录 2 个样本不应触发
        cb.RecordResponseTime(TimeSpan.FromSeconds(100));
        cb.RecordResponseTime(TimeSpan.FromSeconds(100));
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    [Fact]
    public static void RecordResponseTime_NegativeDurationIgnored()
    {
        var cb = CreateBreaker(10);
        cb.RecordResponseTime(TimeSpan.FromSeconds(-5));
        Assert.Equal(0, cb.ResponseTimeSampleCount);
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    [Fact]
    public static void RecordResponseTime_OnlyTrackedInClosedState()
    {
        var cb = CreateBreaker(1, 60);
        cb.RecordFailure("open it");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);

        var samplesBefore = cb.ResponseTimeSampleCount;
        cb.RecordResponseTime(TimeSpan.FromSeconds(20));
        Assert.Equal(samplesBefore, cb.ResponseTimeSampleCount);
    }

    // ───────────────────────── Reset() ─────────────────────────

    [Fact]
    public static void Reset_FromOpen_ReturnsToClosed()
    {
        var cb = CreateBreaker(1, 60);
        cb.RecordFailure("open it");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);

        cb.Reset();
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.True(cb.CanExecute);
        Assert.Equal(DateTime.MinValue, cb.OpenedAt);
    }

    [Fact]
    public static void Reset_FromHalfOpen_ReturnsToClosed()
    {
        var cb = CreateBreaker(1, 1);
        cb.RecordFailure("open it");
        Thread.Sleep(1100);
        cb.TryAcquireExecution(); // → HALF_OPEN

        cb.Reset();
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    [Fact]
    public static void Reset_FromClosed_NoStateChangeFired()
    {
        var cb = CreateBreaker();
        var stateChanges = 0;
        cb.OnStateChanged += (_, _) => stateChanges++;

        cb.Reset();
        Assert.Equal(0, stateChanges);
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
    }

    [Fact]
    public static void Reset_FromOpen_FiresStateChange()
    {
        var cb = CreateBreaker(1, 60);
        cb.RecordFailure("open it");

        var stateChanges = 0;
        cb.OnStateChanged += (_, _) => stateChanges++;
        cb.Reset();
        Assert.Equal(1, stateChanges);
    }

    // ───────────────────────── GetStatus() 快照 ─────────────────────────

    [Fact]
    public static void GetStatus_ReturnsCurrentStateSnapshot()
    {
        var cb = CreateBreaker(2, 30);
        cb.RecordFailure("e1");
        cb.RecordFailure("e2");

        var status = cb.GetStatus();
        Assert.Equal(CircuitBreakerState.OPEN, status.State);
        Assert.Equal(2, status.ConsecutiveFailures);
        Assert.False(status.CanExecute);
        Assert.NotNull(status.Config);
    }

    // ───────────────────────── 综合场景 ─────────────────────────

    [Fact]
    public static void FullCycle_ClosedToOpenToHalfOpenToClosedToOpen()
    {
        var cb = CreateBreaker(2, 1, 1);

        // CLOSED → OPEN
        cb.RecordFailure("e1");
        cb.RecordFailure("e2");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);

        Thread.Sleep(1100);
        // OPEN → HALF_OPEN
        Assert.True(cb.TryAcquireExecution());
        Assert.Equal(CircuitBreakerState.HALF_OPEN, cb.CurrentState);

        // HALF_OPEN → CLOSED
        cb.RecordSuccess();
        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);

        // CLOSED → OPEN again
        cb.RecordFailure("e1");
        cb.RecordFailure("e2");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }

    [Fact]
    public static void FullCycle_ClosedToOpenToHalfOpenBackToOpen()
    {
        var cb = CreateBreaker(1, 1, 1);

        cb.RecordFailure("open it");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);

        Thread.Sleep(1100);
        Assert.True(cb.TryAcquireExecution()); // → HALF_OPEN
        cb.RecordFailure("half-open failed");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);

        // 再次冷却后还能 HALF_OPEN
        Thread.Sleep(1100);
        Assert.True(cb.TryAcquireExecution());
        Assert.Equal(CircuitBreakerState.HALF_OPEN, cb.CurrentState);
    }

    [Fact]
    public static void ConfigCustomization_AppliedCorrectly()
    {
        var config = new CircuitBreakerConfig(
            7,
            5.0,
            120,
            45,
            3,
            5);
        var cb = new CircuitBreaker(config);

        Assert.Equal(7, cb.Config.ConsecutiveFailureThreshold);
        Assert.Equal(5.0, cb.Config.SlowResponseThresholdSeconds);
        Assert.Equal(120, cb.Config.ResponseTimeWindowSeconds);
        Assert.Equal(45, cb.Config.OpenCooldownSeconds);
        Assert.Equal(3, cb.Config.HalfOpenMaxCalls);
        Assert.Equal(5, cb.Config.MinResponseTimeSamples);

        // 6 次失败不应触发（阈值 7）
        for (var i = 0; i < 6; i++)
        {
            cb.RecordFailure($"e{i}");
        }

        Assert.Equal(CircuitBreakerState.CLOSED, cb.CurrentState);
        cb.RecordFailure("e7");
        Assert.Equal(CircuitBreakerState.OPEN, cb.CurrentState);
    }
}