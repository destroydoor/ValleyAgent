using System;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.AI;
using ValleyAgent.Brain;
using ValleyAgent.Config;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using SmaLogLevel = StardewModdingAPI.LogLevel;

namespace ValleyAgent.Debug;

/// <summary>
///     Base class for console command handlers.
///     Provides common validation, error handling, and shared helper methods.
/// </summary>
public abstract class CommandHandlerBase
{
    protected readonly AgentService? _agentService;
    protected readonly ModConfig? _config;
    protected readonly IMonitor? _monitor;

    protected CommandHandlerBase(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config)
    {
        _monitor = monitor;
        _agentService = agentService;
        _config = config;
    }

    /// <summary>
    ///     Ensures a service is available. Returns failure result if not.
    /// </summary>
    protected CommandResult EnsureServiceAvailable<T>(T? service, string serviceName)
    {
        if (service == null)
        {
            return CommandResult.Fail($"{serviceName} not initialized.");
        }

        return CommandResult.Ok(string.Empty);
    }

    /// <summary>
    ///     Safely executes an operation with exception handling.
    /// </summary>
    protected CommandResult SafeExecute(Func<CommandResult> operation, string operationName)
    {
        try
        {
            return operation();
        }
        catch (InvalidOperationException ex)
        {
            _monitor?.Log($"{operationName} failed: {ex.Message}", SmaLogLevel.Error);
            return CommandResult.Fail($"{operationName} failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _monitor?.Log($"{operationName} failed unexpectedly: {ex.Message}", SmaLogLevel.Error);
            return CommandResult.Fail($"{operationName} failed unexpectedly.");
        }
    }

    /// <summary>
    ///     Safely executes an async operation with exception handling.
    /// </summary>
    protected async Task<CommandResult> SafeExecuteAsync(
        Func<Task<CommandResult>> operation, string operationName)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _monitor?.Log($"{operationName} failed: {ex.Message}", SmaLogLevel.Error);
            return CommandResult.Fail($"{operationName} failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _monitor?.Log($"{operationName} failed unexpectedly: {ex.Message}", SmaLogLevel.Error);
            return CommandResult.Fail($"{operationName} failed unexpectedly.");
        }
    }

    // ─── Shared static helpers (moved from EventHandlerInitializer) ──────

    /// <summary>获取当前友谊点数（直接从游戏数据读取）</summary>
    protected static int GetCurrentFriendshipPoints(string npcName) =>
        Game1.player?.friendshipData.TryGetValue(npcName, out var fd) == true ? fd.Points : 0;

    /// <summary>任务1.6：获取NPC可见性格特质文本（按友谊阶段过滤）</summary>
    protected static string GetVisibleTraits(AgentInstance agent, int friendshipPoints = -1)
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
    protected static string GetPhaseAwareBiography(AgentInstance agent, int friendshipPoints)
    {
        if (agent.Brain?.Bio == null)
        {
            return "";
        }

        var phase = FriendshipPhaseHelper.FromFriendship(friendshipPoints);
        return agent.Brain.Bio.GetPhaseSummary(phase) ?? "";
    }

    /// <summary>Issue 13: 获取好感度阶段中文标签</summary>
    protected static string GetFriendshipPhaseLabel(int friendshipPoints) =>
        FriendshipPhaseHelper.ToChineseLabel(FriendshipPhaseHelper.FromFriendship(friendshipPoints));

    /// <summary>Issue 13: 获取重要事项记忆文本</summary>
    protected static string GetSignificantMemoriesText(AgentInstance agent)
    {
        var significant = agent.Brain?.SignificantMemories
            .Take(5)
            .Select(m => m.Text);
        var result = significant != null && significant.Any() ? string.Join("\n", significant) : "";
        return result;
    }

    /// <summary>任务1.1：获取背包物品摘要（含物品名称，让 LLM 知道背包里有什么）</summary>
    protected static string GetInventorySummary(AgentInstance agent)
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

    protected static string GetNearbyObjectsForDialogue(NPC? npc)
    {
        if (npc?.currentLocation == null)
        {
            return "";
        }

        var objects = npc.currentLocation.objects.Keys
            .Select(t => npc.currentLocation.objects[t]?.Name ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .Take(10);
        return string.Join(", ", objects);
    }

    protected static string GetRecentMemoryText(AgentInstance agent)
    {
        var recent = agent.Brain?.ShortTermMemories
            .TakeLast(5)
            .Select(m => m.Text);
        return recent != null && recent.Any() ? string.Join("\n", recent) : "";
    }

    protected static AgentState ParseAgentState(string state, IMonitor? monitor = null, string? npcName = null)
    {
        if (Enum.TryParse<AgentState>(state, true, out var result))
        {
            return result;
        }

        monitor?.Log($"[Decision] {npcName ?? "unknown"}: LLM returned unparseable state '{state}', defaulting to IDLE",
            SmaLogLevel.Warn);
        return AgentState.IDLE;
    }

    protected static bool IsTransitionValid(NPC npc, AgentState targetState, IMonitor? monitor) =>
        StateFeasibility.IsValid(npc, targetState, monitor);

    protected static void InjectBlockedStates(DecisionContext context, string npcName)
    {
        var npc = Game1.getCharacterFromName(npcName);
        if (npc != null)
        {
            context.BlockedStates = StateFeasibility.GetBlockedStatesList(npc);
        }
    }
}