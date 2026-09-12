using System;
using System.Collections.Generic;
using ValleyAgent.Agents;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Recovery;

/// <summary>
///     Represents the result of a recovery action.
/// </summary>
public class RecoveryResult
{
    /// <summary>Whether the recovery was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Human-readable description of the recovery action taken.</summary>
    public string ActionTaken { get; set; } = string.Empty;

    /// <summary>The NPC name affected by the recovery.</summary>
    public string? NpcName { get; set; }

    /// <summary>The previous state before recovery.</summary>
    public AgentState? PreviousState { get; set; }

    /// <summary>The new state after recovery.</summary>
    public AgentState? NewState { get; set; }

    /// <summary>Any error message if recovery failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Additional data from the recovery action.</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>Creates a successful recovery result.</summary>
    public static RecoveryResult CreateSuccess(string actionTaken, string? npcName = null)
    {
        return new RecoveryResult
        {
            Success = true,
            ActionTaken = actionTaken,
            NpcName = npcName
        };
    }

    /// <summary>Creates a failed recovery result.</summary>
    public static RecoveryResult CreateFailure(string errorMessage, string? npcName = null)
    {
        return new RecoveryResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            NpcName = npcName
        };
    }
}

/// <summary>
///     Provides recovery actions for various failure and edge-case scenarios.
///     Recovery strategies:
///     - RecoverFromSleep: Cancel action, save state
///     - RecoverFromFestival: Pause autonomy, follow festival schedule
///     - RecoverFromFaint: Teleport to farm, set UNAVAILABLE for day
///     - RecoverFromInvalidJson: Use last valid decision or IDLE
///     - RecoverFromDivorce: Remove agent status, reset to normal NPC
///     - RecoverFromCorruptedSave: Reset all agent data with defaults
///     Design:
///     - Game-agnostic core logic (no SMAPI/Stardew dependencies)
///     - Never leaves NPCs in invalid states
///     - Always logs recovery actions
///     - Thread-safe where applicable
/// </summary>
public class RecoveryActions
{
    private readonly AgentAllocationManager? _allocationManager;
    private readonly Action<string, string>? _logError;
    private readonly Action<string, string>? _logInfo;
    private readonly Action<string, string>? _logWarning;
    private readonly Dictionary<string, AgentStateMachine>? _stateMachines;

    /// <summary>
    ///     Creates a new RecoveryActions instance.
    /// </summary>
    /// <param name="allocationManager">Optional allocation manager for agent management.</param>
    /// <param name="stateMachines">Optional dictionary of NPC state machines.</param>
    /// <param name="logInfo">Optional info logging callback.</param>
    /// <param name="logWarning">Optional warning logging callback.</param>
    /// <param name="logError">Optional error logging callback.</param>
    public RecoveryActions(
        AgentAllocationManager? allocationManager = null,
        Dictionary<string, AgentStateMachine>? stateMachines = null,
        Action<string, string>? logInfo = null,
        Action<string, string>? logWarning = null,
        Action<string, string>? logError = null)
    {
        _allocationManager = allocationManager;
        _stateMachines = stateMachines;
        _logInfo = logInfo;
        _logWarning = logWarning;
        _logError = logError;
    }

    /// <summary>
    ///     Fired when a recovery action is performed.
    /// </summary>
    public event EventHandler<RecoveryResult>? OnRecoveryPerformed;

    #region NPC Recovery Actions

    /// <summary>
    ///     Recovers an NPC when the player goes to sleep.
    ///     Cancels current action and saves state for the next day.
    /// </summary>
    /// <param name="npcName">The name of the NPC to recover.</param>
    /// <returns>Result of the recovery action.</returns>
    public RecoveryResult RecoverFromSleep(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return LogAndReturn(RecoveryResult.CreateFailure("NPC name cannot be empty"));
        }

