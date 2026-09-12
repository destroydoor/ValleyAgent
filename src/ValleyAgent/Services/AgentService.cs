using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using ValleyAgent.Agents;
using ValleyAgent.Config;
using ValleyAgent.Economy;
using ValleyAgent.Handlers;
using ValleyAgent.Performance;
using ValleyAgent.Protocol;
using ValleyAgent.Resilience;
using ValleyAgent.StateMachine;
using ValleyAgent.StateMachine.States;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Services;

/// <summary>
///     主服务编排器。持有子系统引用，管理 NPC→Agent 映射。
///     C# 只做执行层，所有 LLM/决策/对话由 TS Agent Server 通过 WebSocket 提供。
/// </summary>
public class AgentService : IDisposable
{
    private readonly Dictionary<string, AgentInstance> _agents;
    private readonly object _agentsLock = new();
    private bool _disposed;

    /// <summary>
    ///     阶段 3 休眠 Brain 注册表：默认创建（EnsureBrain）保证全员 AgentBrain+Inventory 存在，
    ///     但休眠 NPC 不入 <see cref="_agents" /> 活跃集（spark/对话/Director 激活时才提升），
    ///     从而尊重 MaxAgentNpcs 上限。两个注册表互斥：提升时从 _brains 移入 _agents。
    /// </summary>
    private readonly Dictionary<string, AgentInstance> _brains =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<AgentState, Action<NPC, AgentInstance, int>>? _handlerActions;

