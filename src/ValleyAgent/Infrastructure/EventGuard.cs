#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace ValleyAgent.Infrastructure;

/// <summary>
///     事件/分段故障隔离守卫（issue #24，异常处理审计 PR2）。
///     审计前提："假设每个功能都有 bug，模组还能否正常运行"——C# 侧此前答案是否
///     （OnUpdateTicked 串起 10+ 子系统零守卫，任一段抛出即让同 tick 后续全部停摆）。
///     SMAPI 的 ManagedEvent.Raise 只兜"事件 handler 不崩游戏"，兜不了
///     "handler 抛出点之后的本 tick 代码全部跳过"+"同一条错误每 tick 重演"。
///     <see cref="SafeRun"/> 让每段独立兜底：任一段失败只丢它自己，链不能断；
///     重复错误经 <see cref="QueueTelemetry.ShouldWarn"/> 节流（首报全栈，窗口内重复降 Trace），
///     并双通道落盘（ModErrorLog）保证玩家侧可回传。
///     全部 SMAPI 事件（OnUpdateTicked/OnTimeChanged/OnDayStarted/OnSaving...）与
///     泵/串行调度点共用本入口，不各自手写 try/catch。
/// </summary>
public static class EventGuard
{
    /// <summary>
    ///     分段守卫：执行 <paramref name="action"/>，异常被隔离（吞掉不外抛），
    ///     记 Error（monitor，含完整栈）+ ModErrorLog 落盘。
    ///     同一 <paramref name="segmentName"/> 在节流窗口（QueueTelemetry 默认 5s）内只放行一次
    ///     Error + 落盘；窗口内的重复失败降级为 Trace 一行（可观测性铁律：被节流丢弃的也要留痕）。
    ///     高频段（60 tick/秒）确定性故障从"每秒 60 条 Error"收敛为"5 秒 1 条 + Trace 心跳"。
    /// </summary>
    /// <param name="monitor">SMAPI monitor（可为 null——测试/早期初始化；仅影响 SMAPI 通道，落盘不受影响）。</param>
    /// <param name="segmentName">段名（日志归因 + 节流 key；建议含实体名如 "agent-tick:Haley"）。</param>
    /// <param name="action">受守卫的段。失败语义由调用方定义——本方法只保证"失败不影响下一段"。</param>
    public static void SafeRun(IMonitor? monitor, string segmentName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // 统一留痕：QueueTelemetry 节流 + ModErrorLog 落盘，见 ReportFailure
            ReportFailure(monitor, segmentName, ex);
        }
    }

    /// <summary>
    ///     段级失败统一留痕：同段首报 Error 全栈 + ModErrorLog 落盘；节流窗口内的重复降级
    ///     Trace 一行（可观测性铁律：被节流丢弃的也要留痕，但不落盘防日志膨胀）。
    ///     供 <see cref="SafeRun"/> 与"必须手写 try/catch 控制回落语义"的调用方
    ///     （Harmony 补丁入口要按返回值回落原版）共用。
    /// </summary>
    public static void ReportFailure(IMonitor? monitor, string segmentName, Exception ex)
    {
        if (QueueTelemetry.ShouldWarn($"safeseg:{segmentName}"))
        {
            var message = $"[SafeRun] segment '{segmentName}' failed (downstream segments unaffected): {ex}";
            monitor?.Log(message, LogLevel.Error);
            ModErrorLog.LogError("SafeRun", message, ex);
        }
        else
        {
            monitor?.Log(
                $"[SafeRun] segment '{segmentName}' failed again (throttled): {ex.GetType().Name}: {ex.Message}",
                LogLevel.Trace);
        }
    }

    /// <summary>
    ///     fire-and-forget 统一封口（issue #24 ⑤）：裸 <c>_ = Task.Run(...)</c> 的异常只有
    ///     TaskScheduler.UnobservedTaskException 兜底，而它由 GC 触发（时机不定、可能不触发）。
    ///     本方法立即挂观察续体：任务失败即时 Error + ModErrorLog 落盘（不再等 GC）；
    ///     同时接既有 StuckOperationTracker（Begin/End），停滞/泄漏的后台任务可被看门狗点名
    ///     （&gt;60s 落 stuck-*.dmp）。opId 带自增序号：同名任务可并发，固定 id 会互相覆盖
    ///     （先结束的把仍在跑的记录 End 掉——与 MakeDecisionsAsync 序号化同一理由）。
    /// </summary>
    /// <param name="task">要观察的后台任务。</param>
    /// <param name="name">任务名（日志归因；建议含实体/批次语义如 "decision-batch:dialogue-end"）。</param>
    /// <param name="monitor">可选 SMAPI monitor（仅影响 SMAPI 通道，落盘不受影响）。</param>
    public static void SafeFireAndForget(Task? task, string name, IMonitor? monitor = null)
    {
        if (task == null)
        {
            return;
        }

        var opId = $"faf:{name}#{Interlocked.Increment(ref _fireAndForgetSequence)}";
        StuckOperationTracker.Begin(opId, "fire-and-forget");

        task.ContinueWith(
            static (t, state) =>
            {
                var ctx = (FireAndForgetContext)state!;
                StuckOperationTracker.End(ctx.OpId);

                if (t.IsCanceled)
                {
                    ctx.Monitor?.Log($"[FireAndForget] '{ctx.Name}' canceled", LogLevel.Debug);
                    return;
                }

                if (!t.IsFaulted)
                {
                    return;
                }

                // GetBaseException 拿根因（AggregateException 解包）；两处皆 null 属极端竞态，跳过
                var ex = t.Exception?.GetBaseException() ?? t.Exception;
                if (ex == null)
                {
                    return;
                }

                ctx.Monitor?.Log($"[FireAndForget] '{ctx.Name}' failed: {ex}", LogLevel.Error);
                ModErrorLog.LogError("FireAndForget", $"'{ctx.Name}' failed", ex);
            },
            new FireAndForgetContext(opId, name, monitor),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static long _fireAndForgetSequence;

    private sealed class FireAndForgetContext
    {
        public FireAndForgetContext(string opId, string name, IMonitor? monitor)
        {
            OpId = opId;
            Name = name;
            Monitor = monitor;
        }

        public string OpId { get; }

        public string Name { get; }

        public IMonitor? Monitor { get; }
    }
}
