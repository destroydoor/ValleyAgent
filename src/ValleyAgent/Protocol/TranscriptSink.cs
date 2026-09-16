using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Inventory;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Protocol;

/// <summary>
///     C# 侧 JSONL 全量留痕：状态转换 + 命令执行结果（action_result）。
///     写入线程模型：任意线程 Enqueue，游戏主线程 Drain（UpdateTicked）。
///     文件布局：{root}/{gameDate}/{npcName}.jsonl，每行一个 JSON 事件。
///     设计文档：docs/design/2026-08-01-memory-narrative-extensibility.md §1。
/// </summary>
public sealed class TranscriptSink : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IMonitor _monitor;
    private readonly ConcurrentQueue<string> _queue = new();

    private readonly string _root;

    // 缓存 StreamWriter：key=(gameDate, npcName)，避免每条事件重新打开文件
    private readonly ConcurrentDictionary<(string gameDate, string npc), StreamWriter> _writers = new();

    private bool _disposed;

    // 记录当前每个 writer 对应的 gameDate/npc，用于轮转检测
    private (string gameDate, string npc) _lastKey = ("", "");

    public TranscriptSink(IMonitor monitor, string root)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
    }

    /// <summary>
    ///     入队一条 JSONL 事件。线程安全，永不抛异常（catch + Warn）。
    ///     任意线程可调用——实际写入延迟到 Drain（主线程）。
    /// </summary>
    public void Enqueue(
        string kind,
        string? runId = null,
        string? requestId = null,
        string? callId = null,
        string? gameDate = null,
        string? npcName = null,
        bool success = true,
        string? reason = null,
        Dictionary<string, object>? extra = null)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                ["success"] = success
            };
            if (runId != null)
            {
                payload["runId"] = runId;
            }

            if (requestId != null)
            {
                payload["requestId"] = requestId;
            }

            if (callId != null)
            {
                payload["callId"] = callId;
            }

            if (gameDate != null)
            {
                payload["gameDate"] = gameDate;
            }

            if (npcName != null)
            {
                payload["npcName"] = npcName;
            }

            if (reason != null)
            {
                payload["reason"] = reason;
            }

            if (extra != null)
            {
                foreach (var kvp in extra)
                {
                    payload[kvp.Key] = kvp.Value;
                }
            }

            var line = JsonSerializer.Serialize(payload, JsonOptions);
            _queue.Enqueue(line);
        }
        catch (Exception ex)
        {
            // 留痕失败绝不能影响游戏——记录警告后吞掉
            _monitor.Log($"[TranscriptSink] Enqueue failed for kind='{kind}': {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     在游戏主线程（UpdateTicked）调用：排空队列，写入 JSONL 文件。
    ///     写入失败 → Warn 并继续，绝不中断游戏 tick。
    /// </summary>
    public void Drain()
    {
        if (_disposed)
        {
            return;
        }

        while (_queue.TryDequeue(out var line))
        {
            WriteLine(line);
        }

        // 批量写入后统一 flush
        foreach (var sw in _writers.Values)
        {
            try
            {
                sw.Flush();
            }
            catch (Exception ex)
            {
                _monitor.Log($"[TranscriptSink] Flush failed: {ex}", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     订阅 AgentStateMachine.OnStateChanged，状态转换后自动入队 state_changed 事件。
    ///     镜像 StateChangedSender 的订阅模式（fire-and-forget + catch-all）。
    /// </summary>
    public void AttachStateMachine(AgentStateMachine sm, string npcName)
    {
        if (sm == null)
        {
            throw new ArgumentNullException(nameof(sm));
        }

        if (string.IsNullOrEmpty(npcName))
        {
            throw new ArgumentNullException(nameof(npcName));
        }

        sm.OnStateChanged += (_, e) =>
        {
            // 状态机回调可能在非主线程触发；Enqueue 线程安全，Drain 在主线程执行
            Enqueue(
                "state_changed",
                npcName: npcName,
                gameDate: SafeGameDate(),
                success: true,
                extra: new Dictionary<string, object>
                {
                    ["previousState"] = e.PreviousState.ToString(),
                    ["newState"] = e.NewState.ToString(),
                    ["wasForced"] = e.WasForced,
                    ["previousStateDurationMs"] = (long)e.PreviousStateDuration.TotalMilliseconds,
                    ["reason"] = e.Reason ?? ""
                });
        };
    }

    /// <summary>
    ///     订阅 AgentInventory.OnWalletChanged，钱包收支自动入队 wallet_changed 事件。
    ///     镜像 AttachStateMachine 的订阅模式（fire-and-forget + 订阅者内 catch-all）。
    ///     事件参数里的 NpcName/GameDate 由这里补充（inventory 层不知道游戏日期）。
    /// </summary>
    public void AttachWallet(AgentInventory inventory, string npcName)
    {
        if (inventory == null)
        {
            throw new ArgumentNullException(nameof(inventory));
        }

        if (string.IsNullOrEmpty(npcName))
        {
            throw new ArgumentNullException(nameof(npcName));
        }

        inventory.OnWalletChanged += args =>
        {
            // 事件在钱包锁内触发，Enqueue 线程安全（ConcurrentQueue），Drain 在主线程执行
            Enqueue(
                "wallet_changed",
                npcName: npcName,
                gameDate: SafeGameDate(),
                success: true,
                extra: new Dictionary<string, object>
                {
                    ["amount"] = args.Amount,
                    ["newBalance"] = args.NewBalance,
                    ["reason"] = args.Reason ?? ""
                });
        };
    }

    /// <summary>
    ///     记录一条 action_result 事件（命令执行结果）。
    ///     由 CommandExecutor.OnSendActionResult 回调驱动。
    /// </summary>
    public void RecordActionResult(
        string npcName,
        string tool,
        bool success,
        Dictionary<string, object>? extra = null)
    {
        if (string.IsNullOrEmpty(npcName) || string.IsNullOrEmpty(tool))
        {
            return;
        }

        var payload = extra != null
            ? new Dictionary<string, object>(extra)
            : new Dictionary<string, object>();
        payload["tool"] = tool;

        Enqueue(
            "action_result",
            npcName: npcName,
            gameDate: SafeGameDate(),
            success: success,
            extra: payload);
    }

    /// <summary>
    ///     关闭所有 StreamWriter 并清空队列。在 Mod 卸载时调用。
    /// </summary>
    public void Close()
    {
        Drain();
        foreach (var kvp in _writers)
        {
            try
            {
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _monitor.Log($"[TranscriptSink] Close writer failed for {kvp.Key}: {ex}", LogLevel.Warn);
            }
        }

        _writers.Clear();
    }

    // ───────────────────────── 内部 ─────────────────────────

    private void WriteLine(string line)
    {
        try
        {
            // 从队列行中解析 gameDate/npcName 以确定文件路径；
            // 这两个字段在 Enqueue 时已写入 payload。
            var doc = JsonDocument.Parse(line);
            var gameDate = SafeReadString(doc.RootElement, "gameDate", "unknown");
            var npc = SafeReadString(doc.RootElement, "npcName", "_global");
            var key = (gameDate, npc);

            var writer = GetOrCreateWriter(key);
            writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[TranscriptSink] WriteLine failed: {ex}", LogLevel.Warn);
        }
    }

    private StreamWriter GetOrCreateWriter((string gameDate, string npc) key)
    {
        // 轮转检测：gameDate 或 npc 变化时，关闭旧 writer 并创建新文件
        if (_lastKey != key && _writers.Count > 0)
        {
            // 不主动关闭——ConcurrentDictionary 缓存所有打开的 writer，
            // Close() 时统一释放。同一 gameDate/npc 复用已打开的 writer。
        }

        _lastKey = key;

        return _writers.GetOrAdd(key, k =>
        {
            var dir = Path.Combine(_root, k.gameDate);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, k.npc + ".jsonl");
            var sw = new StreamWriter(path, true)
            {
                AutoFlush = false
            };
            return sw;
        });
    }

    private static string SafeReadString(JsonElement element, string name, string fallback)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? fallback;
        }

        return fallback;
    }

    private static string SafeGameDate()
    {
        try
        {
            // 游戏未加载时访问 Game1.year 等会抛异常，返回占位符
            var year = Game1.year;
            var season = Game1.currentSeason ?? "spring";
            var day = Game1.dayOfMonth;
            return $"Y{year}_{season}_{day}";
        }
        catch (Exception)
        {
            return "pre-game";
        }
    }
}