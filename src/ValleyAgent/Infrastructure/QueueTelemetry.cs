#nullable enable
using System;
using System.Collections.Concurrent;
using StardewModdingAPI;

namespace ValleyAgent.Infrastructure;

/// <summary>
///     主线程队列深度遥测（2026-09-11 生产化仪器）。
///     本 mod 的所有主线程泵（ws-commands / pre-speak / dialogue-replies / gift-actions /
///     chat-replies / host-request-mainthread ...）每 tick 全量排水，稳态深度应≈0；
///     深度爆表 = 消费侧（主线程泵）停摆的早期信号——早于 5s 看门狗阈值，
///     且不需要 dump 就能在 SMAPI 日志 / ModErrorLog 里看到。
/// </summary>
public static class QueueTelemetry
{
    // 同 key 上次告警放行时间（TickCount64）；单测注入时钟用
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    private static readonly ConcurrentDictionary<string, long> LastReport = new(StringComparer.Ordinal);

    /// <summary>
    ///     节流闸门：同 key 在 intervalMs 内只放行一次（放行时记录本次时间）。
    ///     高频入队点每 tick 调用，放行间隔之外的调用必须零成本返回。
    /// </summary>
    public static bool ShouldWarn(string key, int intervalMs = 5000)
    {
        var now = Clock();
        // 首次调用视为"距上次已过一个完整间隔"，直接放行
        var last = LastReport.GetOrAdd(key, _ => now - intervalMs);
        if (now - last < intervalMs)
        {
            return false;
        }

        // TryUpdate：并发下同一"上次时间"只有一个告警放行，不会重复刷屏
        return LastReport.TryUpdate(key, now, last);
    }

    /// <summary>
    ///     队列深度告警：depth &gt;= threshold 且通过节流时双通道留痕。
    ///     monitor 可为 null（静态入队点无 monitor 实例时仅落 ModErrorLog）。
    ///     阈值取 100 的理由：泵每 tick 全量排水，稳态深度应≈0；
    ///     &gt;=100 说明消费侧停摆而非偶发毛刺（一两个慢消费者来不及排）。
    /// </summary>
    public static void WarnIfDeep(string queueId, int depth, IMonitor? monitor, int threshold = 100)
    {
        if (depth < threshold || !ShouldWarn(queueId))
        {
            return;
        }

        var message =
            $"[QueueTelemetry] queue '{queueId}' backlogged: depth={depth} (threshold={threshold}) — consumer stalled?";
        monitor?.Log(message, LogLevel.Warn);
        ModErrorLog.LogError("QueueTelemetry", message);
    }

    /// <summary>清节流状态并还原默认时钟（单测隔离用，运行时不得调用）。</summary>
    internal static void ResetForTests()
    {
        LastReport.Clear();
        Clock = static () => Environment.TickCount64;
    }
}
