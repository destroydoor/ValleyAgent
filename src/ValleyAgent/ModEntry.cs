using System;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Api;
using ValleyAgent.Chat;
using ValleyAgent.Config;
using ValleyAgent.Friendship;
using ValleyAgent.Infrastructure;
using ValleyAgent.Initialization;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Navigation;
using ValleyAgent.Patches;
using ValleyAgent.Services;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;

namespace ValleyAgent;

public class ModEntry : Mod
{
    private ServiceContainer? _container;

    // GameLaunched 预启动的服务器进程管理器。与 _serverProcessManager 指向同一实例
    // （SaveLoaded 时 ServiceInitializer 复用），避免重复创建导致端口占用误杀重启。
    // Dispose 中调用 StopServer() 停止（ServerProcessManager.Dispose 由 _serverProcessManager 统一触发）。
#pragma warning disable CA2213
    private ServerProcessManager? _earlyServerManager;
#pragma warning restore CA2213
    private EventHandlerInitializer? _eventHandlerInitializer;
    private FarmhandDialogueTransport? _farmhandDialogueTransport;

    // 2026-09-11 生产化看门狗：主线程停滞 / 后台操作停滞 dump 仪器（纯观测，不改行为）。
    // Entry 即布防，进程生命周期即其生命周期（见 Entry 内注释）。
    private MainThreadWatchdog? _watchdog;

    private FarmhandGiftTransport? _farmhandGiftTransport;

    // 是否已完成首次初始化（SaveLoaded 触发后置 true，ReturnedToTitle 后重置为 false）。
    // 防止 SaveLoaded 重复触发导致 InitializeHostMode/ThinClient 被多次执行。
    private bool _initialized;

    // 运行时模式。在 SaveLoaded 时机判定（Entry 时机 Context.IsMultiplayer 还未确定，
    // farmhand 会被误判为 Host）。默认 Host 以防 DetermineRuntimeMode 抛异常时不会误入 ThinClient/Inert 分支。
    private AgentRuntimeMode _mode = AgentRuntimeMode.Host;

    // 联机事件路由器：Host 模式注入 broadcaster+handlers；ThinClient 模式注入 renderer+transports。
    // 单机模式下保持 null，不订阅 Multiplayer 事件。
    private MultiplayerEventRouter? _multiplayerRouter;

    // ThinClient 模式独有依赖：远程渲染器 + Farmhand 端传输层。
    private AgentRemoteRenderer? _remoteRenderer;

#pragma warning disable CA2213
    private ServerProcessManager? _serverProcessManager;
#pragma warning restore CA2213

    private IValleyAgentApi? _valleyApi;
    public static ModEntry? Instance { get; private set; }

    public IValleyAgentApi? API
    {
        get
        {
            if (_valleyApi == null && _container != null)
            {
                var agentService = _container.GetService<AgentService>();
                var movementService = _container.GetService<IMovementService>();
                var friendshipSystem = _container.GetService<FriendshipSystem>();
                _valleyApi = new ValleyAgentApi(agentService, agentService?.AllocationManager, movementService,
                    npcName =>
                    {
                        if (agentService == null)
                        {
                            return false;
                        }

                        if (!agentService.TryGetAgent(npcName, out var agent) || agent == null)
                        {
                            return false;
                        }

                        agentService.TriggerDecisionReEvaluation(agent);
                        return true;
                    },
                    friendshipSystem);
            }

            return _valleyApi;
        }
    }

    public IMovementService? MovementService
    {
        get => _container?.GetService<IMovementService>();
    }

    /// <summary>
    ///     测试侧（ValleyAgent.TestMod）通过 InternalsVisibleTo 访问运行时服务容器，
    ///     用于获取 AgentNavigator/CommandExecutor 等内部服务实例做集成测试。
    /// </summary>
    internal IServiceContainer? Container
    {
        get => _container;
    }

    public override object? GetApi() =>
        _mode == AgentRuntimeMode.Inert ? null : API ?? new ValleyAgentApi(null, null, null, _ => false);

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        ValleyAgentApi.Monitor = Monitor;

