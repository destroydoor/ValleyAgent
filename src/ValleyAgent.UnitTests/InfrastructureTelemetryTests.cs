using ValleyAgent.Infrastructure;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     2026-09-11 生产化仪器单元测试：看门狗阈值解析（ResolveThresholdMs）、
///     QueueTelemetry 节流（ShouldWarn）、StuckOperationTracker 停滞上报（TakeNewlyStalled）。
///     三者均为可注入时钟的纯逻辑；静态状态必须 try/finally 还原（ResetForTests），
///     且全部集中在本类（xUnit 同类串行，避免静态状态跨类竞态）。
///     issue #24（2026-09-16）：EventGuardTests 也触达同一批静态单例（SafeRun 节流 /
///     SafeFireAndForget 的 StuckOp），两类入同一 Collection 串行——DescribeRunning==="none"
///     是进程级断言，并行下会被在途 op 打破（实测 flaky）。
/// </summary>
[Collection("StaticTelemetryState")]
public class InfrastructureTelemetryTests
{
    [Fact]
    public void ResolveThresholdMs_NullOrEmpty_ReturnsDefault()
    {
        Assert.Equal(5000, MainThreadWatchdog.ResolveThresholdMs(null));
        Assert.Equal(3000, MainThreadWatchdog.ResolveThresholdMs(null, 3000));
        Assert.Equal(3000, MainThreadWatchdog.ResolveThresholdMs("", 3000));
    }

    [Fact]
    public void ResolveThresholdMs_ValidNumber_ReturnsParsed()
    {
        Assert.Equal(12345, MainThreadWatchdog.ResolveThresholdMs("12345"));
        Assert.Equal(7000, MainThreadWatchdog.ResolveThresholdMs("7000", 3000));
    }

    [Fact]
    public void ResolveThresholdMs_InvalidString_ReturnsDefault()
    {
        Assert.Equal(5000, MainThreadWatchdog.ResolveThresholdMs("not-a-number"));
        Assert.Equal(3000, MainThreadWatchdog.ResolveThresholdMs("abc", 3000));
    }

    [Fact]
    public void ResolveThresholdMs_ZeroAndNegative_ReturnedAsIs()
    {
        // <=0 原样返回，禁用与否由 Start 判定——纯函数不做策略
        Assert.Equal(0, MainThreadWatchdog.ResolveThresholdMs("0"));
        Assert.Equal(-7, MainThreadWatchdog.ResolveThresholdMs("-7"));
    }

    [Fact]
    public void ShouldWarn_ThrottlesWithinInterval_AndReleasesAfterClockAdvances()
    {
        var now = 100000L;
        QueueTelemetry.Clock = () => now;
        try
        {
            Assert.True(QueueTelemetry.ShouldWarn("test-key"));
            Assert.False(QueueTelemetry.ShouldWarn("test-key"));

            now += 5001;
            Assert.True(QueueTelemetry.ShouldWarn("test-key"));
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public void ShouldWarn_DifferentKeys_Independent()
    {
        var now = 0L;
        QueueTelemetry.Clock = () => now;
        try
        {
            Assert.True(QueueTelemetry.ShouldWarn("key-a"));
            Assert.True(QueueTelemetry.ShouldWarn("key-b"));
            Assert.False(QueueTelemetry.ShouldWarn("key-a"));
            Assert.False(QueueTelemetry.ShouldWarn("key-b"));
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public void ShouldWarn_ResetForTests_ClearsThrottleState()
    {
        var now = 0L;
        QueueTelemetry.Clock = () => now;
        try
        {
            Assert.True(QueueTelemetry.ShouldWarn("k"));
            Assert.False(QueueTelemetry.ShouldWarn("k"));

            QueueTelemetry.ResetForTests();
            Assert.True(QueueTelemetry.ShouldWarn("k"));
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public void StuckOperationTracker_ReportsOncePerStall_AndRefiresAfterEndBegin()
    {
        var now = 0L;
        StuckOperationTracker.Clock = () => now;
        try
        {
            StuckOperationTracker.Begin("op-a", "detail-a");

            // 未超阈值：不报
            Assert.Empty(StuckOperationTracker.TakeNewlyStalled(60000));

            // 超过阈值：报一次（含 opId/detail/时长），同一次停滞不重复报
            now += 61000;
            var stalled = StuckOperationTracker.TakeNewlyStalled(60000);
            var single = Assert.Single(stalled);
            Assert.Equal("op-a", single.OpId);
            Assert.Equal("detail-a", single.Detail);
            Assert.Equal(61000, single.ElapsedMs);
            Assert.Empty(StuckOperationTracker.TakeNewlyStalled(60000));

            // End 后记录移除；重新 Begin 可再次触发上报
            StuckOperationTracker.End("op-a");
            Assert.Empty(StuckOperationTracker.TakeNewlyStalled(60000));

            StuckOperationTracker.Begin("op-a", "detail-b");
            now += 61000;
            var again = Assert.Single(StuckOperationTracker.TakeNewlyStalled(60000));
            Assert.Equal("op-a", again.OpId);
            Assert.Equal("detail-b", again.Detail);
        }
        finally
        {
            StuckOperationTracker.ResetForTests();
        }
    }

    [Fact]
    public void StuckOperationTracker_DescribeRunning_ListsOpId_OrNone()
    {
        var now = 5000L;
        StuckOperationTracker.Clock = () => now;
        try
        {
            Assert.Equal("none", StuckOperationTracker.DescribeRunning());

            StuckOperationTracker.Begin("op-x", "ctx");
            now += 1000;
            Assert.Contains("op-x", StuckOperationTracker.DescribeRunning());

            StuckOperationTracker.End("op-x");
            Assert.Equal("none", StuckOperationTracker.DescribeRunning());
        }
        finally
        {
            StuckOperationTracker.ResetForTests();
        }
    }
}
