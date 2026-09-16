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
    // volatile：Dispose 先置位、启动入口守卫读——跨线程可见性（死锁修复 2026-09-12）
    private volatile bool _disposed;
    private Process? _process;
    private bool _startedByUs;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;

    // 看门狗代号（死锁修复 2026-09-12）：StopWatchdog 不再同步等待旧循环退出，
    // 旧循环可能还卡在 _startLock.WaitAsync()（不可取消）里——它醒来后若继续跑重启分支，
    // 会与新一代看门狗各起一个服务器进程。代号让旧循环在每次跨 await 后自证身份，过期即退出。
    private int _watchdogGeneration;

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
    /// <remarks>
    ///     死锁修复（2026-09-12）：互斥区**只覆盖"杀端口 + 起进程 + 武装看门狗"**，
    ///     就绪探测 <see cref="WaitForServerReadyAsync"/> 移到锁外。
    ///     原实现持锁跨越整个启动流程：KillExistingServerOnPort 最长 ~9s
    ///     （netstat WaitForExit(5000) + taskkill WaitForExit(3000) + Thread.Sleep(1000)）
    ///     + WaitForServerReadyAsync 最长 StartupTimeoutSeconds（默认 30s，每次探测阻塞 ≤2s）
    ///     ⇒ <c>_startLock</c> 可被独占近 40s。而 <c>_startLock.WaitAsync()</c> 不接受
    ///     CancellationToken，等待者无法被取消——看门狗重启分支一旦卡在这里，
    ///     主线程侧 <see cref="StopServer"/> 就构成环形等待（详见 <see cref="StopWatchdog"/>）。
    /// </remarks>
    public async Task<bool> StartServerAsync()
    {
        if (_disposed)
        {
            return false;
        }

        bool spawned;
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            spawned = StartServerCore();
        }
        finally
        {
            _startLock.Release();
        }

        if (!spawned)
        {
            return false;
        }

        // 就绪探测在锁外：这段最长 StartupTimeoutSeconds，且每次 IsServerRunning() 阻塞 ≤2s。
        // 放在锁内会让并发的 StartServerAsync/StopServer 调用方（含游戏主线程）被饿死数十秒。
        var connected = await WaitForServerReadyAsync().ConfigureAwait(false);
        if (connected)
        {
            _monitor.Log("Agent Server is ready.", LogLevel.Info);
            return true;
        }

        _monitor.Log($"Agent Server did not become ready within {StartupTimeoutSeconds}s.", LogLevel.Warn);
        return false;
    }

    /// <summary>
    ///     启动流程的互斥段：解析路径/写 runtime config/杀残留端口/起进程/武装看门狗。
    ///     返回 true 表示进程已起来（就绪与否由调用方在锁外探测）。
    ///     同步方法：本段全程无 await（进程启动是同步 API），保持互斥区内不产生续体切换。
    /// </summary>
    private bool StartServerCore()
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

            // 就绪探测已上移到 StartServerAsync 的锁外段（见该方法注释：持锁探测会饿死并发调用方）。
            return true;
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

    public void StopServer() => StopServerCore(stopWatchdog: true);

    /// <summary>
    ///     停服务器。<paramref name="stopWatchdog"/> = false 供看门狗自身的重启分支调用：
    ///     循环里调 <see cref="StopServer"/> 会走到 <see cref="StopWatchdog"/>，而后者原本
    ///     同步等待的正是当前正在执行的这个任务——自己等自己，必然烧满超时（死锁修复 2026-09-12）。
    /// </summary>
    private void StopServerCore(bool stopWatchdog)
    {
        if (stopWatchdog)
        {
            StopWatchdog();
        }

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
        var generation = Interlocked.Increment(ref _watchdogGeneration);
        _watchTask = WatchdogLoopAsync(_watchCts.Token, generation);
    }

    /// <summary>
    ///     停止进程看门狗——**非阻塞**（死锁修复 2026-09-12）。
    ///     原实现 <c>_watchTask.Wait(TimeSpan.FromSeconds(5))</c> 有三条独立的死锁路径：
    ///     (a) 自等待：看门狗重启分支 <c>StopServer()</c> → 本方法 → 在**自己**身上 Wait，
    ///         任务永远不可能在自己阻塞期间完成 ⇒ 每次崩溃重启必烧满 5s；
    ///     (b) 主线程冻结：房客 SaveLoaded 判定 ThinClient/Inert 时在**游戏主线程**调
    ///         <c>ModEntry.StopEarlyServer()</c> → <c>StopServer()</c> → 本方法（游戏退出 Dispose 同理）
    ///         ⇒ 主线程最长阻塞 5s，Windows 消息泵 5s 饥饿即判定"未响应"，且无异常无日志；
    ///     (c) 环形等待：看门狗卡在 <c>await _startLock.WaitAsync()</c>（不接受 CancellationToken，
    ///         取消不掉）时，(b) 的主线程与持锁线程的 <c>StartWatchdog()</c>→本方法 同时等这个任务
    ///         ⇒ main → watchTask → _startLock → starter → watchTask 成环。
    ///     现在只 Cancel + 摘引用：循环对取消是自限的（Task.Delay 被取消即 break），
    ///     退出后由观察续体释放 CTS，谁都不需要同步等待谁。
    /// </summary>
    private void StopWatchdog()
    {
        // 先摘引用再取消：并发的 StartWatchdog/StopWatchdog 不会重复处理同一个任务。
        var cts = _watchCts;
        var task = _watchTask;
        _watchCts = null;
        _watchTask = null;

        if (cts == null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            // 已被上一轮释放：无循环可停。留痕防"停不掉却无任何信号"（§3.6 铁律）。
            _monitor.Log($"[Watchdog] StopWatchdog: CTS already disposed, no loop to stop: {ex}", LogLevel.Trace);
            return;
        }

        if (task == null)
        {
            cts.Dispose();
            return;
        }

        // 观察续体：吞掉循环退出时的取消异常（否则 unobserved task exception），并在退出后释放 CTS。
        // 不 Wait/不 Join——调用方（可能是游戏主线程）绝不为后台循环的收尾买单。
        _ = task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // 标记已观察
                ((CancellationTokenSource)state!).Dispose();
            },
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WatchdogLoopAsync(CancellationToken ct, int generation)
    {
        var restartAttempts = 0;
        DateTime? lastCrashTime = null;

        // 代号过期 = 已被新一代看门狗接管（StopWatchdog 不再同步等待，旧循环可能刚醒来）。
        bool IsStale() => generation != Volatile.Read(ref _watchdogGeneration);

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
            catch (ObjectDisposedException ex)
            {
                // 纵深防御：修复前 CTS 会被 StopWatchdog 就地 Dispose，循环下一轮 Task.Delay 注册令牌时
                // 抛 ObjectDisposedException，而循环没有兜底 catch → 看门狗任务直接 fault 并**永久死亡**
                // （服务器崩溃后再也没人重启）。CTS 生命周期现在挂在退出续体上，理论上到不了这里。
                _monitor.Log($"[Watchdog] WatchdogLoop 5s tick aborted by disposed CTS: {ex}", LogLevel.Trace);
                break;
            }

            if (ct.IsCancellationRequested || IsStale())
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
                catch (ObjectDisposedException ex)
                {
                    _monitor.Log($"[Watchdog] restart backoff delay aborted by disposed CTS: {ex}", LogLevel.Trace);
                    break; // 同上：不让 CTS 生命周期问题把看门狗打成永久 fault。
                }

                if (ct.IsCancellationRequested || IsStale())
                {
                    break;
                }

                if (!IsStartedByUs)
                {
                    break;
                }

                // 死锁修复（2026-09-12）：这里**不能**调 StopServer()——它会走 StopWatchdog()，
                // 而 StopWatchdog 原本同步等待的正是当前这个循环任务（自己等自己）。
                // 循环自己收尾进程即可，看门狗由 StartServerAsync → StartWatchdog 重新武装。
                StopServerCore(stopWatchdog: false);
                var restarted = await StartServerAsync().ConfigureAwait(false);
                if (restarted)
                {
                    _monitor.Log("Agent Server restarted successfully.", LogLevel.Info);
                }

                // StartServerAsync 成功时会武装一个**新的**看门狗循环；本循环的职责已交接，
                // 继续跑会造成双循环（双份重启决策）。ct 已被 StartWatchdog→StopWatchdog 取消，
                // while 条件下一轮即退出；这里显式 break 让语义不依赖取消时序。
                if (restarted)
                {
                    break;
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
            // 先置位再停：StartServerAsync/EnsureRunningAsync 的入口守卫读到 _disposed 即返回，
            // 收窄"正在 Dispose 却仍有线程 await _startLock.WaitAsync()"的窗口
            //（对已释放信号量 WaitAsync 会抛 ObjectDisposedException）。
            _disposed = true;
            StopServer();

            try
            {
                _startLock.Dispose();
            }
            catch (Exception ex)
            {
                // 仍有线程在 WaitAsync 上排队等这把信号量时释放它会抛；Dispose 绝不能把游戏搞崩。
                _monitor.Log($"[ServerProcessManager] _startLock dispose skipped: {ex}", LogLevel.Debug);
            }
        }

        _disposed = true;
    }

    public record CrashInfo(int ExitCode, int RestartAttempt, DateTime CrashTime);
}