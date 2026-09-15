using System;
using StardewModdingAPI;
using ValleyAgent.Agents;
using ValleyAgent.Brain;
using ValleyAgent.Friendship;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Navigation;
using ValleyAgent.Services;

namespace ValleyAgent.Api;

/// <summary>
///     Public API facade for ValleyAgent. Implements IValleyAgentApi by delegating
///     to specialized sub-APIs: AgentManagementApi, DialogueManagementApi,
///     StateQueryApi, and AgentActionApi.
///     Static members are retained for backward compatibility but now delegate
///     to the DialogueStateManager singleton.
/// </summary>
public class ValleyAgentApi : IValleyAgentApi
{
    // ─── Static backward-compat members ────────────────────────────
    // These delegate to the DialogueStateManager singleton.
    // New code should use DialogueStateManager directly via DI.

    private static IMonitor? _earlyMonitor; // Set before DialogueStateManager is available

    private readonly AgentActionApi? _actionApi;

    // P0-5: 改为 nullable，避免服务未初始化时 NRE
    private readonly AgentManagementApi? _agentApi;
    private readonly DialogueManagementApi? _dialogueApi;
    private readonly IMovementService? _movementService;
    private readonly StateQueryApi? _queryApi;
    private readonly ThinClientDialogueApi? _thinClientDialogueApi;

    // ─── Constructor ───────────────────────────────────────────────

    public ValleyAgentApi(
        AgentService? agentService,
        AgentAllocationManager? allocationManager,
        IMovementService? movementService = null,
        Func<string, bool>? forceDecisionCallback = null,
        FriendshipSystem? friendshipSystem = null)
    {
        _movementService = movementService;

        // Fallback for GetApi() when services are not yet initialized
        if (agentService == null || allocationManager == null)
        {
            // P0-5: 字段已改为 nullable，服务未初始化时返回安全默认值
            _agentApi = null;
            _dialogueApi = null;
            _queryApi = null;
            _actionApi = null;
            return;
        }

        var dialogueState = SharedStateOrNull ?? new DialogueStateManager();

        _agentApi = new AgentManagementApi(agentService, allocationManager);
        _dialogueApi =
            new DialogueManagementApi(agentService, dialogueState, friendshipSystem, SharedStateOrNull?.Monitor);
        _queryApi = new StateQueryApi(agentService);
        _actionApi = new AgentActionApi(agentService, dialogueState, movementService, forceDecisionCallback);
    }

    /// <summary>
    ///     ThinClient（farmhand）模式构造函数。不依赖主机端 AgentService，直接通过
    ///     <see cref="IDialogueTransport" /> 把对话请求转发给主机处理。
    /// </summary>
    public ValleyAgentApi(IDialogueTransport farmhandDialogueTransport, IMonitor? monitor)
    {
        _thinClientDialogueApi = new ThinClientDialogueApi(farmhandDialogueTransport, monitor);
        _agentApi = null;
        _dialogueApi = null;
        _queryApi = null;
        _actionApi = null;
        _movementService = null;
    }

    /// <summary>
    ///     Internal constructor for testing with pre-built sub-APIs.
    /// </summary>
    internal ValleyAgentApi(
        AgentManagementApi agentApi,
        DialogueManagementApi dialogueApi,
        StateQueryApi queryApi,
        AgentActionApi actionApi)
    {
        _agentApi = agentApi;
        _dialogueApi = dialogueApi;
        _queryApi = queryApi;
        _actionApi = actionApi;
    }

    private static DialogueStateManager? SharedStateOrNull { get; set; }

    public static IMonitor? Monitor
    {
        get => SharedStateOrNull?.Monitor ?? _earlyMonitor;
        set
        {
            if (SharedStateOrNull != null)
            {
                SharedStateOrNull.Monitor = value;
            }
            else
            {
                _earlyMonitor = value;
            }
        }
    }

    public static int DialogueCooldownMs
    {
        get => SharedStateOrNull?.DialogueCooldownMs ?? 3000;
        set
        {
            if (SharedStateOrNull != null)
            {
                SharedStateOrNull.DialogueCooldownMs = value;
            }
        }
    }

    public static bool PauseAllDialogue
    {
        get => SharedStateOrNull?.PauseAllDialogue ?? false;
        set
        {
            if (SharedStateOrNull != null)
            {
                SharedStateOrNull.PauseAllDialogue = value;
            }
        }
    }

    // ─── IValleyAgentApi — Agent Management ────────────────────────

    // P0-5: 所有方法加 null 守卫，服务未初始化时返回安全默认值
    public string[] GetActiveAgentNames() => _agentApi?.GetActiveAgentNames() ?? Array.Empty<string>();
    public bool TryAllocateAgent(string npcName) => _agentApi?.TryAllocateAgent(npcName) ?? false;
    public bool TryDeallocateAgent(string npcName) => _agentApi?.TryDeallocateAgent(npcName) ?? false;

    // ─── IValleyAgentApi — Dialogue ─────────────────────────────────

    public bool TryGenerateDialogue(string npcName, string playerInput) =>
        _dialogueApi?.TryGenerateDialogue(npcName, playerInput)
        ?? _thinClientDialogueApi?.TryGenerateDialogue(npcName, playerInput)
        ?? false;

