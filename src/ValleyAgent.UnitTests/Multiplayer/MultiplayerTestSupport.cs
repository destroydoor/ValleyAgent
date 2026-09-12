using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using System.Runtime.CompilerServices;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Resilience;
using ValleyAgent.Services;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     联机并发测试共享基建（2026-09-09 卡死排查）。
///     目标：在无游戏进程的虚拟环境里模拟「主线程泵 + 多房客并发」，
///     复现 3 人房房主随机卡死 / 房客回包错配 / 房客主线程队列无人排水三类问题。
///     注意：本目录所有测试都在 Game1Statics 集合内串行执行（Game1 静态是进程级全局）。
/// </summary>

// DisableParallelization=true：本集合与其它任何集合都不并行，防止 Game1.player/multiplayerMode
// 污染 AdjustExecutorTests 等依赖「Game1.player == null」语义的既有测试。
[CollectionDefinition("Game1Statics", DisableParallelization = true)]
public sealed class Game1StaticsCollection
{
}

/// <summary>记录线程号的日志实现。用于断言「某个动作发生在哪个线程」。</summary>
public sealed class RecordingMonitor : IMonitor
{
    private readonly object _lock = new();
    private readonly List<(LogLevel Level, string Message, int ThreadId, DateTime At)> _entries = new();

    public string LogName => "ValleyAgent.Test";

    public bool IsVerbose { get; set; } = true;

    public void Log(string message, LogLevel level = LogLevel.Debug)
    {
        lock (_lock)
        {
            _entries.Add((level, message, Environment.CurrentManagedThreadId, DateTime.UtcNow));
        }
    }

    public void LogOnce(string message, LogLevel level = LogLevel.Debug) => Log(message, level);

    public void VerboseLog(string message) => Log(message, LogLevel.Trace);

    public void VerboseLog(ref VerboseLogStringHandler message)
    {
        // 结构化插值句柄仅记录最终字符串形态。
        Log(message.ToString() ?? string.Empty, LogLevel.Trace);
    }

    public List<(LogLevel Level, string Message, int ThreadId, DateTime At)> Snapshot()
    {
        lock (_lock)
        {
            return new List<(LogLevel, string, int, DateTime)>(_entries);
        }
    }

