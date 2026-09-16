using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using ValleyAgent.Agents;
using ValleyAgent.AI;
using ValleyAgent.Api;
using ValleyAgent.Brain;
using ValleyAgent.Chat;
using ValleyAgent.Combat;
using ValleyAgent.Config;
using ValleyAgent.Core;
using ValleyAgent.Debug;
using ValleyAgent.Dialogue;
using ValleyAgent.Economy;
using ValleyAgent.Friendship;
using ValleyAgent.Handlers;
using ValleyAgent.Health;
using ValleyAgent.i18n;
using ValleyAgent.Infrastructure;
using ValleyAgent.Multiplayer;
using ValleyAgent.Navigation;
using ValleyAgent.Patches;
using ValleyAgent.Protocol;
using ValleyAgent.RAG;
using ValleyAgent.Rendering;
using ValleyAgent.Save;
using ValleyAgent.Save.Models;
using ValleyAgent.Services;
using ValleyAgent.Services.Schedule;
using ValleyAgent.StateMachine;
using ValleyAgent.Testing;
using ValleyAgent.Tracking;
using ValleyAgent.UI;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;
using AllocateAgentHandler = ValleyAgent.Protocol.AllocateAgentHandler;
using FriendshipChangeRecord = ValleyAgent.Friendship.FriendshipChangeRecord;
using LogLevel = StardewModdingAPI.LogLevel;
using Object = StardewValley.Object;

namespace ValleyAgent.Initialization;

public class EventHandlerInitializer
{
    // P0-4: 决策最大有效期（秒），超过则视为过期并丢弃
    private const double MaxDecisionAgeSeconds = 30.0;

    /// <summary>
    ///     B5 空闲回收的 manual override 空闲阈值（设计 §3.4，本 PR 不做配置）。
    ///     必须明显大于单轮对话间隔：房客中继对话主机无感知关闭时机
    ///     （EndTopicConversation 不触发），靠"每轮中继对话 UpdatePriority 刷新 LastUpdated
    ///     + 空闲超时释放"兜底——阈值太小会把仍被房客活跃对话的身体误释放。
    /// </summary>
    private static readonly TimeSpan IdleOverrideThreshold = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     主线程命令执行队列：后台线程收到 TS Agent Server 命令后入队，
    ///     OnUpdateTicked（主线程）出队执行，避免非主线程访问 Game1 状态。
    /// </summary>
    private static readonly ConcurrentQueue<(string npcName, List<ProtocolV2.CommandAction> commands)>
        _pendingMainThreadCommands = new();

    /// <summary>
    ///     T17/T18: pre_speak 主动说话主线程执行队列。
    ///     后台决策线程收到 LLM 返回的 pre_speak 后入队，
    ///     OnUpdateTicked（主线程）出队调用 ActiveSpeechRouter.Route，避免跨线程访问 Game1。
    /// </summary>
    private static readonly ConcurrentQueue<(string npcName, string text)> _pendingPreSpeakActions = new();

    /// <summary>
    ///     WS 后台线程收到的"改游戏状态"指令队列（execute_adjust/director_command/allocate_agent）。
    ///     入队发生在 WebSocket 读循环线程，OnUpdateTicked（主线程）出队执行——
    ///     避免非主线程访问 Game1 状态（Farmer.Money 是 NetIntDelta，跨线程写会污染联机同步）。
    ///     EnqueuedAt（TickCount64）用于出队时检测队列滞留：主线程泵停滞的细粒度信号
    ///     （2026-09-11 生产化仪器，看门狗 5s 阈值以下的卡顿靠它留证）。
    /// </summary>
    private static readonly ConcurrentQueue<(string Type, string Json, long EnqueuedAt)> _pendingWsCommands = new();

    // 决策批序号：MakeDecisionsAsync 可多线程并发，StuckOperationTracker 的 opId 必须每批唯一
    //（固定 id 会被并发批互相覆盖——见 MakeDecisionsAsync 内注释）
    private static long _decisionBatchSequence;

    private readonly HashSet<string> _dialogueFollowedAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly IModHelper _helper;
    private readonly Dictionary<string, int> _lastFightExitTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastPlayerInDangerEmotionTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastTaskCompleteDecisionTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly IManifest _manifest;

    private readonly IMonitor _monitor;

    // 联机事件路由器：Host/Thin 模式互斥注册。本轮仅声明字段，
    // 实例化与 Register() 由 Task 9 ModEntry 三模式分支完成，避免单机模式无意义订阅。
    private readonly MultiplayerEventRouter? _multiplayerRouter = null;

    // P0-4: 决策队列增加时间戳，用于过期清理；增加优先级支持
    private readonly
        Queue<(AgentInstance agent, AgentState targetState, string reason, string thought, DateTime queuedAt)>
        _pendingDecisions = new();

    private readonly object _pendingDecisionsLock = new();

    /// <summary>
    ///     E5-3 时间触发主动发言决策器（每 10 游戏分钟 OnTimeChanged 驱动）。
    ///     在窗口触发点从已醒 + 额度通过的候选 NPC 生成发言并入队。
    /// </summary>
    private readonly ProactiveSpeechTrigger _proactiveSpeechTrigger = new();

    private readonly IServiceContainer _services;

    private readonly Dictionary<string, (int tick, int count)> _stateRejectionCounts =
        new(StringComparer.OrdinalIgnoreCase);

    private AgentHUD? _agentHud;
    private AgentNavigator? _agentNavigator;
    private AgentRenderer? _agentRenderer;
    private DualPathAgentServerProvider? _agentServerProvider;

    private AgentService? _agentService;

    private AgentTickLoop? _agentTickLoop;

    // AllocateAgentHandler：处理 TS 导演下发的 allocate_agent 消息（设计文档 §4.2.2）
    private AllocateAgentHandler? _allocateAgentHandler;
    private ValleyTalkBioLoader? _bioLoader;
    private CommandExecutor? _commandExecutor;
    /// <summary>2026-08-15 账本迁移（步骤 1）：execute_adjust 原子批执行器（TS 账本权威 → C# 物理校验执行）。</summary>
    private AdjustExecutor? _adjustExecutor;
    private ModConfig? _config;
    private int _currentSpeedMultiplier = 1;
    private bool _debugHudVisible;
    private DebugLogger? _debugLogger;
    private DecisionContextBuilder? _decisionContextBuilder;
    private int _decisionIntervalTicks = 1800;
    private float _extraTimeAccumulator;
    private FriendshipSystem? _friendshipSystem;
    private SmaHUDRenderer? _hudRenderer;
    private string? _lastDialogueNpcName;

    /// <summary>上次触发过的窗口（防止同一窗口重复触发刷屏）。</summary>
    private int _lastProactiveTriggerWindow = -1;

    private LocationGraph? _locationGraph;
    private MonsterAggroManager? _monsterAggroManager;

    private IMovementService? _movementService;

    // 联机 Agent 状态广播器：主机端 60 tick 节流广播 Agent 快照给 Farmhand。
    // 内部自带 ShouldRunAgentLogic && IsMultiplayer 守卫，单机模式下 Update 直接 no-op。
    private AgentSyncBroadcaster? _multiplayerBroadcaster;
    private List<AgentInstance>? _pendingDialogueEndDecisions;

    private List<AgentInstance>? _pendingGiftEventDecisions;

    // E3-3: 待成交单注册表——玩家跨图/换日时作废未结算的待成交单

    private List<AgentInstance>? _pendingTaskCompleteDecisions;

    // B0/E5-3: 主动发言额度服务——日切时调 ResetDaily 重置每日上限与冷却。
    private ProactiveSpeechQuota? _proactiveSpeechQuota;
    private NpcPurchaseRequestService? _purchaseRequestService;
    private SaveDataManager? _saveDataManager;

    private ServerProcessManager? _serverProcessManager;

    // Spark 分配器：玩家附近 NPC 低概率激活（设计文档 §4.2.3）
    private SparkAllocator? _sparkAllocator;

    private int _tickCounter;
    /// <summary>阶段 3 (3.6): 玩家行为采样跟踪器（10-tick 采样，day_started 聚合出百分比 + 趋势）。</summary>
    private PlayerActionTracker? _playerActionTracker;
    /// <summary>阶段 3 (3.7): Director 上下文拼装器（day_started 压缩 800-1500 token 文本随 day_started 发 TS）。</summary>
    private DirectorContextBuilder? _directorContextBuilder;

    // E1-1: JSONL 留痕 Sink——Drain 在主线程 UpdateTicked 调用
    private TranscriptSink? _transcriptSink;

    public EventHandlerInitializer(IModHelper helper, IMonitor monitor, IServiceContainer services, IManifest manifest)
    {
        _helper = helper;
        _monitor = monitor;
        _services = services;
        _manifest = manifest;
    }

