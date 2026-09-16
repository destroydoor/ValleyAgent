using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Agents;
using ValleyAgent.AI;
using ValleyAgent.Api;
using ValleyAgent.Beats;
using ValleyAgent.Chat;
using ValleyAgent.Tracking;
using ValleyAgent.Combat;
using ValleyAgent.Commands;
using ValleyAgent.Config;
using ValleyAgent.Core;
using ValleyAgent.Debug;
using ValleyAgent.Dialogue;
using ValleyAgent.Economy;
using ValleyAgent.Friendship;
using ValleyAgent.Goals;
using ValleyAgent.Handlers;
using ValleyAgent.i18n;
using ValleyAgent.Infrastructure;
using ValleyAgent.Multiplayer;
using ValleyAgent.Navigation;
using ValleyAgent.Patches;
using ValleyAgent.Performance;
using ValleyAgent.Protocol;
using ValleyAgent.RAG;
using ValleyAgent.Rendering;
using ValleyAgent.Resilience;
using ValleyAgent.Save;
using ValleyAgent.Services;
using ValleyAgent.Services.Schedule;
using ValleyAgent.StateMachine;
using ValleyAgent.UI;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;
using AllocateAgentHandler = ValleyAgent.Protocol.AllocateAgentHandler;
using LogLevel = StardewModdingAPI.LogLevel;

namespace ValleyAgent.Initialization;

public class ServiceInitializer
{
    // 跨段共享的服务引用（前段降级时为 null，后段用 EnsureDependencies 显式跳过而非 NRE）。
    private ModConfig? _config;
    private ITranslationProvider? _translation;
    private DebugLogger? _debugLogger;
    private CircuitBreaker? _circuitBreaker;
    private TokenBudgetManager? _tokenBudget;
    private PerformanceMonitor? _performanceMonitor;
    private CacheManager? _cacheManager;
    private AgentAllocationManager? _allocationManager;
    private AgentService? _agentService;
    private TranscriptSink? _transcriptSink;
    private NpcEconomyProfileLoader? _economyProfileLoader;
    private NpcConfigLoader? _npcConfigLoader;
    private AgentSyncBroadcaster? _broadcaster;
    private DualPathAgentServerProvider? _agentServerProvider;
    private ServerProcessManager? _serverProcessManager;
    private MovementService? _movementService;
    private AgentTickLoop? _agentTickLoop;
    private AgentNavigator? _agentNavigator;
    private GameSummaryLoader? _gameSummaryLoader;
    private GoalExecutor? _goalExecutor;
    private CommandRegistry? _commandRegistry;
    private FightHandler? _fightHandler;
    private FarmHandler? _farmHandler;
    private MineHandler? _mineHandler;
    private ForageHandler? _forageHandler;

    private readonly IServiceContainer _container;
    private readonly ServerProcessManager? _externalServerManager;
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;