    public AgentService(
        ModConfig config,
        AgentAllocationManager allocationManager,
        TokenBudgetManager tokenBudget,
        PerformanceMonitor performanceMonitor,
        CacheManager cacheManager,
        CircuitBreaker circuitBreaker)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        AllocationManager = allocationManager ?? throw new ArgumentNullException(nameof(allocationManager));
        TokenBudget = tokenBudget ?? throw new ArgumentNullException(nameof(tokenBudget));
        PerformanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        CacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        CircuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));

        _agents = new Dictionary<string, AgentInstance>(StringComparer.OrdinalIgnoreCase);
        Current = this;
    }

    public ModConfig Config { get; }
    public CircuitBreaker CircuitBreaker { get; }
    public AgentAllocationManager AllocationManager { get; }
    public TokenBudgetManager TokenBudget { get; }
    public PerformanceMonitor PerformanceMonitor { get; }
    public CacheManager CacheManager { get; }

    public IAgentServerProvider? AgentServerProvider { get; set; }

    /// <summary>
    ///     E2-2: 当前会话的 AgentService 实例（构造函数记录）。
    ///     供 WorldSnapshotBuilder 等静态场景构建工具读取 NPC AgentState（无需在调用链传服务引用）。
    /// </summary>
    public static AgentService? Current { get; private set; }

    /// <summary>
    ///     E1-1: JSONL 留痕 Sink。由 ServiceInitializer 注入；CreateAgent 时订阅状态机事件。
    ///     可为 null（测试场景或未配置时），不影响核心逻辑。
    /// </summary>
    public TranscriptSink? TranscriptSink { get; set; }

    /// <summary>
    ///     E3-1: NPC 经济档案装载器。由 ServiceInitializer 注入；
    ///     CreateAgent 时按 NPC 查初始资金回填钱包（存档恢复会覆盖）。可为 null（未配置时钱包 0 起步）。
    /// </summary>
    public NpcEconomyProfileLoader? EconomyProfiles { get; set; }

    /// <summary>
    ///     阶段 3: NPC 人设配置装载器（npc-configs/*.json）。由 ServiceInitializer 注入；
    ///     DirectorContextBuilder 拼装人设摘要、DirectorTools 约束人设使用。可为 null（未配置时无附加人设）。
    /// </summary>
    public NpcConfigLoader? NpcConfigs { get; set; }

    public int ActiveAgentCount
    {
        get
        {
            lock (_agentsLock)
            {
                return _agents.Count;
            }
        }
    }

    public IReadOnlyList<string> ActiveAgentNames
    {
        get
        {
            lock (_agentsLock)
            {
                return _agents.Keys.ToList().AsReadOnly();
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public event Action<AgentInstance>? OnDecisionTriggerRequested;

    public void TriggerDecisionReEvaluation(AgentInstance agent) => OnDecisionTriggerRequested?.Invoke(agent);

    public void SetHandlerActions(Dictionary<AgentState, Action<NPC, AgentInstance, int>> handlerActions) =>
        _handlerActions = handlerActions ?? throw new ArgumentNullException(nameof(handlerActions));

    public AgentInstance? CreateAgent(string npcName) => CreateAgentInternal(npcName, active: true);

    /// <summary>
    ///     阶段 3 默认创建（设计文档 §1.4）：保证 NPC 的 AgentBrain+Inventory 存在但保持休眠。
    ///     只入 <see cref="_brains" /> 休眠注册表，不入 <see cref="_agents" /> 活跃集、
    ///     不接管日程、不创建 StateChangedSender——spark/对话/Director beat 激活时才提升为活跃 Agent。
    ///     DirectorTools / DirectorContextBuilder / WorldSnapshotBuilder 通过 <see cref="TryGetBrain" /> 读取 L2 状态。
    /// </summary>
    public AgentInstance? EnsureBrain(string npcName) => CreateAgentInternal(npcName, active: false);

    /// <summary>
    ///     查找 NPC 的 Brain（活跃或休眠）。阶段 3 后所有 NPC 均有 Brain（默认创建），
    ///     供 L2 状态读取（worldSnapshot / DirectorContextBuilder / DirectorTools）。
    /// </summary>
    public bool TryGetBrain(string npcName, out AgentInstance? agent)
    {
        lock (_agentsLock)
        {
            if (_agents.TryGetValue(npcName, out agent))
            {
                return true;
            }

            return _brains.TryGetValue(npcName, out agent);
        }
    }

    /// <summary>
    ///     全部 NPC 的 Brain 实例（活跃 + 休眠；两个注册表互斥，无重复）。
    ///     供 DirectorContextBuilder 拼装全 NPC L2 摘要。
    /// </summary>
    public IReadOnlyList<AgentInstance> AllBrains
    {
        get
        {
            lock (_agentsLock)
            {
                return _agents.Values.Concat(_brains.Values).ToList().AsReadOnly();
            }
        }
    }

    private AgentInstance? CreateAgentInternal(string npcName, bool active)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be empty.", nameof(npcName));
        }

        lock (_agentsLock)
        {
            if (_agents.ContainsKey(npcName))
            {
                return null;
            }

            // 休眠→活跃提升：复用已有 Brain/Inventory（保留 L2 状态内容），仅补活跃接线。
            // 避免同一 NPC 出现两个 AgentInstance 导致 L2 状态分裂。
            if (active && _brains.Remove(npcName, out var dormant))
            {
                ActivateBrain(dormant, npcName);
                _agents[npcName] = dormant;
                return dormant;
            }

            if (!active && _brains.ContainsKey(npcName))
            {
                return null;
            }

            var agent = BuildAgentCore(npcName);
            if (active)
            {
                ActivateBrain(agent, npcName);
                _agents[npcName] = agent;
            }
            else
            {
                _brains[npcName] = agent;
            }

            return agent;
        }
    }

    /// <summary>
    ///     构建 AgentInstance 的共享核心（活跃/休眠共用）：状态机、Brain、背包、健康、
    ///     TranscriptSink 留痕订阅、经济档案初始资金。不注册、不接管日程、不创建状态推送。
    /// </summary>
    private AgentInstance BuildAgentCore(string npcName)
    {
        var stateMachine = new AgentStateMachine();

        // IDLE 必须且只能注册一次：基础 IdleState 提供转换规则与 TicksInState；
        // 若存在 IDLE 的 handler action（如待机漫步），用复合状态同时保留两者，
        // 避免"State IDLE is already registered"导致存档恢复/创建 Agent 全部崩溃。
        var idleAction = _handlerActions != null
                         && _handlerActions.TryGetValue(AgentState.IDLE, out var foundIdleAction)
            ? foundIdleAction
            : null;
        stateMachine.RegisterState(
            AgentState.IDLE,
            idleAction != null ? new CompositeIdleState(idleAction) : new IdleState());

        if (_handlerActions != null)
        {
            foreach (var kvp in _handlerActions)
            {
                if (kvp.Key == AgentState.IDLE)
                {
                    continue; // 已在上方复合注册
                }

                stateMachine.RegisterState(kvp.Key, new HandlerState(kvp.Key, kvp.Value));
            }
        }

        stateMachine.Initialize();

        var agent = new AgentInstance(npcName, stateMachine, Config.DefaultMaxHealth);
        agent.Brain.EmotionDedupThreshold = Config.EmotionDedupThreshold;

        stateMachine.OnStateChanged += (_, _) => PerformanceMonitor.RecordStateTransition();

        // E1-1: TranscriptSink 订阅状态机事件，状态转换写入 JSONL 留痕
        TranscriptSink?.AttachStateMachine(stateMachine, npcName);

        // E3-1: 钱包留痕订阅 + 初始资金回填（先订阅后回填，回填走静默路径不触发事件）。
        // 读档恢复在 CreateAgent 之后执行，若存档有真实余额则覆盖此处的档案初始资金。
        TranscriptSink?.AttachWallet(agent.Inventory, npcName);
        var economyProfile = EconomyProfiles?.GetProfile(npcName);
        if (economyProfile != null)
        {
            agent.Inventory.Money = economyProfile.InitialMoney;
        }

        // 阶段 3 扩展（人设配置初始化）：npc-configs/{Npc}.json 的人设字段（InitialMoney / InitialInventory）
        // 优先级高于 EconomyProfiles（人设配置是用户/策划面向每个 NPC 的最终权威来源）。
        // 仅在 BuildAgentCore 路径生效——休眠 Brain 提升（EnsureBrain → CreateAgent）复用旧 Inventory
        // （L183-188），不会重跑此处；存档恢复在 CreateAgent 之后显式覆盖 Money/Inventory。
        // 故此钩子本质等价于"全新 NPC 首次创建"的一次性初始化——满足"仅首次写入、不覆盖玩家后续改动"。
        ApplyNpcConfigInitialization(agent);

        return agent;
    }

    /// <summary>
    ///     按 npc-configs/{Npc}.json 的人设字段初始化 agent 的金钱与初始物品（仅 BuildAgentCore 路径触发）。
    ///     - InitialMoney > 0 → 覆盖 EconomyProfiles 已写的值（人设优先）。
    ///     - InitialInventory → 逐个 ItemRegistry.Create + TryAdd；ItemRegistry.Create 抛 NRE / 返回 null
    ///       表示物品不存在（用户配置了非法 QualifiedItemId），跳过该项不抛。
    ///     - 不存在的 NPC 配置 → 完全 no-op，不影响 EconomyProfiles 已生效的初始化。
    /// </summary>
    internal void ApplyNpcConfigInitialization(AgentInstance agent) =>
        ApplyNpcConfigInitialization(agent, NpcConfigs?.GetProfile(agent.NpcName));

    /// <summary>
    ///     按 npc-configs/{Npc}.json 的人设字段初始化 agent 的金钱与初始物品（仅 BuildAgentCore 路径触发）。
    ///     - InitialMoney > 0 → 覆盖 EconomyProfiles 已写的值（人设优先）。
    ///     - InitialInventory → 逐个 ItemRegistry.Create + TryAdd；ItemRegistry.Create 抛 NRE / 返回 null
    ///       表示物品不存在（用户配置了非法 QualifiedItemId），跳过该项不抛。
    ///     - config == null（无该 NPC 配置）→ 完全 no-op，不影响 EconomyProfiles 已生效的初始化。
    ///     设为 internal static 供单测直接覆盖（不依赖 AgentService 构造）。
    /// </summary>
    internal static void ApplyNpcConfigInitialization(AgentInstance agent, NpcConfig? config)
    {
        if (config == null)
        {
            return;
        }

        if (config.InitialMoney > 0)
        {
            agent.Inventory.Money = config.InitialMoney;
        }

        if (config.InitialInventory == null || config.InitialInventory.Count == 0)
        {
            return;
        }

        foreach (var itemId in config.InitialInventory)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            Item? item;
            try
            {
                item = ItemRegistry.Create(itemId, allowNull: true);
            }
            catch (NullReferenceException)
            {
                // 物品数据未初始化（标题屏/测试环境）或非法 QualifiedItemId → 跳过该项
                continue;
            }

            if (item == null)
            {
                continue;
            }

            _ = agent.Inventory.TryAdd(item);
        }
    }

    /// <summary>
    ///     活跃接线（仅分配为 Agent 时执行）：死亡处理、state_changed 推送、接管日程。
    ///     休眠 Brain 提升（EnsureBrain → CreateAgent）时补做，保证与全新创建行为一致。
    /// </summary>
    private void ActivateBrain(AgentInstance agent, string npcName)
    {
        // 死亡时从地图移除 NPC，状态机回 IDLE
        agent.Health.OnDeath += _ =>
        {
            var deadNpc = Game1.getCharacterFromName(npcName);
            if (deadNpc != null)
            {
                agent.RemovedNpcRef = deadNpc;
                deadNpc.currentLocation?.characters.Remove(deadNpc);
                deadNpc.Halt();
                deadNpc.controller = null;
            }

            if (agent.StateMachine.CurrentStateFlag != AgentState.IDLE)
            {
                agent.StateMachine.ForceTransition(AgentState.IDLE);
            }
        };

        // state_changed 推送：状态机转换后通知 TS 端维护 actualState 镜像
        if (AgentServerProvider != null)
        {
            var stateChangedSender = new StateChangedSender(AgentServerProvider, npcName, agent.StateMachine);
            stateChangedSender.Start();
            // TODO: agent dispose 时 Stop()，当前 AgentService 无 dispose 钩子，sender 随 agent 生命周期存在
        }

        var npc = Game1.getCharacterFromName(npcName);
        if (npc != null)
        {
            npc.followSchedule = false;
            npc.ignoreScheduleToday = true;
        }
    }

    public bool RemoveAgent(string npcName)
    {
        lock (_agentsLock)
        {
            if (!_agents.TryGetValue(npcName, out var agent))
            {
                return false;
            }

            agent.StateMachine.Reset();
            HandlerBase.CleanupAll(npcName);
            _ = _agents.Remove(npcName);
            // 阶段 3：降级为休眠而非删除——Brain/Inventory（含 L2 状态）保留，
            // 与"全员默认 AgentBrain"一致；再次激活时提升复用。
            _brains[npcName] = agent;
            return true;
        }
    }

    public bool TryGetAgent(string npcName, out AgentInstance? agent)
    {
        lock (_agentsLock)
        {
            return _agents.TryGetValue(npcName, out agent);
        }
    }

    public bool HasAgent(string npcName)
    {
        lock (_agentsLock)
        {
            return _agents.ContainsKey(npcName);
        }
    }

    public IReadOnlyList<AgentInstance> GetAllAgents()
    {
        lock (_agentsLock)
        {
            return _agents.Values.ToList().AsReadOnly();
        }
    }

    public void ClearAllAgents()
    {
        lock (_agentsLock)
        {
            foreach (var agent in _agents.Values)
            {
                agent.StateMachine.Reset();
            }

            _agents.Clear();
            _brains.Clear();
        }
    }

    public void RecordLlmCall(TimeSpan duration, int tokens)
    {
        PerformanceMonitor.RecordLlmCall(duration, tokens);
        TokenBudget.RecordUsage(tokens);
    }

    public void RecordDecision(string npcName) => PerformanceMonitor.RecordDecision(npcName);

    public string GetPerformanceReport() =>
        PerformanceMonitor.GetFormattedReport() + "\n" + TokenBudget.GetUsageReport();

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            ClearAllAgents();
        }

        _disposed = true;
    }
}