    public int CountContaining(string fragment)
    {
        lock (_lock)
        {
            return _entries.Count(e => e.Message.Contains(fragment, StringComparison.Ordinal));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}

/// <summary>一条被记录的 ModMessage 发送。</summary>
public sealed record RecordedModMessage(
    string MessageType,
    string[]? ModIds,
    long[]? PlayerIds,
    int ThreadId,
    DateTime SentAt,
    object Message);

/// <summary>
///     IMultiplayerHelper 记录版：拦截所有 SendMessage 调用并记录发送线程。
///     「主线程纪律审计」的依据——SMAPI/游戏底层消息队列非线程安全，
///     所有 ModMessage 发送必须发生在主线程泵线程上。
/// </summary>
public sealed class RecordingMultiplayerService : IMultiplayerHelper
{
    private readonly ConcurrentQueue<RecordedModMessage> _messages = new();

    /// <summary>模拟游戏主线程的线程号（由测试设置，作为审计基准）。</summary>
    public volatile int MainThreadId;

    public IReadOnlyList<RecordedModMessage> Snapshot()
    {
        return _messages.ToList();
    }

    public List<RecordedModMessage> OfType(string messageType) =>
        Snapshot().Where(m => m.MessageType == messageType).ToList();

    private void Record(object message, string messageType, string[]? modIds, long[]? playerIds)
    {
        _messages.Enqueue(new RecordedModMessage(
            messageType, modIds, playerIds, Environment.CurrentManagedThreadId, DateTime.UtcNow, message));
    }

    public void SendMessage<TModel>(TModel message, string messageType, string[]? modIDs = null,
        long[]? playerIds = null) =>
        Record(message!, messageType, modIDs, playerIds);

    public void SendMessage(object message, string messageType, string[]? modIDs = null,
        long[]? playerIds = null) =>
        Record(message, messageType, modIDs, playerIds);

    public void SendMessageGlobal<TModel>(TModel message, string messageType) =>
        Record(message!, messageType, null, null);

    public void SendMessageGlobal(object message, string messageType) =>
        Record(message, messageType, null, null);

    public void SendMessageToHost<TModel>(TModel message, string messageType) =>
        Record(message!, messageType, null, null);

    public void SendMessageToHost(object message, string messageType) =>
        Record(message, messageType, null, null);

    public void SendMessageToFarmer<TModel>(TModel message, string messageType, long farmerId) =>
        Record(message!, messageType, null, new[] { farmerId });

    public void SendMessageToFarmer(object message, string messageType, long farmerId) =>
        Record(message, messageType, null, new[] { farmerId });

    public IEnumerable<IMultiplayerPeer> GetConnectedPlayers() => Array.Empty<IMultiplayerPeer>();

    public IMultiplayerPeer? GetConnectedPlayer(long id) => null;

    public IMultiplayerPeer? GetPlayer(long id) => null;

    public string? GetPlayerName(long id) => id.ToString();

    public bool IsLocalPlayer(long id) => false;

    public long GetNewID() => 0;

    public IEnumerable<StardewValley.GameLocation> GetActiveLocations() =>
        System.Linq.Enumerable.Empty<StardewValley.GameLocation>();

    public string ModID => "dandm1.ValleyAgent";
}

/// <summary>
///     IModHelper 记录版：只有 Multiplayer 可用（本套件只测 ModMessage 链路），
///     其余成员访问即抛，防止测试静默走进未桩化的 SMAPI 面。
///     成员面按本仓库随游戏分发的 StardewModdingAPI.xml 对齐（含 ReadConfig/WriteConfig，无 ModManifest）。
/// </summary>
public sealed class RecordingModHelper : IModHelper
{
    public RecordingModHelper(RecordingMultiplayerService multiplayer)
    {
        Multiplayer = multiplayer;
    }

    public IMultiplayerHelper Multiplayer { get; }

    public ICommandHelper ConsoleCommands => throw new NotSupportedException("测试未桩化 ConsoleCommands");

    public IContentPackHelper ContentPacks => throw new NotSupportedException("测试未桩化 ContentPacks");

    public IModContentHelper ModContent => throw new NotSupportedException("测试未桩化 ModContent");

    public IModEvents Events => throw new NotSupportedException("测试未桩化 Events");

    public IDataHelper Data => throw new NotSupportedException("测试未桩化 Data");

    public IGameContentHelper GameContent => throw new NotSupportedException("测试未桩化 GameContent");

    public IInputHelper Input => throw new NotSupportedException("测试未桩化 Input");

    public IReflectionHelper Reflection => throw new NotSupportedException("测试未桩化 Reflection");

    public ITranslationHelper Translation => throw new NotSupportedException("测试未桩化 Translation");

    public IModRegistry ModRegistry => throw new NotSupportedException("测试未桩化 ModRegistry");

    public string DirectoryPath => throw new NotSupportedException("测试未桩化 DirectoryPath");

    TConfig IModHelper.ReadConfig<TConfig>() => new TConfig();

    void IModHelper.WriteConfig<TConfig>(TConfig config)
    {
        // 测试桩：不落盘。
    }
}

/// <summary>
///     IAgentServerProvider 桩：可编程响应与模拟 LLM 延迟。
///     用于在虚拟环境里模拟「LLM 慢响应 + 多房客并发」的真实时序。
/// </summary>
public sealed class FakeAgentServerProvider : IAgentServerProvider
{
    private readonly Func<DialogueRequest, DialogueResponse>? _responder;
    private readonly int _minLatencyMs;
    private readonly int _maxLatencyMs;
    private readonly Random _random = new();
    private readonly ConcurrentQueue<DialogueRequest> _requests = new();

#pragma warning disable CS0067 // 桩不触发连接事件
    public event Action? OnConnected;
    public event Action? OnDisconnected;
#pragma warning restore CS0067

    public FakeAgentServerProvider(Func<DialogueRequest, DialogueResponse>? responder = null,
        int minLatencyMs = 0, int maxLatencyMs = 0)
    {
        _responder = responder;
        _minLatencyMs = minLatencyMs;
        _maxLatencyMs = maxLatencyMs;
    }

    public int RequestCount => _requests.Count;

    public IReadOnlyList<DialogueRequest> Requests => _requests.ToList();

    public async Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default)
    {
        _requests.Enqueue(request);
        if (_maxLatencyMs > 0)
        {
            await Task.Delay(_random.Next(_minLatencyMs, _maxLatencyMs), ct).ConfigureAwait(false);
        }

        if (_responder != null)
        {
            return _responder(request);
        }

        return new DialogueResponse(
            $"回复:{request.NpcName}",
            new List<ToolAction>());
    }

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task SendMessageAsync(string jsonMessage, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>IGiftTransport 桩：主机侧送礼评估即时返回。</summary>
public sealed class StubHostGiftTransport : IGiftTransport
{
    private readonly ConcurrentQueue<(string NpcName, string ItemId, int Quantity, long RequesterPlayerId)> _calls = new();

    public int CallCount => _calls.Count;

    public Task<GiftReactionResult> SendAsync(string npcName, string itemId, int quantity,
        long requesterPlayerId = 0, CancellationToken ct = default)
    {
        _calls.Enqueue((npcName, itemId, quantity, requesterPlayerId));
        return Task.FromResult(new GiftReactionResult("", 5, "谢谢"));
    }
}

/// <summary>
///     Game1 静态作用域：种入假本地玩家（可选打开 host 多人语义），Dispose 时精确还原。
///     - Game1.player 是静态属性、backing 字段 _player 为 private static，setter 会调用旧值 unload()，
///       因此读写都走反射直写字段，绕开 unload 副作用。
///     - hostMode=true 时设 multiplayerMode=2（IsServer → IsMasterGame=true）并种入一个
///       otherFarmers 条目（Game1.IsMultiplayer => otherFarmers.Count > 0），打开
///       MultiplayerHelper/AgentSyncBroadcaster 的联机门控。
/// </summary>
public sealed class Game1TestScope : IDisposable
{
    private static readonly FieldInfo? PlayerField =
        typeof(Game1).GetField("_player", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly Farmer? _prevPlayer;
    private readonly int _prevMultiplayerMode;
    private readonly NetRootDictionary<long, Farmer>? _prevOtherFarmers;

    public Farmer LocalPlayer { get; }

    /// <summary>
    ///     无头 Farmer：Harmony 在 net8 测试进程不可用（MonoMod JIT 补丁崩溃），改用
    ///     GetUninitializedObject 造壳 + 反射补净字段。本套件只读 UniqueMultiplayerID；
    ///     若未来路径触碰其它成员，会在测试里以 NRE 显式暴露（按需补字段）。
    /// </summary>
    private static Farmer CreateHeadlessFarmer(long uniqueMultiplayerId)
    {
        var farmer = (Farmer)RuntimeHelpers.GetUninitializedObject(typeof(Farmer));
        typeof(Farmer)
            .GetField("uniqueMultiplayerID", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .SetValue(farmer, new NetLong(uniqueMultiplayerId));
        return farmer;
    }

    /// <summary>
    ///     SMAPI 门控无头种子：Context.IsMultiplayer = IsWorldReady && multiplayerMode!=0（或 IsSplitScreen）。
    ///     - IsWorldReady 是 SMAPI 自家 auto-property（internal set），反射置 true；
    ///     - IsSplitScreen → GameRunner.instance.gameInstances.Count，无头环境 GameRunner.instance
    ///       为 null 会 NRE——种壳对象 + 空实例列表（Count ≤ 1 ⇒ 判定非分屏）。
    /// </summary>
    private void SeedGameRunnerForSmodGates()
    {
        var runner = (GameRunner)RuntimeHelpers.GetUninitializedObject(typeof(GameRunner));
        typeof(GameRunner)
            .GetField("gameInstances", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .SetValue(runner, new List<Game1>());
        typeof(GameRunner)
            .GetField("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .SetValue(null, runner);

        // Context.IsWorldReady 为 auto-property；若未来 SMAPI 改为计算属性则静默跳过（测试会给出可诊断的失败）。
        typeof(StardewModdingAPI.Context)
            .GetProperty("IsWorldReady", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, true);
    }

    public Game1TestScope(bool hostMode = false, long localPlayerId = 987654321)
    {
        Assert.True(PlayerField != null, "Game1._player 反射失败——游戏版本变更需同步本测试基建");

        _prevPlayer = (Farmer?)PlayerField!.GetValue(null);
        _prevMultiplayerMode = Game1.multiplayerMode;
        _prevOtherFarmers = Game1.otherFarmers;

        // Farmer 构造在无头环境会因 Game1.content == null 崩溃，用壳对象 + 净字段。
        LocalPlayer = CreateHeadlessFarmer(localPlayerId);
        PlayerField!.SetValue(null, LocalPlayer);

        if (hostMode)
        {
            Game1.multiplayerMode = 2;
            Game1.otherFarmers = new NetRootDictionary<long, Farmer>();
            // NetRootDictionary 的 TValue 是裸类型，内部自包 NetRoot。
            Game1.otherFarmers.Add(111222333, CreateHeadlessFarmer(111222333));
            SeedGameRunnerForSmodGates();
        }
    }

    public void Dispose()
    {
        // 反射直写还原，避免属性 setter 触发 unload()。
        PlayerField!.SetValue(null, _prevPlayer);
        Game1.multiplayerMode = (byte)_prevMultiplayerMode;
        Game1.otherFarmers = _prevOtherFarmers;
    }
}

/// <summary>
///     主线程泵：专用线程模拟游戏主线程，循环排空 HostRequestHandlers.MainThreadActions。
///     自带心跳、卡顿检测与全进程线程状态转储（无日志卡死的虚拟环境观测仪器）。
/// </summary>
public sealed class MainThreadPump : IDisposable
{
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _watchdog;
    private readonly double _stallDumpThresholdMs;
    private volatile bool _running = true;
    private double _lastBeatMs;

    private volatile int _threadId;
    public int ThreadId => _threadId;
    public double MaxStallMs { get; private set; }

    /// <summary>卡顿超过阈值时捕获一次的全进程线程状态转储（诊断用，null = 未发生卡顿）。</summary>
    public volatile string? StallDump;

    public MainThreadPump(double stallDumpThresholdMs = 3000)
    {
        _stallDumpThresholdMs = stallDumpThresholdMs;
        _lastBeatMs = 0;
        _threadId = -1;

        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            _threadId = Environment.CurrentManagedThreadId;
            started.TrySetResult(0);
            while (_running)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                var stall = now - _lastBeatMs;
                if (stall > MaxStallMs)
                {
                    MaxStallMs = stall;
                }

                _lastBeatMs = now;
                Interlocked.Increment(ref IterationsField);

                // 与游戏 OnUpdateTicked 相同的排水动作（每 tick）。
                HostRequestHandlers.ProcessMainThreadActions();
                Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "MainThreadPump"
        };
        _thread.Start();
        started.Task.Wait(TimeSpan.FromSeconds(5));
        _threadId = _thread.ManagedThreadId;

        _watchdog = new Timer(_ =>
        {
            var stall = _clock.Elapsed.TotalMilliseconds - _lastBeatMs;
            if (stall > _stallDumpThresholdMs && StallDump == null)
            {
                StallDump = ThreadStackDumper.Dump($"主线程泵心跳停滞 {stall:F0}ms (阈值 {_stallDumpThresholdMs:F0}ms)");
            }
        }, null, 250, 250);
    }

    private long IterationsField;

    /// <summary>泵线程上的迭代数（线程安全读）。</summary>
    public long IterationCount => Interlocked.Read(ref IterationsField);

    public void Dispose()
    {
        _watchdog.Dispose();
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>无外部依赖的全进程线程状态转储（.NET 无跨线程托管栈公共 API，退而记录线程状态/等待原因）。</summary>
public static class ThreadStackDumper
{
    public static string Dump(string reason)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Watchdog] {reason} @ {DateTime.UtcNow:HH:mm:ss.fff}");
        var process = Process.GetCurrentProcess();
        foreach (var t in process.Threads.OfType<ProcessThread>())
        {
            try
            {
                sb.AppendLine(
                    $"  tid={t.Id} state={t.ThreadState} wait={t.WaitReason} totalTime={t.TotalProcessorTime.TotalMilliseconds:F0}ms");
            }
            catch
            {
                // 线程可能恰好退出，跳过。
            }
        }

        return sb.ToString();
    }
}

/// <summary>轮询等待条件成立的小工具（避免测试里到处手写 spin loop）。</summary>
public static class Poll
{
    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }
}

/// <summary>测试用最小 WorldSnapshot（位置记录无默认 ctor，11 个必填参数显式给值）。</summary>
public static class TestSnapshots
{
    public static WorldSnapshot Minimal() => new(
        Season: "Spring",
        Day: 1,
        Time: "600",
        Weather: "Sunny",
        Location: "Farm",
        NpcTile: new TilePosition(0, 0),
        NearbyObjects: "",
        Friendship: 0,
        NpcState: "IDLE",
        Inventory: Array.Empty<InventoryItem>(),
        FarmerName: "测试农夫");
}
