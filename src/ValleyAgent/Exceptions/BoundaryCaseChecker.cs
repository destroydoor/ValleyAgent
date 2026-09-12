using System;
using System.Collections.Generic;
using ValleyAgent.Agents;
using ValleyAgent.RAG;

namespace ValleyAgent.Exceptions;

/// <summary>
///     Represents a boundary case check result.
/// </summary>
public class BoundaryCheckResult
{
    /// <summary>Whether the boundary case is active/triggered.</summary>
    public bool IsActive { get; set; }

    /// <summary>Name of the boundary check.</summary>
    public string CheckName { get; set; } = string.Empty;

    /// <summary>Human-readable description of the boundary case.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Severity level of the boundary case.</summary>
    public BoundarySeverity Severity { get; set; }

    /// <summary>Recommended action when this boundary case is active.</summary>
    public string RecommendedAction { get; set; } = string.Empty;

    /// <summary>Additional metadata about the check.</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>Creates a result indicating the boundary case is active.</summary>
    public static BoundaryCheckResult Active(string checkName, string description, BoundarySeverity severity,
        string recommendedAction)
    {
        return new BoundaryCheckResult
        {
            IsActive = true,
            CheckName = checkName,
            Description = description,
            Severity = severity,
            RecommendedAction = recommendedAction
        };
    }

    /// <summary>Creates a result indicating the boundary case is not active.</summary>
    public static BoundaryCheckResult Inactive(string checkName)
    {
        return new BoundaryCheckResult
        {
            IsActive = false,
            CheckName = checkName,
            Description = $"{checkName} is not active",
            Severity = BoundarySeverity.None
        };
    }
}

/// <summary>
///     Severity levels for boundary case checks.
/// </summary>
public enum BoundarySeverity
{
    /// <summary>No severity - check is inactive.</summary>
    None,

    /// <summary>Informational - minor impact on behavior.</summary>
    Info,

    /// <summary>Warning - moderate impact, may need adjustment.</summary>
    Warning,

    /// <summary>Critical - major impact, immediate action required.</summary>
    Critical
}

/// <summary>
///     Checks for boundary cases and edge conditions that require special handling.
///     Boundary checks:
///     - CheckPlayerSleeping: Is player in bed?
///     - CheckFestivalDay: Is today a festival?
///     - CheckNpcHealth: Is NPC health critical?
///     - CheckMultiplayer: Is multiplayer active?
///     - CheckAgentCapacity: Are we at max agents?
///     Design:
///     - Game-agnostic core logic (no SMAPI/Stardew dependencies)
///     - Uses delegate callbacks for game-specific state queries
///     - Lightweight and fast - suitable for per-tick checks
///     - Never crashes - returns safe defaults on error
/// </summary>
public class BoundaryCaseChecker
{
    private readonly AgentAllocationManager? _allocationManager;
    private readonly Func<(string Season, int Day), FestivalCheckResult>? _getFestivalInfo;
    private readonly Func<int>? _getMaxAgents;
    private readonly Func<string, float>? _getNpcHealthPercent;
    private readonly Func<bool>? _isMultiplayerActive;

    // Game state delegates - injected by the game integration layer
    private readonly Func<bool>? _isPlayerSleeping;
    private readonly Action<string, string>? _logInfo;
    private readonly Action<string, string>? _logWarning;
    private readonly RAGKnowledgeBase? _ragKnowledgeBase;