    public ServiceInitializer(
        IModHelper helper,
        IMonitor monitor,
        IServiceContainer container,
        ServerProcessManager? externalServerManager = null)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _externalServerManager = externalServerManager;
    }

    /// <summary>
    ///     分段初始化（issue #25，异常处理审计 PR3）。
    ///     此前 ~50 个服务顺序 new + 注册零隔离：任何一个抛出（损坏 JSON / 权限 / 配置越界）
    ///     即中断整条链，已注册服务留在容器形成半初始化状态，且无任何降级标记。
    ///     现按职责分段（config / i18n / 数据加载 / 韧性 / 分配 / Agent 运行时 / 经济数据 /
    ///     广播 / WS / 移动导航 / 目标处理器 / HUD / 命令 / 社交 / 控制台），每段独立 try/catch——
    ///     失败段记入 <see cref="DegradedFeatures"/>（ValleyAgent_status / _diag 可查）并继续下一段；
    ///     依赖前段产物的段在依赖缺失时显式跳过（同样记降级），不让 NRE 变成含糊的二次故障。
    /// </summary>
    public void Initialize()
    {
        RunSegment("config-core", InitializeConfigCore);
        RunSegment("i18n-debug", InitializeI18nAndDebug);
        RunSegment("data-bios", InitializeDataLoaders);
        RunSegment("resilience", InitializeResilienceServices);
        RunSegment("allocation", InitializeAllocation);
        RunSegment("agent-runtime", InitializeAgentRuntime);
        RunSegment("economy-data", InitializeEconomyAndNpcConfigs);
        RunSegment("sync-broadcast", InitializeBroadcaster);
        RunSegment("websocket", InitializeWebSocket);
        RunSegment("movement-nav", InitializeMovementAndNavigation);
        RunSegment("goal-handlers", InitializeGoalHandlers);
        RunSegment("hud-ui", InitializeHudAndContext);
        RunSegment("commands", InitializeCommands);
        RunSegment("friendship", InitializeFriendship);
        RunSegment("console", InitializeConsoleCommands);

        if (DegradedFeatures.Any)
        {
            _monitor.Log(
                $"ValleyAgent services initialized WITH DEGRADATION ({DegradedFeatures.Snapshot().Count} segment(s) failed/skipped — see ValleyAgent_status)",
                LogLevel.Error);
        }
        else
        {
            _monitor.Log("ValleyAgent services initialized successfully", LogLevel.Info);
        }
    }

    /// <summary>
    ///     段级守卫：执行 <paramref name="segment"/>，异常被隔离（不外抛、不中断后续段），
    ///     记 Error（全栈）+ ModErrorLog 落盘 + DegradedFeatures 登记（status/diag 可查）。
    /// </summary>
    private void RunSegment(string segmentName, Action segment)
    {
        try
        {
            segment();
        }
        catch (Exception ex)
        {
            DegradedFeatures.Report($"service:{segmentName}", $"{ex.GetType().Name}: {ex.Message}", ex);
            _monitor.Log($"[Init] segment '{segmentName}' failed — degraded (continuing): {ex}", LogLevel.Error);
            ModErrorLog.LogError("InitSegment", $"segment '{segmentName}' failed", ex);
        }
    }

    /// <summary>
    ///     段依赖检查：依赖的前段产物有 null（该段已降级）时，登记"依赖跳过"降级并返回 false，
    ///     调用方直接 return——显式归因，避免把前段故障演成含糊的 NRE。
    /// </summary>
    private bool DependenciesMissing(string segmentName, string dependsOn, params object?[] dependencies)
    {
        if (!dependencies.Any(d => d is null))
        {
            return false;
        }

        DegradedFeatures.Report($"service:{segmentName}", $"依赖段 {dependsOn} 未成功初始化，本段跳过");
        _monitor.Log($"[Init] segment '{segmentName}' skipped: dependency '{dependsOn}' degraded earlier", LogLevel.Warn);
        return true;
    }

    // ── 段 1：配置与全局静态路由装配 ─────────────────────────────────────────

    private void InitializeConfigCore()
    {
        var config = _helper.ReadConfig<ModConfig>();
        _config = config;
        _container.RegisterSingleton(config);
        _container.RegisterSingleton(_monitor);
        _container.RegisterSingleton(_helper);

        // E2-1 动态速度：把 ModConfig 的距离分段参数应用到 MovementConstants 静态查找表。
        MovementConstants.ApplyDynamicSpeedConfig(
            config.DynamicSpeedEnabled,
            config.DynamicSpeedFarThreshold,
            config.DynamicSpeedMidThreshold,
            config.DynamicSpeedNearMultiplier,
            config.DynamicSpeedMidMultiplier,
            config.DynamicSpeedFarMultiplier);

        // E2-3 长文本规则：把 ModConfig 的气泡主开关 / 长文间隔应用到 SpeechDisplayRouter，
        // 并挂接运行时显示出口（聊天栏 addMessage / 头顶气泡 showTextAboveHead）。
        SpeechDisplayRouter.ApplyConfig(config.ChatBubbleEnabled, config.LongTextIntervalMs);
        SpeechDisplayRouter.SetChatSink((message, color) => Game1.chatBox?.addMessage(message, color));
        SpeechDisplayRouter.SetBubbleSink((npc, text, durationMs) => npc.showTextAboveHead(text, duration: durationMs));
    }

    // ── 段 2：i18n 与调试日志 ────────────────────────────────────────────────

    private void InitializeI18nAndDebug()
    {
        if (DependenciesMissing("i18n-debug", "config-core", _config))
        {
            return;
        }

        var translation = new TranslationProvider(onCorruptResource: (message, exception) =>
            _monitor.Log($"[TranslationProvider] {message}: {exception}", LogLevel.Error));
        _container.RegisterSingleton<ITranslationProvider>(translation);
        _container.RegisterSingleton(translation);
        _translation = translation;
        _monitor.Log($"i18n initialized. Language: {translation.CurrentLanguage}", LogLevel.Debug);

        var debugLogger = new DebugLogger(_config!.DebugMode ? Debug.LogLevel.Debug : Debug.LogLevel.Info)
        {
            OutputSink = (msg, level) =>
            {
                var smapiLevel = level switch
                {
                    Debug.LogLevel.Verbose => LogLevel.Trace,
                    Debug.LogLevel.Debug => LogLevel.Debug,
                    Debug.LogLevel.Info => LogLevel.Info,
                    Debug.LogLevel.Warn => LogLevel.Warn,
                    Debug.LogLevel.Error => LogLevel.Error,
                    _ => LogLevel.Debug
                };
                _monitor.Log(msg, smapiLevel);
            }
        };
        _container.RegisterSingleton(debugLogger);
        _debugLogger = debugLogger;
    }

    // ── 段 3：数据加载（人设 bio / 游戏摘要）── 损坏 JSON 的第一现场 ─────────

    private void InitializeDataLoaders()
    {
        if (DependenciesMissing("data-bios", "config-core", _config))
        {
            return;
        }

        // 2026-09-14 死代码清除：RAGKnowledgeBase（flat npcs.json 检索）已删除——容器内无消费者。
        var bioLoader = new ValleyTalkBioLoader(_monitor);
        bioLoader.Initialize(_helper);
        bioLoader.Load();
        _container.RegisterSingleton(bioLoader);

        var gameSummaryLoader = new GameSummaryLoader(_monitor);
        gameSummaryLoader.Initialize(_helper);
        gameSummaryLoader.Load();
        _container.RegisterSingleton(gameSummaryLoader);
        _gameSummaryLoader = gameSummaryLoader;
    }

    // ── 段 4：韧性与遥测（熔断器 / token 预算 / 性能监视 / 缓存）────────────

    private void InitializeResilienceServices()
    {
        if (DependenciesMissing("resilience", "config-core", _config))
        {
            return;
        }

        var config = _config!;
        var cbConfig = new CircuitBreakerConfig
        {
            ConsecutiveFailureThreshold = config.CircuitBreakerThreshold,
            // MiniMax-M3 是推理模型，单次响应常规 10-60s，慢时可至 90s+，阈值需放宽
            SlowResponseThresholdSeconds = 120.0,
            ResponseTimeWindowSeconds = 180,
            OpenCooldownSeconds = 30,
            HalfOpenMaxCalls = 3,
            MinResponseTimeSamples = 5
        };
        var circuitBreaker = new CircuitBreaker(cbConfig);
        circuitBreaker.OnStateChanged += (s, e) =>
            _monitor.Log($"Circuit Breaker: {e.PreviousState} -> {e.NewState} ({e.Reason})", LogLevel.Info);
        circuitBreaker.OnFallbackActivated += (s, e) =>
            _monitor.Log($"Circuit Breaker fallback activated: {e.Reason}", LogLevel.Warn);
        _container.RegisterSingleton(circuitBreaker);
        _circuitBreaker = circuitBreaker;

        var tokenBudget = new TokenBudgetManager(config.TokenBudget);
        tokenBudget.OnBudgetWarning += (s, e) =>
            _monitor.Log($"Token budget warning: {e.UsagePercent:P0} used ({e.CurrentUsage}/{e.Budget})",
                LogLevel.Warn);
        tokenBudget.OnBudgetExceeded += (s, e) =>
            _monitor.Log($"Token budget exceeded: {e.CurrentUsage}/{e.Budget}", LogLevel.Error);
        _container.RegisterSingleton(tokenBudget);
        _tokenBudget = tokenBudget;

        var performanceMonitor = new PerformanceMonitor();
        _container.RegisterSingleton(performanceMonitor);
        _performanceMonitor = performanceMonitor;

        var cacheManager = new CacheManager();
        _container.RegisterSingleton(cacheManager);
        _cacheManager = cacheManager;
    }

    // ── 段 5：身体分配（并发池 + TS 导演的 allocate_agent 处理器）────────────

    private void InitializeAllocation()
    {
        if (DependenciesMissing("allocation", "config-core", _config))
        {
            return;
        }

        var allocationManager = new AgentAllocationManager(_config!.MinAgentNpcs, _config.MaxAgentNpcs);
        _container.RegisterSingleton(allocationManager);
        _allocationManager = allocationManager;

        // Task 9: AllocateAgentHandler — 处理 TS 导演下发的 allocate_agent 消息（设计文档 §4.2.2）
        var allocateAgentHandler = new AllocateAgentHandler(allocationManager);
        _container.RegisterSingleton(allocateAgentHandler);
    }

    // ── 段 6：Agent 运行时（对话状态 / AgentService / 留痕 Sink）─────────────

    private void InitializeAgentRuntime()
    {
        if (DependenciesMissing(
                "agent-runtime", "resilience+allocation",
                _config, _circuitBreaker, _tokenBudget, _performanceMonitor, _cacheManager, _allocationManager))
        {
            return;
        }

        var config = _config!;
#pragma warning disable CA2000 // ConversationStateManager 由 DI 容器管理生命周期
        var conversationStateManager = new ConversationStateManager();
#pragma warning restore CA2000
        _container.RegisterSingleton(conversationStateManager);

        var dialogueStateManager = new DialogueStateManager
        {
            DialogueCooldownMs = config.DialogueCooldownSeconds * 1000,
            GiftCooldownMs = config.GiftCooldownSeconds * 1000,
            CooldownHintCallback = (npcName, msg) =>
                _monitor?.Log($"[DialogueCooldown] {npcName}: {msg}", LogLevel.Debug)
        };
        _container.RegisterSingleton(dialogueStateManager);
        ValleyAgentApi.SetSharedDialogueState(dialogueStateManager);

        var agentService = new AgentService(
            config, _allocationManager!, _tokenBudget!, _performanceMonitor!, _cacheManager!, _circuitBreaker!);
        _container.RegisterSingleton(agentService);
        _agentService = agentService;

        // E1-1: TranscriptSink 需在 AgentService 创建 Agent 前注入，以便 CreateAgent 时订阅状态机
        var transcriptSinkEarly = new TranscriptSink(
            _monitor, Path.Combine(_helper.DirectoryPath, config.Transcript.RootDir));
        agentService.TranscriptSink = transcriptSinkEarly;
        _container.RegisterSingleton(transcriptSinkEarly);
        _transcriptSink = transcriptSinkEarly;
    }

    // ── 段 7：经济档案 + NPC 人设配置装载（文件缺失/损坏自带兜底警告）────────

    private void InitializeEconomyAndNpcConfigs()
    {
        if (DependenciesMissing("economy-data", "config-core+agent-runtime", _config, _agentService))
        {
            return;
        }

        var config = _config!;
        var agentService = _agentService!;

        // E3-1: NPC 经济档案装载器——CreateAgent 时按 NPC 查初始资金回填钱包
        var economyProfileLoader = new NpcEconomyProfileLoader(_monitor);
        if (config.Economy.Enabled)
        {
            economyProfileLoader.Load(_helper, config.Economy.DataFile);
            if (economyProfileLoader.Count > 0)
            {
                _monitor.Log(
                    $"E3-1 economy profiles loaded: {economyProfileLoader.Count} NPCs from {config.Economy.DataFile}",
                    LogLevel.Info);
            }
            else
            {
                _monitor.Log($"E3-1 economy profiles empty/missing: {config.Economy.DataFile} — NPC wallets start at 0",
                    LogLevel.Warn);
            }
        }
        else
        {
            _monitor.Log("E3-1 economy system disabled via config.", LogLevel.Info);
        }

        _container.RegisterSingleton(economyProfileLoader);
        _economyProfileLoader = economyProfileLoader;
        agentService.EconomyProfiles = economyProfileLoader;

        // 阶段 3 (3.2.1): NPC 人设配置装载器——DirectorContextBuilder 拼装人设摘要、DirectorTools 约束人设
        var npcConfigLoader = new NpcConfigLoader(_monitor);
        npcConfigLoader.LoadFromDirectory(_helper, "npc-configs");
        if (npcConfigLoader.Count > 0)
        {
            _monitor.Log(
                $"Phase3 npc-configs loaded: {npcConfigLoader.Count} NPCs from npc-configs/",
                LogLevel.Info);
        }
        else
        {
            _monitor.Log("Phase3 npc-configs empty/missing — NPC personality configs unavailable", LogLevel.Warn);
        }

        _container.RegisterSingleton(npcConfigLoader);
        _npcConfigLoader = npcConfigLoader;
        agentService.NpcConfigs = npcConfigLoader;
    }

    // ── 段 8：联机状态广播器 ────────────────────────────────────────────────

    private void InitializeBroadcaster()
    {
        if (DependenciesMissing("sync-broadcast", "agent-runtime", _agentService))
        {
            return;
        }

        var modId = ModEntry.Instance?.ModManifest.UniqueID
                    ?? throw new InvalidOperationException(
                        "ModEntry.Instance is not initialized before service initialization.");
        var broadcaster = new AgentSyncBroadcaster(
            _agentService!,
            _monitor,
            _helper,
            modId);
        _container.RegisterSingleton(broadcaster);
        _broadcaster = broadcaster;
    }

    // ── 段 9：WS 客户端 / 双路 Provider / TS 服务器进程管理 ─────────────────

    private void InitializeWebSocket()
    {
        if (DependenciesMissing("websocket", "config-core+agent-runtime", _config, _agentService))
        {
            return;
        }

        var config = _config!;
        var agentService = _agentService!;

        // WebSocket URL auto-bound from AgentServerHost + ServerPort (GMCM WebSocketUrl/AgentServerUri no longer used).
        var wsHost = string.IsNullOrWhiteSpace(config.AgentServerHost) ? "127.0.0.1" : config.AgentServerHost;
        var wsUrl = $"ws://{wsHost}:{config.ServerPort}";
        var wsClient = new WebSocketClient(wsUrl, "ValleyAgent");
        // WS 客户端内部日志（心跳失败/重连失败）落盘：服务器长期不可用时有可观测信号
        wsClient.LogCallback = msg => ModErrorLog.LogServerLine(msg);
        var agentServerProvider = new DualPathAgentServerProvider(wsClient)
        {
            LogCallback = (msg, isError) =>
            {
                _monitor.Log($"[AgentServer] {msg}", isError ? LogLevel.Error : LogLevel.Trace);
                if (isError)
                {
                    ModErrorLog.LogError("AgentServer", msg);
                }
            }
        };
        // 2026-08-23 审计 P1：本地对话路径回写 LastDialoguePlayerId（此前仅房客中继路径写，
        // FOLLOW 的"最近对话发起玩家"在多人下语义破损）。回调在后台续体触发，
        // 写 Brain 转投 HostRequestHandlers 主线程队列，与中继路径同一纪律。
        agentServerProvider.DialogueCompleted = (npcName, playerId) =>
        {
            Multiplayer.HostRequestHandlers.EnqueueMainThread(() =>
            {
                if (agentService.TryGetBrain(npcName, out var brain) && brain?.Brain != null)
                {
                    brain.Brain.LastDialoguePlayerId = playerId;
                }
            });
        };
        agentService.AgentServerProvider = agentServerProvider;
        _container.RegisterSingleton(agentServerProvider);
        _container.RegisterSingleton<IAgentServerProvider>(agentServerProvider);
        _agentServerProvider = agentServerProvider;

#pragma warning disable CA2000
        var serverManager = _externalServerManager ?? new ServerProcessManager(_monitor, _helper, config);
#pragma warning restore CA2000
        serverManager.AutoStart = config.AutoStartServer;
        serverManager.ConsoleWindow = config.ServerConsoleWindow;
        serverManager.ServerExecutablePath = config.ServerExecutablePath;
        serverManager.ServerDirectory = config.ServerDirectory;
        serverManager.ServerPort = config.ServerPort;
        serverManager.StartupTimeoutSeconds = config.ServerStartupTimeoutSeconds;
        serverManager.MaxRestartAttempts = config.ServerMaxRestartAttempts;
        serverManager.LogCallback += msg => _monitor.Log($"[AgentServer] {msg}");
        // 复用 GameLaunched 预启动的管理器时，把其内部配置重绑为容器当前实例
        // （否则重启 server 时 --llm-config/--llm-provider 等参数读旧实例，GMCM/测试热改静默失效）。
        serverManager.RebindConfig(config);
        _container.RegisterSingleton(serverManager);
        _serverProcessManager = serverManager;

        _monitor.Log(
            $"Agent Server initialized (WS: {wsUrl}, AutoStart: {config.AutoStartServer}, Port: {config.ServerPort}).",
            LogLevel.Debug);
    }

    // ── 段 10：移动 / 导航 / 日程 / 求购等基础服务 ──────────────────────────

    private void InitializeMovementAndNavigation()
    {
        if (DependenciesMissing(
                "movement-nav", "config-core+agent-runtime", _config, _agentService))
        {
            return;
        }

        var config = _config!;
        var agentService = _agentService!;

        var movementService = new MovementService(_monitor);
        _container.RegisterSingleton<IMovementService>(movementService);
        _container.RegisterSingleton(movementService);
        _movementService = movementService;

        var agentTickLoop = new AgentTickLoop(_monitor, movementService, config);
        _container.RegisterSingleton(agentTickLoop);
        _agentTickLoop = agentTickLoop;

        // Spark 分配器：玩家附近 NPC 低概率激活（设计文档 §4.2.3）
        var sparkAllocator = new SparkAllocator();
        _container.RegisterSingleton(sparkAllocator);

        var monsterAggroManager = new MonsterAggroManager(_monitor);
        _container.RegisterSingleton(monsterAggroManager);

        var saveDataManager = new SaveDataManager((message, exception) =>
            _monitor.Log($"[SaveDataManager] {message}: {exception}", LogLevel.Error));
        _container.RegisterSingleton(saveDataManager);

        // E5-1: NPC 作息表查询服务（数据层）。晨间喊话 E5-2 稍后从此服务取作息，此处只注册不接线。
        var npcScheduleService = new NpcScheduleService();
        _container.RegisterSingleton(npcScheduleService);

        // B0/E5-3: 主动发言额度服务（E5-2 喊话/E5-3 搭话共享）。
        // 包装 ChatSessionRegistry.Instance 的每日计数，叠加每日上限 + 冷却 + 会话豁免；
        // OnDayStarted 日切时由 EventHandlerInitializer 调 ResetDaily 重置。
        var proactiveSpeechQuota = new ProactiveSpeechQuota(ChatSessionRegistry.Instance)
        {
            DailyLimit = config.ProactiveSpeechDailyLimit,
            CooldownMinutes = config.ProactiveSpeechCooldownMinutes,
            Enabled = config.EnableProactiveSpeech
        };
        _container.RegisterSingleton(proactiveSpeechQuota);

        // E3-5: NPC 求购服务（长 TTL 求购单，当日有效）。OnDayStarted 由
        // EventHandlerInitializer 遍历 Agent 调 TryGenerate 生成求购并发布聊天栏。
        var purchaseRequestService = new NpcPurchaseRequestService
        {
            TradeProbability = config.ProactiveTradeProbability
        };
        _container.RegisterSingleton(purchaseRequestService);

        var locationGraph = new LocationGraph();
        _container.RegisterSingleton(locationGraph);

        var agentNavigator = new AgentNavigator(locationGraph, movementService, _monitor)
        {
            FarDistanceThreshold = config.FollowDistance,
            DepartureSkipDistance = config.DepartureSkipDistance,
            // B1/B2/F3: 旅行失败时回调 → ForceTransition(IDLE, reason="travel_failed")
            // 通过回调注入避免 Abstractions 项目依赖 ValleyAgent（AgentService 所在）
            OnTravelFailed = npcName =>
            {
                if (agentService.TryGetAgent(npcName, out var agent) && agent != null)
                {
                    _ = agent.StateMachine.ForceTransition(
                        AgentState.IDLE, true, reason: "travel_failed");
                }
            },
            // 2026-08-16 联机：FOLLOW 目标 = 最近一次对话的发起玩家（房客对话 → 跟房客）。
            // brain.LastDialoguePlayerId 由 HostRequestHandlers 在房客对话回包时写入；
            // 本地/单机对话不写（null → 回落本机玩家，行为与联机适配前一致）。
            FollowTargetResolver = npc =>
            {
                if (agentService.TryGetBrain(npc.Name, out var followAgent)
                    && followAgent?.Brain.LastDialoguePlayerId is { Length: > 0 }
                    && long.TryParse(followAgent.Brain.LastDialoguePlayerId, out var followId))
                {
                    return Game1.GetPlayer(followId) ?? Game1.player;
                }

                return Game1.player;
            }
        };
        _container.RegisterSingleton(agentNavigator);
        _agentNavigator = agentNavigator;
    }

    // ── 段 11：状态处理器 + GoalExecutor + 渲染器 ───────────────────────────

    private void InitializeGoalHandlers()
    {
        if (DependenciesMissing(
                "goal-handlers", "movement-nav+websocket+agent-runtime",
                _movementService, _agentNavigator, _agentTickLoop, _agentService, _agentServerProvider, _translation))
        {
            return;
        }

        var movementService = _movementService!;
        var agentNavigator = _agentNavigator!;
        var agentService = _agentService!;
        var translation = _translation!;

        var fightHandler = new FightHandler(_monitor, movementService);
        var farmHandler = new FarmHandler(_monitor, movementService, agentNavigator);
        var mineHandler = new MineHandler(_monitor, movementService, agentNavigator);
        var forageHandler = new ForageHandler(_monitor, movementService, agentNavigator);
        _fightHandler = fightHandler;
        _farmHandler = farmHandler;
        _mineHandler = mineHandler;
        _forageHandler = forageHandler;
        var talkHandler = new TalkHandler(_monitor, movementService, translation);
        var idleWanderHandler = new IdleWanderHandler(
            _monitor,
            movementService,
            _agentTickLoop!.IsReleasedToVanilla,
            npcName => string.Equals(DialogueBoxInputPatch.GetActiveAgentNpc(), npcName,
                StringComparison.OrdinalIgnoreCase));
        _container.RegisterSingleton(fightHandler);
        _container.RegisterSingleton(farmHandler);
        _container.RegisterSingleton(mineHandler);
        _container.RegisterSingleton(forageHandler);
        _container.RegisterSingleton(talkHandler);
        _container.RegisterSingleton(idleWanderHandler);

        // 阶段 2：GoalExecutor 调度器（set_goal 执行态）。EXECUTING_GOAL / TRAVELING_TO_REPORT
        // 作为状态注册进状态机，每 tick 由 ApplyControllerToNpc 驱动（见 EventHandlerInitializer）。
        var goalExecutor = new GoalExecutor(
            _monitor, movementService, agentService, _config!, _agentServerProvider!, agentNavigator,
            farmHandler, mineHandler, fightHandler, forageHandler);
        _container.RegisterSingleton(goalExecutor);
        _goalExecutor = goalExecutor;
        agentService.SetHandlerActions(new Dictionary<AgentState, Action<NPC, AgentInstance, int>>
        {
            { AgentState.IDLE, idleWanderHandler.Update },
            { AgentState.FOLLOW, (npc, agent, tick) => agentNavigator.Update(npc, tick) },
            { AgentState.FIGHT, fightHandler.Update },
            { AgentState.FARM, farmHandler.Update },
            { AgentState.MINE, mineHandler.Update },
            { AgentState.FORAGE, forageHandler.Update },
            { AgentState.TALK, talkHandler.Update },
            { AgentState.EXECUTING_GOAL, goalExecutor.TickExecuting },
            { AgentState.TRAVELING_TO_REPORT, goalExecutor.TickReporting }
        });

        var agentRenderer = new AgentRenderer(agentService, fightHandler, mineHandler);
        _container.RegisterSingleton(agentRenderer);
    }

    // ── 段 12：HUD 与决策上下文 ─────────────────────────────────────────────

    private void InitializeHudAndContext()
    {
        if (DependenciesMissing("hud-ui", "allocation+data-bios", _allocationManager, _gameSummaryLoader))
        {
            return;
        }

        var hudConfig = new HUDConfig();
        var agentHud = new AgentHUD(hudConfig, _allocationManager!);
        _container.RegisterSingleton(agentHud);

        var hudRenderer = new SmaHUDRenderer();
        _container.RegisterSingleton(hudRenderer);

        var decisionContextBuilder = new DecisionContextBuilder(_gameSummaryLoader!);
        _container.RegisterSingleton(decisionContextBuilder);
    }

    // ── 段 13：命令注册表 + 导演工具 + 执行器 ───────────────────────────────

    private void InitializeCommands()
    {
        if (DependenciesMissing(
                "commands", "goal-handlers+websocket+sync-broadcast",
                _agentService, _movementService, _agentNavigator, _agentServerProvider, _broadcaster, _goalExecutor,
                _agentTickLoop, _transcriptSink, _economyProfileLoader, _npcConfigLoader,
                _fightHandler, _farmHandler, _mineHandler, _forageHandler))
        {
            return;
        }

        var commandRegistry = new CommandRegistry(_monitor);
        commandRegistry.Register(new SetStateCommand(_monitor));
        commandRegistry.Register(new MoveToCommand(_monitor, _movementService!, _agentNavigator!));
        commandRegistry.Register(new FollowCommand(_monitor));
        commandRegistry.Register(new StopCommand(_monitor, _movementService!, _agentTickLoop!));
        commandRegistry.Register(new WaitCommand(_monitor, _movementService!));
        // 与 GoalExecutor 共用同一 handler 实例（状态机驱动与命令执行同一份运行状态）
        commandRegistry.Register(new HarvestCommand(_monitor, _movementService!, _farmHandler!));
        commandRegistry.Register(new MineCommand(_monitor, _movementService!, _mineHandler!));
        commandRegistry.Register(new ForageCommand(_monitor, _movementService!, _forageHandler!));
        commandRegistry.Register(new WaterCommand(_monitor, _movementService!, _farmHandler!));
        commandRegistry.Register(new AttackCommand(_monitor, _fightHandler!));
        commandRegistry.Register(new SpeakCommand(_monitor));
        commandRegistry.Register(new EmoteCommand(_monitor));
        commandRegistry.Register(new GiveItemCommand(_monitor));
        commandRegistry.Register(new GiveGiftCommand(_monitor));
        commandRegistry.Register(new ShowDialogueCommand(_monitor));
        commandRegistry.Register(new EatFoodCommand(_monitor));
        commandRegistry.Register(new DropItemCommand(_monitor));
        commandRegistry.Register(new UseItemCommand(_monitor));
        commandRegistry.Register(new SetFriendshipCommand(_monitor));
        commandRegistry.Register(new RememberCommand(_monitor));
        commandRegistry.Register(new ForgetCommand(_monitor));
        _container.RegisterSingleton(commandRegistry);
        _commandRegistry = commandRegistry;

        // 阶段 3: BeatStore（当前活跃 beat 场景描述，供 L3 注入）——先于 DirectorTools/CommandExecutor 注册
        var beatStore = new BeatStore(_monitor);
        _container.RegisterSingleton(beatStore);
        BeatStore.Current = beatStore;

        // 阶段 3: DirectorTools（director_command 工具执行器）。元层工具：只改状态数据，不进 NPC 上下文。
        var directorTools = new DirectorTools(
            _monitor, _agentService!, beatStore, _config!.Director.BeatDefaultDurationMinutes);
        _container.RegisterSingleton(directorTools);

        // 阶段 3 (3.6): 玩家行为采样跟踪器（10-tick 采样，day_started 聚合；DirectorContextBuilder 注入用）
        var playerActionTracker = new PlayerActionTracker(_monitor);
        _container.RegisterSingleton(playerActionTracker);

        // 阶段 3 (3.7): Director 上下文拼装器（day_started 压缩成 800-1500 token 结构化文本，随 day_started 发 TS）
        var directorContextBuilder = new DirectorContextBuilder(
            _monitor,
            _agentService!,
            playerActionTracker,
            _npcConfigLoader!,
            beatStore,
            _config.Director.ContextMinTokens,
            _config.Director.ContextMaxTokens);
        _container.RegisterSingleton(directorContextBuilder);

        var commandExecutor = new CommandExecutor(
            _monitor, _agentService!, commandRegistry, _agentServerProvider!, _broadcaster!, _goalExecutor!, directorTools, _movementService!, _agentTickLoop!);
        _container.RegisterSingleton(commandExecutor);

        // 2026-08-15 账本迁移（步骤 1）：adjust 原子批执行器——TS 账本权威，C# 只做物理校验 + 原语执行。
        // 与 CommandExecutor 完全独立：仅由 execute_adjust 消息驱动（EventHandlerInitializer 路由），旧路径不动。
        var adjustExecutor = new AdjustExecutor(_monitor, _agentService!, _agentServerProvider!);
        _container.RegisterSingleton(adjustExecutor);

        // E1-1: 命令执行结果回发时同步入队留痕（OnSendActionResult 签名：npcName, action, success, extra）
        commandExecutor.OnSendActionResult += (npcName, action, success, extra) =>
        {
            _transcriptSink!.RecordActionResult(npcName, action, success, extra);
            return Task.CompletedTask;
        };
    }

    // ── 段 14：好感度系统 ───────────────────────────────────────────────────

    private void InitializeFriendship()
    {
        if (DependenciesMissing("friendship", "config-core+commands", _config, _commandRegistry))
        {
            return;
        }

        var friendshipSystem = new FriendshipSystem(_config!.MaxFriendshipChangePerInteraction);
        _container.RegisterSingleton(friendshipSystem);
        NPCGiftPatch.FriendshipSystem = friendshipSystem;

        var setFriendshipCmd = _commandRegistry!.GetCommand("set_friendship") as SetFriendshipCommand;
        if (setFriendshipCmd != null)
        {
            setFriendshipCmd.FriendshipSystem = friendshipSystem;
        }
    }

    // ── 段 15：控制台命令（game-agnostic）───────────────────────────────────

    private void InitializeConsoleCommands()
    {
        if (DependenciesMissing("console", "allocation+resilience+i18n-debug", _allocationManager, _circuitBreaker, _debugLogger, _config))
        {
            return;
        }

        var consoleCommands = new ConsoleCommands(_allocationManager, _circuitBreaker, _debugLogger, _config);
        _container.RegisterSingleton(consoleCommands);
    }
}
