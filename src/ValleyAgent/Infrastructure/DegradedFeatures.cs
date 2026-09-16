using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ValleyAgent.Infrastructure;

/// <summary>一条降级记录：哪个特性 / 为什么降级 / 归因异常类型 / 报告时间（本地时区）。</summary>
public sealed record DegradedFeatureRecord(string Feature, string Reason, string? ExceptionType, DateTime ReportedAt)
{
    public override string ToString()
    {
        var ex = ExceptionType is null ? null : $" [{ExceptionType}]";
        return $"{Feature} — {Reason}{ex}（{ReportedAt:HH:mm:ss}）";
    }
}

/// <summary>
///     初始化降级特性登记簿（issue #25，异常处理审计 PR3）。
///     审计前提："装上了但某个子系统坏了"的玩家拿不到降级模式——初始化链条任一环抛出即
///     半初始化（已注册服务留在容器、半套 Harmony 补丁生效）且无任何标记。
///     本登记簿是降级状态的**唯一事实源**：初始化各段（ServiceInitializer / Harmony 注册 /
///     OnSaveLoadedInitialize 兜底）失败时把段名 + 原因摘要记进来并继续下一段；
///     <c>ValleyAgent_status</c> 与 <c>ValleyAgent_diag</c> 都从这里读取降级项，
///     玩家据此能回答"AI 为什么没反应"。
///     静态可达（与 EventGuard/ModErrorLog 同范式）：登记点分散在三个初始化类 +
///     两个命令出口，实例注入需要把容器传递拉进所有构造链，收益不成比例。
/// </summary>
public static class DegradedFeatures
{
    private static readonly object Lock = new();
    private static readonly List<DegradedFeatureRecord> Records = new();

    /// <summary>是否存在任何降级项。</summary>
    public static bool Any
    {
        get
        {
            lock (Lock)
            {
                return Records.Count > 0;
            }
        }
    }

    /// <summary>
    ///     登记一条降级。不抛异常、不落日志——留痕由调用方负责（初始化段通常已有 Error 日志 +
    ///     ModErrorLog 落盘），本方法只做状态登记。
    /// </summary>
    /// <param name="feature">特性/段名（如 "harmony:NPCGiftPatch" / "service:data-bios"）。</param>
    /// <param name="reason">失败原因摘要（玩家可读，一行以内）。</param>
    /// <param name="ex">归因异常（可空——依赖段未就绪而主动跳过时没有异常）。</param>
    public static void Report(string feature, string reason, Exception? ex = null)
    {
        lock (Lock)
        {
            Records.Add(new DegradedFeatureRecord(feature, reason, ex?.GetType().Name, DateTime.Now));
        }
    }

    /// <summary>当前全部降级记录（快照，只读）。</summary>
    public static IReadOnlyList<DegradedFeatureRecord> Snapshot()
    {
        lock (Lock)
        {
            return Records.ToList();
        }
    }

    /// <summary>
    ///     玩家可读汇总：无降级返回 "none"，否则每条一行（带序号）。
    ///     ValleyAgent_status / ValleyAgent_diag 共用同一格式。
    /// </summary>
    public static string Describe()
    {
        lock (Lock)
        {
            if (Records.Count == 0)
            {
                return "none";
            }

            var sb = new StringBuilder();
            for (var i = 0; i < Records.Count; i++)
            {
                _ = sb.AppendLine($"  {i + 1}. {Records[i]}");
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>清空记录。仅供单元测试隔离静态状态；生产生命周期内登记即保留（初始化一次性的）。</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Records.Clear();
        }
    }
}