    /// <summary>
    ///     E5-3 主动发言入队（公开静态，供时间触发与后续 TS pre_speak 复用）。
    ///     仅入队，渲染由主线程 ProcessPendingPreSpeakActions 消费。
    /// </summary>
    public static void EnqueuePreSpeak(string npcName, string text)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _pendingPreSpeakActions.Enqueue((npcName, text));
        // 深度告警：静态入队点无 monitor 实例可触达，只落 ModErrorLog（QueueTelemetry 的既定降级路径）
        QueueTelemetry.WarnIfDeep("pre-speak", _pendingPreSpeakActions.Count, null);
    }

    public void Initialize()
    {
        _agentService = _services.GetService<AgentService>();
        _config = _services.GetService<ModConfig>();
        _bioLoader = _services.GetService<ValleyTalkBioLoader>();
        _locationGraph = _services.GetService<LocationGraph>();
        _agentNavigator = _services.GetService<AgentNavigator>();
        _movementService = _services.GetService<IMovementService>();
        _agentServerProvider = _services.GetService<DualPathAgentServerProvider>();
        _serverProcessManager = _services.GetService<ServerProcessManager>();
        _agentTickLoop = _services.GetService<AgentTickLoop>();
        _sparkAllocator = _services.GetService<SparkAllocator>();
        _allocateAgentHandler = _services.GetService<AllocateAgentHandler>();
        if (_agentTickLoop != null)
        {
            _agentTickLoop.OnVanillaReleaseFinalized += OnVanillaReleaseFinalized;
        }

        _agentRenderer = _services.GetService<AgentRenderer>();
        _agentHud = _services.GetService<AgentHUD>();
        _hudRenderer = _services.GetService<SmaHUDRenderer>();
        _decisionContextBuilder = _services.GetService<DecisionContextBuilder>();
        _monsterAggroManager = _services.GetService<MonsterAggroManager>();
        _saveDataManager = _services.GetService<SaveDataManager>();
        _commandExecutor = _services.GetService<CommandExecutor>();
        _adjustExecutor = _services.GetService<AdjustExecutor>();
        _debugLogger = _services.GetService<DebugLogger>();
        _friendshipSystem = _services.GetService<FriendshipSystem>();
        _proactiveSpeechQuota = _services.GetService<ProactiveSpeechQuota>();
        ChatBarRouter.ProactiveQuota = _proactiveSpeechQuota; // E5-3: 话痨度引用配额
        _multiplayerBroadcaster = _services.GetService<AgentSyncBroadcaster>();
        _transcriptSink = _services.GetService<TranscriptSink>();
        // E3-5: NPC 求购服务（长 TTL 求购单）。NPCGiftPatch 注入其自持 Registry 供求购命中判定
        // （2026-08-15 步骤 2：结算已迁 TS，C# 只做命中提示）。
        _purchaseRequestService = _services.GetService<NpcPurchaseRequestService>();
        NPCGiftPatch.PurchaseOffers = _purchaseRequestService?.PurchaseOffers;
        // 阶段 3 (3.6): 玩家行为采样跟踪器（10-tick 采样，day_started 聚合）
        _playerActionTracker = _services.GetService<PlayerActionTracker>();
        // 阶段 3 (3.7): Director 上下文拼装器（day_started 压缩 800-1500 token 文本随 day_started 发 TS）
        _directorContextBuilder = _services.GetService<DirectorContextBuilder>();

        if (_config != null)
        {
            _decisionIntervalTicks = (int)(_config.DecisionIntervalMinutes * 60 * 60);
            // 装配主动发言概率到触发器（默认 1.0 = 全量通过）
            _proactiveSpeechTrigger.SpeechProbability = _config.ProactiveSpeechProbability;
        }

        if (_agentService != null)
        {
            _agentService.OnDecisionTriggerRequested += agent =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MakeDecisionsAsync(new List<AgentInstance> { agent }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _monitor.Log($"[DecisionTrigger] Force decision failed for {agent.NpcName}: {ex.Message}",
                            LogLevel.Error);
                    }
                });
            };

            // B5.4 常驻拆除接线（设计 §3.4 步骤 4）：池表任何一条路径失去槽位
            // （TryAllocate 挤出 / 换日裁剪 / 空闲淘汰 / promote 挤人）都做完整身体拆除，
            // 此前只有 PromoteToAgent 的临时捕获做拆除，其余路径"只释放账面不拆身体"是潜伏缺口。
            // 反订阅在 UnsubscribeEvents 对称注销。
            _agentService.AllocationManager.OnAgentDeallocated += OnAgentDeallocatedTeardown;
        }

        // GMCM 注册已移到 ModEntry.OnGameLaunchedEarly：
        // GMCM 必须在 GameLaunched 事件中注册，玩家在标题屏才能看到"配置"按钮。
        // 之前放在这里（SaveLoaded 触发）会导致标题屏 ValleyAgent 设置项完全消失。
        // OnConfigChanged 也已由 ModEntry.OnGameLaunchedEarly 订阅，避免重复。
        _monitor.Log($"ValleyAgent v{_manifest.Version} loaded", LogLevel.Info);
    }

    public void RegisterEventHandlers()
    {
        // GMCM 注册已移到 ModEntry.OnGameLaunchedEarly（GameLaunched 事件），
        // 这里不再重复注册（重复注册会导致 GMCM 配置页 UI 项叠加）。
        _helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        _helper.Events.GameLoop.Saving += OnSaving;
        _helper.Events.GameLoop.DayStarted += OnDayStarted;
        _helper.Events.GameLoop.DayEnding += OnDayEnding;
        _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        _helper.Events.Player.Warped += OnPlayerWarped;
        _helper.Events.Input.ButtonPressed += OnButtonPressed;
        _helper.Events.Display.RenderedWorld += OnRenderedWorld;
        _helper.Events.Display.RenderedHud += OnRenderedHud;
        _helper.Events.Display.MenuChanged += OnMenuChanged;
    }

    /// <summary>
    ///     P1-8: 取消所有 SMAPI 事件订阅，防止 Mod 卸载后事件回调仍被触发。
    ///     在 ModEntry.Dispose 中调用。
    /// </summary>
    public void UnsubscribeEvents()
    {
        _helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        _helper.Events.GameLoop.Saving -= OnSaving;
        _helper.Events.GameLoop.DayStarted -= OnDayStarted;
        _helper.Events.GameLoop.DayEnding -= OnDayEnding;
        _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _helper.Events.GameLoop.TimeChanged -= OnTimeChanged;
        _helper.Events.Player.Warped -= OnPlayerWarped;
        _helper.Events.Input.ButtonPressed -= OnButtonPressed;
        _helper.Events.Display.RenderedWorld -= OnRenderedWorld;
        _helper.Events.Display.RenderedHud -= OnRenderedHud;
        _helper.Events.Display.MenuChanged -= OnMenuChanged;

        if (_agentTickLoop != null)
        {
            _agentTickLoop.OnVanillaReleaseFinalized -= OnVanillaReleaseFinalized;
        }

        if (_agentService != null)
        {
            _agentService.AllocationManager.OnAgentDeallocated -= OnAgentDeallocatedTeardown;
        }

        // 防御性反注册：本轮 Router 未注册则 no-op，Task 9 实例化后生效
        _multiplayerRouter?.Unregister();
    }

    public void RegisterHarmonyPatches(IManifest _)
    {
        try
        {
            var harmony = new Harmony(GameConstants.HarmonyId);
            var translation = _services.GetService<ITranslationProvider>();

            NPCDialoguePatch.AgentService = _agentService;
            NPCDialoguePatch.AgentServerProvider = _agentServerProvider;
            NPCDialoguePatch.Monitor = _monitor;
            NPCDialoguePatch.Translation = translation;
            NPCDialoguePatch.Config = _config;
            // Spark 激活回调：玩家与非 Agent 村民对话时低概率激活为 Agent（设计文档 §4.2.3）
            NPCDialoguePatch.OnSparkCandidate = TrySparkActivate;

            DialogueBoxInputPatch.Initialize(_monitor, _agentService, _agentServerProvider, _commandExecutor,
                _bioLoader, WireAgentStateEvents);

            // E2-2: 聊天栏玩家→NPC 路由（ChatBox 输入拦截 + 四层消歧 + 会话模式）
            ChatBoxInputPatch.Initialize(_monitor);
            ChatBarRouter.Initialize(
                _monitor,
                _agentService,
                _agentServerProvider,
                _commandExecutor,
                _config,
                _proactiveSpeechQuota,
                _services.GetService<NpcScheduleService>());

            NPCGiftPatch.AgentService = _agentService;
            NPCGiftPatch.AgentServerProvider = _agentServerProvider;
            NPCGiftPatch.Monitor = _monitor;
            SocialPagePatch.AgentService = _agentService;
            SocialPagePatch.Monitor = _monitor;

            harmony.PatchAll();
            _monitor.Log("Harmony patches applied successfully", LogLevel.Debug);
        }
        catch (TargetInvocationException ex)
        {
            _monitor.Log($"Failed to apply Harmony patches: {ex.Message}", LogLevel.Error);
        }
    }

    public void RegisterConsoleCommands()
    {
        var consoleCommands = _services.GetService<ConsoleCommands>();
        if (consoleCommands == null)
        {
            return;
        }

        foreach (var cmd in consoleCommands.CommandMap)
        {
            _ = _helper.ConsoleCommands.Add(cmd.Key, consoleCommands.GetHelpText(cmd.Key), (cmdName, args) =>
            {
                var result = consoleCommands.Execute(cmdName, args);
                if (result.Success)
                {
                    _monitor.Log(result.Message, LogLevel.Info);
                }
                else
                {
                    _monitor.Log(result.Message, LogLevel.Warn);
                }
            });
        }

        // Create command handlers and register callbacks
        var commandHandlers = new ConsoleCommandHandlers(
            _monitor,
            _agentService,
            _config,
            _decisionContextBuilder,
            _agentServerProvider,
            _debugLogger,
            _bioLoader,
            _helper,
            EnqueueDecision);

        commandHandlers.RegisterCallbacks(consoleCommands);
    }

    /// <summary>
    ///     Enqueues a decision for main-thread processing.
    ///     Used by ConsoleCommandHandlers to submit forced decisions.
    /// </summary>
    private void EnqueueDecision(AgentInstance agent, AgentState targetState, string reason, string thought)
    {
        lock (_pendingDecisionsLock)
        {
            _pendingDecisions.Enqueue((agent, targetState, reason, thought, DateTime.UtcNow));
        }
    }

    /// <summary>
    ///     P0-4: 清除指定 NPC 的所有待处理决策。
    ///     用于玩家关闭对话菜单、紧急逃生等场景，避免旧决策覆盖恢复后的状态。
    /// </summary>
    private void ClearPendingDecisions(string npcName)
    {
        lock (_pendingDecisionsLock)
        {
            if (_pendingDecisions.Count == 0)
            {
                return;
            }

            // 重建队列，只保留其他 NPC 的决策
            var remaining =
                new Queue<(AgentInstance agent, AgentState targetState, string reason, string thought, DateTime queuedAt
                    )>();
            var removed = 0;
            while (_pendingDecisions.Count > 0)
            {
                var item = _pendingDecisions.Dequeue();
                if (!string.Equals(item.agent?.NpcName, npcName, StringComparison.OrdinalIgnoreCase))
                {
                    remaining.Enqueue(item);
                }
                else
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                _monitor.Log($"Cleared {removed} pending decision(s) for {npcName}");
            }

            // 把保留的决策放回原队列
            while (remaining.Count > 0)
            {
                _pendingDecisions.Enqueue(remaining.Dequeue());
            }
        }
    }

    /// <summary>
    ///     P1-10: 返回标题画面时清理所有运行时状态。
    ///     防止旧存档的 Agent/对话/决策残留污染新存档加载。
    ///     必须在主线程调用（OnReturnedToTitle 事件本身在主线程触发）。
    /// </summary>
    public void CleanupForTitleReturn()
    {
        _monitor.Log("Cleaning up ValleyAgent state for title return...", LogLevel.Debug);

        // 1. 清理对话状态
        _monitor.Log("Cleaning up dialogue state...", LogLevel.Debug);

        // 2. 清空所有决策队列与跟踪字典
        lock (_pendingDecisionsLock)
        {
            _pendingDecisions.Clear();
        }

        _pendingDialogueEndDecisions = null;
        _pendingTaskCompleteDecisions = null;
        _pendingGiftEventDecisions = null;
        _lastDialogueNpcName = null;
        _dialogueFollowedAgents.Clear();
        _stateRejectionCounts.Clear();
        _lastPlayerInDangerEmotionTick.Clear();
        _lastFightExitTick.Clear();
        _lastTaskCompleteDecisionTick.Clear();

        // 3. 清空主线程命令队列
        while (_pendingMainThreadCommands.TryDequeue(out _))
        {
        }

        // T17/T18: 清空 pre_speak 主动说话队列
        while (_pendingPreSpeakActions.TryDequeue(out _))
        {
        }

        // 2026-08-16 联机审计 P0：清空 WS 改状态指令队列（标题返回时不再执行残留指令）
        while (_pendingWsCommands.TryDequeue(out _))
        {
        }

        // E2-2: 清空聊天栏路由状态（交互时间戳 / 在途请求 / 回复队列 / 会话注册表）
        try
        {
            ChatBarRouter.Reset();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear ChatBarRouter state: {ex.Message}", LogLevel.Warn);
        }

        // 4. 清理对话补丁状态（包含 _activeConversations、_preDialogueStates 等）
        try
        {
            NPCDialoguePatch.ClearAllConversations();
            // 对话中途返回标题时恢复键盘 subscriber 与 chatButton 绑定，防输入劫持泄漏
            DialogueBoxInputPatch.ClearActiveAgentNpc();
            NPCGiftPatch.ClearDayStart();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear dialogue patch state: {ex.Message}", LogLevel.Warn);
        }

        // 5. 清理 ConversationStateManager（对话历史 + 待处理响应 + 放弃标记）
        try
        {
            _services.GetService<ConversationStateManager>()?.ClearAll();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear ConversationStateManager: {ex.Message}", LogLevel.Warn);
        }

        // 6. 清理 DialogueStateManager（对话冷却）
        try
        {
            _services.GetService<DialogueStateManager>()?.ClearAllDialogueState();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear DialogueStateManager: {ex.Message}", LogLevel.Warn);
        }

        // 7. 清理 AgentService 中所有 Agent 实例（含状态机 Reset）
        try
        {
            _agentService?.ClearAllAgents();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear AgentService: {ex.Message}", LogLevel.Warn);
        }

        // 8. 清理 AllocationManager 分配记录
        try
        {
            _agentService?.AllocationManager?.ClearAllAllocations();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear AllocationManager: {ex.Message}", LogLevel.Warn);
        }

        // 9. 清理 AgentTickLoop 释放状态
        try
        {
            _agentTickLoop?.ClearAllReleaseState();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear AgentTickLoop: {ex.Message}", LogLevel.Warn);
        }

        // 10. 清理 FriendshipSystem 历史
        try
        {
            _friendshipSystem?.ClearHistory();
            _friendshipSystem?.ClearDailyTracker();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to clear FriendshipSystem: {ex.Message}", LogLevel.Warn);
        }

        // 11. 重置 CircuitBreaker，新存档从 CLOSED 状态开始
        try
        {
            _agentService?.CircuitBreaker.Reset();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Cleanup] Failed to reset CircuitBreaker: {ex.Message}", LogLevel.Warn);
        }

        // 12. 重置 tick 计数器和加速累加器
        _tickCounter = 0;
        _extraTimeAccumulator = 0f;

        _monitor.Log("ValleyAgent state cleanup completed.", LogLevel.Debug);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e) => OnSaveLoadedCore();

    /// <summary>
    ///     公开入口：由 ModEntry.OnSaveLoadedInitialize 在 InitializeHostMode 完成后立即调用。
    ///     原因：InitializeHostMode 在 SaveLoaded 事件触发期间才订阅 OnSaveLoaded，
    ///     而 SMAPI 事件系统不会在当前触发周期执行新订阅的 handler，要等下次 SaveLoaded。
    ///     这会导致主机第一次进入存档时 Agent 存档数据不加载、WebSocket 不连接。
    ///     此方法让 ModEntry 在初始化完成后同步触发一次 OnSaveLoaded 逻辑。
    /// </summary>
    public void TriggerSaveLoadedInitialization() => OnSaveLoadedCore();

    private void OnSaveLoadedCore()
    {
        if (_agentService == null || _config == null)
        {
            return;
        }

        _monitor.Log("Loading ValleyAgent agent state...", LogLevel.Debug);

        try
        {
#pragma warning disable CS0618
            var saveData = _helper.Data.ReadSaveData<AgentSaveData>(GameConstants.SaveDataKey);
#pragma warning restore CS0618
            if (saveData != null)
            {
                if (saveData.Agents != null)
                {
                    foreach (var agentData in saveData.Agents)
                    {
                        if (string.IsNullOrWhiteSpace(agentData.NpcName))
                        {
                            continue;
                        }

                        // 只恢复手动分配的 Agent；自动分配的 Agent 读档后不恢复
                        // （设计文档 §4.3.1：NPC 由 spark/导演/玩家交互激活）
                        if (!agentData.IsManuallyOverridden)
                        {
                            _monitor.Log(
                                $"Skipping non-manual agent {agentData.NpcName} on load (not manually allocated)",
                                LogLevel.Debug);
                            continue;
                        }

                        _ = _agentService!.AllocationManager.ForceAllocate(agentData.NpcName);

                        var agent = _agentService.CreateAgent(agentData.NpcName);
                        if (agent != null)
                        {
                            _bioLoader?.InjectBio(agent.Brain);
                            WireAgentStateEvents(agent);
                            if (Enum.TryParse<AgentState>(agentData.CurrentState, true, out var restoredState))
                            {
                                agent.StateMachine.ForceTransition(restoredState);
                            }

                            if (agentData.Health >= 0)
                            {
                                // P1-14: 存档恢复时抑制 OnDeath 回调，避免游戏世界未完全加载时触发
                                agent.Health.SetHealth(agentData.Health, true);
                            }

                            if (agentData.Inventory != null)
                            {
                                agent.Inventory.Clear();
                                foreach (var entry in agentData.Inventory)
                                {
                                    var parts = entry.Split(':');
                                    if (parts.Length >= 2 && int.TryParse(parts[1], out var stack))
                                    {
                                        var item = ItemRegistry.Create<Object>(parts[0]);
                                        item.Stack = stack;
                                        _ = agent.Inventory.TryAdd(item);
                                    }
                                }
                            }

                            // E3-1: 钱包余额恢复。-1 哨兵 = 旧档无钱包记录，不覆盖档案初始资金（静默路径）
                            if (agentData.Money >= 0)
                            {
                                agent.Inventory.Money = agentData.Money;
                            }

                            if (!string.IsNullOrEmpty(agentData.Emotion)
                                && Enum.TryParse<NpcEmotion>(agentData.Emotion, out var restoredEmotion))
                            {
                                agent.Brain.SyncEmotion(restoredEmotion, 1.0f, "Restored from save");
                            }

                            if (!string.IsNullOrEmpty(agentData.FarmerNickname))
                            {
                                agent.Brain.FarmerNickname = agentData.FarmerNickname;
                            }

                            if (agentData.StructuredMemories != null && agentData.StructuredMemories.Count > 0)
                            {
                                foreach (var memData in agentData.StructuredMemories)
                                {
                                    agent.Brain.ShortTermMemories.Add(MemoryEntry.FromSaveData(memData));
                                }
                            }
                            else if (agentData.ShortTermMemories != null && agentData.ShortTermMemories.Count > 0)
                            {
                                foreach (var mem in agentData.ShortTermMemories)
                                {
                                    agent.Brain.AddMemory(mem);
                                }
                            }

                            if (agentData.LongTermMemories != null && agentData.LongTermMemories.Count > 0)
                            {
                                foreach (var memData in agentData.LongTermMemories)
                                {
                                    agent.Brain.LongTermMemories.Add(MemoryEntry.FromSaveData(memData));
                                }
                            }

                            if (agentData.EmotionHistory != null && agentData.EmotionHistory.Count > 0)
                            {
                                foreach (var entry in agentData.EmotionHistory)
                                {
                                    agent.Brain.EmotionHistory.Enqueue(entry);
                                }
                            }

                            if (!string.IsNullOrEmpty(agentData.LastDecisionState))
                            {
                                agent.Brain.LastDecisionState = agentData.LastDecisionState;
                            }

                            if (!string.IsNullOrEmpty(agentData.LastDecisionReason))
                            {
                                agent.Brain.LastDecisionReason = agentData.LastDecisionReason;
                            }
                        }
                    }

                    _monitor.Log($"Restored {saveData.Agents.Count} agents from save", LogLevel.Info);
                }
            }
            else
            {
                _monitor.Log("No existing ValleyAgent save data found", LogLevel.Debug);
            }

            try
            {
                var structuredData = _helper.Data.ReadSaveData<SaveData>(GameConstants.StructuredSaveDataKey);
                if (structuredData != null && _saveDataManager != null)
                {
                    var validated = _saveDataManager.Deserialize(_saveDataManager.Serialize(structuredData));
                    _monitor.Log(
                        $"Structured save data loaded: v{validated.Version}, {validated.AgentStates.Count} agents, {validated.Memories.Count} memory entries",
                        LogLevel.Debug);

                    foreach (var kvp in validated.AgentStates)
                    {
                        var stateData = kvp.Value;
                        if (string.IsNullOrWhiteSpace(stateData.NpcName))
                        {
                            continue;
                        }

                        if (!_agentService!.TryGetAgent(stateData.NpcName, out var agent) || agent == null)
                        {
                            continue;
                        }

                        var npc = Game1.getCharacterFromName(stateData.NpcName);
                        if (npc != null && (stateData.PositionX != 0 || stateData.PositionY != 0))
                        {
                            npc.setTileLocation(new Vector2(stateData.PositionX, stateData.PositionY));
                        }

                        if (stateData.EmotionData != null
                            && Enum.TryParse<NpcEmotion>(stateData.EmotionData.Emotion, out var restoredEmotion))
                        {
                            agent.Brain.SyncEmotion(restoredEmotion, stateData.EmotionData.Intensity,
                                stateData.EmotionData.Source);
                        }

                        if (!string.IsNullOrEmpty(stateData.CurrentGoal))
                        {
                            agent.Brain.CurrentGoal = stateData.CurrentGoal;
                        }

                        // E3-1: 结构化存档钱包余额恢复（后写覆盖 legacy 轨）。-1 哨兵 = 未设置，不覆盖档案初始资金
                        if (stateData.Money >= 0)
                        {
                            agent.Inventory.Money = stateData.Money;
                        }
                    }

                    foreach (var kvp in validated.Memories)
                    {
                        var memData = kvp.Value;
                        if (string.IsNullOrWhiteSpace(memData.NpcName))
                        {
                            continue;
                        }

                        if (!_agentService!.TryGetAgent(memData.NpcName, out var agent) || agent == null)
                        {
                            continue;
                        }

                        // P1-11: 结构化存档是权威源，加载前清空 legacy 存档已追加的记忆，避免重复
                        agent.Brain.ShortTermMemories.Clear();
                        agent.Brain.LongTermMemories.Clear();

                        if (memData.ShortTermMemories != null && memData.ShortTermMemories.Count > 0)
                        {
                            foreach (var sme in memData.ShortTermMemories)
                            {
                                var entry = new MemoryEntry
                                {
                                    Text = sme.Text,
                                    Importance = sme.Importance,
                                    Timestamp = sme.Timestamp != 0
                                        ? DateTime.FromBinary(sme.Timestamp)
                                        : DateTime.UtcNow
                                };
                                if (Enum.TryParse<MemoryEntryType>(sme.EntryType, true, out var type))
                                {
                                    entry.EntryType = type;
                                }

                                agent.Brain.ShortTermMemories.Add(entry);
                            }
                        }

                        if (memData.LongTermMemories != null && memData.LongTermMemories.Count > 0)
                        {
                            foreach (var sme in memData.LongTermMemories)
                            {
                                var entry = new MemoryEntry
                                {
                                    Text = sme.Text,
                                    Importance = sme.Importance,
                                    Timestamp = sme.Timestamp != 0
                                        ? DateTime.FromBinary(sme.Timestamp)
                                        : DateTime.UtcNow
                                };
                                if (Enum.TryParse<MemoryEntryType>(sme.EntryType, true, out var type))
                                {
                                    entry.EntryType = type;
                                }

                                agent.Brain.LongTermMemories.Add(entry);
                            }
                        }
                    }

                    if (_friendshipSystem != null && validated.FriendshipHistory != null)
                    {
                        var restoredHistory = new Dictionary<string, List<FriendshipChangeRecord>>();
                        foreach (var kvp in validated.FriendshipHistory)
                        {
                            var records = new List<FriendshipChangeRecord>();
                            if (kvp.Value?.Changes != null)
                            {
                                foreach (var change in kvp.Value.Changes)
                                {
                                    var record = new FriendshipChangeRecord
                                    {
                                        NpcName = kvp.Value.NpcName,
                                        ChangeAmount = change.ChangeAmount,
                                        Reason = change.Reason,
                                        DateKey = change.DateKey,
                                        Timestamp = change.Timestamp
                                    };
                                    if (Enum.TryParse<InteractionType>(change.InteractionType, true, out var it))
                                    {
                                        record.InteractionType = it;
                                    }

                                    records.Add(record);
                                }
                            }

                            restoredHistory[kvp.Key] = records;
                        }

                        _friendshipSystem.RestoreHistory(restoredHistory);
                    }
                }
            }
#pragma warning disable CA1031
            catch (Exception sEx)
            {
                _monitor.Log($"Structured save data load skipped: {sEx.Message}");
            }
#pragma warning restore CA1031

            _locationGraph?.Build();
            _monitor.Log($"Location graph built: {_locationGraph?.KnownLocations.Count ?? 0} maps connected",
                LogLevel.Debug);

            _agentService?.CircuitBreaker.Reset();
            _monitor.Log("CircuitBreaker reset on save load.", LogLevel.Debug);

            // 阶段 3（3.1.1 默认创建）：遍历全部村民确保 AgentBrain+Inventory 存在。
            // 只建休眠 Brain，不 ForceAllocate——活跃分配仍由 spark/对话/Director 驱动，尊重 MaxAgentNpcs。
            EnsureAllNpcBrainsExist();
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"Failed to load ValleyAgent save data: {ex.Message}", LogLevel.Error);
        }

        // WebSocket 连接独立于存档加载，即使存档加载失败也要尝试连接
        if (_agentServerProvider != null)
        {
            // 订阅 unsolicited 消息（TS 主动下发的 allocate_agent 等）。
            // 在 WebSocketClient 上订阅一次即可，断连重连后事件源仍指向同一实例。
            var wsClient = _agentServerProvider.WebSocketClient;
            wsClient.OnUnsolicitedMessage -= OnUnsolicitedMessage;
            wsClient.OnUnsolicitedMessage += OnUnsolicitedMessage;

            // 2026-08-15 步骤 4：断线重连成功 → 发 reconnect_sync（outbox 补发 + 全量状态），
            // TS 据此对账 in-flight adjust pending（凭 instructionId 重发，C# 幂等返回缓存）。
            wsClient.OnReconnected -= OnReconnected;
            wsClient.OnReconnected += OnReconnected;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ConnectAgentServerAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    _monitor.Log($"Agent Server connection failed: {ex.Message}", LogLevel.Warn);
                }
            });
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (_agentService == null)
        {
            return;
        }

        try
        {
#pragma warning disable CS0618
            var saveData = new AgentSaveData();

            foreach (var agent in _agentService.GetAllAgents())
            {
                saveData.Agents.Add(new AgentData
                {
                    NpcName = agent.NpcName,
                    CurrentState = agent.StateMachine.CurrentStateFlag.ToString(),
                    IsManuallyOverridden = _agentService!.AllocationManager.IsManuallyOverridden(agent.NpcName),
                    Health = agent.Health.Health,
                    Inventory = agent.Inventory.GetAllItems()
                        .Where(i => i != null)
                        .Select(i => $"{i!.QualifiedItemId ?? i.ItemId}:{i.Stack}")
                        .ToList(),
                    Money = agent.Inventory.Money,
                    Emotion = agent.Brain.Emotion.ToString(),
                    FarmerNickname = agent.Brain.FarmerNickname,
                    ShortTermMemories = agent.Brain.ShortTermMemories.Select(m => m.Text).ToList(),
                    StructuredMemories = agent.Brain.ShortTermMemories.Select(m => m.ToSaveData()).ToList(),
                    LongTermMemories = agent.Brain.LongTermMemories.Select(m => m.ToSaveData()).ToList(),
                    EmotionHistory = agent.Brain.EmotionHistory.ToList(),
                    LastDecisionState = agent.Brain.LastDecisionState,
                    LastDecisionReason = agent.Brain.LastDecisionReason
                });
            }

            _helper.Data.WriteSaveData(GameConstants.SaveDataKey, saveData);
#pragma warning restore CS0618

            var structuredSaveData = BuildStructuredSaveData();
            if (_saveDataManager != null)
            {
                if (_saveDataManager.Validate(structuredSaveData))
                {
                    _helper.Data.WriteSaveData(GameConstants.StructuredSaveDataKey, structuredSaveData);
                    _monitor.Log(
                        $"Saved {structuredSaveData.AgentStates.Count} agents via SaveDataManager (v{SaveDataManager.CurrentVersion})",
                        LogLevel.Debug);
                }
                else
                {
                    _monitor.Log("SaveDataManager validation failed — structured save skipped", LogLevel.Warn);
                }
            }

            _monitor.Log($"Saved {saveData.Agents.Count} agents to save data", LogLevel.Debug);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"Failed to save ValleyAgent data: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     阶段 3（3.1.1）默认创建：遍历全部村民 NPC，确保 AgentBrain+Inventory 存在。
    ///     只调 EnsureBrain（入休眠注册表），不 ForceAllocate——活跃分配仍由 spark/对话/Director
    ///     驱动（TrySparkActivate 的 MaxAgentNpcs 守卫不受影响）。
    ///     与手动恢复路径（L618 IsManuallyOverridden skip）并行：恢复只处理手动 Agent，此处补齐其余 NPC。
    /// </summary>
    private void EnsureAllNpcBrainsExist()
    {
        if (_agentService == null)
        {
            return;
        }

        var created = 0;
        var existing = 0;
        foreach (var npc in Utility.getAllCharacters())
        {
            if (npc is not NPC villager || !villager.IsVillager || string.IsNullOrWhiteSpace(villager.Name))
            {
                continue;
            }

            if (_agentService.HasAgent(villager.Name))
            {
                existing++;
                continue;
            }

            var agent = _agentService.EnsureBrain(villager.Name);
            if (agent != null)
            {
                _bioLoader?.InjectBio(agent.Brain);
                created++;
            }
        }

        _monitor.Log(
            $"[Phase3] Default creation: ensured {created} NPC brains (dormant), {existing} already active",
            LogLevel.Debug);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (_agentService == null || _config == null)
        {
            return;
        }

        _monitor.Log("Day started - reevaluating agents and clearing caches", LogLevel.Debug);

        // B0/E5-3: 新的一天重置主动发言额度（每日计数 + 冷却），E5-2 喊话/E5-3 搭话共享。
        _proactiveSpeechQuota?.ResetDaily(GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth));

        // 阶段 3 (3.6): day_started 聚合昨日玩家活动百分比 + 趋势（供 DirectorContextBuilder 注入导演上下文）
        _playerActionTracker?.StartDay(GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth));

        // 阶段 3 (3.4.1): day_started 清理超保留天数的 L2 todayEvents（设计 doc §5.1：保留近 3 天，
        // 配置在 L2.TodayEventsRetentionDays）。全部 Brain（活跃 + 休眠）统一清理。
        var dateIso = GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth);
        var prunedTotal = 0;
        foreach (var brainAgent in _agentService.AllBrains)
        {
            prunedTotal += brainAgent.Brain.PruneTodayEvents(dateIso, _config.L2.TodayEventsRetentionDays);
        }

        if (prunedTotal > 0)
        {
            _monitor.Log($"[Phase3] Day started: pruned {prunedTotal} stale L2 todayEvents entries", LogLevel.Debug);
        }


        // E3-5: 换日生成当日求购（每 NPC 每天 ≤1 条，价格 1.0~1.1× 公道价，当日有效）
        GenerateDayPurchaseRequests();

        // Task 10: 通知 TS 新的一天开始（换日情绪重置由 TS 引擎执行；旧叙事 Director
        // morningPlan 已于 2026-09-14 砍除，directorContext 继续推送供未来工具脑消费）
        _ = NotifyDayStartedAsync();

        _agentTickLoop?.ClearAllReleaseState();

        _agentService!.AllocationManager.ReevaluateAllocations();

        var hasTestMod = TestModHelper.IsTestModPresent(_helper);
        if (hasTestMod)
        {
            _monitor.Log("TestMod detected — skipping auto-allocation.", LogLevel.Debug);
            TestModHelper.ForceAllocateTestAgents(_agentService, _bioLoader!, _config, _monitor, WireAgentStateEvents);
        }

        // 设计文档 §4.3.1：NPC 不在 day_started 时自动分配。
        // 第1天默认 0 个 Agent，由 spark/导演/玩家交互激活。
        // 读档恢复的 manual override Agent 已在 OnLoaded 中处理。

        NPCDialoguePatch.ClearCache();
        NPCGiftPatch.ClearDayStart();

        var agents = _agentService.GetAllAgents();
        _monitor.Log(
            $"Active agents: {agents.Count} (target [Min={_config.MinAgentNpcs}, Normal={_config.NormalAgentNpcs}, Max={_config.MaxAgentNpcs}])",
            LogLevel.Debug);
        foreach (var agent in agents)
        {
            _monitor.Log($"  - {agent.NpcName}: {agent.StateMachine.CurrentStateFlag}");
        }

        foreach (var agent in agents)
        {
            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc == null)
            {
                continue;
            }

            if (agent.Health.IsDead)
            {
                agent.Health.Respawn();
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                _monitor.Log($"{agent.NpcName} has respawned with full health.", LogLevel.Info);
            }

            // 所有 Agent（含 FOLLOW / 刚复活）：新的一天交给原版日程安置到合理位置，
            // 不再把 FOLLOW NPC 直接传送到玩家脚下（读档即见的贴脸瞬移）。
            // FOLLOW 恢复由逐 tick 逻辑触发跨图旅行——NPC 从地图入口走进玩家视野。
            npc.followSchedule = true;
            npc.ignoreScheduleToday = false;
            try
            {
                npc.checkSchedule((int)Game1.currentGameTime.TotalGameTime.TotalMilliseconds);
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[DayStarted] {agent.NpcName} schedule check failed: {ex.Message}", LogLevel.Warn);
            }

            // 日程执行后重新接管
            npc.followSchedule = false;
            npc.ignoreScheduleToday = true;
            // 清理残留的旅行状态，防止跨天旅行计时错乱
            _agentNavigator?.CancelTravel(agent.NpcName);
        }
    }

    /// <summary>
    ///     E3-5: 换日为每个 Agent NPC 生成当日求购（每 NPC 每天 ≤1 条），并发布聊天栏公告。
    ///     求购价 1.0~1.1× 公道价，求购单当日有效（NpcPurchaseRequestService 长 TTL Registry）。
    ///     交付（2026-08-15 步骤 2）：命中求购单时 NPCGiftPatch 拒绝送礼交接并提示走对话议价，
    ///     结算由 TS 对话流 trade 工具 → execute_adjust 原子批完成。
    /// </summary>
    private void GenerateDayPurchaseRequests()
    {
        if (_agentService == null || _purchaseRequestService == null || _agentService.EconomyProfiles == null)
        {
            return;
        }

        var gameDate = GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth);
        var published = 0;

        foreach (var npcName in _agentService.ActiveAgentNames)
        {
            var profile = _agentService.EconomyProfiles.GetProfile(npcName);
            var request = _purchaseRequestService.TryGenerate(
                profile,
                gameDate,
                ResolveItemSalePrice,
                ResolveItemDisplayName);

            if (request == null)
            {
                continue;
            }

            Game1.chatBox?.addMessage(
                $"📢 {request.NpcName} 想收购 {request.ItemName}×{request.Quantity}（出价 {request.Price}g）",
                Color.White);
            _monitor.Log(
                $"[PurchaseRequest] {request.NpcName}: wants {request.ItemName}×{request.Quantity} @ {request.Price}g",
                LogLevel.Info);
            published++;
        }

        if (published > 0)
        {
            _monitor.Log($"[PurchaseRequest] published {published} request(s) for {gameDate}", LogLevel.Debug);
        }
    }

    /// <summary>E3-5: 求购物品基准售价解析（ItemRegistry 无此物品返回 -1 → 跳过生成）。</summary>
    private static int ResolveItemSalePrice(string itemId)
    {
        var item = ItemRegistry.Create(itemId, allowNull: true);
        if (item == null)
        {
            return -1;
        }

        return item is Object obj ? obj.sellToStorePrice() : item.salePrice();
    }

    /// <summary>E3-5: 求购物品显示名解析（ItemRegistry 无此物品返回原始 id）。</summary>
    private static string ResolveItemDisplayName(string itemId)
    {
        var item = ItemRegistry.Create(itemId, allowNull: true);
        return item?.DisplayName ?? itemId;
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (_agentService == null)
        {
            return;
        }

        _monitor.Log("Day ending - saving daily stats", LogLevel.Debug);

        var circuitStatus = _agentService.CircuitBreaker.GetStatus();
        _monitor.Log(
            $"Circuit breaker status: {circuitStatus.State}, Failures: {circuitStatus.ConsecutiveFailures}, Avg Response: {circuitStatus.AverageResponseTimeSeconds:F2}s",
            LogLevel.Debug);
    }

    /// <summary>
    ///     断线重连成功（2026-08-15 步骤 4）：发 reconnect_sync 给 TS。
    ///     outbox 补发已由 WebSocketClient 完成（replayedOutbox 条）；附 active agent 名单，
    ///     TS 对账 in-flight adjust pending（断线期间不会有新经济指令——TS 不可达，账本天然无漂移）。
    ///     fire-and-forget；连接刚恢复，发送失败只降级日志。
    /// </summary>
    private void OnReconnected(int replayedOutbox)
    {
        if (_agentServerProvider == null || _agentService == null)
        {
            _monitor?.Log("[ReconnectSync] provider or agentService not ready, skipping", LogLevel.Warn);
            return;
        }

        try
        {
            var msg = new ProtocolV2.ReconnectSyncMessage
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ReplayedOutbox = replayedOutbox,
                Agents = _agentService.ActiveAgentNames.ToList(),
                GameDate = GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth)
            };
            var json = MessageProtocol.Serialize(msg);
            _ = _agentServerProvider.SendMessageAsync(json);
            _monitor?.Log(
                $"[ReconnectSync] sent: replayed={replayedOutbox}, agents={msg.Agents.Count}, date={msg.GameDate}",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[ReconnectSync] failed to send: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     收到非 pending request 响应的 unsolicited 消息时按 type 路由。
    ///     目前仅处理 allocate_agent（TS→C#，导演请求分配某 NPC 为 Agent，设计文档 §4.2.2）。
    ///     其余类型 Warn 日志（含消息体前 200 字符，issue #22），不抛异常。
    /// </summary>
    private void OnUnsolicitedMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            var type = typeProp.GetString();

            switch (type)
            {
                case ProtocolV2.MessageTypeAllocateAgent:
                    EnqueueWsCommand(ProtocolV2.MessageTypeAllocateAgent, json, "AllocateAgent");
                    break;
                case ProtocolV2.MessageTypeDirectorCommand:
                    EnqueueWsCommand(ProtocolV2.MessageTypeDirectorCommand, json, "DirectorCommand");
                    break;
                case ProtocolV2.MessageTypeExecuteAdjust:
                    EnqueueWsCommand(ProtocolV2.MessageTypeExecuteAdjust, json, "ExecuteAdjust");
                    break;
                default:
                {
                    // issue #22：默认分支原为 Trace 级（控制台默认不可见）且不读消息体——
                    // TS 崩溃/协议漂移的 error 帧或未知帧被静默吞掉，玩家侧退化成 120s 盲等。
                    // 提升到 Warn 并打印前 200 字符，保证不可归因帧至少留痕。
                    var preview = json?.Length > 200 ? json[..200] + "..." : json;
                    _monitor?.Log($"[WS] Unhandled unsolicited message type: {type ?? "(null)"}; body: {preview}", LogLevel.Warn);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[WS] Failed to route unsolicited message: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     处理 allocate_agent 消息：调用 AllocateAgentHandler 执行 ForceAllocate + 写入 KeepUntil。
    ///     回 action_result 给 TS，包含 success/reason（Allocated/MaxCapacityReached/InvalidState）。
    ///     fire-and-forget 发送，失败仅日志。
    /// </summary>
    private void HandleAllocateAgent(string json)
    {
        if (_allocateAgentHandler == null || _agentServerProvider == null)
        {
            _monitor?.Log("[AllocateAgent] Handler or server provider not initialized, skipping", LogLevel.Warn);
            return;
        }

        try
        {
            var msg = MessageProtocol.Deserialize<ProtocolV2.AllocateAgentMessage>(json);
            var (success, reason) = _allocateAgentHandler.Handle(msg);

            // 成功时补做 InjectBio + WireAgentStateEvents（与 SparkAllocator 路径一致），
            // 让新分配的 Agent 立即可用。已分配的情况下 CreateAgent 会返回 null，跳过即可。
            if (success && _agentService != null && !string.IsNullOrWhiteSpace(msg.NpcName))
            {
                var agent = _agentService.CreateAgent(msg.NpcName);
                if (agent != null)
                {
                    _bioLoader?.InjectBio(agent.Brain);
                    WireAgentStateEvents(agent);
                    _monitor?.Log(
                        $"[AllocateAgent] {msg.NpcName} allocated by director (keepUntil={msg.KeepUntilIso ?? "none"})",
                        LogLevel.Info);
                }
            }

            // 回 action_result（fire-and-forget）
            var actionResult = new ProtocolV2.ActionResultMessage
            {
                Type = ProtocolV2.MessageTypeActionResult,
                RequestId = Guid.NewGuid().ToString("N"),
                NpcName = msg.NpcName,
                Action = "allocate_agent",
                Tool = "allocate_agent",
                Success = success,
                Reason = reason,
                Result = success
                    ? $"allocated {msg.NpcName}"
                    : $"max capacity reached for {msg.NpcName}"
            };
            var responseJson = MessageProtocol.Serialize(actionResult);
            _ = _agentServerProvider.SendMessageAsync(responseJson);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[AllocateAgent] Failed to handle message: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     execute_adjust 原子批经济指令处理（TS→C#，2026-08-15 账本迁移设计 §4.1）。
    ///     反序列化 → AdjustExecutor.Execute（幂等 + 物理校验 + 原子批执行）
    ///     → fire-and-forget 回 adjust_result（echo instructionId，TS 据此推进账本 pending → committed/rolled_back）。
    ///     回执发送失败只降级日志——TS 超时重发时凭 instructionId 幂等命中缓存回执（设计 §6）。
    /// </summary>
    private void HandleExecuteAdjust(string json)
    {
        if (_adjustExecutor == null || _agentServerProvider == null)
        {
            _monitor?.Log("[ExecuteAdjust] Executor or server provider not initialized, skipping", LogLevel.Warn);
            return;
        }

        try
        {
            var msg = MessageProtocol.Deserialize<ProtocolV2.ExecuteAdjustMessage>(json);
            var result = _adjustExecutor.Execute(msg);
            _ = _adjustExecutor.SendAdjustResultAsync(result);
            _monitor?.Log(
                $"[ExecuteAdjust] {msg.InstructionId}: {(result.Success ? "ok" : $"failed ({result.FailureCode})")} " +
                $"{result.Steps.Count} step(s) for {msg.NpcName}",
                result.Success ? LogLevel.Info : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[ExecuteAdjust] Failed to handle message: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     阶段 3 Director 工具命令处理（director_command，TS→C#）。
    ///     反序列化 → CommandExecutor.ExecuteDirectorCommand（独立于 NPC 工具 switch 的元层路由）
    ///     → 回 action_result（echo requestId，协议纪律：响应回带相同 id）。
    /// </summary>
    private void HandleDirectorCommand(string json)
    {
        if (_commandExecutor == null || _agentServerProvider == null)
        {
            _monitor?.Log("[DirectorCommand] Executor or server provider not initialized, skipping", LogLevel.Warn);
            return;
        }

        try
        {
            var msg = MessageProtocol.Deserialize<ProtocolV2.DirectorCommandMessage>(json);
            var (success, reason, message) = _commandExecutor.ExecuteDirectorCommand(msg.Tool, msg.Args);
            var actionResult = new ProtocolV2.ActionResultMessage
            {
                Type = ProtocolV2.MessageTypeActionResult,
                RequestId = string.IsNullOrWhiteSpace(msg.RequestId) ? Guid.NewGuid().ToString("N") : msg.RequestId,
                NpcName = msg.NpcName ?? string.Empty,
                Action = msg.Tool,
                Tool = msg.Tool,
                Success = success,
                Reason = reason,
                Result = success ? $"director tool '{msg.Tool}' ok" : message
            };
            var responseJson = MessageProtocol.Serialize(actionResult);
            _ = _agentServerProvider.SendMessageAsync(responseJson);
            _monitor?.Log(
                success
                    ? $"[DirectorCommand] '{msg.Tool}' ok"
                    : $"[DirectorCommand] '{msg.Tool}' failed: {message}",
                success ? LogLevel.Info : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[DirectorCommand] Failed to handle message: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     Task 10: 通知 TS Agent Server 新的一天开始（fire-and-forget）。
    ///     TS 收到后按 10% 概率触发导演 morningPlan，产出的 beat NPC 通过 allocate_agent 下发回 C#。
    ///     设计文档 §4.2.2。
    /// </summary>
    private async Task NotifyDayStartedAsync()
    {
        if (_agentServerProvider == null)
        {
            return;
        }

        try
        {
            var dateIso = GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth);

            // 2026-08-09: game_context_sync 必须先于 day_started 发送！
            // TS 端 WS 消息处理是串行的（await adapter.routeMessage），morningPlan() 在
            // 首行同步调用 gameCtxMgr.getCurrent()。若 day_started 先到，morningPlan 读到
            // ctx=null 直接走 no-game-context 分支返回 []，导演 LLM 永不运行，game_context_sync
            // 随后才被处理——为时已晚。先发上下文再发 day_started。
            try
            {
                var ctx = GameContextSyncBuilder.Build(_monitor);
                var syncMsg = new ProtocolV2.GameContextSyncMessage
                {
                    Type = ProtocolV2.MessageTypeGameContextSync,
                    RequestId = Guid.NewGuid().ToString("N"),
                    Context = ctx
                };
                await _agentServerProvider.SendMessageAsync(MessageProtocol.Serialize(syncMsg)).ConfigureAwait(false);
                _monitor?.Log(
                    $"[GameContextSync] sent (Y{Game1.year} {Game1.currentSeason} {Game1.dayOfMonth}, npcStates={(ctx.TryGetValue("npcStates", out var npcStatesObj) && npcStatesObj is System.Collections.ICollection c ? c.Count : 0)})",
                    LogLevel.Debug);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
            {
                _monitor?.Log($"[GameContextSync] failed: {ex.Message}", LogLevel.Warn);
            }

            var msg = new ProtocolV2.DayStartedMessage
            {
                Type = ProtocolV2.MessageTypeDayStarted,
                RequestId = Guid.NewGuid().ToString("N"),
                DateIso = dateIso,
                // 阶段 3 (3.7): 导演上下文（压缩结构化文本，预算 800-1500 token）。构建失败不阻塞 day_started 主流程。
                DirectorContext = BuildDirectorContextSafe(dateIso)
            };
            var json = MessageProtocol.Serialize(msg);
            await _agentServerProvider.SendMessageAsync(json).ConfigureAwait(false);
            _monitor?.Log($"[DayStarted] Notified TS server (date={dateIso})", LogLevel.Debug);
        }
        catch (InvalidOperationException ex)
        {
            _monitor?.Log($"[DayStarted] Failed to notify TS: {ex.Message}", LogLevel.Debug);
        }
    }

    /// <summary>
    ///     安全拼装导演上下文：任何失败都降级为 null 而非拖垮 day_started（导演日志静默 bug 的教训：
    ///     失败要可见——记录原因，但不中断主流程）。
    /// </summary>
    private string? BuildDirectorContextSafe(string dateIso)
    {
        if (_directorContextBuilder == null)
        {
            return null;
        }

        try
        {
            var context = _directorContextBuilder.Build(dateIso);
            _monitor?.Log(
                $"[DirectorContext] built ({DirectorContextBuilder.EstimateTokens(context)} tokens)",
                LogLevel.Debug);
            return context;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[DirectorContext] build failed, downgraded to null: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    /// <summary>
    ///     根据游戏日期生成 ISO 风格日期字符串，格式 "Y{year}_{season}_{day}"。
    /// </summary>
    /// <param name="year">游戏年份。</param>
    /// <param name="season">当前季节（spring/summer/fall/winter），可为 null。</param>
    /// <param name="day">当月日期（1-28）。</param>
    /// <returns>ISO 风格日期字符串。</returns>
    private static string GetGameDateIso(int year, string? season, int day)
    {
        var safeSeason = season ?? "spring";
        return $"Y{year}_{safeSeason}_{day}";
    }

    /// <summary>
    ///     NPC 交还原版日程后的回调。确保 schedule 标志正确。
    /// </summary>
    private void OnVanillaReleaseFinalized(object? sender, VanillaReleaseEventArgs e)
    {
        // E0-4 灰色系统消息：交还原版日程时提示玩家。AI 主动离开走 SpeakCommand 彩色发言（Color.Gold），不走此路径。
        TravelSpeechHelper.SystemSpeak(e.NpcName, "离开了");
        var npc = Game1.getCharacterFromName(e.NpcName);
        if (npc != null)
        {
            npc.followSchedule = true;
            npc.ignoreScheduleToday = false;
            _monitor.Log($"{e.NpcName}: vanilla release finalized — schedule flags confirmed");
        }
    }

    /// <summary>
    ///     spark 检查：玩家进入 NPC 半径内时低概率激活该 NPC 为 Agent。
    ///     设计文档 §4.2.3：5% 概率（BelowMin 翻倍 10%，>=Normal 停止），每日每 NPC 一次。
    /// </summary>
    private void TrySparkNearbyNpcs()
    {
        if (_sparkAllocator == null || _agentService == null || _config == null)
        {
            return;
        }

        var player = Game1.player;
        if (player?.currentLocation == null)
        {
            return;
        }

        var dateKey = $"{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";
        var nearbyDistance = (double)_config.ChatNearbyDistanceTiles;
        var currentCount = _agentService.AllocationManager.CurrentAgentCount;

        foreach (var npc in player.currentLocation.characters)
        {
            if (npc?.IsVillager != true)
            {
                continue;
            }

            if (_agentService.AllocationManager.IsAllocated(npc.Name))
            {
                continue;
            }

            var dx = (double)(npc.Tile.X - player.Tile.X);
            var dy = (double)(npc.Tile.Y - player.Tile.Y);
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > nearbyDistance)
            {
                continue;
            }

            if (_sparkAllocator.TrySpark(
                    npc.Name, dateKey, currentCount,
                    _config.MinAgentNpcs, _config.NormalAgentNpcs, _config.MaxAgentNpcs))
            {
                if (_agentService.AllocationManager.TryAllocate(npc.Name, 0, 0, 0))
                {
                    var agent = _agentService.CreateAgent(npc.Name);
                    if (agent != null)
                    {
                        _bioLoader?.InjectBio(agent.Brain);
                        _monitor.Log($"[Spark] Allocated {npc.Name} (nearby spark)", LogLevel.Info);
                    }
                }

                currentCount++; // 防止一次 tick 分配多个超 Normal
            }
        }
    }

    /// <summary>
    ///     互动空闲淘汰：Agent 长时间未与玩家互动且无 keep 豁免时清理。
    ///     设计文档 §4.3.2：超 IdleThresholdSeconds 且无 KeepUntil/已过期 → RemoveAgent + Deallocate。
    ///     manual override 不淘汰（玩家显式分配）。
    /// </summary>
    private void CheckInteractionIdleEviction()
    {
        if (_agentService == null || _config == null)
        {
            return;
        }

        // TestMod 测试豁免：V3 长跑测试需要持续持有目标 NPC 的分配，
        // 90s 互动空闲淘汰会在测试中途逐出 NPC（agent not found → 后续断言全失败）。
        // 仅测试环境（TestMod 存在）豁免，生产行为不变。IT04 测的是 AllocationManager
        // 层 ForceAllocate 淘汰事件，与本豁免无关。
        if (TestModHelper.IsTestModPresent(_helper))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var idleThreshold = _config.IdleThresholdSeconds;
        var toEvict = new List<string>();

        foreach (var info in _agentService.AllocationManager.GetAllAllocatedAgents())
        {
            // manual override 不淘汰（玩家显式分配）
            if (info.IsManuallyOverridden)
            {
                continue;
            }

            if (info.ShouldEvict(now, idleThreshold))
            {
                toEvict.Add(info.NpcName);
            }
        }

        foreach (var npcName in toEvict)
        {
            _monitor.Log($"[IdleEviction] {npcName} evicted (no player interaction for {idleThreshold}s)",
                LogLevel.Info);
            _ = _agentService.RemoveAgent(npcName);
            _ = _agentService.AllocationManager.Deallocate(npcName);
        }
    }

    /// <summary>
    ///     将 WS 后台线程收到的改状态指令入队，由主线程 ProcessPendingWsCommands 出队执行。
    ///     具体 Handler（HandleAllocateAgent/HandleDirectorCommand/HandleExecuteAdjust）
    ///     内部已有服务可用性守卫，入队无需重复检查。
    /// </summary>
    private void EnqueueWsCommand(string type, string json, string label)
    {
        _pendingWsCommands.Enqueue((type, json, Environment.TickCount64));
        _monitor?.Log($"[WS] {label} queued for main thread (queue={_pendingWsCommands.Count})", LogLevel.Debug);
        // 深度告警：泵每 tick 全量排水，稳态深度应≈0——爆表 = 主线程泵停摆的早期信号
        QueueTelemetry.WarnIfDeep("ws-commands", _pendingWsCommands.Count, _monitor);
    }

    /// <summary>
    ///     在主线程（OnUpdateTicked）中处理 WS 后台线程入队的改状态指令。
    ///     execute_adjust/director_command/allocate_agent 都会直接改写 Game1 状态
    ///     （Farmer.Money/背包/NPC 状态），必须在主线程执行（2026-08-16 联机审计 P0）。
    /// </summary>
    private void ProcessPendingWsCommands()
    {
        while (_pendingWsCommands.TryDequeue(out var item))
        {
            // 队列滞留检测：入队到出队 >2s 说明主线程泵曾停摆（看门狗 5s 阈值以下的卡顿只有这里能看到）
            var waitedMs = Environment.TickCount64 - item.EnqueuedAt;
            if (waitedMs > 2000)
            {
                _monitor?.Log($"[WS] Main-thread command waited {waitedMs}ms in queue (type={item.Type})", LogLevel.Warn);
            }

            try
            {
                switch (item.Type)
                {
                    case ProtocolV2.MessageTypeAllocateAgent:
                        HandleAllocateAgent(item.Json);
                        break;
                    case ProtocolV2.MessageTypeDirectorCommand:
                        HandleDirectorCommand(item.Json);
                        break;
                    case ProtocolV2.MessageTypeExecuteAdjust:
                        HandleExecuteAdjust(item.Json);
                        break;
                    default:
                        _monitor?.Log($"[WS] Unknown queued command type: {item.Type}", LogLevel.Warn);
                        break;
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[WS] Main-thread command ({item.Type}) failed: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     在主线程（OnUpdateTicked）中处理来自 TS Agent Server 的 state_sync 命令。
    ///     命令在 ThreadPool 线程收到后入队，这里出队在主线程执行，
    ///     确保 Game1 状态访问的线程安全。
    /// </summary>
    private void ProcessPendingMainThreadCommands()
    {
        if (_commandExecutor == null)
        {
            // 清空队列避免堆积
            while (_pendingMainThreadCommands.TryDequeue(out _))
            {
            }

            return;
        }

        while (_pendingMainThreadCommands.TryDequeue(out var item))
        {
            try
            {
                _commandExecutor.ExecuteCommands(item.npcName, item.commands);
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[ProtocolV2] MainThread command execution failed for {item.npcName}: {ex.Message}",
                    LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     T17/T18: 在主线程处理 pre_speak 主动说话队列。
    ///     从后台决策线程入队的 (npcName, text) 会被 ActiveSpeechRouter.Route 消费。
    ///     必须在 OnUpdateTicked（主线程）中调用，避免跨线程访问 Game1。
    /// </summary>
    private void ProcessPendingPreSpeakActions()
    {
        while (_pendingPreSpeakActions.TryDequeue(out var item))
        {
            try
            {
                var npc = Game1.getCharacterFromName(item.npcName);
                if (npc != null && Game1.player != null)
                {
                    ActiveSpeechRouter.Route(npc, Game1.player, item.text);
                    _monitor.Log($"[PreSpeak] {item.npcName}: {item.text}", LogLevel.Debug);
                }
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[PreSpeak] Failed to route for {item.npcName}: {ex.Message}", LogLevel.Warn);
            }
            catch (ArgumentException ex)
            {
                _monitor.Log($"[PreSpeak] Failed to route for {item.npcName}: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     主线程泵的统一兜底执行器（死锁修复 2026-09-12）。
    ///     OnUpdateTicked 里的排水段是一条**串行责任链**：后台线程只入队，回包渲染 /
    ///     ModMessage 发送 / 好感落账 / execute_adjust 执行全靠每 tick 排干。
    ///     链上任一环抛异常，下游所有环当 tick 全部失效——其中
    ///     DialogueBoxInputPatch.ProcessPendingReplies 是 <c>_isWaitingForResponse</c>
    ///     的唯一清除点，被跳过就意味着输入框永久停在"等待回复"、Enter 与键盘输入全被吞。
    ///     所以每个泵独立兜底：单泵失败只丢当 tick 的一个动作，链不能断。
    /// </summary>
    private void Pump(string name, Action pump)
    {
        try
        {
            pump();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Pump] '{name}' failed this tick (downstream pumps unaffected): {ex}", LogLevel.Error);
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // 只量"排水段"（TranscriptSink.Drain → ProcessPendingWsCommands）：这段全部是主线程队列消费，
        // >100ms 说明某个泵 handler 在挂起；后面的 agent tick 逻辑不在此计时范围（它有自己的观测面）
        var drainStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // E1-1: 排空 TranscriptSink 队列，在主线程写入 JSONL 文件
        // 死锁修复（2026-09-12）：以下每个泵各自兜底。这些泵是串行责任链——
        // 任一环抛异常，当 tick 下游全部泵（含 DialogueBoxInputPatch 的回复渲染，
        // 即 _isWaitingForResponse 的唯一清除点）都被跳过，输入框会卡在"等待回复"。
        Pump("transcript-sink", () => _transcriptSink?.Drain());

        // E2-3: 排空到期的长文分句聊天消息（主线程访问 Game1.chatBox）
        Pump("speech-display", SpeechDisplayRouter.Tick);

        // ThinClient 模式下 _agentService 为 null，但 farmhand 仍需要处理
        // 礼物响应、对话回复与 state_sync 命令的主线程队列，否则 UI 无法渲染。
        Pump("gift-actions", NPCGiftPatch.ProcessMainThreadActions);
        Pump("dialogue-api-actions", ValleyAgentApi.ProcessMainThreadActions);
        // 2026-08-23 审计 P0：房客中继链路（HostRequestHandlers）的 await 续体改状态也走主线程队列
        Pump("host-request-mainthread", Multiplayer.HostRequestHandlers.ProcessMainThreadActions);
        Pump("dialogue-replies", DialogueBoxInputPatch.ProcessPendingReplies);
        // 陈旧等待自愈：回包与超时兜底双双丢失时强制解锁输入框（防永久"等待回复"）
        Pump("dialogue-stale-wait", DialogueBoxInputPatch.ResetStaleWait);
        // E2-2: 聊天栏路由的 LLM 回复主线程渲染（ActiveSpeechRouter，不打开对话框）
        Pump("chat-replies", ChatBarRouter.ProcessPendingReplies);
        // E5-2: 远程喊话延迟回应主线程渲染（NPC 听到喊话 3~8s 后回应）
        Pump("shout-replies", ChatBarRouter.ProcessShoutReplies);
        Pump("mainthread-commands", ProcessPendingMainThreadCommands);
        Pump("ws-commands", ProcessPendingWsCommands);

        // 排水段耗时告警（节流 5s）：主线程泵停滞的细粒度信号，补看门狗 5s 阈值以下的盲区
        var drainMs = drainStopwatch.Elapsed.TotalMilliseconds;
        if (drainMs > 100 && QueueTelemetry.ShouldWarn("update-drain"))
        {
            _monitor.Log($"[Perf] OnUpdateTicked drain took {drainMs:F0}ms — a pump handler may be hanging",
                LogLevel.Warn);
        }

        if (_agentService == null)
        {
            return;
        }

        // T17/T18: 处理 pre_speak 主动说话（主线程执行，避免跨线程访问 Game1）
        ProcessPendingPreSpeakActions();

        _tickCounter++;

        // 阶段 3 (3.6): 每 10 tick 采样玩家活动（采样是廉价的只读分类，不阻塞主线程）
        if (_tickCounter % PlayerActionTracker.SampleIntervalTicks == 0 && Game1.currentLocation != null)
        {
            _playerActionTracker?.Sample();
        }

        // ── 加速模式：推进游戏时间 ──
        // 游戏本身以 1x 推进 timeOfDay，我们额外追加 (multiplier-1)x
        // 正常速率：10 游戏分钟 / 420 ticks（7秒 @60fps）
        _currentSpeedMultiplier = DebugFlags.GameSpeedMultiplier;
        if (_currentSpeedMultiplier > 1 && !Game1.paused && Game1.timeOfDay < 2600)
        {
            _extraTimeAccumulator += (_currentSpeedMultiplier - 1) * (10.0f / 420.0f);
            if (_extraTimeAccumulator >= 10.0f)
            {
                var advance = (int)(_extraTimeAccumulator / 10.0f) * 10;
                Game1.timeOfDay += advance;
                _extraTimeAccumulator -= advance;
            }
        }

        // ── 加速模式：缩放决策间隔 ──
        var effectiveDecisionInterval = _currentSpeedMultiplier > 1
            ? Math.Max(60, _decisionIntervalTicks / _currentSpeedMultiplier)
            : _decisionIntervalTicks;

        if (Game1.activeClickableMenu is DialogueBox)
        {
            var speaker = Game1.currentSpeaker;
            _lastDialogueNpcName = speaker?.Name;
        }
        else if (_lastDialogueNpcName != null)
        {
            _lastDialogueNpcName = null;
        }

        if (!DebugFlags.SuppressDecisions && _tickCounter % effectiveDecisionInterval == 0)
        {
            var decisionAgents = _agentService.GetAllAgents()
                .Where(a => _agentNavigator?.IsTravelling(a.NpcName) != true)
                .ToList();
            if (decisionAgents.Count > 0)
            {
                _ = Task.Run(async () => await MakeDecisionsAsync(decisionAgents).ConfigureAwait(false));
            }
        }

        // spark 检查：每 60 tick（约 1 秒）检查玩家附近 NPC 是否低概率激活
        // 设计文档 §4.2.3：5% 概率（BelowMin 翻倍到 10%，>=Normal 停止）
        if (_tickCounter % 60 == 0)
        {
            TrySparkNearbyNpcs();
        }

        // 互动空闲淘汰检查：每 120 tick（约 2 秒）检查一次
        // 设计文档 §4.3.2：超过 IdleThresholdSeconds 未互动且无 keep 豁免 → 淘汰
        if (_tickCounter % 120 == 0)
        {
            CheckInteractionIdleEviction();
        }

        var agents = _agentService.GetAllAgents();

        // 在游戏 NPC.Update() 之后强制禁用日程，防止 schedule 将 agent NPC 拉走。
        // IDLE 未释放的 agent 由 IdleWanderHandler 管（flag 保持 false 单一所有权）；
        // 释放后的 agent 由 AgentTickLoop released 分支接管（FinalizeVanillaRelease 恢复 flag）。
        foreach (var agent in agents)
        {
            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc == null) continue;

            if (agent.StateMachine.CurrentStateFlag != AgentState.IDLE)
            {
                npc.followSchedule = false;
                npc.ignoreScheduleToday = true;
            }
        }

        foreach (var agent in agents)
        {
            var result = _agentTickLoop?.ProcessAgent(agent, _lastDialogueNpcName);

            switch (result)
            {
                case AgentTickLoop.ProcessResult.Skip:
                case AgentTickLoop.ProcessResult.Dead:
                case null:
                    continue;
                case AgentTickLoop.ProcessResult.Normal:
                    var currentState = agent.StateMachine.CurrentStateFlag;
                    ApplyControllerToNpc(agent, currentState);
                    break;
            }
        }

        // state_sync 管道已删除：TS 端无路由，每秒 1 个 60s 阻塞 Task 导致稳态 ~60 并发。

        // 加速模式下更频繁地处理状态机更新
        var stateMachineInterval = _currentSpeedMultiplier > 1 ? Math.Max(2, 10 / _currentSpeedMultiplier) : 10;
        if (_tickCounter % stateMachineInterval != 0)
        {
            return;
        }

        lock (_pendingDecisionsLock)
        {
            // 收集 "state too young" 的延迟决策，while 循环结束后统一重新入队。
            // 避免在 while 循环内直接 Enqueue 导致同一 item 被无限 Dequeue/Enqueue 的死循环。
            var deferred =
                new List<(AgentInstance agent, AgentState targetState, string reason, string thought, DateTime queuedAt
                    )>();
            var deferredAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (_pendingDecisions.Count > 0)
            {
                var (agent, targetState, reason, thought, queuedAt) = _pendingDecisions.Dequeue();

                // P0-4: 过期检查，丢弃超过 MaxDecisionAgeSeconds 的陈旧决策
                // 避免基于过期游戏状态的决策被执行
                var decisionAge = (DateTime.UtcNow - queuedAt).TotalSeconds;
                if (decisionAge > MaxDecisionAgeSeconds)
                {
                    _monitor.Log($"Decision discarded for {agent?.NpcName ?? "?"}: expired ({decisionAge:F1}s old)");
                    continue;
                }

                if (agent?.StateMachine == null)
                {
                    continue;
                }

                var safeAgent = agent!;

                var minDuration = GetMinimumStateDuration(safeAgent.StateMachine.CurrentStateFlag);
                if (safeAgent.StateMachine.StateDuration.TotalSeconds < minDuration)
                {
                    // 同一 agent 只保留一条延迟决策，避免队列膨胀
                    if (!deferredAgents.Contains(safeAgent.NpcName))
                    {
                        _monitor.Log(
                            $"Decision deferred for {safeAgent.NpcName}: state too young ({safeAgent.StateMachine.StateDuration.TotalSeconds:F1}s < {minDuration}s)");
                        deferredAgents.Add(safeAgent.NpcName);
                        deferred.Add((safeAgent, targetState, reason, thought, queuedAt));
                    }

                    // 不在 while 循环内 Enqueue，避免死循环
                    continue;
                }

                if (safeAgent.StateMachine.CurrentStateFlag == AgentState.FOLLOW
                    && targetState != AgentState.FOLLOW
                    && _dialogueFollowedAgents.Contains(safeAgent.NpcName))
                {
                    _monitor.Log(
                        $"Decision skipped for {safeAgent.NpcName}: dialogue-triggered FOLLOW guarded against {targetState}");
                    continue;
                }

                var npc = Game1.getCharacterFromName(safeAgent.NpcName);
                if (npc != null && !IsTransitionValid(npc, targetState, _monitor))
                {
                    var originalTargetState = targetState;
                    _monitor.Log(
                        $"Guard denied {safeAgent.NpcName}: {targetState} invalid at location '{npc.currentLocation?.NameOrUniqueName ?? "?"}'",
                        LogLevel.Warn);

                    // Issue 11: TALK 被地点守卫拒绝时，若好友度>=200 回退 FOLLOW 而不是 IDLE
                    if (targetState == AgentState.TALK)
                    {
                        var friendship = 0;
                        if (Game1.player != null &&
                            Game1.player.friendshipData.TryGetValue(safeAgent.NpcName, out var fd))
                        {
                            friendship = fd.Points;
                        }

                        if (friendship >= _config!.TalkRejectionFollowThreshold &&
                            IsTransitionValid(npc, AgentState.FOLLOW, _monitor))
                        {
                            targetState = AgentState.FOLLOW;
                            _monitor.Log(
                                $"Guard fallback for {safeAgent.NpcName}: TALK→FOLLOW (friendship={friendship})");
                        }
                        else
                        {
                            targetState = AgentState.IDLE;
                        }
                    }
                    else
                    {
                        targetState = AgentState.IDLE;
                    }
                }
                else if (npc != null)
                {
                    _ = _stateRejectionCounts.Remove($"{safeAgent.NpcName}:{targetState}");
                }

                // 任务2.3+2.4：用 TryTransition 替换 ForceTransition，只在成功时更新 Brain 记录
                var transitionResult = safeAgent.StateMachine.TryTransition(targetState);
                if (transitionResult == StateTransitionResult.Success)
                {
                    safeAgent.LastDecisionState = targetState.ToString();
                    safeAgent.LastDecisionReason = reason;

                    // Fix (2026-08-02): 决策真实生效后才更新 idle 跟踪/vanilla 释放状态。
                    // 之前 ResetIdleTracking 在守卫之前调用——被地点守卫拒绝的决策（如 TALK 地点无效）
                    // 会把已交还原版日程的 NPC 错误拽回 Agent 控制（禁用 schedule），
                    // 造成 NPC 在原版日程与 Agent 控制间反复横跳（“跟随系统和默认逻辑打架”）。
                    // 移到守卫 + TryTransition 都通过之后：被拒决策不再产生副作用。
                    if (targetState == AgentState.IDLE)
                    {
                        _agentTickLoop?.TrackIdleDecision(safeAgent, safeAgent.NpcName);
                    }
                    else
                    {
                        _agentTickLoop?.ResetIdleTracking(safeAgent.NpcName, targetState);
                    }
                }
                else
                {
                    _monitor.Log(
                        $"[Decision] {safeAgent.NpcName}: transition to {targetState} rejected by state machine ({transitionResult})",
                        LogLevel.Warn);
                    safeAgent.LastDecisionState = safeAgent.StateMachine.CurrentStateFlag.ToString();
                    safeAgent.LastDecisionReason = $"转换被拒绝({transitionResult}): {reason}";
                }

                if (!string.IsNullOrWhiteSpace(thought))
                {
                    npc?.showTextAboveHead(thought, duration: 3000);
                }

                _monitor.Log($"Decision applied for {safeAgent.NpcName}: {targetState} | {reason}");
            }

            // 将延迟的决策重新入队，下一 tick 再处理
            foreach (var item in deferred)
            {
                _pendingDecisions.Enqueue(item);
            }
        }

        if (!DebugFlags.SuppressDecisions && _pendingDialogueEndDecisions != null &&
            _pendingDialogueEndDecisions.Count > 0)
        {
            var dialogueEndAgents = _pendingDialogueEndDecisions
                .Where(a => _agentNavigator?.IsTravelling(a.NpcName) != true)
                .ToList();
            _pendingDialogueEndDecisions = null;
            if (dialogueEndAgents.Count > 0)
            {
                _monitor.Log($"[DecisionTrigger] Processing {dialogueEndAgents.Count} dialogue-end decisions...");
                _ = Task.Run(async () => await MakeDecisionsAsync(dialogueEndAgents).ConfigureAwait(false));
            }
        }

        if (!DebugFlags.SuppressDecisions && _pendingTaskCompleteDecisions != null &&
            _pendingTaskCompleteDecisions.Count > 0)
        {
            var taskCompleteAgents = _pendingTaskCompleteDecisions
                .Where(a => _agentNavigator?.IsTravelling(a.NpcName) != true)
                .ToList();
            _pendingTaskCompleteDecisions = null;
            if (taskCompleteAgents.Count > 0)
            {
                _monitor.Log($"[DecisionTrigger] Processing {taskCompleteAgents.Count} task-complete decisions...");
                _ = Task.Run(async () => await MakeDecisionsAsync(taskCompleteAgents).ConfigureAwait(false));
            }
        }

        if (!DebugFlags.SuppressDecisions && _pendingGiftEventDecisions != null && _pendingGiftEventDecisions.Count > 0)
        {
            var giftEventAgents = _pendingGiftEventDecisions
                .Where(a => _agentNavigator?.IsTravelling(a.NpcName) != true)
                .ToList();
            _pendingGiftEventDecisions = null;
            if (giftEventAgents.Count > 0)
            {
                _monitor.Log($"[DecisionTrigger] Processing {giftEventAgents.Count} gift-event decisions...");
                _ = Task.Run(async () => await MakeDecisionsAsync(giftEventAgents).ConfigureAwait(false));
            }
        }

        _monsterAggroManager?.ProcessMonsterAttacks(agents, _tickCounter);

        foreach (var agent in agents)
        {
            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc == null)
            {
                continue;
            }

            if (agent.Health.IsDead)
            {
                HandleDeadNpc(npc);
                continue;
            }

            var emergencyState = CheckEmergencyState(npc, agent.StateMachine.CurrentStateFlag, agent.Health, _config!,
                agent.NpcName);
            if (emergencyState.HasValue && emergencyState.Value != agent.StateMachine.CurrentStateFlag)
            {
                agent.StateMachine.SetEmergencyEscape(true);
                agent.StateMachine.ForceTransition(emergencyState.Value);
                agent.StateMachine.SetEmergencyEscape(false);
                _monitor.Log($"{agent.NpcName} emergency switch to {emergencyState.Value}");
            }

            if (Game1.player?.currentLocation == npc.currentLocation
                && agent.StateMachine.CurrentStateFlag != AgentState.FIGHT)
            {
                var playerLocation = Game1.player!.currentLocation;
                Monster? nearestToPlayer = null;
                var nearestDist = float.MaxValue;
                foreach (var c in playerLocation.characters)
                {
                    if (c is Monster monster && !monster.IsInvisible && !monster.Name.Contains("Slime Ball"))
                    {
                        var dist = Vector2.Distance(Game1.player!.Tile, monster.Tile);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestToPlayer = monster;
                        }
                    }
                }

                if (nearestToPlayer != null && nearestDist <= _config!.PlayerInDangerDistance)
                {
                    // Issue 10: 冷却+去重，防止每 tick 刷屏
                    var lastTick = _lastPlayerInDangerEmotionTick.TryGetValue(agent.NpcName, out var lt) ? lt : 0;
                    if (Game1.ticks - lastTick >= _config!.EmotionCooldownTicks &&
                        agent.Brain.Emotion != NpcEmotion.Worried)
                    {
                        agent.Brain.SyncEmotion(NpcEmotion.Worried, 0.7f, "PlayerInDanger");
                        _lastPlayerInDangerEmotionTick[agent.NpcName] = Game1.ticks;
                    }
                }
            }
        }

        foreach (var agent in agents)
        {
            if (agent.Health.IsDead)
            {
            }
        }

        // 联机：60 tick 节流快照广播（broadcaster 内部自带 ShouldRunAgentLogic && IsMultiplayer 守卫）
        _multiplayerBroadcaster?.Update(_tickCounter);
    }

    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        // 情绪衰减由 TS Agent Server 管理，C# 不再本地执行 TickEmotionDecay

        // B5.2 周期驱动（设计 §3.4 步骤 2）：每个时间跳（游戏内 10 分钟）做一次池位级空闲回收。
        // 原 OnDayStarted 的 ReevaluateAllocations 保留（换日兜底 + 缓存清理语义不变）。
        RunIdleAllocationReclaim();

        // E5-3: 时间触发主动发言。仅在进入新窗口（600/1200/1800）时触发一次，
        // 同窗口不重复触发（_lastProactiveTriggerWindow 防刷屏）。
        TryTriggerProactiveSpeech(e.NewTime);
    }

    /// <summary>
    ///     B5 空闲回收编排（设计 §3.4）：先释放空闲 override，再按容量裁剪。
    ///     顺序不可倒：先释放 override，被释放者才能在随后的 ReevaluateAllocations 里
    ///     以普通分配身份参与超容量淘汰（manual 垫底规则不再遮蔽它们）。
    /// </summary>
    private void RunIdleAllocationReclaim()
    {
        if (_agentService == null)
        {
            return;
        }

        var manager = _agentService.AllocationManager;
        // "是否活跃对话"由本层判定、以委托传入（Manager 不得依赖 Patches 层）：
        // 主机侧当前活跃对话 NPC 不释放（对话框还开着，override 释放了也会立刻被下一轮对话重建）。
        var released = manager.ReleaseIdleManualOverrides(
            DateTime.UtcNow,
            IdleOverrideThreshold,
            name => string.Equals(DialogueBoxInputPatch.GetActiveAgentNpc(), name, StringComparison.OrdinalIgnoreCase));
        if (released > 0)
        {
            _monitor.Log($"[Allocation] Idle reclaim: released {released} idle manual override(s)", LogLevel.Debug);
        }

        manager.ReevaluateAllocations();
    }

    /// <summary>
    ///     B5.4 常驻身体拆除（设计 §3.4 步骤 4）：NPC 失去池位时的统一收尾仪式。
    ///     ForceTransition 必须在 RemoveAgent 之前——RemoveAgent 会把实例挪进休眠注册表，
    ///     先发带 reason="evicted" 的 state_changed 让 TS 端 updateActualState 记录被逐
    ///     （RemoveAgent 内部 Reset() 会再发一次空 reason 的 state_changed，TS 端仅在
    ///     reason 非空时更新 lastTransitionReason，evicted 不会被覆盖）。
    ///     幂等：agent 已不在（如返回标题清理时 ClearAllAgents 先于 ClearAllAllocations 执行）
    ///     直接返回，不重复任何副作用——PromoteToAgent 收编后依赖此保证不重复弹提示。
    /// </summary>
    private void OnAgentDeallocatedTeardown(object? sender, AgentAllocationEventArgs e)
    {
        if (_agentService == null)
        {
            return;
        }

        if (!_agentService.TryGetAgent(e.NpcName, out var agent) || agent == null)
        {
            return;
        }

        _ = agent.StateMachine.ForceTransition(AgentState.IDLE, true, reason: "evicted");

        // 降级休眠不删除：StateMachine.Reset + Handler 清理 + Brain/Inventory 保留复用（AgentService.RemoveAgent）
        _ = _agentService.RemoveAgent(e.NpcName);

        // 交还原版日程（照抄 PromoteToAgent 对被挤者的收尾仪式）
        var npc = Game1.getCharacterFromName(e.NpcName);
        if (npc != null)
        {
            npc.controller = null;
            npc.Halt();
            npc.followSchedule = true;
            npc.ignoreScheduleToday = false;
        }

        // 玩家可见通知只对"被玩家手动挤掉"发：换日裁剪 / 空闲淘汰是静默后台回收，不弹聊天栏。
        if (string.Equals(e.Reason, "Replaced by manual override", StringComparison.Ordinal))
        {
            Game1.chatBox?.addMessage($"{e.NpcName} 告别离开了", Color.White);
        }

        _monitor.Log($"[Allocation] {e.NpcName} body torn down to dormant brain (reason: {e.Reason})", LogLevel.Debug);
    }

    /// <summary>
    ///     E5-3 时间触发主动发言。窗口触发点（600/1200/1800）首次进入时，
    ///     从已醒 + 额度通过的候选 NPC 生成发言并入队 _pendingPreSpeakActions。
    /// </summary>
    private void TryTriggerProactiveSpeech(int time)
    {
        if (_agentService == null || _proactiveSpeechQuota == null)
        {
            return;
        }

        var window = time switch
        {
            >= ProactiveSpeechTrigger.MorningTrigger and < ProactiveSpeechTrigger.NoonTrigger => ProactiveSpeechTrigger
                .MorningTrigger,
            >= ProactiveSpeechTrigger.NoonTrigger and < ProactiveSpeechTrigger.EveningTrigger => ProactiveSpeechTrigger
                .NoonTrigger,
            >= ProactiveSpeechTrigger.EveningTrigger => ProactiveSpeechTrigger.EveningTrigger,
            _ => -1
        };

        if (window < 0 || window == _lastProactiveTriggerWindow)
        {
            return; // 非触发点 或 同窗口已触发过
        }

        _lastProactiveTriggerWindow = window;

        var candidates = BuildProactiveCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        var gameDate = GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth);
        var speeches = _proactiveSpeechTrigger.Evaluate(time, gameDate, candidates, _proactiveSpeechQuota);
        foreach (var s in speeches)
        {
            EnqueuePreSpeak(s.NpcName, s.Text);
            _monitor.Log($"[ProactiveSpeech] {s.NpcName}: {s.Text}", LogLevel.Debug);
        }
    }

    /// <summary>
    ///     无 NpcScheduleService 时的保守唤醒兜底：6:00~26:00 视为醒着
    ///     （SDV 所有 NPC 默认 6:00 起床；服务缺失属异常态，取宽松判定避免漏触发）。
    /// </summary>
    private static bool IsNpcAwakeFallback(NPC npc)
    {
        _ = npc; // 保守兜底不依赖 NPC 状态，仅按时间判定
        return Game1.timeOfDay >= 600;
    }

    /// <summary>
    ///     装配主动发言候选：全部 Agent NPC 中，已醒者 + 经济档案话痨度（默认 0.5）。
    /// </summary>
    private List<ProactiveSpeechTrigger.ProactiveCandidate> BuildProactiveCandidates()
    {
        var result = new List<ProactiveSpeechTrigger.ProactiveCandidate>();
        var scheduleService = _services.GetService<NpcScheduleService>();
        var agents = _agentService!.GetAllAgents();
        if (agents == null)
        {
            return result;
        }

        foreach (var agent in agents)
        {
            if (agent?.NpcName == null)
            {
                continue;
            }

            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc == null)
            {
                continue;
            }

            var isAwake = scheduleService == null
                ? IsNpcAwakeFallback(npc)
                : scheduleService.IsAwake(agent.NpcName, Game1.timeOfDay);

            var talkativeness = _agentService.EconomyProfiles?.GetProfile(agent.NpcName)?.Talkativeness ?? 0.5;
            result.Add(new ProactiveSpeechTrigger.ProactiveCandidate(
                agent.NpcName,
                npc.displayName ?? agent.NpcName,
                isAwake,
                talkativeness));
        }

        return result;
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (_agentService == null)
        {
            return;
        }

        if (e.OldMenu is DialogueBox && e.NewMenu == null)
        {
            DialogueBoxInputPatch.ClearActiveAgentNpc();

            if (!string.IsNullOrEmpty(_lastDialogueNpcName))
            {
                var agent = _agentService.GetAllAgents().FirstOrDefault(a => a.NpcName == _lastDialogueNpcName);
                if (agent != null)
                {
                    var preDialogueState = NPCDialoguePatch.GetPreDialogueState(_lastDialogueNpcName);
                    if (preDialogueState.HasValue && preDialogueState.Value != agent.StateMachine.CurrentStateFlag)
                    {
                        agent.StateMachine.ForceTransition(preDialogueState.Value);
                        _monitor.Log(
                            $"Restored {_lastDialogueNpcName} to pre-dialogue state: {preDialogueState.Value}");
                    }

                    NPCDialoguePatch.ClearPreDialogueState(_lastDialogueNpcName);

                    // P0-4: 清除该 NPC 的所有待处理决策，避免旧决策覆盖恢复后的状态
                    ClearPendingDecisions(_lastDialogueNpcName);

                    _monitor.Log(
                        $"[DecisionTrigger] Dialogue ended with {_lastDialogueNpcName}, triggering LLM re-evaluation...");
                    _pendingDialogueEndDecisions ??= new List<AgentInstance>();
                    _pendingDialogueEndDecisions.Add(agent);
                }

                _lastDialogueNpcName = null;
            }
        }
    }

    private void OnPlayerWarped(object? sender, WarpedEventArgs e)
    {
        if (_agentService == null)
        {
            return;
        }



        // E3-5: 求购单同语义——玩家跨图离开 NPC 时，该 NPC 的求购单作废（当面才能交付）
        if (_purchaseRequestService != null)
        {
            foreach (var agent in _agentService.GetAllAgents())
            {
                var npc = Game1.getCharacterFromName(agent.NpcName);
                if (npc == null || npc.currentLocation == e.NewLocation)
                {
                    continue;
                }

                if (_purchaseRequestService.VoidFor(agent.NpcName))
                {
                    _monitor.Log($"[PurchaseRequest] {agent.NpcName}: request voided on player warp away",
                        LogLevel.Debug);
                }
            }
        }

        foreach (var agent in _agentService.GetAllAgents())
        {
            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc == null)
            {
                continue;
            }

            // NPC 已在新地图：无需处理
            if (npc.currentLocation == e.NewLocation)
            {
                continue;
            }

            var currentState = agent.StateMachine.CurrentStateFlag;

            // P1-13: FOLLOW 状态 NPC 跟随玩家跨图
            if (currentState == AgentState.FOLLOW)
            {
                // D2 守卫：事件/节日期间不发起跨图旅行（防破坏节日流程），节后自动恢复
                if (GameEventGuard.IsEventOrFestivalActive)
                {
                    _monitor.Log(
                        $"[Follow] {agent.NpcName}: event/festival active — skipping cross-map travel initiation",
                        LogLevel.Debug);
                    continue;
                }

                // NavigateToTaskLocation 内部处理旅行重定向：
                // - 已在前往相同目标 → 跳过（幂等）
                // - 已在前往不同目标 → CancelTravel + 重启到新目标
                // 不再外部 CancelTravel，避免取消和重启之间的竞态窗口被 tick 循环钻空子
                var startedTravel = false;
                _agentNavigator?.NavigateToTaskLocation(
                    npc,
                    new[] { e.NewLocation?.NameOrUniqueName ?? "Farm" },
                    _tickCounter,
                    out startedTravel);
                if (startedTravel)
                {
                    _monitor.Log(
                        $"[Follow] {agent.NpcName}: cross-map travel started to {e.NewLocation?.NameOrUniqueName}",
                        LogLevel.Debug);
                }

                continue;
            }

            // P1-13: 其他活跃任务状态（FARM/MINE/FORAGE/FIGHT/TALK）的 NPC 在玩家跨图后
            // 已脱离玩家视野，任务失去协作意义。清除待处理决策并转 IDLE，等待下次决策周期重新评估。
            if (currentState == AgentState.FARM
                || currentState == AgentState.MINE
                || currentState == AgentState.FORAGE
                || currentState == AgentState.FIGHT
                || currentState == AgentState.TALK)
            {
                _agentNavigator?.CancelTravel(agent.NpcName);
                ClearPendingDecisions(agent.NpcName);

                var transitionResult = agent.StateMachine.TryTransition(AgentState.IDLE);
                if (transitionResult == StateTransitionResult.Success)
                {
                    _monitor.Log(
                        $"[Warp] {agent.NpcName}: {currentState} → IDLE (player left {e.OldLocation?.Name} → {e.NewLocation?.Name})",
                        LogLevel.Debug);
                }
                else
                {
                    // 状态机拒绝转换（如最小持续时间未满足），强制转换以避免 NPC 卡在无人地图
                    agent.StateMachine.ForceTransition(AgentState.IDLE);
                    _monitor.Log(
                        $"[Warp] {agent.NpcName}: {currentState} → IDLE (forced, player left {e.OldLocation?.Name} → {e.NewLocation?.Name})");
                }
            }
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e) =>
        _agentRenderer?.OnRenderedWorld(sender, e);

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (_agentHud == null || _hudRenderer == null)
        {
            return;
        }

        _agentHud.Update();
        var data = _agentHud.GetRenderData();
        if (data.IsVisible)
        {
            _hudRenderer.Render(data);
        }
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == SButton.F9)
        {
            _debugHudVisible = !_debugHudVisible;
            _monitor.Log($"Debug HUD: {(_debugHudVisible ? "ON" : "OFF")}", LogLevel.Info);

            if (_debugHudVisible && _agentService != null)
            {
                PrintDebugStats();
            }
        }
    }

    private Task MakeDecisionsAsync(List<AgentInstance> agents)
    {
        if (_agentService == null)
        {
            return Task.CompletedTask;
        }

        // 决策批此前完全无日志，且候选 2（后台裸读 Game1）就发生在这里——
        // Begin/End 让停滞看门狗能点名本批，总耗时给出亚看门狗阈值的负载证据（2026-09-11 生产化仪器）。
        // opId 带自增序号：本方法可被多个 Task.Run 并发调用（30s 周期 + 对话/送礼/任务完成事件），
        // 固定 opId 会让并发批互相覆盖、先结束的把仍在跑的记录 End 掉——停滞检测就漏了
        var batchOpId = $"decision-batch#{Interlocked.Increment(ref _decisionBatchSequence)}";
        StuckOperationTracker.Begin(batchOpId, $"{agents.Count} agents");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var agent in agents)
            {
                try
                {
                    // decision 管道已删除：TS 端无路由，60s 阻塞导致线程池 starvation。
                    // 唯一路径：RuleBasedDecisionEngine（原 catch 兜底逻辑提升为常态路径）。
                    var ruleResult = DecideByRules(agent);
                    ApplyRuleDecision(agent, ruleResult);
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Decision error for {agent.NpcName}: {ex.Message}", LogLevel.Error);
                    _debugLogger?.LogError(ex, $"Decision for {agent.NpcName}");
                }
            }
        }
        finally
        {
            StuckOperationTracker.End(batchOpId);
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            _monitor.Log($"[Decision] batch: {agents.Count} agents in {elapsedMs}ms",
                elapsedMs > 1000 ? LogLevel.Warn : LogLevel.Debug);
        }

        return Task.CompletedTask;
    }

    private void ApplyControllerToNpc(AgentInstance agent, AgentState state)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        if (npc == null)
        {
            return;
        }

        switch (state)
        {
            case AgentState.IDLE:
                // 不再每 tick 强制 Stop：IDLE 的状态 Update 会驱动待机漫步
                // （IdleWanderHandler，见 ServiceInitializer 的 handler action 注册）。
                // 旧的 "idle-halt every tick" 会让任何漫步移动在下一 tick 立即被清掉，
                // 且 StateMachine.Update 从未被调用——这是“NPC 死板站桩”的根因。
                agent.StateMachine.Update(npc, agent, _tickCounter);
                break;

            case AgentState.FIGHT:
                if (_config!.EnableCombatAssist)
                {
                    agent.StateMachine.Update(npc, agent, _tickCounter);
                }

                break;

            case AgentState.FARM:
                if (_config!.EnableFarmingAssist)
                {
                    agent.StateMachine.Update(npc, agent, _tickCounter);
                }

                break;

            case AgentState.MINE:
                if (_config!.EnableMiningAssist)
                {
                    agent.StateMachine.Update(npc, agent, _tickCounter);
                }

                break;

            case AgentState.FORAGE:
                if (_config!.EnableForagingAssist)
                {
                    agent.StateMachine.Update(npc, agent, _tickCounter);
                }

                break;

            case AgentState.FOLLOW:
            case AgentState.TALK:
                npc.followSchedule = false;
                npc.ignoreScheduleToday = true;
                // controller 生命周期由 MovementService 统一管理，不再每 tick 清除

                // FOLLOW 状态：如果 NPC 不在玩家地图，启动跨地图旅行
                // D2 守卫：事件/节日期间禁止每 tick 自动旅行，堵 OnPlayerWarped 守卫的旁路
                if (state == AgentState.FOLLOW && npc.currentLocation != Game1.player.currentLocation
                                               && !GameEventGuard.IsEventOrFestivalActive)
                {
                    if (_agentNavigator != null && !_agentNavigator.IsTravelling(npc.Name))
                    {
                        var started = false;
                        _agentNavigator.NavigateToTaskLocation(
                            npc,
                            new[] { Game1.player.currentLocation?.NameOrUniqueName ?? "Farm" },
                            _tickCounter,
                            out started);
                        if (started)
                        {
                            _monitor.Log(
                                $"[Follow] {agent.NpcName}: auto cross-map travel to {Game1.player.currentLocation?.NameOrUniqueName}",
                                LogLevel.Debug);
                        }
                    }
                }

                agent.StateMachine.Update(npc, agent, _tickCounter);
                break;

            // 阶段 2：Goal 执行态 / 汇报态（零 LLM 循环，终止判定全在 C#）
            case AgentState.EXECUTING_GOAL:
            case AgentState.TRAVELING_TO_REPORT:
                npc.followSchedule = false;
                npc.ignoreScheduleToday = true;
                agent.StateMachine.Update(npc, agent, _tickCounter);
                break;

            default:
                _movementService?.Stop(npc, "unknown-state-halt");
                break;
        }
    }

    private void HandleDeadNpc(NPC npc)
    {
        _movementService?.Stop(npc, "dead-npc");
        npc.Halt();
        npc.followSchedule = false;
        npc.ignoreScheduleToday = true;
        if (npc.currentLocation != null && npc.currentLocation.characters.Contains(npc))
        {
            npc.Position = new Vector2(-10000, -10000);
        }
    }

    private AgentState? CheckEmergencyState(NPC npc, AgentState currentState, AgentHealth health, ModConfig config,
        string npcName)
    {
        if (health.IsDead)
        {
            return null;
        }

        if (health.HealthPercent < config.EmergencyHealthThreshold && currentState != AgentState.FOLLOW)
        {
            return AgentState.FOLLOW;
        }

        // Issue 6: Fight exit cooldown — don't emergency-re-enter FIGHT within configured ticks
        // after FightHandler exited. Prevents oscillation when monsters leave scan range briefly.
        if (currentState != AgentState.FIGHT
            && _lastFightExitTick.TryGetValue(npcName, out var exitTick)
            && _tickCounter - exitTick < _config!.FightExitCooldownTicks)
        {
            return null;
        }

        if (currentState != AgentState.FIGHT)
        {
            var monster = FightHandler.FindNearestMonster(npc);
            if (monster != null && Vector2.Distance(npc.Tile, monster.Tile) <= config.EmergencyMonsterDistance)
            {
                return AgentState.FIGHT;
            }
        }

        return null;
    }

    /// <summary>
    ///     Spark 激活回调：玩家与非 Agent 村民对话时低概率激活为 Agent（设计文档 §4.2.3）。
    ///     命中时 ForceAllocate + CreateAgent + InjectBio + WireAgentStateEvents。
    ///     返回 true 表示 NPC 已成功激活为 Agent，调用方应重新判定 isAgent。
    /// </summary>
    private bool TrySparkActivate(string npcName)
    {
        if (_sparkAllocator == null || _agentService == null || _config == null)
        {
            return false;
        }

        if (_agentService.AllocationManager.CurrentAgentCount >= _config.MaxAgentNpcs)
        {
            return false;
        }

        var dateKey = $"{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";
        if (!_sparkAllocator.TrySpark(
                npcName, dateKey,
                _agentService.AllocationManager.CurrentAgentCount,
                _config.MinAgentNpcs, _config.NormalAgentNpcs, _config.MaxAgentNpcs))
        {
            return false;
        }

        if (!_agentService.AllocationManager.ForceAllocate(npcName))
        {
            _monitor?.Log($"[Spark] {npcName}: spark hit but ForceAllocate failed", LogLevel.Warn);
            return false;
        }

        var agent = _agentService.CreateAgent(npcName);
        if (agent == null)
        {
            _monitor?.Log($"[Spark] {npcName}: ForceAllocate succeeded but CreateAgent failed", LogLevel.Warn);
            return false;
        }

        _bioLoader?.InjectBio(agent.Brain);
        WireAgentStateEvents(agent);
        _monitor?.Log(
            $"[Spark] {npcName} spark-activated as Agent (count={_agentService.AllocationManager.CurrentAgentCount})",
            LogLevel.Info);
        return true;
    }

    private void WireAgentStateEvents(AgentInstance agent)
    {
        // Issue 10: 情绪变更视觉反馈 — 播放 emote 和头顶文字
        agent.Brain.OnEmotionChanged += (npcName, previousEmotion, newEmotion) =>
        {
            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
            {
                return;
            }

            var emoteId = newEmotion.GetEmoteId();
            if (emoteId.HasValue)
            {
                npc.doEmote(emoteId.Value);
            }

            // 简短中文情绪标签
            var emotionLabel = newEmotion switch
            {
                NpcEmotion.Happy => "开心~",
                NpcEmotion.Sad => "唉...",
                NpcEmotion.Angry => "哼！",
                NpcEmotion.Worried => "！",
                NpcEmotion.Excited => "太好了！",
                NpcEmotion.Tired => "好累...",
                NpcEmotion.Grateful => "谢谢！",
                _ => null
            };
            if (emotionLabel != null)
            {
                npc.showTextAboveHead(emotionLabel, duration: 2000);
            }
        };

        // emotion_sync / memory_sync 管道已删除：TS 端无路由，fire-and-forget 静默丢弃。

        agent.StateMachine.OnStateChanged += (_, args) =>
        {
            // P1-7: 同步状态到 AllocationManager，使 HUD 能显示真实状态而非永远 IDLE
            _agentService?.AllocationManager?.SetAgentState(agent.NpcName, args.NewState);

            if (args.NewState == AgentState.FOLLOW || args.PreviousState == AgentState.FOLLOW)
            {
                var npc = Game1.getCharacterFromName(agent.NpcName);
                var loc = npc?.currentLocation?.Name ?? "?";
                var playerLoc = Game1.player?.currentLocation?.Name ?? "?";
                _monitor.Log(
                    $"[Follow] {agent.NpcName}: {args.PreviousState} → {args.NewState} (NPC at {loc}, Player at {playerLoc})",
                    LogLevel.Debug);

                if (args.NewState == AgentState.FOLLOW)
                {
                    _ = _dialogueFollowedAgents.Add(agent.NpcName);
                }
                else if (args.PreviousState == AgentState.FOLLOW)
                {
                    _ = _dialogueFollowedAgents.Remove(agent.NpcName);
                }
            }

            if (args.NewState == AgentState.IDLE && IsActiveTaskState(args.PreviousState))
            {
                agent.Brain.SyncEmotion(NpcEmotion.Excited, 0.7f, "TaskCompleted");
                var taskName = args.PreviousState.ToString().ToLower();
                if (args.PreviousState != AgentState.FIGHT)
                {
                    agent.Brain.AddMemory($"I just finished {taskName} with the player nearby.", 2.0,
                        MemoryEntryType.Task);
                }

                _monitor.Log(
                    $"[Emotion] {agent.NpcName}: TaskCompleted ({args.PreviousState}) → {agent.Brain.Emotion}");

                // 协作行为完成时增加好感度
                var friendshipDelta = args.PreviousState switch
                {
                    AgentState.FIGHT => 10,
                    AgentState.FARM => 5,
                    AgentState.FORAGE => 3,
                    AgentState.MINE => 5,
                    // E6: FOLLOW→IDLE 通常是切图被动解除（玩家走入新地图触发 NPC 被踢出 FOLLOW），
                    // 不是真正的"跟随任务完成"。原 +2 会让玩家每次切图都给 NPC 加好感，是 bug。
                    // 真正的跟随奖励应由 LLM 通过 dialogue_response.friendshipDelta（方案B）评估。
                    AgentState.FOLLOW => 0,
                    AgentState.TALK => 3,
                    _ => 0
                };
                if (friendshipDelta > 0 && Game1.player is not null &&
                    Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fd))
                {
                    if (_friendshipSystem != null)
                    {
                        var fsContext = new FriendshipChangeContext
                        {
                            NpcName = agent.NpcName,
                            InteractionType = args.PreviousState switch
                            {
                                AgentState.FIGHT => InteractionType.Combat,
                                AgentState.FARM => InteractionType.Farming,
                                AgentState.MINE => InteractionType.Mining,
                                _ => InteractionType.Conversation
                            },
                            CurrentFriendshipPoints = fd.Points,
                            MaxFriendshipPoints = 2500,
                            CurrentDateKey = $"{Game1.currentSeason}_{Game1.dayOfMonth}_Year{Game1.year}"
                        };
                        var fsResult = _friendshipSystem.ApplyDirectChange(fsContext, friendshipDelta,
                            $"TaskCompleted:{args.PreviousState}");
                        fd.Points = Math.Clamp(fsResult.NewPoints, 0, 2500);
                        _monitor.Log(
                            $"[Friendship] {agent.NpcName}: +{friendshipDelta} ({args.PreviousState} completed) → {fd.Points}");
                    }
                    else
                    {
                        fd.Points = Math.Min(fd.Points + friendshipDelta, 2500);
                        _monitor.Log(
                            $"[Friendship] {agent.NpcName}: +{friendshipDelta} ({args.PreviousState} completed) → {fd.Points}");
                    }
                }
            }

            if (args.NewState == AgentState.IDLE && args.PreviousState == AgentState.FIGHT)
            {
                agent.Brain.SyncEmotion(NpcEmotion.Tired, 0.5f, "FoughtMonsters");
                agent.Brain.AddMemory("I was just fighting monsters. It was exhausting.", 3.0, MemoryEntryType.Combat);
                _monitor.Log($"[Emotion] {agent.NpcName}: FoughtMonsters → {agent.Brain.Emotion}");

                // Issue 6: Fight exit cooldown — prevent immediate re-entry into FIGHT
                // Monsters may still be "nearby" in scan results but handler already found none reachable
                _lastFightExitTick[agent.NpcName] = _tickCounter;
            }

            if (args.NewState == AgentState.IDLE)
            {
                if (_lastTaskCompleteDecisionTick.TryGetValue(agent.NpcName, out var lastTick)
                    && _tickCounter - lastTick < _config!.TaskCompleteDecisionCooldownTicks)
                {
                    _monitor.Log(
                        $"[DecisionTrigger] Skipped task-complete for {agent.NpcName} — cooldown active ({_tickCounter - lastTick}/{_config.TaskCompleteDecisionCooldownTicks} ticks)");
                    return;
                }

                _pendingTaskCompleteDecisions ??= new List<AgentInstance>();
                if (!_pendingTaskCompleteDecisions.Contains(agent))
                {
                    _pendingTaskCompleteDecisions.Add(agent);
                    _lastTaskCompleteDecisionTick[agent.NpcName] = _tickCounter;
                }

                agent.LastDecisionState = args.PreviousState.ToString();
                agent.LastDecisionReason = $"Just completed {args.PreviousState.ToString().ToLower()}";
                _monitor.Log(
                    $"[DecisionTrigger] Task complete for {agent.NpcName} ({args.PreviousState}→IDLE), queued for re-evaluation");
            }
        };

        // 联机广播：状态变化立即同步给 Farmhand（独立订阅，与上方业务逻辑解耦）
        // 注：本订阅在 WireAgentStateEvents 中完成，因为 agent 是动态创建的
        // （OnSaveLoaded / OnDayStarted），Initialize() 执行时尚无 agent 可遍历。
        // TODO: 若未来支持运行时销毁 agent，需在销毁时取消订阅避免泄漏。
        agent.StateMachine.OnStateChanged += (_, _) =>
        {
            try
            {
                _multiplayerBroadcaster?.BroadcastImmediateState(agent.NpcName);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Broadcaster] OnStateChanged broadcast failed for {agent.NpcName}: {ex}",
                    LogLevel.Error);
            }
        };

        // 联机广播：死亡立即同步（OnDeath 由 AgentHealth 内部 lock 保证只触发一次）
        agent.Health.OnDeath += _ =>
        {
            try
            {
                _multiplayerBroadcaster?.BroadcastImmediateState(agent.NpcName);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Broadcaster] OnDeath broadcast failed for {agent.NpcName}: {ex}", LogLevel.Error);
            }
        };

        // 联机广播：血量变化立即同步（非致死路径，致死由 OnDeath 处理避免双发）
        agent.Health.OnHealthChanged += (_, _, _) =>
        {
            try
            {
                _multiplayerBroadcaster?.BroadcastImmediateState(agent.NpcName);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Broadcaster] OnHealthChanged broadcast failed for {agent.NpcName}: {ex}",
                    LogLevel.Error);
            }
        };
    }

    private async Task ConnectAgentServerAsync()
    {
        if (_agentServerProvider == null)
        {
            return;
        }

        if (_serverProcessManager != null)
        {
            var serverReady = await _serverProcessManager.EnsureRunningAsync().ConfigureAwait(false);
            if (!serverReady)
            {
                _monitor.Log("Agent Server auto-start failed or disabled. Will retry via WebSocket auto-reconnect.",
                    LogLevel.Warn);
            }
        }

        var maxRetries = 10;
        var delayMs = 1000;

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                await _agentServerProvider.ConnectAsync().ConfigureAwait(false);
                _monitor.Log("Agent Server WebSocket connected.", LogLevel.Info);
                return;
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"Agent Server WebSocket connection attempt {i + 1}/{maxRetries} failed: {ex.Message}",
                    LogLevel.Warn);
            }
            catch (WebSocketException ex)
            {
                _monitor.Log($"Agent Server WebSocket connection attempt {i + 1}/{maxRetries} failed: {ex.Message}",
                    LogLevel.Warn);
            }
            catch (AggregateException ex)
            {
                _monitor.Log(
                    $"Agent Server WebSocket connection attempt {i + 1}/{maxRetries} failed: {ex.InnerException?.Message ?? ex.Message}",
                    LogLevel.Warn);
            }

            if (i < maxRetries - 1)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 10000);
            }
        }

        _monitor.Log(
            $"Agent Server WebSocket connection failed after {maxRetries} attempts. Will retry via auto-reconnect.",
            LogLevel.Error);
    }

    /// <summary>获取当前友谊点数（直接从游戏数据读取，不依赖死代码 Provider）</summary>
    private static int GetCurrentFriendshipPoints(string npcName) =>
        Game1.player?.friendshipData.TryGetValue(npcName, out var fd) == true ? fd.Points : 0;

    /// <summary>Issue 13: 获取NPC可见性格特质文本（任务1.6：按友谊阶段过滤）</summary>
    private static string GetVisibleTraits(AgentInstance agent, int friendshipPoints = -1)
    {
        if (agent.Brain?.Bio?.Traits == null || agent.Brain.Bio.Traits.Count == 0)
        {
            return "";
        }

        var points = friendshipPoints >= 0 ? friendshipPoints : GetCurrentFriendshipPoints(agent.NpcName);
        var phase = FriendshipPhaseHelper.FromFriendship(points);
        return TraitPhaseMapper.GetVisibleTraitNames(agent.Brain.Bio.Traits, phase);
    }

    /// <summary>任务1.7：获取按友谊阶段分层的传记摘要</summary>
    private static string GetPhaseAwareBiography(AgentInstance agent, int friendshipPoints)
    {
        if (agent.Brain?.Bio == null)
        {
            return "";
        }

        var phase = FriendshipPhaseHelper.FromFriendship(friendshipPoints);
        return agent.Brain.Bio.GetPhaseSummary(phase) ?? "";
    }

    /// <summary>Issue 13: 获取好感度阶段中文标签</summary>
    private static string GetFriendshipPhaseLabel(int friendshipPoints) =>
        FriendshipPhaseHelper.ToChineseLabel(FriendshipPhaseHelper.FromFriendship(friendshipPoints));

    /// <summary>Issue 13: 获取重要事项记忆文本</summary>
    private static string GetSignificantMemoriesText(AgentInstance agent)
    {
        var significant = agent.Brain?.SignificantMemories
            .Take(5)
            .Select(m => m.Text);
        var result = significant != null && significant.Any() ? string.Join("\n", significant) : "";
        return result;
    }

    /// <summary>任务1.1：获取背包物品摘要（含物品名称，让 LLM 知道背包里有什么）</summary>
    private static string GetInventorySummary(AgentInstance agent)
    {
        if (agent.Inventory == null)
        {
            return "";
        }

        var count = agent.Inventory.Count;
        var isFull = agent.Inventory.IsFull;
        var items = agent.Inventory.GetAllItems()
            .Where(i => i != null)
            .Select(i => i!.DisplayName)
            .Take(20);
        var itemList = string.Join("、", items);
        var header = isFull ? $"背包已满({count}件)" : $"{count}件物品";
        return string.IsNullOrEmpty(itemList) ? header : $"{header}: {itemList}";
    }

    private SaveData BuildStructuredSaveData()
    {
        var data = new SaveData
        {
            Version = SaveDataManager.CurrentVersion,
            AgentStates = new Dictionary<string, AgentStateData>(StringComparer.OrdinalIgnoreCase),
            Memories = new Dictionary<string, MemoryData>(StringComparer.OrdinalIgnoreCase),
            FriendshipHistory = new Dictionary<string, FriendshipHistoryData>(StringComparer.OrdinalIgnoreCase),
            Allocations = new AllocationData
            {
                AgentNpcNames = _agentService!.AllocationManager.GetAllocatedNames().ToList(),
                ManualOverrides = _agentService!.AllocationManager.GetAllManualOverrides()
            },
            Statistics = new StatisticsData
            {
                SessionStartTime = DateTime.UtcNow,
                TotalLlmCalls = _agentService.PerformanceMonitor.GetMetricsReport().TotalLlmCalls,
                TotalDecisions = (int)_agentService.PerformanceMonitor.GetMetricsReport().TotalStateTransitions
            }
        };

        foreach (var agent in _agentService.GetAllAgents())
        {
            var npc = Game1.getCharacterFromName(agent.NpcName);
            data.AgentStates[agent.NpcName] = new AgentStateData
            {
                NpcName = agent.NpcName,
                CurrentState = agent.StateMachine.CurrentStateFlag,
                CurrentLocation = npc?.currentLocation?.Name ?? "Unknown",
                PositionX = npc?.Tile.X ?? 0,
                PositionY = npc?.Tile.Y ?? 0,
                CurrentTarget = "",
                StateStartTime = (DateTime.UtcNow - agent.StateMachine.StateDuration).Ticks,
                CurrentGoal = agent.Brain.CurrentGoal,
                Health = agent.Health.Health,
                Inventory = agent.Inventory.GetAllItems()
                    .Where(i => i != null)
                    .Select(i => $"{i!.QualifiedItemId ?? i.ItemId}:{i.Stack}")
                    .ToList(),
                Money = agent.Inventory.Money,
                FarmerNickname = agent.Brain.FarmerNickname,
                Emotion = agent.Brain.Emotion.ToString(),
                EmotionData = new EmotionSaveData
                {
                    Emotion = agent.Brain.Emotion.ToString(),
                    Intensity = agent.Brain.CurrentEmotionState.Intensity,
                    Source = agent.Brain.CurrentEmotionState.Source ?? ""
                }
            };

            var memoryData = new MemoryData { NpcName = agent.NpcName };
            foreach (var mem in agent.Brain.ShortTermMemories.TakeLast(20))
            {
                memoryData.ShortTermMemories.Add(new StructuredMemoryEntry
                {
                    Text = mem.Text,
                    Importance = mem.Importance,
                    EntryType = mem.EntryType.ToString(),
                    Timestamp = mem.Timestamp.Ticks
                });
            }

            foreach (var mem in agent.Brain.LongTermMemories)
            {
                memoryData.LongTermMemories.Add(new StructuredMemoryEntry
                {
                    Text = mem.Text,
                    Importance = mem.Importance,
                    EntryType = mem.EntryType.ToString(),
                    Timestamp = mem.Timestamp.Ticks
                });
            }

            data.Memories[agent.NpcName] = memoryData;

            if (_friendshipSystem != null &&
                Game1.player?.friendshipData.TryGetValue(agent.NpcName, out var fd) == true)
            {
                var historyRecords = _friendshipSystem.GetHistory(agent.NpcName);
                data.FriendshipHistory[agent.NpcName] = new FriendshipHistoryData
                {
                    NpcName = agent.NpcName,
                    CurrentPoints = fd.Points,
                    Changes = historyRecords.Select(r => new FriendshipChangeData
                    {
                        Timestamp = r.Timestamp,
                        DateKey = r.DateKey,
                        InteractionType = r.InteractionType.ToString(),
                        ChangeAmount = r.ChangeAmount,
                        Reason = r.Reason
                    }).ToList()
                };
            }
        }

        return data;
    }

    private static AgentState ParseAgentState(string state, IMonitor? monitor = null, string? npcName = null)
    {
        if (Enum.TryParse<AgentState>(state, true, out var result))
        {
            return result;
        }

        monitor?.Log($"[Decision] {npcName ?? "unknown"}: LLM returned unparseable state '{state}', defaulting to IDLE",
            LogLevel.Warn);
        return AgentState.IDLE;
    }

    private double GetMinimumStateDuration(AgentState state)
    {
        var key = state.ToString();
        return _config!.MinimumStateDuration.TryGetValue(key, out var duration) ? duration : 0.0;
    }

    private static bool IsActiveTaskState(AgentState state) => state switch
    {
        AgentState.FARM or AgentState.MINE or AgentState.FORAGE or AgentState.FIGHT or AgentState.FOLLOW => true,
        AgentState.IDLE or AgentState.TALK => false,
        _ => false
    };

    private static bool IsTransitionValid(NPC npc, AgentState targetState, IMonitor monitor) =>
        StateFeasibility.IsValid(npc, targetState, monitor);

    private static void InjectBlockedStates(DecisionContext context, string npcName)
    {
        var npc = Game1.getCharacterFromName(npcName);
        if (npc != null)
        {
            context.BlockedStates = StateFeasibility.GetBlockedStatesList(npc);
        }
    }

    /// <summary>
    ///     P0-3: 构建 RuleDecisionContext 并调用 RuleBasedDecisionEngine 生成降级决策。
    ///     在 CircuitBreaker OPEN 时使用，避免 NPC 因 LLM 不可用而植物化。
    /// </summary>
    private RuleDecisionResult DecideByRules(AgentInstance agent)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        var location = npc?.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "Town";
        var isRaining = Game1.isRaining || Game1.isSnowing;
        var playerDistance = npc != null
            ? Vector2.Distance(npc.Tile, Game1.player.Tile)
            : float.MaxValue;

        var nearbyObjects = new List<string>();
        if (!string.IsNullOrEmpty(agent.LastDecisionReason))
        {
            nearbyObjects.Add(agent.LastDecisionReason);
        }

        // 复用 Handler 的环境扫描，提取 nearby_objects 文本作为规则输入
        if (npc != null)
        {
            if (FightHandler.ScanEnvironment(npc) is { } fightScan)
            {
                nearbyObjects.Add(fightScan);
            }

            if (FarmHandler.ScanEnvironment(npc) is { } farmScan)
            {
                nearbyObjects.Add(farmScan);
            }

            if (MineHandler.ScanEnvironment(npc) is { } mineScan)
            {
                nearbyObjects.Add(mineScan);
            }

            if (ForageHandler.ScanEnvironment(npc) is { } forageScan)
            {
                nearbyObjects.Add(forageScan);
            }
        }

        Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fsData);
        var consecutiveIdle = _agentTickLoop?.GetConsecutiveIdleCount(agent.NpcName) ?? 0;

        var ruleContext = new RuleDecisionContext
        {
            Location = location,
            Health = agent.Health.Health,
            MaxHealth = agent.Health.MaxHealth,
            Friendship = fsData?.Points ?? 0,
            PlayerDistance = playerDistance,
            IsRaining = isRaining,
            NearbyObjects = nearbyObjects,
            Emotion = agent.Brain.CurrentEmotionState,
            ConsecutiveIdleCount = consecutiveIdle,
            CurrentState = agent.StateMachine.CurrentStateFlag,
            FollowProbability = _config?.ProactiveFollowProbability ?? 1.0f
        };

        return RuleBasedDecisionEngine.Decide(ruleContext);
    }

    /// <summary>
    ///     P0-3: 应用规则引擎决策到 agent，与 LLM 决策路径保持一致：
    ///     同步 Brain 决策记录、按需入队 _pendingDecisions、记录调试日志。
    /// </summary>
    private void ApplyRuleDecision(AgentInstance agent, RuleDecisionResult result)
    {
        agent.Brain.SyncDecision(result.TargetState.ToString(), result.Reason);

        if (result.TargetState != agent.StateMachine.CurrentStateFlag)
        {
            lock (_pendingDecisionsLock)
            {
                _pendingDecisions.Enqueue((agent, result.TargetState, result.Reason, result.Thought, DateTime.UtcNow));
            }
        }
        else
        {
            _monitor.Log(
                $"Same-state rule decision for {agent.NpcName}: staying in {result.TargetState} | {result.Reason}");
        }

        // ApplyRuleDecision 仅在 MakeDecisionsAsync 内（_agentService 已 null 检查）调用，
        // 但编译器无法跨方法流分析，这里显式检查以消除 CS8602 警告。
        if (_agentService == null)
        {
            return;
        }

        _agentService.RecordDecision(agent.NpcName);
        _debugLogger?.LogDecision(
            agent.NpcName,
            agent.StateMachine.CurrentStateFlag,
            result.TargetState,
            $"[RULE] {result.Reason}");
    }

    private void PrintDebugStats()
    {
        if (_agentService == null)
        {
            return;
        }

        _monitor.Log("=== ValleyAgent Debug Stats ===", LogLevel.Info);
        _monitor.Log($"Active Agents: {_agentService.ActiveAgentCount}", LogLevel.Info);
        _monitor.Log($"Circuit Breaker: {_agentService.CircuitBreaker.CurrentState}", LogLevel.Info);

        foreach (var agent in _agentService.GetAllAgents())
        {
            _monitor.Log(
                $"  [{agent.NpcName}] State: {agent.StateMachine.CurrentStateFlag}, Duration: {agent.StateMachine.StateDuration.TotalSeconds:F1}s",
                LogLevel.Info);
        }

        _monitor.Log("===============================", LogLevel.Info);
    }
}