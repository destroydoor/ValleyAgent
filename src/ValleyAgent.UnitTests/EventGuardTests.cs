using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using ValleyAgent.Infrastructure;
using ValleyAgent.UnitTests.Multiplayer;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     EventGuard / InfrastructureTelemetry 共享的静态遥测状态（QueueTelemetry/StuckOperationTracker
///     进程级单例）必须串行触达：DescribeRunning()==="none" 是进程级断言，SafeFireAndForget 的
///     在途 op 会让并行类的同类断言 flaky（实测复现）。同 Collection 内类间串行。
/// </summary>
[CollectionDefinition("StaticTelemetryState", DisableParallelization = true)]
public sealed class StaticTelemetryStateCollection
{
}

/// <summary>
///     EventGuard 故障隔离守卫单测（issue #24 验收）：
///     - 注入抛异常的段 → 其余段仍执行（隔离语义）；
///     - 同段重复失败 → Error 被节流（QueueTelemetry.ShouldWarn，窗口内只报一次）；
///     - per-agent 循环（OnSaving 的 saving:&lt;npc&gt; 模式）→ 一个 agent 抛异常不影响同批其余 agent，
///       且 Error 日志按 npcName 归因；
///     - SafeFireAndForget → 失败任务即时 Error（不等 GC 的 UnobservedTaskException），
///       StuckOperationTracker 正常 Begin/End。
///     静态状态（QueueTelemetry/StuckOperationTracker）用唯一 key + ResetForTests 还原，
///     不注入 Clock（避免与 InfrastructureTelemetryTests 的时钟注入竞态）；
///     ModErrorLog 未 Initialize（path=null）时 AppendFile 自动 no-op，测试零落盘副作用。
///     与 InfrastructureTelemetryTests 同 Collection 串行（共享静态遥测状态，见集合注释）。
/// </summary>
[Collection("StaticTelemetryState")]
public class EventGuardTests
{
    [Fact]
    public void SafeRun_SuccessfulAction_DoesNotLog()
    {
        var monitor = new RecordingMonitor();
        var executed = false;

        EventGuard.SafeRun(monitor, "eg:happy-path", () => executed = true);

        Assert.True(executed);
        Assert.DoesNotContain(monitor.Snapshot(), e => e.Level >= LogLevel.Debug);
    }

    [Fact]
    public void SafeRun_ThrowingSegment_IsIsolatedAndLoggedWithError()
    {
        var monitor = new RecordingMonitor();
        try
        {
            QueueTelemetry.ResetForTests();

            var reachedAfterThrow = false;
            EventGuard.SafeRun(monitor, "eg:throwing", () =>
            {
                throw new InvalidOperationException("boom-segment");
            });
            reachedAfterThrow = true; // SafeRun 吞掉异常才会执行到这里

            Assert.True(reachedAfterThrow);
            var errors = monitor.Snapshot().Where(e => e.Level == LogLevel.Error).ToList();
            var error = Assert.Single(errors);
            Assert.Contains("eg:throwing", error.Message);
            Assert.Contains("InvalidOperationException", error.Message);
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public void SafeRun_FailingSegment_DoesNotStopSubsequentSegments()
    {
        var monitor = new RecordingMonitor();
        try
        {
            QueueTelemetry.ResetForTests();

            // 模拟 OnUpdateTicked 的分段编排：中间段失败，前后段照常执行
            var executed = new List<string>();
            foreach (var segment in new[] { "seg-a", "seg-b", "seg-c" })
            {
                var name = segment;
                EventGuard.SafeRun(monitor, name, () =>
                {
                    if (name == "seg-b")
                    {
                        throw new InvalidOperationException("boom-b");
                    }

                    executed.Add(name);
                });
            }

            Assert.Equal(new[] { "seg-a", "seg-c" }, executed);
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public void SafeRun_RepeatFailures_AreThrottledToOneError()
    {
        var monitor = new RecordingMonitor();
        try
        {
            QueueTelemetry.ResetForTests();

            for (var i = 0; i < 5; i++)
            {
                EventGuard.SafeRun(monitor, "eg:repeat", () => throw new InvalidOperationException("boom-repeat"));
            }

            // 5 次确定性失败：只有首报落 Error，其余被节流降为 Trace（不丢可观测性、不刷屏）
            var errors = monitor.Snapshot().Where(e => e.Level == LogLevel.Error).ToList();
            var error = Assert.Single(errors);
            Assert.Contains("eg:repeat", error.Message);
            Assert.Contains("boom-repeat", error.Message);
            Assert.True(monitor.Snapshot().Count(e => e.Level == LogLevel.Trace) >= 4, "被节流的重复失败应留 Trace 心跳");
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public void SafeRun_PerAgentSavingLoop_FailingAgentDoesNotAffectOthers()
    {
        var monitor = new RecordingMonitor();
        try
        {
            QueueTelemetry.ResetForTests();

            // OnSaving per-agent 隔离的同一模式：一个 agent 序列化抛异常，
            // 同批其余 agent 照常保存，Error 日志按 npcName 归因
            var saved = new List<string>();
            foreach (var agentName in new[] { "Haley", "BadAgent", "Abigail" })
            {
                var name = agentName;
                EventGuard.SafeRun(monitor, $"saving:{name}", () =>
                {
                    if (name == "BadAgent")
                    {
                        throw new InvalidOperationException("serialize failed");
                    }

                    saved.Add(name);
                });
            }

            Assert.Equal(new[] { "Haley", "Abigail" }, saved);
            Assert.Contains(monitor.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("saving:BadAgent"));
        }
        finally
        {
            QueueTelemetry.ResetForTests();
        }
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_LogsErrorImmediately_AndEndsStuckOperation()
    {
        var monitor = new RecordingMonitor();

        EventGuard.SafeFireAndForget(
            Task.Run(new Action(() => throw new InvalidOperationException("faf-boom"))),
            "eg-faf:fail",
            monitor);

        // 观察续体应立即（而非等 GC 触发 UnobservedTaskException）记 Error
        Assert.True(
            Poll.Until(() => monitor.Snapshot().Any(e => e.Level == LogLevel.Error && e.Message.Contains("eg-faf:fail")),
                TimeSpan.FromSeconds(5)),
            "失败任务应在续体中即时记 Error");
        Assert.Contains("faf-boom", monitor.Snapshot().First(e => e.Message.Contains("eg-faf:fail")).Message);

        // StuckOperationTracker 应已 End（不留下永久"运行中"的停滞记录）
        Assert.True(
            Poll.Until(() => !StuckOperationTracker.DescribeRunning().Contains("faf:eg-faf:fail"),
                TimeSpan.FromSeconds(5)),
            "任务结束后 StuckOperationTracker 应移除该 opId");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SafeFireAndForget_SuccessfulTask_DoesNotLogError_AndEndsStuckOperation()
    {
        var monitor = new RecordingMonitor();

        EventGuard.SafeFireAndForget(Task.Run(() => "ok"), "eg-faf:ok", monitor);

        Assert.True(
            Poll.Until(() => !StuckOperationTracker.DescribeRunning().Contains("faf:eg-faf:ok"),
                TimeSpan.FromSeconds(5)),
            "成功任务同样应 End 掉 StuckOperationTracker 记录");
        Assert.DoesNotContain(monitor.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("eg-faf:ok"));
        await Task.CompletedTask;
    }

    [Fact]
    public void SafeFireAndForget_NullTask_DoesNotThrow()
    {
        var monitor = new RecordingMonitor();
        EventGuard.SafeFireAndForget(null!, "eg-faf:null", monitor);
        Assert.Empty(monitor.Snapshot());
    }
}