    public bool TryGetLastDialogue(string npcName, out string response)
    {
        if (_dialogueApi != null)
        {
            return _dialogueApi.TryGetLastDialogue(npcName, out response);
        }

        if (_thinClientDialogueApi != null)
        {
            return _thinClientDialogueApi.TryGetLastDialogue(npcName, out response);
        }

        response = string.Empty;
        return false;
    }

    public bool TryGetLastDialogueSource(string npcName, out DialogueResponseSource source)
    {
        if (_dialogueApi != null)
        {
            return _dialogueApi.TryGetLastDialogueSource(npcName, out source);
        }

        if (_thinClientDialogueApi != null)
        {
            return _thinClientDialogueApi.TryGetLastDialogueSource(npcName, out source);
        }

        source = DialogueResponseSource.None;
        return false;
    }

    public bool TryGetLastDecision(string npcName, out string state, out string reason)
    {
        if (_dialogueApi == null)
        {
            state = string.Empty;
            reason = string.Empty;
            return false;
        }

        return _dialogueApi.TryGetLastDecision(npcName, out state, out reason);
    }

    public void ClearDialogueState(string npcName) => SharedStateOrNull?.ClearDialogueState(npcName);

    // ─── IValleyAgentApi — 实例访问器（显式接口实现以避免与静态成员签名冲突）───

    // 注意：ClearDialogueCooldown 和 DialogueCooldownMs 已有静态实现供旧代码使用，
    // 此处通过显式接口实现为 IValleyAgentApi 消费者（如 TestMod）提供实例访问入口。
    void IValleyAgentApi.ClearDialogueCooldown(string npcName) => SharedStateOrNull?.ClearDialogueCooldown(npcName);

    int IValleyAgentApi.DialogueCooldownMs
    {
        get => SharedStateOrNull?.DialogueCooldownMs ?? 3000;
        set
        {
            if (SharedStateOrNull != null)
            {
                SharedStateOrNull.DialogueCooldownMs = value;
            }
        }
    }

    IMovementService? IValleyAgentApi.GetMovementService() => _movementService;

    // ─── IValleyAgentApi — State Queries ────────────────────────────

    public string GetAgentState(string npcName) => _queryApi?.GetAgentState(npcName) ?? "UNKNOWN";
    public string[] GetEnabledFeatures() => _queryApi?.GetEnabledFeatures() ?? Array.Empty<string>();
    public int GetDialogueInterceptCount() => StateQueryApi.GetDialogueInterceptCount();
    public int GetNpcFriendshipPoints(string npcName) => StateQueryApi.GetNpcFriendshipPoints(npcName);
    public int GetNpcHealth(string npcName) => _queryApi?.GetNpcHealth(npcName) ?? 0;
    public int GetNpcMaxHealth(string npcName) => _queryApi?.GetNpcMaxHealth(npcName) ?? 0;
    public string GetNpcEmotion(string npcName) => _queryApi?.GetNpcEmotion(npcName) ?? "Neutral";
    public string[] GetNpcInventory(string npcName) => _queryApi?.GetNpcInventory(npcName) ?? Array.Empty<string>();
    public string[] GetNpcMemories(string npcName) => _queryApi?.GetNpcMemories(npcName) ?? Array.Empty<string>();
    public AgentInstance? GetAgentInstance(string npcName) => _queryApi?.GetAgentInstance(npcName);

    public string[] GetRegisteredStates(string npcName) =>
        _queryApi?.GetRegisteredStates(npcName) ?? Array.Empty<string>();

    // ─── IValleyAgentApi — Actions ──────────────────────────────────

    public bool TryForceDecision(string npcName) => _actionApi?.TryForceDecision(npcName) ?? false;

    public bool TrySetAgentState(string npcName, string stateName) =>
        _actionApi?.TrySetAgentState(npcName, stateName) ?? false;

    public bool TryTriggerGift(string npcName, out string itemName)
    {
        if (_actionApi == null)
        {
            itemName = string.Empty;
            return false;
        }

        return _actionApi.TryTriggerGift(npcName, out itemName);
    }

    public bool TrySpeak(string npcName, string text, int durationMs) =>
        _actionApi?.TrySpeak(npcName, text, durationMs) ?? false;

    public bool TryEmote(string npcName, int emoteIndex) => AgentActionApi.TryEmote(npcName, emoteIndex);
    public bool TryHeal(string npcName, int amount) => _actionApi?.TryHeal(npcName, amount) ?? false;
    public bool TryRevive(string npcName) => _actionApi?.TryRevive(npcName) ?? false;
    public bool SetNpcHealth(string npcName, int health) => _actionApi?.SetNpcHealth(npcName, health) ?? false;
    public void SetFriendshipForNpc(string npcName, int points) => AgentActionApi.SetFriendshipForNpc(npcName, points);

    public bool FillNpcInventory(string npcName, string itemId, int count) =>
        _actionApi?.FillNpcInventory(npcName, itemId, count) ?? false;

    internal static void SetSharedDialogueState(DialogueStateManager state)
    {
        SharedStateOrNull = state;
        // Propagate any early-set monitor value
        if (_earlyMonitor != null)
        {
            state.Monitor = _earlyMonitor;
            _earlyMonitor = null;
        }
    }

    public static void ProcessMainThreadActions() => SharedStateOrNull?.ProcessMainThreadActions();

    public static bool HasPendingDialogue(string npcName) => SharedStateOrNull?.HasPendingDialogue(npcName) ?? false;

    public static void ClearDialogueCooldown(string npcName) => SharedStateOrNull?.ClearDialogueCooldown(npcName);
}