    /// <summary>
    ///     Creates a new BoundaryCaseChecker.
    /// </summary>
    /// <param name="allocationManager">Optional allocation manager for capacity checks.</param>
    /// <param name="ragKnowledgeBase">Optional RAG knowledge base for festival lookups.</param>
    /// <param name="isPlayerSleeping">Optional callback to check if player is sleeping.</param>
    /// <param name="getFestivalInfo">Optional callback to get festival info for a date.</param>
    /// <param name="getNpcHealthPercent">Optional callback to get NPC health percentage (0-100).</param>
    /// <param name="isMultiplayerActive">Optional callback to check if multiplayer is active.</param>
    /// <param name="getMaxAgents">Optional callback to get the maximum number of agents allowed.</param>
    /// <param name="logWarning">Optional warning logging callback.</param>
    /// <param name="logInfo">Optional info logging callback.</param>
    public BoundaryCaseChecker(
        AgentAllocationManager? allocationManager = null,
        RAGKnowledgeBase? ragKnowledgeBase = null,
        Func<bool>? isPlayerSleeping = null,
        Func<(string Season, int Day), FestivalCheckResult>? getFestivalInfo = null,
        Func<string, float>? getNpcHealthPercent = null,
        Func<bool>? isMultiplayerActive = null,
        Func<int>? getMaxAgents = null,
        Action<string, string>? logWarning = null,
        Action<string, string>? logInfo = null)
    {
        _allocationManager = allocationManager;
        _ragKnowledgeBase = ragKnowledgeBase;
        _isPlayerSleeping = isPlayerSleeping;
        _getFestivalInfo = getFestivalInfo;
        _getNpcHealthPercent = getNpcHealthPercent;
        _isMultiplayerActive = isMultiplayerActive;
        _getMaxAgents = getMaxAgents;
        _logWarning = logWarning;
        _logInfo = logInfo;
    }

    /// <summary>
    ///     Fired when a boundary case is detected.
    /// </summary>
    public event EventHandler<BoundaryCheckResult>? OnBoundaryCaseDetected;

    #region Individual Boundary Checks

    /// <summary>
    ///     Checks if the player is currently sleeping.
    /// </summary>
    /// <returns>Boundary check result indicating whether player is sleeping.</returns>
    public BoundaryCheckResult CheckPlayerSleeping()
    {
        try
        {
            var isSleeping = _isPlayerSleeping?.Invoke() ?? false;

            if (isSleeping)
            {
                var result = BoundaryCheckResult.Active(
                    "PlayerSleeping",
                    "Player is currently sleeping",
                    BoundarySeverity.Info,
                    "Pause agent autonomy and save state");
                result.Metadata["should_save"] = true;
                result.Metadata["pause_autonomy"] = true;
                return FireAndReturn(result);
            }

            return BoundaryCheckResult.Inactive("PlayerSleeping");
        }
        catch (InvalidOperationException ex)
        {
            LogWarning("BoundaryCheck", $"Error checking player sleep state: {ex.Message}");
            return BoundaryCheckResult.Inactive("PlayerSleeping");
        }
    }

    /// <summary>
    ///     Checks if today is a festival day.
    /// </summary>
    /// <param name="currentSeason">Current in-game season.</param>
    /// <param name="currentDay">Current in-game day of month.</param>
    /// <returns>Boundary check result indicating whether today is a festival.</returns>
    public BoundaryCheckResult CheckFestivalDay(string currentSeason, int currentDay)
    {
        try
        {
            FestivalCheckResult? festivalInfo = null;

            // Try delegate first
            if (_getFestivalInfo != null)
            {
                festivalInfo = _getFestivalInfo.Invoke((currentSeason, currentDay));
            }
            // Fallback to RAG knowledge base
            else if (_ragKnowledgeBase?.IsLoaded == true)
            {
                var festival = _ragKnowledgeBase.GetFestivalByDate(currentSeason, currentDay);
                if (festival != null)
                {
                    festivalInfo = new FestivalCheckResult
                    {
                        IsFestival = true,
                        FestivalName = festival.Name,
                        Location = festival.Location,
                        ParticipatingNpcs = festival.ParticipatingNpcs
                    };
                }
            }

            if (festivalInfo?.IsFestival == true)
            {
                var result = BoundaryCheckResult.Active(
                    "FestivalDay",
                    $"Today is {festivalInfo.FestivalName}",
                    BoundarySeverity.Warning,
                    "Pause agent autonomy, follow festival schedule");
                result.Metadata["festival_name"] = festivalInfo.FestivalName;
                result.Metadata["location"] = festivalInfo.Location ?? "Unknown";
                result.Metadata["participants"] = festivalInfo.ParticipatingNpcs?.Count ?? 0;
                return FireAndReturn(result);
            }

            return BoundaryCheckResult.Inactive("FestivalDay");
        }
        catch (InvalidOperationException ex)
        {
            LogWarning("BoundaryCheck", $"Error checking festival day: {ex.Message}");
            return BoundaryCheckResult.Inactive("FestivalDay");
        }
    }