        // 运行时错误落盘到 Mods 目录（ValleyAgent-error.log / ValleyAgent-server.log），
        // 分发给朋友玩时对方直接把这两个文件发回来即可排障，无需翻 SMAPI ErrorLogs。
        ModErrorLog.Initialize(helper.DirectoryPath);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ModErrorLog.LogError(
                "Unhandled",
                $"Unhandled exception (Terminating={args.IsTerminating})",
                args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ModErrorLog.LogError("UnobservedTask", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // 2026-09-11 生产化仪器：3 人联机主机无报错卡死（2026-09-10 结案未定罪）发行包零现场证据——
        // 看门狗必须在 Entry 布防而非按模式初始化，Host/ThinClient/Inert 全覆盖
        // （Inert 也能采集"原版也崩"的对照组，用于排除/坐实本 mod 嫌疑）。纯观测，不改任何行为。
        _watchdog = MainThreadWatchdog.Start(helper.DirectoryPath, Monitor);
        // lambda 永不退订：进程生命周期即看门狗生命周期（后台线程 IsBackground，
        // 游戏退出时随进程终止；Dispose 不需要也不应该停掉它——退订反而丢最后的现场）
        helper.Events.GameLoop.UpdateTicked += (_, _) => _watchdog?.Beat();

        // 关键修复：mode 判定推迟到 SaveLoaded。
        // Entry 时机 SMAPI 的 Context.IsMultiplayer 还是 false（即使对 farmhand），
        // 此时判定会让 farmhand 误入 Host 模式，启动本地 Agent 服务器，绕过权威服务器架构。
        // SaveLoaded 时 Context.IsMultiplayer/IsMainPlayer 已确定，能正确分流 Host/ThinClient/Inert。
        //
        // 服务器进程生命周期绑定游戏（而非存档）：
        //   - GameLaunched  → 预启动 TS Agent Server（cmd 窗口随游戏启动即弹出，早于存档加载）
        //   - SaveLoaded    → ServiceInitializer 复用同一实例，EnsureRunningAsync 幂等
        //   - ThinClient/Inert 判定 → 停止预启动服务器（farmhand 不跑本地服务器）
        //   - Dispose       → 停止服务器（游戏退出）
        helper.Events.GameLoop.GameLaunched += OnGameLaunchedEarly;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoadedInitialize;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    /// <summary>
    ///     GameLaunched（游戏加载完成、进入标题画面时）预启动 TS Agent Server。
    ///     与 SaveLoaded 后的 EnsureRunningAsync 幂等：同一 ServerProcessManager 实例，
    ///     启动后 EnsureRunningAsync 检测到端口已由本实例占用直接返回 true，不重启。
    ///     服务器是否真的需要由本 mod 管理，由 AutoStartServer 配置控制。
    /// </summary>
    private void OnGameLaunchedEarly(object? sender, GameLaunchedEventArgs e)
    {
        // GMCM 注册必须在 GameLaunched 中执行：GMCM 在标题屏就显示"配置"按钮，
        // 玩家在载入存档前就需要能修改 LLM 提供商 / API Key / 端口等设置。
        // 之前放在 EventHandlerInitializer.Initialize()（SaveLoaded 触发）会导致
        // 标题屏 ValleyAgent 设置项完全消失。
        try
        {
            var config = Helper.ReadConfig<ModConfig>();
            GMCMIntegration.RegisterIfAvailable(Helper, ModManifest, config);
            GMCMIntegration.OnConfigChanged += cfg =>
            {
                try
                {
                    Helper.WriteConfig(cfg);
                    Monitor.Log("ValleyAgent config updated via GMCM hot-reload.", LogLevel.Info);
                    SpeechDisplayRouter.ApplyConfig(cfg.ChatBubbleEnabled, cfg.LongTextIntervalMs);
                    NPCDialoguePatch.ClearCache();
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[GMCM] OnConfigChanged handler error: {ex}", LogLevel.Warn);
                }
            };
        }
        catch (Exception ex)
        {
            Monitor.Log($"[GameLaunched] GMCM registration error: {ex}", LogLevel.Warn);
        }

        try
        {
            var config = Helper.ReadConfig<ModConfig>();
            if (!config.AutoStartServer || !config.UseAgentServer)
            {
                return;
            }

            // 2026-08-16 联机测试：同机双实例下房客（VALLEY_TEST_INSTANCE=farmhand）在 SaveLoaded
            // 前也会走到这里——两实例都起 8765 服务器会触发 ServerProcessManager 端口互杀循环。
            // 房客最终判定 ThinClient（不需要本地服务器），这里直接跳过预启动。
            if (Environment.GetEnvironmentVariable("VALLEY_TEST_INSTANCE") == "farmhand")
            {
                Monitor.Log("[GameLaunched] VALLEY_TEST_INSTANCE=farmhand — skipping server pre-start (ThinClient uses host server)",
                    LogLevel.Info);
                return;
            }

            var manager = new ServerProcessManager(Monitor, Helper, config)
            {
                AutoStart = config.AutoStartServer,
                ConsoleWindow = config.ServerConsoleWindow,
                ServerExecutablePath = config.ServerExecutablePath,
                ServerDirectory = config.ServerDirectory,
                ServerPort = config.ServerPort,
                StartupTimeoutSeconds = config.ServerStartupTimeoutSeconds,
                MaxRestartAttempts = config.ServerMaxRestartAttempts
            };
            manager.LogCallback += msg => Monitor.Log($"[AgentServer] {msg}");
            _earlyServerManager = manager;

            // 启动放后台：不阻塞 GameLaunched 事件（SMAPI 事件处理器同步执行）。
            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await manager.StartServerAsync().ConfigureAwait(false);
                    Monitor.Log(
                        ok
                            ? "Agent Server pre-started on game launch."
                            : "Agent Server pre-start failed (will retry on save load).",
                        ok ? LogLevel.Info : LogLevel.Warn);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[GameLaunched] Agent Server pre-start error: {ex}", LogLevel.Error);
                }
            });
        }
        catch (Exception ex)
        {
            Monitor.Log($"[GameLaunched] Agent Server pre-start setup error: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     SaveLoaded 时机：判定 mode 并完成一次性初始化。
    ///     此时 SMAPI 的 Context.IsMultiplayer/IsMainPlayer 已确定，farmhand 能正确走 ThinClient 路径。
    /// </summary>
    private void OnSaveLoadedInitialize(object? sender, SaveLoadedEventArgs e)
    {
        // ThinClient 模式下，Router/Renderer/Transport 实例在 ReturnedToTitle 后保留，
        // 只需重新注入 Transport 到 Patches（ReturnedToTitle 时置空了）。
        // 这避免重复创建 Router 导致 SMAPI Multiplayer 事件被重复订阅。
        if (_initialized && _mode == AgentRuntimeMode.ThinClient)
        {
            if (_farmhandDialogueTransport != null)
            {
                DialogueBoxInputPatch.SetDialogueTransport(_farmhandDialogueTransport);
            }

            if (_farmhandGiftTransport != null)
            {
                NPCGiftPatch.SetGiftTransport(_farmhandGiftTransport);
            }

            Monitor.Log("[ThinClient] Transports re-injected after save load", LogLevel.Debug);
            return;
        }

        if (_initialized)
        {
            return;
        }

        try
        {
            _mode = DetermineRuntimeMode(Helper);

            switch (_mode)
            {
                case AgentRuntimeMode.Host:
                    InitializeHostMode(Helper);
                    // 关键：InitializeHostMode 在 SaveLoaded 触发期间才订阅 EventHandlerInitializer.OnSaveLoaded，
                    // 但 SMAPI 事件系统不会在当前触发周期执行新订阅的 handler。
                    // 手动同步触发一次 OnSaveLoaded 逻辑，让存档数据加载与 WebSocket 连接立即执行。
                    _eventHandlerInitializer?.TriggerSaveLoadedInitialization();
                    break;
                case AgentRuntimeMode.ThinClient:
                    // farmhand 不运行本地 TS Agent Server：GameLaunched 预启动的服务器在此停止，
                    // 避免绕过权威服务器架构（主机才是 LLM/决策的唯一执行方）。
                    StopEarlyServer();
                    InitializeThinClientMode(Helper);
                    break;
                case AgentRuntimeMode.Inert:
                    StopEarlyServer();
                    Monitor.Log(
                        "ValleyAgent is inactive on this farmhand (host does not have ValleyAgent or version incompatible). " +
                        "Original game experience preserved.",
                        LogLevel.Info);
                    break;
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            // issue #25（异常处理审计 PR3）：初始化失败不再 rethrow——异常逃进 SMAPI 存档加载
            // 流程会中断进档，此前只打 ex.Message 丢栈、且无任何降级标记（半初始化容器 +
            // 玩家侧"AI 全无"无解释）。现在落降级模式：原版体验保留 + 全栈留痕 + 玩家可读提示
            // + DegradedFeatures 登记（ValleyAgent_status / ValleyAgent_diag 可查）。
            // 降级为终态（_initialized 置 true 不重试）：在半初始化容器上重跑完整初始化
            // 会二次叠加部分事件订阅/静态注入，比降级更危险；重进存档或重启游戏可重试。
            _initialized = true;
            DegradedFeatures.Report(
                "mod-initialize",
                $"初始化失败，本会话降级为原版体验: {ex.GetType().Name}: {ex.Message}",
                ex);
            Monitor.Log(
                $"Failed to initialize ValleyAgent — entering degraded mode (vanilla experience preserved): {ex}",
                LogLevel.Error);
            ModErrorLog.LogError("Init", "OnSaveLoadedInitialize failed — degraded mode", ex);
            NotifyDegradedMode();
        }
    }

    /// <summary>
    ///     降级模式的玩家可读提示：聊天栏一行灰字（不弹窗、不打断进档流程）。
    ///     提示自身失败只 Trace 留痕——通知是锦上添花，不能反过来把进档流程再炸一次。
    /// </summary>
    private void NotifyDegradedMode()
    {
        try
        {
            Game1.chatBox?.addMessage(
                "ValleyAgent 部分功能未能启动，已回退原版体验（详见 ValleyAgent-error.log，ValleyAgent_status 可查降级项）",
                Microsoft.Xna.Framework.Color.Gray);
        }
        catch (Exception ex)
        {
            Monitor.Log($"[Init] degraded-mode notify failed: {ex}", LogLevel.Trace);
        }
    }

    /// <summary>
    ///     判定当前 Mod 运行时模式。在 SaveLoaded 时机调用（非 Entry 早期），决定后续初始化路径。
    ///     - 单机或联机主机 → Host
    ///     - 联机 farmhand 且主机装有兼容版本本 mod → ThinClient
    ///     - 联机 farmhand 但主机未装 mod/版本不兼容 → Inert
    /// </summary>
    private AgentRuntimeMode DetermineRuntimeMode(IModHelper helper)
    {
        // 测试自动化：通过环境变量强制指定运行时模式，使 farmhand 从标题菜单加入时即可走 ThinClient 路径。
        var testInstance = Environment.GetEnvironmentVariable("VALLEY_TEST_INSTANCE");
        if (string.Equals(testInstance, "farmhand", StringComparison.OrdinalIgnoreCase))
        {
            Monitor.Log("[DetermineRuntimeMode] VALLEY_TEST_INSTANCE=farmhand，强制进入 ThinClient 模式。", LogLevel.Info);
            return AgentRuntimeMode.ThinClient;
        }

        if (string.Equals(testInstance, "host", StringComparison.OrdinalIgnoreCase))
        {
            Monitor.Log("[DetermineRuntimeMode] VALLEY_TEST_INSTANCE=host，强制进入 Host 模式。", LogLevel.Info);
            return AgentRuntimeMode.Host;
        }

        // SaveLoaded 时机 Context.IsMultiplayer/IsMainPlayer 已确定。
        // 单机或联机主机：完整初始化。
        // 使用全限定名避免与 ValleyAgent.Context 命名空间冲突（参考 MultiplayerHelper.cs 同样的处理）。
        if (!StardewModdingAPI.Context.IsMultiplayer || StardewModdingAPI.Context.IsMainPlayer)
        {
            return AgentRuntimeMode.Host;
        }

        // 分屏副屏：在主机电脑上运行但不是主屏，主机模式已在主屏运行，
        // 副屏走 Inert 避免重复初始化 + 副屏无法访问主屏的 ServiceContainer。
        // MultiplayerHelper.IsSplitScreenFarmhand 同时排除联机远程 farmhand（其 IsOnHostComputer=false）。
        if (MultiplayerHelper.IsSplitScreenFarmhand)
        {
            Monitor.Log(
                "ValleyAgent is inactive on split-screen secondary screens (multiplayer not supported on split-screen). " +
                "Original game experience preserved.",
                LogLevel.Info);
            return AgentRuntimeMode.Inert;
        }

        // 联机远程 farmhand：检查主机是否装有兼容版本本 mod
        var hostMod = helper.Multiplayer
            .GetConnectedPlayer(Game1.MasterPlayer.UniqueMultiplayerID)
            ?.GetMod(ModManifest.UniqueID);

        if (hostMod == null)
        {
            Monitor.Log(
                "ValleyAgent disabled on farmhand: host does not have ValleyAgent installed (or mod not visible to farmhand).",
                LogLevel.Warn);
            return AgentRuntimeMode.Inert;
        }

        if (hostMod.Version.MajorVersion != ModManifest.Version.MajorVersion)
        {
            Monitor.Log(
                $"ValleyAgent disabled: host version {hostMod.Version} incompatible with local {ModManifest.Version}.",
                LogLevel.Warn);
            return AgentRuntimeMode.Inert;
        }

        return AgentRuntimeMode.ThinClient;
    }

    /// <summary>
    ///     Host 模式初始化：保留原全量初始化逻辑，新增联机 Router 接线。
    /// </summary>
    private void InitializeHostMode(IModHelper helper)
    {
        var container = new ServiceContainer();
        _container = container;

        var serviceInitializer = new ServiceInitializer(helper, Monitor, container, _earlyServerManager);
        serviceInitializer.Initialize();

        _serverProcessManager = container.GetService<ServerProcessManager>();

        _eventHandlerInitializer = new EventHandlerInitializer(helper, Monitor, container, ModManifest);
        _eventHandlerInitializer.Initialize();
        _eventHandlerInitializer.RegisterEventHandlers();
        _eventHandlerInitializer.RegisterHarmonyPatches(ModManifest);
        _eventHandlerInitializer.RegisterConsoleCommands();

        // Host 模式联机接线：创建 HostGiftTransport + HostRequestHandlers + Router 并注册。
        // broadcaster/serverProvider/friendshipSystem/commandExecutor 都由 ServiceInitializer 注册到 container。
        var broadcaster = container.GetService<AgentSyncBroadcaster>();
        var serverProvider = container.GetService<IAgentServerProvider>();
        var friendshipSystem = container.GetService<FriendshipSystem>();
        var commandExecutor = container.GetService<CommandExecutor>();
        if (broadcaster != null && serverProvider != null && friendshipSystem != null)
        {
            // gift_eval 管道已删除（TS 端无路由），HostGiftTransport 始终用本地兜底反应文本。
            var hostGiftTransport = new HostGiftTransport(friendshipSystem, Monitor);
            // C3 修复：注入 CommandExecutor，主机侧执行 farmhand 对话请求的 response.Actions
            var hostHandlers =
                new HostRequestHandlers(Monitor, serverProvider, broadcaster, hostGiftTransport, commandExecutor,
                    container.GetService<AgentService>());
            _multiplayerRouter = new MultiplayerEventRouter(helper, Monitor);
            _multiplayerRouter.InitializeHost(broadcaster, hostHandlers);
            _multiplayerRouter.Register();
        }
        else
        {
            Monitor.Log(
                "[Multiplayer] Host mode Router wiring skipped: missing broadcaster/serverProvider/friendshipSystem.",
                LogLevel.Warn);
        }

        Monitor.Log("ValleyAgent initialized successfully (Host mode)", LogLevel.Info);
    }

    /// <summary>
    ///     主线程泵的统一兜底执行器（死锁修复 2026-09-12）。
    ///     本 mod 的主线程泵是**串行的责任链**：后台线程只入队，回包渲染/ModMessage 发送/
    ///     好感落账全靠每 tick 排干。链上任一环抛异常，下游所有环当 tick 全部失效——
    ///     对房客而言下游正是它唯一的发送泵，请求发不出去 ⇒ 回包永远不来 ⇒
    ///     等待标记无人清除 ⇒ 对话框永久"等待回复"（联机专属硬死锁）。
    ///     所以每个泵必须独立兜底：单个泵失败只丢当 tick 的那一个动作，链不能断。
    /// </summary>
    /// <param name="name">泵名（留痕用，与 QueueTelemetry 的 queueId 口径一致）。</param>
    /// <param name="pump">泵体（内部自行 while(TryDequeue) 全量排水）。</param>
    internal void Pump(string name, Action pump)
    {
        try
        {
            pump();
        }
        catch (Exception ex)
        {
            Monitor.Log($"[Pump] '{name}' failed this tick (downstream pumps unaffected): {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     停止 GameLaunched 预启动的 TS Agent Server（farmhand/Inert 场景）。
    ///     与容器实例指向同一对象时，Dispose 会在游戏退出时统一清理。
    /// </summary>
    private void StopEarlyServer()
    {
        try
        {
            _earlyServerManager?.StopServer();
            _earlyServerManager = null;
        }
        catch (Exception ex)
        {
            Monitor.Log($"[StopEarlyServer] Failed to stop server: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     ThinClient 模式初始化：仅创建 Renderer + Transports + Router + 薄 Harmony 补丁。
    ///     不创建 AgentService/TS Agent Server/SaveData 等主机端依赖。
    /// </summary>
    private void InitializeThinClientMode(IModHelper helper)
    {
        var modId = ModManifest.UniqueID;

        // 1. 实例化 Renderer（位置插值 + 状态缓存）
        _remoteRenderer = new AgentRemoteRenderer(Monitor);

        // 2. 实例化 Transports（通过 ModMessage 把请求转发给主机）
        _farmhandDialogueTransport = new FarmhandDialogueTransport(helper, Monitor, modId);
        _farmhandGiftTransport = new FarmhandGiftTransport(helper, Monitor, modId);

        // 3. 实例化 Router 并初始化 ThinClient（注入 renderer + transports）
        _multiplayerRouter = new MultiplayerEventRouter(helper, Monitor);
        _multiplayerRouter.InitializeThinClient(_remoteRenderer, _farmhandDialogueTransport, _farmhandGiftTransport);
        _multiplayerRouter.Register();

        // 4. 注入 Transport 到 Patch（供 DialogueBoxInputPatch/NPCGiftPatch 在 farmhand 端使用）
        DialogueBoxInputPatch.SetDialogueTransport(_farmhandDialogueTransport);
        NPCGiftPatch.SetGiftTransport(_farmhandGiftTransport);

        // 4.5 初始化 farmhand 可用的公共 API（对话请求通过 FarmhandDialogueTransport 转发给主机）
        _valleyApi = new ValleyAgentApi(_farmhandDialogueTransport, Monitor);

        // 4.6 M3 多玩家化：聊天栏路由（房客形态）。
        // 此前 ChatBarRouter 只在主机 EventHandlerInitializer 初始化，房客侧 ChatBoxInputPatch
        // 捕获了聊天输入却无人路由 → 聊天栏发起的 NPC 对话静默丢失（"客户端没有主机的功能"）。
        // 房客注入 FarmhandDialogueTransport 版路由器：在场候选取主机广播的 Agent 名单，
        // 对话请求转发主机执行，回复在本地渲染（气泡/聊天栏）。
        ChatBoxInputPatch.Initialize(Monitor);
        ChatBarRouter.InitializeFarmhand(
            Monitor,
            _farmhandDialogueTransport!,
            _remoteRenderer!,
            Helper.ReadConfig<ModConfig>());

        // 5. 薄 Harmony 补丁：只打 ThinClient 需要的（NPCDialoguePatch/NPCGiftPatch/DialogueBoxInputPatch）。
        // 不打 SocialPagePatch（记忆宫殿是主机数据，farmhand 不可见）。
        // 必须初始化 patch 类静态字段，否则 Prefix 会因 null 守卫返回 true（让原版处理）。
        DialogueBoxInputPatch.Initialize(Monitor, null, null, null);
        NPCDialoguePatch.Monitor = Monitor;
        NPCDialoguePatch.RemoteRenderer = _remoteRenderer; // C1 修复：让 ThinClient 端能用主机广播的 Agent 名单判定 isAgent
        NPCGiftPatch.Monitor = Monitor;
        NPCGiftPatch.RemoteRenderer = _remoteRenderer; // C2 修复：让 ThinClient 端 transport 分支可触达

        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.PatchAll();
        UnpatchSocialPage(harmony);
        Monitor.Log("ThinClient Harmony patches applied (excluding SocialPagePatch)", LogLevel.Debug);

        // 6. UpdateTicked 订阅：驱动 renderer 位置插值 + 排空三个主线程队列。
        // 房客与主机同一纪律（2026-09-09 缺口①③）：后台线程（Task.Run/await 续体）只入队，
        // 对话渲染/送礼落账/ModMessage 发送必须由本 tick 在主线程消费——
        // 队列无人排水的后果是"网络上对了也没法实际使用"（回复不渲染、好感不落账）。
        // 三个 drain 均为非阻塞 TryDequeue，帧预算无风险。
        // 死锁修复（2026-09-12）：每个泵**各自**兜底，绝不用一个 try 串起来。
        // 原来 4 个泵共用一个 try/catch：任一上游泵抛异常（renderer 在切图瞬间取到
        // 正在卸载的 NPC、送礼动作写 friendshipData 抛 NRE……）就会跳过下游全部泵，
        // 其中 HostRequestHandlers.ProcessMainThreadActions 是房客**唯一**的 ModMessage
        // 发送泵——它一被跳过，请求发不出去、回包收不到、_isWaitingForResponse 无人清除，
        // 对话框永久停在"等待回复"，Enter 与键盘输入全被吞（联机专属 UI 硬死锁）。
        var tickCounter = 0;
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            Pump("renderer", () => _remoteRenderer?.Update(tickCounter++));
            Pump("dialogue-replies", DialogueBoxInputPatch.ProcessPendingReplies);      // AI 回复渲染（对话框）
            Pump("chat-replies", ChatBarRouter.ProcessPendingReplies);                  // M3：聊天栏 AI 回复渲染（房客形态）
            Pump("dialogue-stale-wait", DialogueBoxInputPatch.ResetStaleWait);
            Pump("gift-actions", NPCGiftPatch.ProcessMainThreadActions);                // 送礼反应 + fd.Points 落账
            Pump("host-request-mainthread", Multiplayer.HostRequestHandlers.ProcessMainThreadActions); // 房客侧入队路径（含 transport 主线程化发送）
        };

        Monitor.Log("ValleyAgent initialized successfully (ThinClient mode)", LogLevel.Info);
    }

    /// <summary>
    ///     移除 SocialPagePatch 的两个 Harmony patch（draw + receiveLeftClick）。
    ///     ThinClient 模式下不打这两个补丁，避免 farmhand 端绘制记忆宫殿按钮但点击无响应。
    /// </summary>
    private static void UnpatchSocialPage(Harmony harmony)
    {
        // SocialPage.draw 与 receiveLeftClick 均存在重载，必须显式指定参数类型以避免 AmbiguousMatchException。
        var drawMethod = AccessTools.Method(typeof(SocialPage), nameof(SocialPage.draw), new[] { typeof(SpriteBatch) });
        if (drawMethod != null)
        {
            harmony.Unpatch(drawMethod, HarmonyPatchType.All, harmony.Id);
        }

        var receiveLeftClickMethod = AccessTools.Method(typeof(SocialPage), nameof(SocialPage.receiveLeftClick),
            new[] { typeof(int), typeof(int) });
        if (receiveLeftClickMethod != null)
        {
            harmony.Unpatch(receiveLeftClickMethod, HarmonyPatchType.All, harmony.Id);
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        // ThinClient 模式清理：仅清空 Renderer 缓存与 Transport 注入，Router 保持订阅。
        // Router/Renderer/Transport 实例本身保留，下次 SaveLoaded 时只重新注入 Transport 到 Patches。
        // 这避免重复创建 Router 导致 SMAPI Multiplayer 事件被重复订阅。
        if (_mode == AgentRuntimeMode.ThinClient)
        {
            try
            {
                _remoteRenderer?.Clear();
                // Transport 注入置空，避免 farmhand 在标题画面（无 NPC）误触发请求
                DialogueBoxInputPatch.SetDialogueTransport(null);
                NPCGiftPatch.SetGiftTransport(null);
                // M3：聊天栏路由状态复位（会话簿记/在途守卫/待渲染队列）。
                // 路由器实例本身保留（持有 transport 引用），下次 SaveLoaded 无需重建。
                ChatBarRouter.Reset();
            }
            catch (Exception ex)
            {
                Monitor.Log($"[ThinClient] Cleanup failed: {ex}", LogLevel.Error);
            }

            // 不重置 _initialized：ThinClient 的 Router/Renderer/Transport 实例保留，
            // 下次 SaveLoaded 时 OnSaveLoadedInitialize 走"重新注入 Transport"分支而非完整初始化。
            return;
        }

        // Host 模式：保留现有清理逻辑（清理 AgentService/决策队列/patch 状态）。
        if (_mode == AgentRuntimeMode.Host)
        {
            // P1-10: 返回标题画面时清理所有运行时状态，防止旧存档残留污染新存档
            try
            {
                _eventHandlerInitializer?.CleanupForTitleReturn();
            }
            catch (Exception ex)
            {
                Monitor.Log($"[OnReturnedToTitle] Cleanup failed: {ex}", LogLevel.Warn);
            }

            // 服务器生命周期绑定 Mod（而非存档）：返回标题不杀服务器，
            // 只在 Mod.Dispose（游戏退出/卸载）时停止。此前在这里 Dispose 会导致
            // EventHandlerInitializer 持有的旧引用已 disposed，再进档时
            // EnsureRunningAsync 走 `if (_disposed) return false` 静默失败。
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // P1-8: Mod 卸载时清理所有运行时状态并取消事件订阅
            try
            {
                if (_mode == AgentRuntimeMode.Host)
                {
                    _eventHandlerInitializer?.CleanupForTitleReturn();
                    // UnsubscribeEvents 内部已调用 _multiplayerRouter?.Unregister()（Host 模式 router 由本类持有）。
                    _eventHandlerInitializer?.UnsubscribeEvents();
                }
                else
                {
                    // ThinClient 模式无 _eventHandlerInitializer，单独反注册 Router。
                    _multiplayerRouter?.Unregister();
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[Dispose] Cleanup failed: {ex}", LogLevel.Warn);
            }

            try
            {
                Helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
                Helper.Events.GameLoop.SaveLoaded -= OnSaveLoadedInitialize;
                Helper.Events.GameLoop.GameLaunched -= OnGameLaunchedEarly;
            }
            catch (Exception ex)
            {
                Monitor.Log($"[Dispose] Failed to unsubscribe GameLoop events: {ex}", LogLevel.Warn);
            }

            // 服务器生命周期绑定游戏：游戏退出时统一停止（_earlyServerManager 与
            // _serverProcessManager 可能指向同一实例，Dispose 幂等）。
            try
            {
                _earlyServerManager?.StopServer();
            }
            catch (Exception ex)
            {
                Monitor.Log($"[Dispose] Failed to stop early server: {ex}", LogLevel.Warn);
            }

            _serverProcessManager?.Dispose();
            _serverProcessManager = null;
            _earlyServerManager = null;
            _valleyApi = null;
            _eventHandlerInitializer = null;
            _multiplayerRouter = null;
            _remoteRenderer = null;
            _farmhandDialogueTransport = null;
            _farmhandGiftTransport = null;
            _container = null;

            if (Instance == this)
            {
                Instance = null;
            }
        }

        base.Dispose(disposing);
    }
}