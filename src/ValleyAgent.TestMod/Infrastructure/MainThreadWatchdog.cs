#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using StardewModdingAPI;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     游戏主线程卡死看门狗（2026-09-09 房主 3-4 人随机无日志卡死猎捕，Phase 4 纯仪器不改行为）。
///     主线程每 tick 调 <see cref="Beat"/>；专用后台线程检测心跳停滞，
///     超过阈值（默认 3s，可用环境变量 VALLEY_WATCHDOG_MS 覆盖，0=关闭）时：
///     用 dbghelp MiniDumpWriteDump 对当前进程落一份 .dmp（含全部线程栈）+ 一份伴随日志。
///     卡死现场的主线程栈是"卡在哪"的唯一直接证据——SMAPI 日志对此完全沉默。
///     host / farmhand 实例都会装载 TestMod，因此两侧自动同时挂上看门狗。
///     每个停滞事件只 dump 一次（心跳恢复后重新武装），最多保留最近 5 份转储。
/// </summary>
public sealed class MainThreadWatchdog
{
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    private const int MaxDumpsToKeep = 5;
    private const int PollIntervalMs = 250;

    private readonly string _dumpDirectory;
    private readonly IMonitor _monitor;
    private readonly long _thresholdMs;
    private readonly Thread _thread;

    // 主线程写 / 看门狗线程读；Interlocked 保证可见性
    private long _lastBeatTimestamp;

    // 首次心跳门控：游戏加载期（SMAPI 初始化/读档）主线程本来就不 tick，
    // 不算停滞——收到第一拍之后才开始评估（防加载期误报，2026-09-09 容器实测）。
    private bool _sawFirstBeat;

    // 停滞事件去重：0=武装中，1=本事件已 dump
    private int _dumpedForCurrentStall;

    private MainThreadWatchdog(string dumpDirectory, IMonitor monitor, long thresholdMs)
    {
        _dumpDirectory = dumpDirectory;
        _monitor = monitor;
        _thresholdMs = thresholdMs;
        _lastBeatTimestamp = Environment.TickCount64;

        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "ValleyAgent.TestMod.MainThreadWatchdog"
        };
        _thread.Start();
    }

        /// <summary>启动看门狗。thresholdMs&lt;=0 时返回 null（禁用）。</summary>
        public static MainThreadWatchdog? Start(string modDirectory, IMonitor monitor)
        {
            // 2026-09-10 修复：默认阈值 5s——3s 会在读档期慢 tick 误报
            // （容器实测布防后 5s 内 3234ms 停滞假阳性；真冻结是无限期停摆，5s 不漏）。
            var configured = 5000L;
        var env = Environment.GetEnvironmentVariable("VALLEY_WATCHDOG_MS");
        if (long.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            configured = parsed;
        }

        if (configured <= 0)
        {
            monitor.Log("[Watchdog] Disabled via VALLEY_WATCHDOG_MS<=0", LogLevel.Info);
            return null;
        }

        var dumpDirectory = Path.Combine(modDirectory, "watchdog");
        Directory.CreateDirectory(dumpDirectory);
        var watchdog = new MainThreadWatchdog(dumpDirectory, monitor, configured);
        monitor.Log($"[Watchdog] Armed on main thread; stall threshold {configured}ms, dumps → {dumpDirectory}",
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

            // 游戏尚未开始 tick（还在 SMAPI 初始化/读档）→ 不评估
            if (!Volatile.Read(ref _sawFirstBeat))
            {
                continue;
            }

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastBeatTimestamp);
            var stall = now - last;
            if (stall < _thresholdMs)
            {
                continue;
            }

            // 每个停滞事件只 dump 一次；心跳未恢复前不再重复落盘
            if (Interlocked.CompareExchange(ref _dumpedForCurrentStall, 1, 0) != 0)
            {
                continue;
            }

            CaptureDump(stall);
        }
    }

    private void CaptureDump(long stallMs)
    {
        var timestamp = DateTime.Now;
        var baseName = $"freeze-{timestamp:yyyyMMdd-HHmmss}";
        try
        {
            var dmpPath = Path.Combine(_dumpDirectory, baseName + ".dmp");
            var written = WriteDump(dmpPath);

            var logPath = Path.Combine(_dumpDirectory, baseName + ".log");
            File.WriteAllText(logPath, BuildCompanionLog(timestamp, stallMs, dmpPath, written));

            PruneOldDumps();

            _monitor.Log(
                $"[Watchdog] Main thread stalled {stallMs:F0}ms (> {_thresholdMs}ms). "
                + (written ? $"Dump captured: {dmpPath}" : "MiniDumpWriteDump FAILED — see companion log"),
                LogLevel.Error);
        }
        catch (Exception ex)
        {
            // 看门狗自身绝不能把进程带崩：兜底留痕到稳定位置
            _monitor.Log($"[Watchdog] Capture failed: {ex}", LogLevel.Error);
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

    private string BuildCompanionLog(DateTime timestamp, long stallMs, string dmpPath, bool dumpOk)
    {
        var process = Process.GetCurrentProcess();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ValleyAgent.TestMod main-thread watchdog capture @ {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"stallMs            : {stallMs:F0} (threshold {_thresholdMs})");
        sb.AppendLine($"dumpPath           : {dmpPath} ({(dumpOk ? "written" : "FAILED")})");
        sb.AppendLine($"process            : {process.ProcessName} pid={process.Id} 64bit={Environment.Is64BitProcess}");
        sb.AppendLine($"uptime             : {process.StartTime:yyyy-MM-dd HH:mm:ss} → now");
        sb.AppendLine($"runtimeMode        : multiplayerMode={(object)StardewValley.Game1.multiplayerMode} "
                      + $"isMaster={StardewValley.Game1.IsMasterGame} otherFarmers={StardewValley.Game1.otherFarmers.Count}");
        sb.AppendLine("analysis           : dotnet-dump analyze <dmp>  →  clrthreads / clrstack（找主线程即 game tick 线程）");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux 容器不进程内产 dump（安全扫描约束）——用本 pid 在容器内外部抓栈
            sb.AppendLine(
                "linuxHint          : docker exec <容器> sh -c '<dotnet>/shared/Microsoft.NETCore.App/*/createdump "
                + $"--with-threadinfo -f /data/Mods/ValleyAgent.TestMod/watchdog/hang.dmp {process.Id}'");
        }
        sb.AppendLine("note               : SMAPI 完整日志见 %appdata%\\StardewValley\\ErrorLogs（卡死时通常无新增条目——这正是本仪器的价值）");
        return sb.ToString();
    }

    private void PruneOldDumps()
    {
        var dumps = Directory.GetFiles(_dumpDirectory, "freeze-*.dmp");
        if (dumps.Length <= MaxDumpsToKeep)
        {
            return;
        }

        Array.Sort(dumps, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in dumps[..^MaxDumpsToKeep])
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // 清理失败不影响主职责
            }
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
