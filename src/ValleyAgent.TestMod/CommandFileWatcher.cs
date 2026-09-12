#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using StardewModdingAPI;

namespace ValleyAgent.TestMod;

/// <summary>
///     SMAPI 当前版本的 ICommandHelper 未公开 Trigger 方法；通过反射调用内部 CommandManager 实现。
/// </summary>
internal static class CommandHelperExtensions
{
    public static void Trigger(this ICommandHelper helper, string commandName, string[] args)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(commandName);

        var commandHelperType = helper.GetType();
        var commandManagerField =
            commandHelperType.GetField("CommandManager", BindingFlags.Instance | BindingFlags.NonPublic);
        if (commandManagerField == null)
        {
            throw new InvalidOperationException("[CommandFileWatcher] 无法从 ICommandHelper 获取 CommandManager 字段。");
        }

        var commandManager = commandManagerField.GetValue(helper);
        if (commandManager == null)
        {
            throw new InvalidOperationException("[CommandFileWatcher] CommandManager 实例为空。");
        }

        var commandManagerType = commandManager.GetType();
        var getMethod = commandManagerType.GetMethod("Get",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        if (getMethod == null)
        {
            throw new InvalidOperationException("[CommandFileWatcher] 无法从 CommandManager 获取 Get(string) 方法。");
        }

        var command = getMethod.Invoke(commandManager, new object[] { commandName });
        if (command == null)
        {
            throw new InvalidOperationException($"[CommandFileWatcher] 未找到命令 '{commandName}'。");
        }

        var commandType = command.GetType();
        var callbackProperty = commandType.GetProperty("Callback",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (callbackProperty == null)
        {
            throw new InvalidOperationException("[CommandFileWatcher] 无法从 Command 获取 Callback 属性。");
        }

        var callback = callbackProperty.GetValue(command) as Action<string, string[]>;
        if (callback == null)
        {
            throw new InvalidOperationException("[CommandFileWatcher] 命令回调为空或类型不匹配。");
        }

        callback(commandName, args);
    }
}

/// <summary>
///     文件命令触发器：外部自动化可通过写入 test_commands.txt 来触发任意 SMAPI 控制台命令。
///     使用 FileSystemWatcher + 防抖定时器检测文件变更，执行后清空源文件并把历史追加到 test_commands.done.txt。
/// </summary>
internal sealed class CommandFileWatcher : IDisposable
{
    private const string CommandFileName = "test_commands.txt";
    private const string DoneFileName = "test_commands.done.txt";
    private const int DebounceMilliseconds = 200;
    private const int PollPeriodMilliseconds = 2000;

    private static readonly char[] s_argSeparators = { ' ' };
    private readonly string _commandFilePath;
    private readonly Timer _debounceTimer;
    private readonly string _doneFilePath;

    private readonly IModHelper _helper;
    private readonly string? _instanceCommandFilePath;
    private readonly IMonitor _monitor;
    private readonly Timer _pollTimer;
    private readonly object _processLock = new();
    private readonly FileSystemWatcher _watcher;
    // 2026-08-17：命令统一入队、主线程（ModEntry.OnUpdateTicked → DrainPendingCommands）执行。
    // FileSystemWatcher/Timer 在后台线程——直接 Trigger 会让 va_mp_host 等改游戏状态的命令
    // 跑在非主线程，与主线程 updatePendingConnections 竞态（开服 10048）。
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Command, string[] Args)> _pendingCommands = new();

    private bool _disposed;

    public CommandFileWatcher(IModHelper helper, IMonitor monitor)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

        _commandFilePath = Path.Combine(helper.DirectoryPath, CommandFileName);
        _doneFilePath = Path.Combine(helper.DirectoryPath, DoneFileName);

        var instance = Environment.GetEnvironmentVariable("VALLEY_TEST_INSTANCE");
        if (!string.IsNullOrWhiteSpace(instance))
        {
            _instanceCommandFilePath = Path.Combine(helper.DirectoryPath, $"test_commands_{instance}.txt");
            EnsureFileExists(_instanceCommandFilePath);
        }

        EnsureFileExists(_commandFilePath);

        _debounceTimer = new Timer(_ => ProcessCommands(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new Timer(_ => ProcessCommands(), null, PollPeriodMilliseconds, PollPeriodMilliseconds);

        var directory = Path.GetDirectoryName(_commandFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException($"[CommandFileWatcher] 无法从路径 {_commandFilePath} 解析目录。");
        }

        // FileSystemWatcher.Filter only supports a single wildcard pattern, so watch all test_commands*.txt files.
        _watcher = new FileSystemWatcher(directory, "test_commands*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;

        var message = $"[CommandFileWatcher] 已启动监听: {_commandFilePath}";
        if (_instanceCommandFilePath != null)
        {
            message += $" (实例: {_instanceCommandFilePath})";
        }

        monitor.Log(message, LogLevel.Info);
    }

    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceTimer.Dispose();
            _pollTimer.Dispose();
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }
    }

    public static CommandFileWatcher Start(IModHelper helper, IMonitor monitor) => new(helper, monitor);

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        try
        {
            _ = _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }

    private void ProcessCommands()
    {
        lock (_processLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var processedAny = false;
                processedAny |= ProcessCommandFile(_commandFilePath);
                if (_instanceCommandFilePath != null)
                {
                    processedAny |= ProcessCommandFile(_instanceCommandFilePath);
                }

                if (processedAny)
                {
                    _monitor.Log("[CommandFileWatcher] 命令已处理，源文件已清空，历史已追加到 test_commands.done.txt。", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[CommandFileWatcher] 处理命令文件失败: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private bool ProcessCommandFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            return false;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            _monitor.Log($"[CommandFileWatcher] 读取命令文件失败（可能正在写入）: {ex.Message}", LogLevel.Debug);
            return false;
        }

        var history = new StringBuilder();
        var executedAny = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(s_argSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var command = parts[0];
            var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

            // 2026-08-17：入队而非直接 Trigger——由主线程 DrainPendingCommands 执行。
            _pendingCommands.Enqueue((command, args));
            _monitor.Log($"[CommandFileWatcher] 已入队命令: {line}", LogLevel.Info);

            _ = history.AppendLine(line);
            executedAny = true;
        }

        if (!executedAny)
        {
            return false;
        }

        try
        {
            File.AppendAllText(_doneFilePath, history.ToString(), Encoding.UTF8);
            File.WriteAllText(path, string.Empty, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[CommandFileWatcher] 清空命令文件或写入历史失败: {ex.Message}", LogLevel.Error);
        }

        return true;
    }

    /// <summary>
    ///     主线程（ModEntry.OnUpdateTicked）排空命令队列并执行。
    ///     命令文件读取在后台 Timer 线程，执行必须回主线程（改游戏状态的命令
    ///     va_mp_host/va_test_c4 等在非主线程跑会与游戏 tick 竞态）。
    /// </summary>
    public void DrainPendingCommands()
    {
        while (_pendingCommands.TryDequeue(out var item))
        {
            try
            {
                _helper.ConsoleCommands.Trigger(item.Command, item.Args);
                _monitor.Log($"[CommandFileWatcher] 已执行命令: {item.Command} {string.Join(" ", item.Args)}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[CommandFileWatcher] 执行命令 '{item.Command}' 失败: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# SMAPI console commands: one per line.\n", Encoding.UTF8);
        }
    }
}