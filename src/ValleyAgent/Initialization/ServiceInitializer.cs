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

    public void Initialize()
    {
        var config = _helper.ReadConfig<ModConfig>();
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

        var translation = new TranslationProvider();
        _container.RegisterSingleton<ITranslationProvider>(translation);
        _container.RegisterSingleton(translation);
        _monitor.Log($"i18n initialized. Language: {translation.CurrentLanguage}", LogLevel.Debug);

        var debugLogger = new DebugLogger(config.DebugMode ? Debug.LogLevel.Debug : Debug.LogLevel.Info)
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

        // 2026-09-14 死代码清除：RAGKnowledgeBase（flat npcs.json 检索）已删除——容器内无消费者。
        var bioLoader = new ValleyTalkBioLoader(_monitor);
        bioLoader.Initialize(_helper);
        bioLoader.Load();
        _container.RegisterSingleton(bioLoader);

        var gameSummaryLoader = new GameSummaryLoader(_monitor);
        gameSummaryLoader.Initialize(_helper);
        gameSummaryLoader.Load();
        _container.RegisterSingleton(gameSummaryLoader);

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

        var allocationManager = new AgentAllocationManager(config.MinAgentNpcs, config.MaxAgentNpcs);
        _container.RegisterSingleton(allocationManager);

        // Task 9: AllocateAgentHandler — 处理 TS 导演下发的 allocate_agent 消息（设计文档 §4.2.2）
        var allocateAgentHandler = new AllocateAgentHandler(allocationManager);
        _container.RegisterSingleton(allocateAgentHandler);

        var tokenBudget = new TokenBudgetManager(config.TokenBudget);
        tokenBudget.OnBudgetWarning += (s, e) =>
            _monitor.Log($"Token budget warning: {e.UsagePercent:P0} used ({e.CurrentUsage}/{e.Budget})",
                LogLevel.Warn);
        tokenBudget.OnBudgetExceeded += (s, e) =>
            _monitor.Log($"Token budget exceeded: {e.CurrentUsage}/{e.Budget}", LogLevel.Error);
        _container.RegisterSingleton(tokenBudget);

        var performanceMonitor = new PerformanceMonitor();
        _container.RegisterSingleton(performanceMonitor);

        var cacheManager = new CacheManager();
        _container.RegisterSingleton(cacheManager);

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
            config, allocationManager, tokenBudget, performanceMonitor, cacheManager, circuitBreaker);
        _container.RegisterSingleton(agentService);

        // E1-1: TranscriptSink 需在 AgentService 创建 Agent 前注入，以便 CreateAgent 时订阅状态机
        var transcriptSinkEarly = new TranscriptSink(
            _monitor, Path.Combine(_helper.DirectoryPath, config.Transcript.RootDir));
        agentService.TranscriptSink = transcriptSinkEarly;
        _container.RegisterSingleton(transcriptSinkEarly);

        // E3-1: NPC 经济档案装载器——CreateAgent 时按 NPC 查初始资金回填钱包
        var economyProfileLoader = new NpcEconomyProfileLoader();
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
        agentService.EconomyProfiles = economyProfileLoader;

        // 阶段 3 (3.2.1): NPC 人设配置装载器——DirectorContextBuilder 拼装人设摘要、DirectorTools 约束人设
        var npcConfigLoader = new NpcConfigLoader();
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
        agentService.NpcConfigs = npcConfigLoader;

        var modId = ModEntry.Instance?.ModManifest.UniqueID
                    ?? throw new InvalidOperationException(
                        "ModEntry.Instance is not initialized before service initialization.");
        var broadcaster = new AgentSyncBroadcaster(
            agentService,
            _monitor,
            _helper,
            modId);
        _container.RegisterSingleton(broadcaster);

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

        _monitor.Log(
            $"Agent Server initialized (WS: {wsUrl}, AutoStart: {config.AutoStartServer}, Port: {config.ServerPort}).",
            LogLevel.Debug);

        var movementService = new MovementService(_monitor);
        _container.RegisterSingleton<IMovementService>(movementService);
        _container.RegisterSingleton(movementService);

        var agentTickLoop = new AgentTickLoop(_monitor, movementService, config);
        _container.RegisterSingleton(agentTickLoop);

        // Spark 分配器：玩家附近 NPC 低概率激活（设计文档 §4.2.3）
        var sparkAllocator = new SparkAllocator();
        _container.RegisterSingleton(sparkAllocator);

        var monsterAggroManager = new MonsterAggroManager(_monitor);
        _container.RegisterSingleton(monsterAggroManager);

        var saveDataManager = new SaveDataManager();
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

        var fightHandler = new FightHandler(_monitor, movementService);
        var farmHandler = new FarmHandler(_monitor, movementService, agentNavigator);
        var mineHandler = new MineHandler(_monitor, movementService, agentNavigator);
        var forageHandler = new ForageHandler(_monitor, movementService, agentNavigator);
        var talkHandler = new TalkHandler(_monitor, movementService, translation);
        var idleWanderHandler = new IdleWanderHandler(
            _monitor,
            movementService,
            agentTickLoop.IsReleasedToVanilla,
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
            _monitor, movementService, agentService, config, agentServerProvider, agentNavigator,
            farmHandler, mineHandler, fightHandler, forageHandler);
        _container.RegisterSingleton(goalExecutor);
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

        var hudConfig = new HUDConfig();
        var agentHud = new AgentHUD(hudConfig, allocationManager);
        _container.RegisterSingleton(agentHud);

        var hudRenderer = new SmaHUDRenderer();
        _container.RegisterSingleton(hudRenderer);

        var decisionContextBuilder = new DecisionContextBuilder(gameSummaryLoader);
        _container.RegisterSingleton(decisionContextBuilder);

        var commandRegistry = new CommandRegistry(_monitor);
        commandRegistry.Register(new SetStateCommand(_monitor));
        commandRegistry.Register(new MoveToCommand(_monitor, movementService, agentNavigator));
        commandRegistry.Register(new FollowCommand(_monitor));
        commandRegistry.Register(new StopCommand(_monitor, movementService, agentTickLoop));
        commandRegistry.Register(new WaitCommand(_monitor, movementService));
        commandRegistry.Register(new HarvestCommand(_monitor, movementService, farmHandler));
        commandRegistry.Register(new MineCommand(_monitor, movementService, mineHandler));
        commandRegistry.Register(new ForageCommand(_monitor, movementService, forageHandler));
        commandRegistry.Register(new WaterCommand(_monitor, movementService, farmHandler));
        commandRegistry.Register(new AttackCommand(_monitor, fightHandler));
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

        // 阶段 3: BeatStore（当前活跃 beat 场景描述，供 L3 注入）——先于 DirectorTools/CommandExecutor 注册
        var beatStore = new BeatStore(_monitor);
        _container.RegisterSingleton(beatStore);
        BeatStore.Current = beatStore;

        // 阶段 3: DirectorTools（director_command 工具执行器）。元层工具：只改状态数据，不进 NPC 上下文。
        var directorTools = new DirectorTools(
            _monitor, agentService, beatStore, config.Director.BeatDefaultDurationMinutes);
        _container.RegisterSingleton(directorTools);

        // 阶段 3 (3.6): 玩家行为采样跟踪器（10-tick 采样，day_started 聚合；DirectorContextBuilder 注入用）
        var playerActionTracker = new PlayerActionTracker(_monitor);
        _container.RegisterSingleton(playerActionTracker);

        // 阶段 3 (3.7): Director 上下文拼装器（day_started 压缩成 800-1500 token 结构化文本，随 day_started 发 TS）
        var directorContextBuilder = new DirectorContextBuilder(
            _monitor,
            agentService,
            playerActionTracker,
            npcConfigLoader,
            beatStore,
            config.Director.ContextMinTokens,
            config.Director.ContextMaxTokens);
        _container.RegisterSingleton(directorContextBuilder);

        var commandExecutor = new CommandExecutor(
            _monitor, agentService, commandRegistry, agentServerProvider, broadcaster, goalExecutor, directorTools, movementService, agentTickLoop);
        _container.RegisterSingleton(commandExecutor);

        // 2026-08-15 账本迁移（步骤 1）：adjust 原子批执行器——TS 账本权威，C# 只做物理校验 + 原语执行。
        // 与 CommandExecutor 完全独立：仅由 execute_adjust 消息驱动（EventHandlerInitializer 路由），旧路径不动。
        var adjustExecutor = new AdjustExecutor(_monitor, agentService, agentServerProvider);
        _container.RegisterSingleton(adjustExecutor);

        // E1-1: 命令执行结果回发时同步入队留痕（OnSendActionResult 签名：npcName, action, success, extra）
        commandExecutor.OnSendActionResult += (npcName, action, success, extra) =>
        {
            transcriptSinkEarly.RecordActionResult(npcName, action, success, extra);
            return Task.CompletedTask;
        };

        var friendshipSystem = new FriendshipSystem(config.MaxFriendshipChangePerInteraction);
        _container.RegisterSingleton(friendshipSystem);
        NPCGiftPatch.FriendshipSystem = friendshipSystem;

        var setFriendshipCmd = commandRegistry.GetCommand("set_friendship") as SetFriendshipCommand;
        if (setFriendshipCmd != null)
        {
            setFriendshipCmd.FriendshipSystem = friendshipSystem;
        }

        var consoleCommands = new ConsoleCommands(allocationManager, circuitBreaker, debugLogger, config);
        _container.RegisterSingleton(consoleCommands);

        _monitor.Log("ValleyAgent services initialized successfully", LogLevel.Info);
    }
}