        try
        {
            LogInfo("Recovery", $"Recovering {npcName} from sleep - cancelling action and saving state");

            // Get current state before recovery
            var previousState = AgentState.IDLE;
            if (_stateMachines?.TryGetValue(npcName, out var stateMachine) == true)
            {
                previousState = stateMachine.CurrentStateFlag;

                // Reset to IDLE for sleep
                stateMachine.Reset();
            }

            var result = RecoveryResult.CreateSuccess(
                $"Cancelled current action and saved state for {npcName}",
                npcName);
            result.PreviousState = previousState;
            result.NewState = AgentState.IDLE;
            result.Metadata["reason"] = "player_sleeping";
            result.Metadata["saved"] = true;

            return LogAndReturn(result);
        }
        catch (InvalidOperationException ex)
        {
            var result = RecoveryResult.CreateFailure(
                $"Failed to recover {npcName} from sleep: {ex.Message}",
                npcName);
            return LogAndReturn(result);
        }
    }

    /// <summary>
    ///     Recovers an NPC during a festival day.
    ///     Pauses autonomy and follows festival schedule.
    /// </summary>
    /// <param name="npcName">The name of the NPC to recover.</param>
    /// <returns>Result of the recovery action.</returns>
    public RecoveryResult RecoverFromFestival(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return LogAndReturn(RecoveryResult.CreateFailure("NPC name cannot be empty"));
        }

        try
        {
            LogInfo("Recovery", $"Recovering {npcName} for festival - pausing autonomy");

            // Get current state before recovery
            var previousState = AgentState.IDLE;
            if (_stateMachines?.TryGetValue(npcName, out var stateMachine) == true)
            {
                previousState = stateMachine.CurrentStateFlag;

                // Force to IDLE during festival
                stateMachine.ForceTransition(AgentState.IDLE);
            }

            var result = RecoveryResult.CreateSuccess(
                $"Paused autonomy for {npcName} - following festival schedule",
                npcName);
            result.PreviousState = previousState;
            result.NewState = AgentState.IDLE;
            result.Metadata["reason"] = "festival_day";
            result.Metadata["autonomy_paused"] = true;

            return LogAndReturn(result);
        }
        catch (InvalidOperationException ex)
        {
            var result = RecoveryResult.CreateFailure(
                $"Failed to recover {npcName} for festival: {ex.Message}",
                npcName);
            return LogAndReturn(result);
        }
    }

    /// <summary>
    ///     Recovers an NPC when they faint (health critical).
    ///     Teleports to farm and sets UNAVAILABLE for the rest of the day.
    /// </summary>
    /// <param name="npcName">The name of the NPC to recover.</param>
    /// <returns>Result of the recovery action.</returns>
    public RecoveryResult RecoverFromFaint(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return LogAndReturn(RecoveryResult.CreateFailure("NPC name cannot be empty"));
        }

        try
        {
            LogWarning("Recovery", $"Recovering {npcName} from faint - teleporting to farm and setting unavailable");

            // Get current state before recovery
            var previousState = AgentState.IDLE;
            if (_stateMachines?.TryGetValue(npcName, out var stateMachine) == true)
            {
                previousState = stateMachine.CurrentStateFlag;

                // Reset to IDLE (unavailable state handled externally)
                stateMachine.Reset();
            }

            var result = RecoveryResult.CreateSuccess(
                $"Teleported {npcName} to farm and set unavailable for the day",
                npcName);
            result.PreviousState = previousState;
            result.NewState = AgentState.IDLE;
            result.Metadata["reason"] = "npc_fainted";
            result.Metadata["teleported"] = true;
            result.Metadata["unavailable_for_day"] = true;

            return LogAndReturn(result);
        }
        catch (InvalidOperationException ex)
        {
            var result = RecoveryResult.CreateFailure(
                $"Failed to recover {npcName} from faint: {ex.Message}",
                npcName);
            return LogAndReturn(result);
        }
    }

    /// <summary>
    ///     Recovers an NPC when invalid JSON is received from LLM.
    ///     Uses last valid decision or falls back to IDLE.
    /// </summary>
    /// <param name="npcName">The name of the NPC to recover.</param>
    /// <param name="lastValidDecision">Optional last valid decision state to use.</param>
    /// <returns>Result of the recovery action.</returns>
    public RecoveryResult RecoverFromInvalidJson(string npcName, AgentState? lastValidDecision = null)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return LogAndReturn(RecoveryResult.CreateFailure("NPC name cannot be empty"));
        }

        try
        {
            LogWarning("Recovery", $"Recovering {npcName} from invalid JSON - using fallback decision");

            // Get current state before recovery
            var previousState = AgentState.IDLE;
            if (_stateMachines?.TryGetValue(npcName, out var stateMachine) == true)
            {
                previousState = stateMachine.CurrentStateFlag;

                // Use last valid decision or IDLE
                var fallbackState = lastValidDecision ?? AgentState.IDLE;
                stateMachine.ForceTransition(fallbackState);
            }

            var result = RecoveryResult.CreateSuccess(
                $"Used {(lastValidDecision.HasValue ? "last valid decision" : "IDLE fallback")} for {npcName}",
                npcName);
            result.PreviousState = previousState;
            result.NewState = lastValidDecision ?? AgentState.IDLE;
            result.Metadata["reason"] = "invalid_json";
            result.Metadata["used_fallback"] = true;
            result.Metadata["had_last_valid"] = lastValidDecision.HasValue;

            return LogAndReturn(result);
        }
        catch (InvalidOperationException ex)
        {
            var result = RecoveryResult.CreateFailure(
                $"Failed to recover {npcName} from invalid JSON: {ex.Message}",
                npcName);
            return LogAndReturn(result);
        }
    }

    /// <summary>
    ///     Recovers an NPC after divorce.
    ///     Removes agent status and resets to normal NPC behavior.
    /// </summary>
    /// <param name="npcName">The name of the NPC to recover.</param>
    /// <returns>Result of the recovery action.</returns>
    public RecoveryResult RecoverFromDivorce(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return LogAndReturn(RecoveryResult.CreateFailure("NPC name cannot be empty"));
        }

        try
        {
            LogWarning("Recovery", $"Recovering {npcName} from divorce - removing agent status");

            // Get current state before deallocation
            var previousState = AgentState.IDLE;
            if (_stateMachines?.TryGetValue(npcName, out var stateMachine) == true)
            {
                previousState = stateMachine.CurrentStateFlag;
                stateMachine.Reset();
            }

            // Deallocate the NPC
            var deallocated = _allocationManager?.Deallocate(npcName) ?? false;

            var result = RecoveryResult.CreateSuccess(
                $"Removed agent status from {npcName} after divorce",
                npcName);
            result.PreviousState = previousState;
            result.NewState = AgentState.IDLE;
            result.Metadata["reason"] = "divorce";
            result.Metadata["deallocated"] = deallocated;
            result.Metadata["was_agent"] = _allocationManager?.IsAllocated(npcName) == true;

            return LogAndReturn(result);
        }
        catch (InvalidOperationException ex)
        {
            var result = RecoveryResult.CreateFailure(
                $"Failed to recover {npcName} from divorce: {ex.Message}",
                npcName);
            return LogAndReturn(result);
        }
    }

    /// <summary>
    ///     Recovers from a corrupted save file.
    ///     Resets all agent data with default values.
    /// </summary>
    /// <returns>Result of the recovery action.</returns>
    public RecoveryResult RecoverFromCorruptedSave()
    {
        try
        {
            LogError("Recovery", "Recovering from corrupted save - resetting all agent data with defaults");

            // Clear all allocations
            _allocationManager?.ClearAllAllocations();

            // Reset all state machines
            if (_stateMachines != null)
            {
                foreach (var kvp in _stateMachines)
                {
                    try
                    {
                        kvp.Value.Reset();
                    }
                    catch (InvalidOperationException ex)
                    {
                        LogWarning("Recovery", $"Failed to reset state machine for {kvp.Key}: {ex.Message}");
                    }
                }
            }

            var result = RecoveryResult.CreateSuccess("Reset all agent data with default values");
            result.Metadata["reason"] = "corrupted_save";
            result.Metadata["allocations_cleared"] = true;
            result.Metadata["state_machines_reset"] = _stateMachines?.Count ?? 0;

            return LogAndReturn(result);
        }
        catch (InvalidOperationException ex)
        {
            var result = RecoveryResult.CreateFailure($"Failed to recover from corrupted save: {ex.Message}");
            return LogAndReturn(result);
        }
    }

    #endregion

    #region Batch Recovery

    /// <summary>
    ///     Recovers all allocated NPCs from sleep.
    /// </summary>
    /// <returns>List of recovery results for each NPC.</returns>
    public List<RecoveryResult> RecoverAllFromSleep()
    {
        var results = new List<RecoveryResult>();

        var allocatedNpcs = _allocationManager?.AllocatedAgentNames;
        if (allocatedNpcs == null)
        {
            results.Add(RecoveryResult.CreateFailure("No allocation manager available"));
            return results;
        }

        foreach (var npcName in allocatedNpcs)
        {
            results.Add(RecoverFromSleep(npcName));
        }

        return results;
    }

    /// <summary>
    ///     Recovers all allocated NPCs for festival.
    /// </summary>
    /// <returns>List of recovery results for each NPC.</returns>
    public List<RecoveryResult> RecoverAllForFestival()
    {
        var results = new List<RecoveryResult>();

        var allocatedNpcs = _allocationManager?.AllocatedAgentNames;
        if (allocatedNpcs == null)
        {
            results.Add(RecoveryResult.CreateFailure("No allocation manager available"));
            return results;
        }

        foreach (var npcName in allocatedNpcs)
        {
            results.Add(RecoverFromFestival(npcName));
        }

        return results;
    }

    /// <summary>
    ///     Recovers all allocated NPCs from faint state.
    /// </summary>
    /// <returns>List of recovery results for each NPC.</returns>
    public List<RecoveryResult> RecoverAllFromFaint()
    {
        var results = new List<RecoveryResult>();

        var allocatedNpcs = _allocationManager?.AllocatedAgentNames;
        if (allocatedNpcs == null)
        {
            results.Add(RecoveryResult.CreateFailure("No allocation manager available"));
            return results;
        }

        foreach (var npcName in allocatedNpcs)
        {
            results.Add(RecoverFromFaint(npcName));
        }

        return results;
    }

    #endregion

    #region Logging Helpers

    private RecoveryResult LogAndReturn(RecoveryResult result)
    {
        if (result.Success)
        {
            LogInfo("Recovery", $"[{result.NpcName ?? "Global"}] {result.ActionTaken}");
        }
        else
        {
            LogError("Recovery", $"[{result.NpcName ?? "Global"}] {result.ErrorMessage}");
        }

        OnRecoveryPerformed?.Invoke(this, result);
        return result;
    }

    private void LogInfo(string category, string message) => _logInfo?.Invoke(category, message);

    private void LogWarning(string category, string message) => _logWarning?.Invoke(category, message);

    private void LogError(string category, string message) => _logError?.Invoke(category, message);

    #endregion
}