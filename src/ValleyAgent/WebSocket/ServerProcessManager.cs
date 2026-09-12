using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using ValleyAgent.Config;
using ValleyAgent.Infrastructure;

namespace ValleyAgent.WebSocket;

public class ServerProcessManager : IDisposable
{
    private static readonly char[] s_spaceSeparator = { ' ' };
    private static readonly int[] s_backoffDelays = { 5, 15, 45 };
    private ModConfig _config;
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly object _processLock = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _disposed;
    private Process? _process;
    private bool _startedByUs;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;

    public ServerProcessManager(IMonitor monitor, IModHelper helper, ModConfig config)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    ///     重绑配置实例：本管理器可能在 GameLaunched 预启动（ServiceInitializer 复用 externalServerManager）
    ///     时持有旧 config 实例，而容器内当前实例可能已被 GMCM 热改/测试修改。
    ///     不复绑会导致重启后 --director-probability 等参数静默漂移（实测：测试改
    ///     TriggerProbability=1.0，server 仍收到旧实例的 0.1）。
    /// </summary>
    internal void RebindConfig(ModConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string ServerExecutablePath { get; set; } = "";
    public string ServerDirectory { get; set; } = "";
    public bool AutoStart { get; set; } = true;

    /// <summary>
    ///     为 true 时服务器在独立可见的 cmd 窗口中运行（UseShellExecute，输出不重定向），
    ///     方便直接查看 Agent 纯逻辑日志/报错；为 false 时隐藏窗口并把输出重定向到 SMAPI 日志。
    /// </summary>
    public bool ConsoleWindow { get; set; } = true;

    public int ServerPort { get; set; } = 8765;
    public int StartupTimeoutSeconds { get; set; } = 30;
    public int CrashRestartDelaySeconds { get; set; } = 5;
    public int MaxRestartAttempts { get; set; } = 3;

    public bool IsProcessAlive
    {
        get
        {
            lock (_processLock)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    private bool IsStartedByUs
    {
        get
        {
            lock (_processLock)
            {
                return _startedByUs;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public event Action<string>? LogCallback;
    public event Action<CrashInfo>? OnCrashed;

    public bool IsServerRunning()
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", ServerPort, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            if (success)
            {
                client.EndConnect(result);
                return true;
            }

            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<bool> EnsureRunningAsync()
    {
        if (_disposed)
        {
            return false;
        }

        // 若端口已被占用但不是当前 SMAPI 启动的（例如上次游戏崩溃后残留的 TS 服务器进程），
        // 必须强制 kill 重启——否则不会弹出新的 cmd 窗口，用户看不到 Agent 日志。
        if (IsServerRunning())
        {
            if (IsStartedByUs)
            {
                Log("Agent Server already running on port " + ServerPort);
                return true;
            }

            Log("Agent Server port " + ServerPort + " occupied by stale process, killing and restarting...");
        }

        if (!AutoStart)
        {
            Log("AutoStart disabled, skipping Agent Server launch");
            return false;
        }

        return await StartServerAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     启动服务器进程（并发安全）。GameLaunched 预启动与 SaveLoaded 的
    ///     EnsureRunningAsync 可能并发调用 StartServerAsync，用信号量互斥，
    ///     防止双进程启动（cmd 窗口闪断 + 端口竞争）。
    /// </summary>
    public async Task<bool> StartServerAsync()
    {
        if (_disposed)
        {
            return false;
        }

        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartServerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<bool> StartServerCoreAsync()
    {
        if (_disposed)
        {
            return false;
        }

        var serverExe = ResolveServerExecutable();
        if (serverExe == null)
        {
            _monitor.Log(
                "Cannot find server executable. Set ServerExecutablePath in config or place valley-ai-server.exe in the mod directory.",
                LogLevel.Error);
            return false;
        }

        var serverDir = ResolveServerDirectory();
        if (serverDir == null || !Directory.Exists(serverDir))
        {
            _monitor.Log($"Cannot find server directory: {ServerDirectory}. Set ServerDirectory in config.",
                LogLevel.Error);
            return false;
        }

        // ─── 多 Provider 模式：写 runtime JSON，供 TS 端 --llm-config 读取 ───
        // 成功时 llmConfigPath 非空，后续只传 --llm-config；失败则降级到单 provider 模式。
        string? llmConfigPath = null;
        if (_config.MultiProviderEnabled)
        {
            llmConfigPath = LlmConfigWriter.WriteRuntimeConfig(_config, _helper.DirectoryPath, _monitor);
            if (llmConfigPath == null)
            {
                _monitor.Log("Failed to write LLM runtime config; falling back to single provider mode.",
                    LogLevel.Warn);
                _config.MultiProviderEnabled = false; // 降级到单 provider 模式
            }
        }

        // ─── 单 Provider 模式：解析 apiKey/model/provider/baseUrl ───
        // 多 provider 模式下跳过解析（不需要这些参数，避免无谓的环境变量读取与 apiKey 缺失误报）。
        var apiKey = "";
        var llmModel = "";
        var llmProvider = "";
        var llmBaseUrl = "";
        if (llmConfigPath == null)
        {
            // Resolve API key: config first, then env var fallback (LLM_API_KEY)
            apiKey = !string.IsNullOrEmpty(_config.LlmApiKey)
                ? _config.LlmApiKey
                : Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "";
            if (string.IsNullOrEmpty(apiKey))
            {
                // LM Studio / 本地 OpenAI 兼容服务端通常不校验 key，但 TS server 启动参数要求非空。
                // 自动填入占位 key，避免本地模型用户因漏配 key 导致服务器无法启动。
                if (_config.Provider == LlmProvider.LMStudio
                    || _config.LlmProvider == LlmProvider.LMStudio)
                {
                    // 本地服务端不校验 key，但 TS server 启动参数要求非空。
                    // 占位值每次启动随机生成：固定字面量会被安全扫描按硬编码凭据计，
                    // 且随机值不含任何凭据含义（本地服务端只检查非空）。
                    apiKey = Guid.NewGuid().ToString("N");
                    _monitor.Log(
                        "[ServerProcessManager] No LLM API key configured; using random placeholder for LM Studio local server.",
                        LogLevel.Info);
                }
                else
                {
                    _monitor.Log(
                        "[ServerProcessManager] LLM API key missing. Set LlmApiKey in config or LLM_API_KEY env var.",
                        LogLevel.Error);
                    return false;
                }
            }

            llmModel = !string.IsNullOrEmpty(_config.LlmModel)
                ? _config.LlmModel
                : "MiniMax-M3";

            // Map C# LlmProvider enum → cli.ts provider string (minimax|openai|deepseek|
            // lmstudio|openrouter|anthropic|google). Kimi routes through the OpenAI-compatible
            // branch because cli.ts has no dedicated kimi provider. This prevents the TS server
            // from silently falling back to its built-in default (minimax) when the user has
            // configured a different provider in config.json — which previously caused the
            // LM Studio random placeholder key to be sent to the real MiniMax API (1004 error).
            llmProvider = _config.LlmProvider switch
            {
                LlmProvider.MiniMax => "minimax",
                LlmProvider.DeepSeek => "deepseek",
                LlmProvider.LMStudio => "lmstudio",
                LlmProvider.OpenRouter => "openrouter",
                // Kimi uses an OpenAI-compatible endpoint; route via the openai branch.
                _ => "openai"
            };

            // LlmBaseUrl: empty → let cli.ts fall back to its built-in default
            // (https://api.minimax.chat/v1). Non-empty → forwarded verbatim.
            llmBaseUrl = _config.LlmBaseUrl ?? "";
        }

        var agentsDir = Path.Combine(_helper.DirectoryPath, "agents");
        Directory.CreateDirectory(agentsDir);

        try
        {
            KillExistingServerOnPort();

            // ─── 构造启动参数：多 provider 模式只传 --llm-config；单 provider 模式传 apiKey 等 ───
            string serverArgs;
            if (llmConfigPath != null)
            {
                // 多 provider 模式：runtime JSON 已写入 llmConfigPath，不传 --llm-api-key 等
                serverArgs = $"--port {ServerPort} --agents-dir \"{agentsDir}\" --llm-config \"{llmConfigPath}\"";
            }
            else
            {
                // 单 provider 模式（向后兼容）：传 apiKey/model/provider/baseUrl
                serverArgs =
                    $"--port {ServerPort} --llm-api-key {apiKey} --llm-model {llmModel} --llm-provider {llmProvider}"
                    + (string.IsNullOrEmpty(llmBaseUrl) ? "" : $" --llm-base-url {llmBaseUrl}")
                    + $" --agents-dir \"{agentsDir}\"";
            }

            // 导演开关：关闭时追加 --disable-director（TS 端 cli.ts 解析）
            if (!_config.EnableDirector)
            {
                serverArgs += " --disable-director";
            }

            // 导演触发概率：透传 --director-probability（TS 端 handleDayStarted roll 用）
            // 诊断：打印实际概率值（不含 API 密钥，安全），避免"测试改了 1.0 但 server 收到 0.1"这类静默漂移。
            _monitor.Log(
                $"Starting server with directorTriggerProbability={_config.Director?.TriggerProbability ?? 0.1} (enableDirector={_config.EnableDirector}, multiProvider={_config.MultiProviderEnabled})",
                LogLevel.Info);
            serverArgs += $" --director-probability {_config.Director?.TriggerProbability ?? 0.1}";

            ProcessStartInfo psi;
            if (ConsoleWindow)
            {
                // 用 cmd.exe /c start 强制创建独立 cmd 窗口：
                // SMAPI 本身是控制台应用，直接 UseShellExecute=true 启动 cmd.exe/server.exe 时，
                // 子进程会附加到 SMAPI 的控制台而不是创建新窗口（Windows 控制台继承行为）。
                // `start` 命令内部使用 CreateProcess(CREATE_NEW_CONSOLE)，必定创建新窗口。
                // /wait 让 cmd.exe 等待 server.exe 退出——这样 Process 对象跟踪 cmd.exe，
                // 其生命周期与 server.exe 一致；Kill(entireProcessTree:true) 能同时终止两者。
                // 隐藏的 cmd.exe (CreateNoWindow=true) 只是启动器，用户看到的是 start 创建的新窗口。
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"ValleyAI Server (port {ServerPort})\" /wait \"{serverExe}\" {serverArgs}",
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // 发行包配置（ServerConsoleWindow=true，package-distribution.ps1 强制）：stdout 不重定向，
                // TS 服务器输出只进 cmd 窗口——整机卡死/玩家关窗即丢现场。让 TS 端读该变量自己把
                // 控制台输出 tee 到文件（与 C# 侧分工：false 分支 C# 已在 OutputDataReceived 捕获落盘，
                // 不设此变量避免双写）。
                psi.Environment["VALLEY_SERVER_LOGFILE"] = Path.Combine(_helper.DirectoryPath, "ValleyAgent-server.log");
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = serverExe,
                    Arguments = serverArgs,
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // TS 端统一输出 UTF-8；不显式指定时用系统默认代码页（中文系统=GBK），
                    // 中文日志会变乱码（实测 [director] 触发/保留等行全部花屏）。
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
            }

            // 服务器输出落盘留痕：启动/停止事件写入 ValleyAgent-server.log，排障时有完整时间线
            ModErrorLog.LogServerEvent($"server starting (port {ServerPort}, multiProvider={_config.MultiProviderEnabled})");

            var envVars = GetEnvironmentVariables();
            if (envVars != null)
            {
                foreach (var kv in envVars)
                {
                    psi.Environment[kv.Key] = kv.Value;
                }
            }

            int pid;
            lock (_processLock)
            {
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!ConsoleWindow)
                {
                    _process.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Log($"[Server] {e.Data}");
                            ModErrorLog.LogServerLine(e.Data);
                        }
                    };
                    _process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Log($"[Server:ERR] {e.Data}");
                            ModErrorLog.LogServerLine($"[STDERR] {e.Data}");
                            ModErrorLog.LogError("ServerStderr", e.Data);
                        }
                    };
                }

                _process.Start();
                if (!ConsoleWindow)
                {
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }

                _startedByUs = true;
                pid = _process.Id;
            }

            // SECURITY: Do NOT log Arguments (contains LLM API key)
            _monitor.Log($"Agent Server started (PID={pid}, port={ServerPort})", LogLevel.Info);
            StartWatchdog();

            var connected = await WaitForServerReadyAsync().ConfigureAwait(false);
            if (connected)
            {
                _monitor.Log("Agent Server is ready.", LogLevel.Info);
                return true;
            }

            _monitor.Log($"Agent Server did not become ready within {StartupTimeoutSeconds}s.", LogLevel.Warn);
            return false;
        }
        catch (Win32Exception ex)
        {
            _monitor.Log($"Failed to start server process: {ex.Message} (NativeErrorCode={ex.NativeErrorCode})",
                LogLevel.Error);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"Failed to start server process: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    public void StopServer()
    {
        StopWatchdog();
        ModErrorLog.LogServerEvent("server stopped");

        lock (_processLock)
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(true);
                    _monitor.Log("Agent Server process killed.", LogLevel.Info);
                }
                catch (InvalidOperationException ex)
                {
                    _monitor.Log($"Failed to kill server process: {ex.Message}", LogLevel.Warn);
                }
            }

            _process?.Dispose();
            _process = null;
            _startedByUs = false;
        }
    }

    public async Task<bool> RestartServerAsync()
    {
        StopServer();
        await Task.Delay(CrashRestartDelaySeconds * 1000).ConfigureAwait(false);
        return await StartServerAsync().ConfigureAwait(false);
    }

    private async Task<bool> WaitForServerReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(StartupTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (IsServerRunning())
            {
                return true;
            }

                lock (_processLock)
                {
                    if (_process != null && _process.HasExited)
                    {
                        var exitCode = _process.ExitCode;
                        _monitor.Log($"Server process exited prematurely with code {exitCode}.", LogLevel.Error);
                        ModErrorLog.LogError(
                            "ServerProcess",
                            $"Server process exited prematurely with code {exitCode} (startup failed — check ValleyAgent-server.log)");
                        return false;
                    }
                }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return false;
    }

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchCts = new CancellationTokenSource();
        _watchTask = WatchdogLoopAsync(_watchCts.Token);
    }

    private void StopWatchdog()
    {
        _watchCts?.Cancel();

        if (_watchTask != null)
        {
            try
            {
                _ = _watchTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        _watchCts?.Dispose();
        _watchCts = null;
        _watchTask = null;
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        var restartAttempts = 0;
        DateTime? lastCrashTime = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!IsStartedByUs)
            {
                break;
            }

            if (!IsProcessAlive)
            {
                int exitCode;
                lock (_processLock)
                {
                    exitCode = _process?.ExitCode ?? -1;
                }

                var now = DateTime.UtcNow;
                if (lastCrashTime.HasValue && (now - lastCrashTime.Value).TotalSeconds > 60)
                {
                    restartAttempts = 0;
                }

                restartAttempts++;
                lastCrashTime = now;

                ModErrorLog.LogError(
                    "ServerProcess",
                    $"Agent Server crashed (exit={exitCode}), restart attempt {restartAttempts}/{MaxRestartAttempts}");
                OnCrashed?.Invoke(new CrashInfo(exitCode, restartAttempts, now));

                if (restartAttempts > MaxRestartAttempts)
                {
                    _monitor.Log($"Agent Server crashed {restartAttempts} times. Giving up auto-restart.",
                        LogLevel.Error);
                    ModErrorLog.LogError(
                        "ServerProcess",
                        $"Agent Server crashed {restartAttempts} times. Giving up auto-restart — AI dialogue will be unavailable.");
                    break;
                }

                var delayIndex = Math.Min(restartAttempts - 1, s_backoffDelays.Length - 1);
                var delaySeconds = s_backoffDelays[delayIndex];
                _monitor.Log(
                    $"Agent Server process died (exit={exitCode}). Restart attempt {restartAttempts}/{MaxRestartAttempts} in {delaySeconds}s...",
                    LogLevel.Warn);

                try
                {
                    await Task.Delay(delaySeconds * 1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (!IsStartedByUs)
                {
                    break;
                }

                StopServer();
                var restarted = await StartServerAsync().ConfigureAwait(false);
                if (restarted)
                {
                    _monitor.Log("Agent Server restarted successfully.", LogLevel.Info);
                }
            }
            else
            {
                if (lastCrashTime.HasValue && (DateTime.UtcNow - lastCrashTime.Value).TotalSeconds > 60)
                {
                    restartAttempts = 0;
                    lastCrashTime = null;
                }
            }
        }
    }

    /// <summary>
    ///     解析服务器可执行文件路径。
    ///     优先级：显式配置的 ServerExecutablePath > mod 目录下的 valley-ai-server.exe。
    ///     配置为相对路径时，相对于 ServerDirectory 或 mod 目录解析；
    ///     找不到时返回原值让 Process.Start 尝试 PATH 查找。
    /// </summary>
    private string? ResolveServerExecutable()
    {
        // 1. 显式配置的路径
        if (!string.IsNullOrWhiteSpace(ServerExecutablePath))
        {
            if (Path.IsPathRooted(ServerExecutablePath))
            {
                return File.Exists(ServerExecutablePath) ? ServerExecutablePath : null;
            }

            // 相对路径：相对于 ServerDirectory 或 mod 目录解析
            var baseDir = !string.IsNullOrWhiteSpace(ServerDirectory) && Directory.Exists(ServerDirectory)
                ? ServerDirectory
                : _helper.DirectoryPath;
            var full = Path.Combine(baseDir, ServerExecutablePath);
            return File.Exists(full) ? full : ServerExecutablePath;
        }

        // 2. 默认: mod 目录下的 valley-ai-server.exe
        var defaultPath = Path.Combine(_helper.DirectoryPath, "valley-ai-server.exe");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    /// <summary>
    ///     解析服务器工作目录。
    ///     优先级：显式配置的 ServerDirectory > mod 目录。
    /// </summary>
    private string? ResolveServerDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ServerDirectory) && Directory.Exists(ServerDirectory))
        {
            return ServerDirectory;
        }

        // 默认: mod 目录
        var modDir = _helper.DirectoryPath;
        return Directory.Exists(modDir) ? modDir : ServerDirectory;
    }

    private void KillExistingServerOnPort()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                ArgumentList = { "-ano" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return;
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            var pids = new HashSet<int>();
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains($":{ServerPort}") && line.Contains("LISTENING"))
                {
                    var parts = line.Trim().Split(s_spaceSeparator, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        pids.Add(pid);
                    }
                }
            }

            foreach (var pid in pids)
            {
                try
                {
                    using var killProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        // pid 已经过 int.TryParse 解析（不可注入），仍按参数列表传递以走无 shell 的参数边界
                        ArgumentList = { "/F", "/PID", pid.ToString() },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    killProc?.WaitForExit(3000);
                    _monitor.Log($"Killed existing process on port {ServerPort} (PID={pid})", LogLevel.Debug);
                }
                catch (Win32Exception)
                {
                }
            }

            if (pids.Count > 0)
            {
                Thread.Sleep(1000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected virtual Dictionary<string, string>? GetEnvironmentVariables() => null;

    private void Log(string message) => LogCallback?.Invoke(message);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            StopServer();
            _startLock.Dispose();
        }

        _disposed = true;
    }

    public record CrashInfo(int ExitCode, int RestartAttempt, DateTime CrashTime);
}