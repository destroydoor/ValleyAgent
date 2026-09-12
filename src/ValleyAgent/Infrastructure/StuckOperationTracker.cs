#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StardewModdingAPI;

namespace ValleyAgent.Infrastructure;

/// <summary>
///     后台操作停滞追踪器（2026-09-11 生产化仪器）。
///     猎捕 2026-09-10 结案文档候选 2/3（后台线程死循环 / 集合损坏）：这两类故障
///     无异常、无日志、主线程看门狗抓不到（主线程可能还在正常 tick）——
///     本类是它们唯一的主动观测面。
///     用法：后台操作入口 <see cref="Begin"/> / 出口 finally <see cref="End"/>；
///     <see cref="MainThreadWatchdog"/> 的轮询线程每 250ms 调 <see cref="TakeNewlyStalled"/>，
///     运行超过阈值（VALLEY_STUCKOP_MS，默认 60s）的操作会被点名并落 stuck-*.dmp。
/// </summary>
public static class StuckOperationTracker
{
    // 单测注入用：默认单调时钟（TickCount64 不受系统时间回拨影响）
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    // 可选 monitor：Begin 覆盖告警（re-begin without End）用它落 SMAPI 日志。
    // 由 MainThreadWatchdog.Start 注入（那是唯一确定持有 monitor 的时机）。
    internal static IMonitor? Monitor { get; set; }

    private static readonly ConcurrentDictionary<string, OpRecord> Running = new(StringComparer.Ordinal);

    /// <summary>标记一段后台操作开始。若 opId 已在运行（前次未 End，可能停滞后泄漏），告警后覆盖。</summary>
    public static void Begin(string opId, string detail)
    {
        if (string.IsNullOrWhiteSpace(opId))
        {
            return;
        }

        if (Running.TryGetValue(opId, out var previous))
        {
            Monitor?.Log(
                $"[StuckOp] '{opId}' re-begin without End（前次未完成，可能停滞后泄漏；前次已运行 {Clock() - previous.StartMs}ms）",
                LogLevel.Warn);
        }

        Running[opId] = new OpRecord(Clock(), detail);
    }

    /// <summary>标记操作结束（含已报状态一并移除；之后再 Begin 可重新触发停滞上报）。</summary>
    public static void End(string opId)
    {
        _ = Running.TryRemove(opId, out _);
    }

    /// <summary>
    ///     取"运行超过阈值且本次停滞尚未上报"的操作并标记已报。同一次停滞只返回一次；
    ///     End 后重新 Begin 可再次触发。由看门狗线程轮询调用，Begin/End 在工作线程——并发安全。
    /// </summary>
    internal static List<(string OpId, string Detail, long ElapsedMs)> TakeNewlyStalled(long thresholdMs)
    {
        var now = Clock();
        var stalled = new List<(string OpId, string Detail, long ElapsedMs)>();
        foreach (var kvp in Running)
        {
            var elapsed = now - kvp.Value.StartMs;
            if (elapsed < thresholdMs)
            {
                continue;
            }

            // CompareExchange 保证同一次停滞只有一个轮询线程拿到上报权
            if (Interlocked.CompareExchange(ref kvp.Value.Reported, 1, 0) != 0)
            {
                continue;
            }

            stalled.Add((kvp.Key, kvp.Value.Detail, elapsed));
        }

        return stalled;
    }

    /// <summary>列出所有运行中操作及已运行时长（无则 "none"），供看门狗伴随日志取证。</summary>
    internal static string DescribeRunning()
    {
        var now = Clock();
        var snapshot = Running.ToArray();
        return snapshot.Length == 0
            ? "none"
            : string.Join("; ", snapshot.Select(kv => $"'{kv.Key}' {now - kv.Value.StartMs}ms ({kv.Value.Detail})"));
    }

    /// <summary>清全部状态并还原默认时钟（单测隔离用，运行时不得调用）。</summary>
    internal static void ResetForTests()
    {
        Running.Clear();
        Monitor = null;
        Clock = static () => Environment.TickCount64;
    }

    private sealed class OpRecord
    {
        public readonly string Detail;

        // 0=未上报，1=本次停滞已上报（Interlocked 保证看门狗线程与 Begin/End 线程的可见性）
        public int Reported;

        public readonly long StartMs;

        public OpRecord(long startMs, string detail)
        {
            StartMs = startMs;
            Detail = detail;
        }
    }
}