    /// <summary>
    ///     Checks if an NPC's health is critical.
    /// </summary>
    /// <param name="npcName">The name of the NPC to check.</param>
    /// <param name="criticalThreshold">Health percentage below which health is considered critical (default 20%).</param>
    /// <returns>Boundary check result indicating whether NPC health is critical.</returns>
    public BoundaryCheckResult CheckNpcHealth(string npcName, float criticalThreshold = 20f)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            LogWarning("BoundaryCheck", "CheckNpcHealth called with empty NPC name");
            return BoundaryCheckResult.Inactive("NpcHealth");
        }

        try
        {
            var healthPercent = _getNpcHealthPercent?.Invoke(npcName) ?? 100f;

            if (healthPercent <= criticalThreshold)
            {
                var result = BoundaryCheckResult.Active(
                    "NpcHealth",
                    $"{npcName}'s health is critical ({healthPercent:F1}%)",
                    BoundarySeverity.Critical,
                    "Stop combat, teleport to farm, set unavailable for day");
                result.Metadata["npc_name"] = npcName;
                result.Metadata["health_percent"] = healthPercent;
                result.Metadata["critical_threshold"] = criticalThreshold;
                return FireAndReturn(result);
            }

            return BoundaryCheckResult.Inactive("NpcHealth");
        }
        catch (InvalidOperationException ex)
        {
            LogWarning("BoundaryCheck", $"Error checking NPC health for {npcName}: {ex.Message}");
            return BoundaryCheckResult.Inactive("NpcHealth");
        }
    }

    /// <summary>
    ///     Checks if multiplayer is currently active.
    /// </summary>
    /// <returns>Boundary check result indicating whether multiplayer is active.</returns>
    public BoundaryCheckResult CheckMultiplayer()
    {
        try
        {
            var isMultiplayer = _isMultiplayerActive?.Invoke() ?? false;

            if (isMultiplayer)
            {
                var result = BoundaryCheckResult.Active(
                    "Multiplayer",
                    "Multiplayer session is active",
                    BoundarySeverity.Warning,
                    "Reduce agent complexity, respect host settings");
                result.Metadata["should_reduce_complexity"] = true;
                result.Metadata["respect_host_settings"] = true;
                return FireAndReturn(result);
            }

            return BoundaryCheckResult.Inactive("Multiplayer");
        }
        catch (InvalidOperationException ex)
        {
            LogWarning("BoundaryCheck", $"Error checking multiplayer state: {ex.Message}");
            return BoundaryCheckResult.Inactive("Multiplayer");
        }
    }

    /// <summary>
    ///     Checks if the agent allocation is at maximum capacity.
    /// </summary>
    /// <returns>Boundary check result indicating whether at max agent capacity.</returns>
    public BoundaryCheckResult CheckAgentCapacity()
    {
        try
        {
            if (_allocationManager == null)
            {
                return BoundaryCheckResult.Inactive("AgentCapacity");
            }

            var currentCount = _allocationManager.CurrentAgentCount;
            var maxAgents = _getMaxAgents?.Invoke() ?? _allocationManager.MaxAgents;

            if (currentCount >= maxAgents)
            {
                var result = BoundaryCheckResult.Active(
                    "AgentCapacity",
                    $"At maximum agent capacity ({currentCount}/{maxAgents})",
                    BoundarySeverity.Info,
                    "Use priority-based replacement for new allocations");
                result.Metadata["current_count"] = currentCount;
                result.Metadata["max_agents"] = maxAgents;
                result.Metadata["at_capacity"] = true;
                return FireAndReturn(result);
            }

            return BoundaryCheckResult.Inactive("AgentCapacity");
        }
        catch (InvalidOperationException ex)
        {
            LogWarning("BoundaryCheck", $"Error checking agent capacity: {ex.Message}");
            return BoundaryCheckResult.Inactive("AgentCapacity");
        }
    }

    #endregion

    #region Batch Checks

    /// <summary>
    ///     Runs all boundary checks and returns active ones.
    /// </summary>
    /// <param name="currentSeason">Current in-game season.</param>
    /// <param name="currentDay">Current in-game day of month.</param>
    /// <returns>List of active boundary check results.</returns>
    public List<BoundaryCheckResult> CheckAll(string currentSeason, int currentDay)
    {
        var results = new List<BoundaryCheckResult>();

        var playerSleeping = CheckPlayerSleeping();
        if (playerSleeping.IsActive)
        {
            results.Add(playerSleeping);
        }

        var festivalDay = CheckFestivalDay(currentSeason, currentDay);
        if (festivalDay.IsActive)
        {
            results.Add(festivalDay);
        }

        var multiplayer = CheckMultiplayer();
        if (multiplayer.IsActive)
        {
            results.Add(multiplayer);
        }

        var agentCapacity = CheckAgentCapacity();
        if (agentCapacity.IsActive)
        {
            results.Add(agentCapacity);
        }

        return results;
    }

    /// <summary>
    ///     Checks all NPCs for critical health.
    /// </summary>
    /// <param name="npcNames">List of NPC names to check.</param>
    /// <param name="criticalThreshold">Health percentage threshold for critical health.</param>
    /// <returns>List of NPCs with critical health.</returns>
    public List<BoundaryCheckResult> CheckAllNpcHealth(IEnumerable<string> npcNames, float criticalThreshold = 20f)
    {
        var results = new List<BoundaryCheckResult>();

        foreach (var npcName in npcNames)
        {
            var result = CheckNpcHealth(npcName, criticalThreshold);
            if (result.IsActive)
            {
                results.Add(result);
            }
        }

        return results;
    }

    /// <summary>
    ///     Checks if any critical boundary cases are active.
    /// </summary>
    /// <param name="currentSeason">Current in-game season.</param>
    /// <param name="currentDay">Current in-game day of month.</param>
    /// <returns>True if any critical boundary case is active.</returns>
    public bool HasCriticalBoundaryCases(string currentSeason, int currentDay)
    {
        var allChecks = CheckAll(currentSeason, currentDay);
        return allChecks.Exists(r => r.Severity == BoundarySeverity.Critical);
    }

    /// <summary>
    ///     Gets the highest severity boundary case currently active.
    /// </summary>
    /// <param name="currentSeason">Current in-game season.</param>
    /// <param name="currentDay">Current in-game day of month.</param>
    /// <returns>The highest severity level, or None if no boundary cases are active.</returns>
    public BoundarySeverity GetHighestSeverity(string currentSeason, int currentDay)
    {
        var allChecks = CheckAll(currentSeason, currentDay);

        // 使用条件表达式替代 if-return 链
        return allChecks.Exists(r => r.Severity == BoundarySeverity.Critical) ? BoundarySeverity.Critical
            : allChecks.Exists(r => r.Severity == BoundarySeverity.Warning) ? BoundarySeverity.Warning
            : allChecks.Exists(r => r.Severity == BoundarySeverity.Info) ? BoundarySeverity.Info
            : BoundarySeverity.None;
    }

    #endregion

    #region Event Helpers

    private BoundaryCheckResult FireAndReturn(BoundaryCheckResult result)
    {
        if (result.IsActive)
        {
            LogInfo("BoundaryCheck", $"[{result.CheckName}] {result.Description} - {result.RecommendedAction}");
            OnBoundaryCaseDetected?.Invoke(this, result);
        }

        return result;
    }

    private void LogWarning(string category, string message) => _logWarning?.Invoke(category, message);

    private void LogInfo(string category, string message) => _logInfo?.Invoke(category, message);

    #endregion
}

/// <summary>
///     Result of a festival check.
/// </summary>
public class FestivalCheckResult
{
    /// <summary>Whether a festival is active.</summary>
    public bool IsFestival { get; set; }

    /// <summary>Name of the festival.</summary>
    public string FestivalName { get; set; } = string.Empty;

    /// <summary>Location of the festival.</summary>
    public string? Location { get; set; }

    /// <summary>List of NPCs participating in the festival.</summary>
    public List<string>? ParticipatingNpcs { get; set; }
}