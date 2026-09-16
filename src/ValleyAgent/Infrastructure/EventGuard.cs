#nullable enable
using System;
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
            if (QueueTelemetry.ShouldWarn($"safeseg:{segmentName}"))
            {
                var message = $"[SafeRun] segment '{segmentName}' failed (downstream segments unaffected): {ex}";
                monitor?.Log(message, LogLevel.Error);
                ModErrorLog.LogError("SafeRun", message, ex);
            }
            else
            {
                // 节流窗口内的重复故障：不丢可观测性——Trace 级留一行（类型+消息，不落盘防日志膨胀）
                monitor?.Log(
                    $"[SafeRun] segment '{segmentName}' failed again (throttled): {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Trace);
            }
        }
    }
}