/// <summary>
///     将 Handler 的 Update 方法桥接到 IAgentState 接口。
/// </summary>
public class HandlerState : IAgentState
{
    private readonly AgentState _stateFlag;
    private readonly Action<NPC, AgentInstance, int> _updateAction;

    public HandlerState(AgentState stateFlag, Action<NPC, AgentInstance, int> updateAction)
    {
        _stateFlag = stateFlag;
        _updateAction = updateAction ?? throw new ArgumentNullException(nameof(updateAction));
    }

    public string StateName
    {
        get => _stateFlag.ToString();
    }

    public void Entry()
    {
    }

    public void Exit()
    {
    }

    public void Update(NPC npc, AgentInstance agent, int ticks) => _updateAction(npc, agent, ticks);
    public bool CanTransitionTo(AgentState state) => true;
}

/// <summary>
///     复合 IDLE 状态：保留 IdleState 的转换规则与 TicksInState，
///     同时执行 IDLE 的 handler action（如待机漫步）。
/// </summary>
internal sealed class CompositeIdleState : IAgentState
{
    private readonly Action<NPC, AgentInstance, int> _action;
    private readonly IdleState _inner = new();

    public CompositeIdleState(Action<NPC, AgentInstance, int> action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public int TicksInState
    {
        get => _inner.TicksInState;
    }

    public string StateName
    {
        get => _inner.StateName;
    }

    public void Entry() => _inner.Entry();
    public void Exit() => _inner.Exit();

    public void Update(NPC npc, AgentInstance agent, int ticks)
    {
        _inner.Update(npc, agent, ticks);
        _action(npc, agent, ticks);
    }

    public bool CanTransitionTo(AgentState state) => _inner.CanTransitionTo(state);
}