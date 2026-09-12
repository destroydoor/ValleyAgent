#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.Infrastructure;

/// <summary>
///     生产 mod 主线程卡死看门狗（2026-09-11 生产化仪器，移植自 TestMod 同名类）。
///     3 人联机主机无报错整机卡死至今未定罪（docs/plan/2026-09-10-host-freeze-root-cause.md），
///     发行包不含 TestMod，实机两次崩溃零现场证据——所以仪器必须随生产 mod 分发，
///     不能依赖测试环境。Mod Entry 即布防，Host/ThinClient/Inert 全模式覆盖
///     （Inert 也能采集"原版也崩"的对照组，用于排除/坐实本 mod 嫌疑）。
///     主线程每 tick 调 <see cref="Beat"/>；专用后台线程检测心跳停滞，
///     超过阈值（默认 5s，VALLEY_WATCHDOG_MS 覆盖，&lt;=0 关闭）时用 dbghelp MiniDumpWriteDump
///     落 .dmp（含全部线程栈）+ 伴随日志。同一轮询线程还顺带轮询后台操作停滞
///     （<see cref="StuckOperationTracker"/>，VALLEY_STUCKOP_MS，默认 60s，&lt;=0 关闭），
///     落 stuck-*.dmp——候选 2/3（后台死循环/字典损坏）主线程可能还活着，5s 冻结检测抓不到。
///     每个停滞事件只 dump 一次（心跳恢复后重新武装），最多保留最近 5 份转储。
///     所有告警双通道留痕：SMAPI Monitor（Error）+ ModErrorLog（立即写文件兜底，
///     SMAPI 日志缓冲在卡死现场可能永远不落盘）。
/// </summary>
public sealed class MainThreadWatchdog
{
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    private const int MaxDumpsToKeep = 5;
    private const int PollIntervalMs = 250;

    private readonly string _dumpDirectory;
    private readonly IMonitor _monitor;

    // 后台操作停滞阈值（VALLEY_STUCKOP_MS，<=0 = 跳过检查）
    private readonly long _stuckOpThresholdMs;
    private readonly long _thresholdMs;
    private readonly Thread _thread;

    // 主线程写 / 看门狗线程读；Interlocked 保证可见性
    private long _lastBeatTimestamp;

    // 首次心跳门控：游戏加载期（SMAPI 初始化/读档）主线程本来就不 tick，
    // 不算停滞——收到第一拍之后才开始评估（防加载期误报，2026-09-09 容器实测）。
    private bool _sawFirstBeat;

    // 停滞事件去重：0=武装中，1=本事件已 dump
    private int _dumpedForCurrentStall;

