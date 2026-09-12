#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     分模块日志输出器：按测试分组分离日志文件，同时输出结构化 JSON 和可读文本。
///     每个分组一个日志文件，避免所有测试输出混在一起。
/// </summary>
public sealed class TestLogger : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _baseDir;
    private readonly List<GlobalLogEntry> _globalEntries = new();
    private readonly ConcurrentDictionary<string, ModuleLog> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor _monitor;

    private readonly string _runTimestamp;
    private bool _disposed;

    public TestLogger(string baseDir, string runTimestamp, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(baseDir);
        ArgumentNullException.ThrowIfNull(runTimestamp);
        ArgumentNullException.ThrowIfNull(monitor);

        _baseDir = baseDir;
        _runTimestamp = runTimestamp;
        _monitor = monitor;
        _ = Directory.CreateDirectory(baseDir);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Flush();

        foreach (var kvp in _modules)
        {
            kvp.Value.Dispose();
        }
    }

    /// <summary>记录一条测试日志到指定模块。</summary>
    public void Log(string module, LogLevel level, string message)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        ArgumentNullException.ThrowIfNull(module);

        var entry = new LogEntry(DateTime.Now, level, message);
        _modules.GetOrAdd(module, _ => new ModuleLog(module, _baseDir, _runTimestamp))
            .Entries.Add(entry);

        // 同时输出到 SMAPI 日志（带模块前缀）
        _monitor.Log($"[{module}] {message}", level);

        // 记录到全局日志
        _globalEntries.Add(new GlobalLogEntry(DateTime.Now, module, level, message));
    }

    /// <summary>记录测试开始事件。</summary>
    public void TestStarted(string testName, string group) => Log(group, LogLevel.Info, $"▶ 开始测试: {testName}");

    /// <summary>记录测试完成事件。</summary>
    public void TestCompleted(string testName, string group, int passed, int failed, bool timedOut)
    {
        var status = timedOut ? "超时" : failed > 0 ? "失败" : "通过";
        var color = timedOut ? LogLevel.Warn : failed > 0 ? LogLevel.Error : LogLevel.Info;
        Log(group, color, $"■ 测试完成: {testName} — {passed}通过/{failed}失败 ({status})");
    }

    /// <summary>记录断言结果。</summary>
    public void Assertion(string module, string label, bool passed, string detail, int score = 0)
    {
        var prefix = passed ? "[PASS]" : "[FAIL]";
        var scoreStr = score > 0 ? $" (评分:{score})" : "";
        var detailStr = string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
        var level = passed ? LogLevel.Info : LogLevel.Error;
        Log(module, level, $"{prefix} {label}{detailStr}{scoreStr}");
    }

    /// <summary>记录评分断言结果。</summary>
    public void ScoredAssertion(string module, string label, int score, string detail)
    {
        var level = score >= 4 ? LogLevel.Info : score >= 3 ? LogLevel.Warn : LogLevel.Error;
        Log(module, level, $"[SCORE:{score}/5] {label} — {detail}");
    }

    /// <summary>将所有模块日志写入磁盘。</summary>
    public void Flush()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        foreach (var kvp in _modules)
        {
            kvp.Value.Flush();
        }

        // 写全局日志
        WriteGlobalLog();
    }

    private void WriteGlobalLog()
    {
        var runDir = Path.Combine(_baseDir, _runTimestamp);
        _ = Directory.CreateDirectory(runDir);

        // 全局可读文本日志
        var textPath = Path.Combine(runDir, "_full_log.txt");
        using (var w = new StreamWriter(textPath, true))
        {
            foreach (var entry in _globalEntries)
            {
                var prefix = entry.Level switch
                {
                    LogLevel.Error => "ERR",
                    LogLevel.Warn => "WRN",
                    LogLevel.Info => "INF",
                    LogLevel.Debug => "DBG",
                    LogLevel.Trace => "TRC",
                    _ => "???"
                };
                w.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{prefix}] [{entry.Module}] {entry.Message}");
            }
        }

        // 全局结构化 JSON 日志
        var jsonPath = Path.Combine(runDir, "_full_log.json");
        var data = _globalEntries.Select(e => new
        {
            timestamp = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            module = e.Module,
            level = e.Level.ToString(),
            message = e.Message
        });
        var json = JsonSerializer.Serialize(data, s_jsonOpts);
        File.WriteAllText(jsonPath, json);
    }

    // ── 内部类型 ──

    private sealed class ModuleLog : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _module;
        private readonly string _runTimestamp;
        public readonly List<LogEntry> Entries = new();
        private bool _flushed;

        public ModuleLog(string module, string baseDir, string runTimestamp)
        {
            _module = module;
            _baseDir = baseDir;
            _runTimestamp = runTimestamp;
        }

        public void Dispose() => Flush();

        public void Flush()
        {
            if (_flushed || Entries.Count == 0)
            {
                return;
            }

            _flushed = true;
            var runDir = Path.Combine(_baseDir, _runTimestamp);
            _ = Directory.CreateDirectory(runDir);

            var safeName = _module.Replace(" ", "_");

            // 可读文本日志
            var textPath = Path.Combine(runDir, $"{safeName}_log.txt");
            using (var w = new StreamWriter(textPath))
            {
                w.WriteLine($"=== {_module} 模块日志 ===");
                w.WriteLine($"时间戳: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"条目数: {Entries.Count}");
                w.WriteLine();

                foreach (var entry in Entries)
                {
                    var prefix = entry.Level switch
                    {
                        LogLevel.Error => "ERR",
                        LogLevel.Warn => "WRN",
                        LogLevel.Info => "INF",
                        LogLevel.Debug => "DBG",
                        LogLevel.Trace => "TRC",
                        _ => "???"
                    };
                    w.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{prefix}] {entry.Message}");
                }
            }

            // 结构化 JSON 日志
            var jsonPath = Path.Combine(runDir, $"{safeName}_log.json");
            var data = new
            {
                module = _module,
                timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                entry_count = Entries.Count,
                entries = Entries.Select(e => new
                {
                    time = e.Timestamp.ToString("HH:mm:ss.fff"),
                    level = e.Level.ToString(),
                    message = e.Message
                })
            };
            var json = JsonSerializer.Serialize(data, s_jsonOpts);
            File.WriteAllText(jsonPath, json);
        }
    }

    private sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message);

    private sealed record GlobalLogEntry(DateTime Timestamp, string Module, LogLevel Level, string Message);
}