    private MainThreadWatchdog(string dumpDirectory, IMonitor monitor, long thresholdMs, long stuckOpThresholdMs)
    {
        _dumpDirectory = dumpDirectory;
        _monitor = monitor;
        _thresholdMs = thresholdMs;
        _stuckOpThresholdMs = stuckOpThresholdMs;
        _lastBeatTimestamp = Environment.TickCount64;

        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "ValleyAgent.MainThreadWatchdog"
        };
        _thread.Start();
    }

    /// <summary>
    ///     当前已启动的生产看门狗实例（TestMod 据此互斥：两个 MiniDumpWriteDump 并发写同一进程会互相干扰）。
    ///     Start 成功时赋值，进程生命周期内不重置。
    /// </summary>
    public static MainThreadWatchdog? Current { get; private set; }

    /// <summary>
    ///     阈值解析纯函数：envValue 为 null/空白/非法 → defaultMs；解析成功原样返回
    ///     （含 0 和负数——禁用策略由 Start 判定，纯函数不做策略，便于单测）。
    /// </summary>
    internal static long ResolveThresholdMs(string? envValue, long defaultMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(envValue)
            || !long.TryParse(envValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultMs;
        }

        return parsed;
    }

    /// <summary>启动看门狗。thresholdMs&lt;=0 时返回 null（禁用）。</summary>
    public static MainThreadWatchdog? Start(string modDirectory, IMonitor monitor)
    {
        // 默认阈值 5s（2026-09-10 修正）：3s 会在读档期慢 tick 误报；真冻结是无限期停摆，5s 不漏
        var configured = ResolveThresholdMs(Environment.GetEnvironmentVariable("VALLEY_WATCHDOG_MS"));
        if (configured <= 0)
        {
            monitor.Log("[Watchdog] Disabled via VALLEY_WATCHDOG_MS<=0", LogLevel.Info);
            return null;
        }

        // 后台操作停滞阈值独立配置：默认 60s（一次 LLM 决策/好感度落账的正常上限远小于此）
        var stuckOpMs = ResolveThresholdMs(Environment.GetEnvironmentVariable("VALLEY_STUCKOP_MS"), 60000);

        var dumpDirectory = Path.Combine(modDirectory, "watchdog");
        Directory.CreateDirectory(dumpDirectory);
        var watchdog = new MainThreadWatchdog(dumpDirectory, monitor, configured, stuckOpMs);

        // StuckOperationTracker 的 Begin 覆盖告警用同一 monitor（Start 是唯一确定有 monitor 的时机）
        StuckOperationTracker.Monitor = monitor;
        Current = watchdog;
        monitor.Log(
            $"[Watchdog] Armed on main thread; stall threshold {configured}ms, stuck-op threshold {stuckOpMs}ms, dumps → {dumpDirectory}",
            LogLevel.Info);
        return watchdog;
    }

    /// <summary>主线程心跳：UpdateTicked 每 tick 调用一次。</summary>
    public void Beat()
    {
        Interlocked.Exchange(ref _lastBeatTimestamp, Environment.TickCount64);
        Volatile.Write(ref _sawFirstBeat, true);
        Interlocked.Exchange(ref _dumpedForCurrentStall, 0);
    }

    private void WatchLoop()
    {
        while (true)
        {
            Thread.Sleep(PollIntervalMs);

            CheckMainThreadStall();

            // 同一线程顺带轮询后台操作停滞：候选 2/3 是后台死循环，主线程可能仍在正常 tick
            PollStuckOperations();
        }
    }

    private void CheckMainThreadStall()
    {
        // 游戏尚未开始 tick（还在 SMAPI 初始化/读档）→ 不评估
        if (!Volatile.Read(ref _sawFirstBeat))
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastBeatTimestamp);
        var stall = now - last;
        if (stall < _thresholdMs)
        {
            return;
        }

        // 每个停滞事件只 dump 一次；心跳未恢复前不再重复落盘
        if (Interlocked.CompareExchange(ref _dumpedForCurrentStall, 1, 0) != 0)
        {
            return;
        }

        CaptureDump("freeze", stall, $"Main thread stalled {stall:F0}ms (> {_thresholdMs}ms)");
    }

    private void PollStuckOperations()
    {
        if (_stuckOpThresholdMs <= 0)
        {
            return; // VALLEY_STUCKOP_MS<=0 显式禁用后台操作检查
        }

        foreach (var (opId, detail, elapsedMs) in StuckOperationTracker.TakeNewlyStalled(_stuckOpThresholdMs))
        {
            var message =
                $"[Watchdog] Background operation '{opId}' stalled {elapsedMs:F0}ms (> {_stuckOpThresholdMs}ms), "
                + $"detail: {detail} — capturing dump";
            _monitor.Log(message, LogLevel.Error);
            ModErrorLog.LogError("Watchdog", message);
            CaptureDump($"stuck-{SanitizeOpId(opId)}", elapsedMs,
                $"Background operation '{opId}' stalled {elapsedMs:F0}ms (> {_stuckOpThresholdMs}ms)");
        }
    }

    private void CaptureDump(string filePrefix, long stallMs, string reason)
    {
        var timestamp = DateTime.Now;
        var baseName = $"{filePrefix}-{timestamp:yyyyMMdd-HHmmss}";
        try
        {
            var dmpPath = Path.Combine(_dumpDirectory, baseName + ".dmp");
            var written = WriteDump(dmpPath);

            var logPath = Path.Combine(_dumpDirectory, baseName + ".log");
            File.WriteAllText(logPath, BuildCompanionLog(timestamp, reason, stallMs, dmpPath, written));

            PruneOldDumps();

            var message = $"{reason}. " + (written ? $"Dump captured: {dmpPath}" : "MiniDumpWriteDump FAILED — see companion log");
            // 双通道留痕：SMAPI 日志缓冲在卡死现场可能不落盘，ModErrorLog 立即写文件是兜底
            _monitor.Log($"[Watchdog] {message}", LogLevel.Error);
            ModErrorLog.LogError("Watchdog", message);
        }
        catch (Exception ex)
        {
            // 看门狗自身绝不能把进程带崩：兜底留痕到稳定位置（同样双通道）
            var failure = $"Capture failed ({reason}): {ex}";
            _monitor.Log($"[Watchdog] {failure}", LogLevel.Error);
            ModErrorLog.LogError("Watchdog", failure);
        }
    }

    private bool WriteDump(string dmpPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux（容器验证环境）：进程内自产 core 需要派生 createdump 子进程，
            // 其可变参数/工作目录写法被安全扫描零容忍拦截（2026-09-09 实测三次）——
            // 停滞现场以伴随日志为准；需要线程栈时用伴随日志里的 pid 外部触发：
            //   <dotnet>/shared/Microsoft.NETCore.App/*/createdump --with-threadinfo -f <path> <pid>
            return false;
        }

        using var stream = new FileStream(dmpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var ok = MiniDumpWriteDump(
            GetCurrentProcess(),
            GetCurrentProcessId(),
            stream.SafeFileHandle.DangerousGetHandle(),
            MiniDumpWithHandleData | MiniDumpWithThreadInfo,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        stream.Flush();
        return ok && stream.Length > 0;
    }

    private string BuildCompanionLog(DateTime timestamp, string reason, long stallMs, string dmpPath, bool dumpOk)
    {
        var process = Process.GetCurrentProcess();
        var sb = new StringBuilder();
        sb.AppendLine($"ValleyAgent main-thread watchdog capture @ {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"reason             : {reason}");
        sb.AppendLine($"stallMs            : {stallMs:F0} (threshold {_thresholdMs})");
        sb.AppendLine($"dumpPath           : {dmpPath} ({(dumpOk ? "written" : "FAILED")})");
        sb.AppendLine($"process            : {process.ProcessName} pid={process.Id} 64bit={Environment.Is64BitProcess}");
        sb.AppendLine($"uptime             : {process.StartTime:yyyy-MM-dd HH:mm:ss} → now");
        sb.AppendLine("gameContext        :");
        sb.AppendLine(BuildGameContext());
        sb.AppendLine("threads            : (coarse per-OS-thread state; exact stacks live in the .dmp)");
        sb.AppendLine(BuildThreadOverview());
        sb.AppendLine($"runningOps         : {StuckOperationTracker.DescribeRunning()}");
        sb.AppendLine("analysis           : dotnet-dump analyze <dmp>  →  clrthreads / clrstack（找主线程即 game tick 线程）");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux 容器不进程内产 dump（安全扫描约束）——用本 pid 在容器内外部抓栈
            sb.AppendLine(
                "linuxHint          : docker exec <容器> sh -c '<dotnet>/shared/Microsoft.NETCore.App/*/createdump "
                + $"--with-threadinfo -f /data/Mods/ValleyAgent/watchdog/hang.dmp {process.Id}'");
        }

        sb.AppendLine("note               : SMAPI 完整日志见 %appdata%\\StardewValley\\ErrorLogs（卡死时通常无新增条目——这正是本仪器的价值）");
        return sb.ToString();
    }

    /// <summary>
    ///     游戏上下文取证：timeOfDay 直接对应实机"过午崩溃"的时间相关性（2026-09-10 台账），
    ///     season/day/playerLocation 给出事发场景，multiplayerMode/isMaster/otherFarmers 区分主机与房客。
    /// </summary>
    private static string BuildGameContext()
    {
        try
        {
            return
                $"  timeOfDay={Game1.timeOfDay} date={Game1.season} {Game1.dayOfMonth}\n"
                + $"  playerLocation={Game1.player?.currentLocation?.Name ?? "unknown"}\n"
                + $"  multiplayerMode={Game1.multiplayerMode} isMaster={Game1.IsMasterGame} otherFarmers={Game1.otherFarmers.Count}";
        }
        catch (Exception)
        {
            // 早期初始化/状态损坏读不到时，不能让整份伴随日志失败——留"读不到"本身也是线索
            return "  unavailable";
        }
    }

    /// <summary>线程概览表（粗粒度信号：仅 OS 线程状态/等待原因；精确托管栈以 .dmp 的 clrthreads/clrstack 为准）。</summary>
    private static string BuildThreadOverview()
    {
        var sb = new StringBuilder();
        try
        {
            var threads = Process.GetCurrentProcess().Threads;
            sb.AppendLine($"  threadCount={threads.Count}");
            foreach (ProcessThread thread in threads)
            {
                try
                {
                    // WaitReason 仅在 Wait 态有意义，其余状态读取可能抛异常
                    var waitReason = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                        ? thread.WaitReason.ToString()
                        : "-";
                    sb.AppendLine($"  tid={thread.Id} state={thread.ThreadState} waitReason={waitReason}");
                }
                catch (Exception)
                {
                    sb.AppendLine($"  tid={thread.Id} state=<unreadable>");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  <thread enumeration failed: {ex.Message}>");
        }

        return sb.ToString();
    }

    private static string SanitizeOpId(string opId) =>
        new(opId.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    private void PruneOldDumps()
    {
        // 按写入时间保留最近 5 份；不能按文件名排序——freeze-* 与 stuck-* 前缀不同，
        // 跨前缀时名字序≠时间序（例如会把新 stuck 挤掉而留下旧 freeze）
        var dumps = Directory.GetFiles(_dumpDirectory, "*.dmp");
        if (dumps.Length <= MaxDumpsToKeep)
        {
            return;
        }

        var stale = dumps
            .Select(path => (Path: path, WrittenAt: TryGetWriteTimeUtc(path)))
            .Where(entry => entry.WrittenAt.HasValue)
            .OrderByDescending(entry => entry.WrittenAt!.Value)
            .Skip(MaxDumpsToKeep)
            .Select(entry => entry.Path)
            .ToList();
        foreach (var path in stale)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 清理失败不影响主职责
            }
        }
    }

    private static DateTime? TryGetWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
}
