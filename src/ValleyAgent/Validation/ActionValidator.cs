using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Validation;

/// <summary>
///     Validates agent actions with comprehensive security and game rule checks.
///     Performs the following validations in order (fastest first):
///     1. Whitelist check - action must be in allowed set
///     2. State transition check - action must be valid from current state
///     3. Target existence check - target must exist in current location or be player
///     4. Distance check - agent must be within range of target
///     5. Capability check - agent must have required skills/abilities
///     6. Frequency/cooldown check - action must not be on cooldown
///     Thread-safe. All shared state uses ConcurrentDictionary.
/// </summary>
public class ActionValidator : IActionValidator
{
    // Whitelist of allowed actions - security boundary
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "follow",
        "fight",
        "farm",
        "forage",
        "mine",
        "idle",
        "talk",
        "give_gift"
    };

    // Mapping from action name to required agent state(s)
    private static readonly Dictionary<string, HashSet<AgentState>> ValidStatesForAction =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["idle"] = new HashSet<AgentState>
            {
                AgentState.IDLE, AgentState.FOLLOW, AgentState.FIGHT, AgentState.FARM, AgentState.FORAGE,
                AgentState.MINE, AgentState.TALK
            },
            ["follow"] = new HashSet<AgentState> { AgentState.IDLE, AgentState.TALK },
            ["fight"] = new HashSet<AgentState> { AgentState.IDLE, AgentState.FOLLOW, AgentState.MINE },
            ["farm"] = new HashSet<AgentState> { AgentState.IDLE },
            ["forage"] = new HashSet<AgentState> { AgentState.IDLE },
            ["mine"] = new HashSet<AgentState> { AgentState.IDLE },
            ["talk"] = new HashSet<AgentState> { AgentState.IDLE, AgentState.FOLLOW },
            ["give_gift"] = new HashSet<AgentState> { AgentState.IDLE, AgentState.TALK }
        };

    // Maximum allowed distance per action (in game tiles/units)
    private static readonly Dictionary<string, float> MaxDistanceForAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["follow"] = 15f,
        ["fight"] = 10f,
        ["farm"] = 5f,
        ["forage"] = 8f,
        ["mine"] = 5f,
        ["idle"] = float.MaxValue, // No distance limit for idle
        ["talk"] = 5f,
        ["give_gift"] = 3f
    };

    // Required capabilities per action
    private static readonly Dictionary<string, HashSet<string>> RequiredCapabilities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["follow"] = new HashSet<string>(),
            ["fight"] = new HashSet<string> { "can_fight" },
            ["farm"] = new HashSet<string> { "can_farm" },
            ["forage"] = new HashSet<string> { "can_forage" },
            ["mine"] = new HashSet<string> { "can_mine" },
            ["idle"] = new HashSet<string>(),
            ["talk"] = new HashSet<string> { "can_talk" },
            ["give_gift"] = new HashSet<string> { "can_gift" }
        };

    // Cooldown durations per action
    private static readonly Dictionary<string, TimeSpan> CooldownDurations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["follow"] = TimeSpan.Zero,
        ["fight"] = TimeSpan.FromSeconds(2),
        ["farm"] = TimeSpan.Zero,
        ["forage"] = TimeSpan.FromSeconds(5),
        ["mine"] = TimeSpan.FromSeconds(3),
        ["idle"] = TimeSpan.Zero,
        ["talk"] = TimeSpan.Zero,
        ["give_gift"] = TimeSpan.FromDays(1) // Max 1 per day
    };

    // Thread-safe tracking of last action execution times per agent
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _lastActionTimes;

    // Thread-safe lock for registration operations
    private readonly object _registrationLock = new();

    /// <summary>
    ///     Creates a new ActionValidator.
    /// </summary>
    public ActionValidator()
    {
        _lastActionTimes = new ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>>();
    }

    /// <summary>
    ///     Read-only view of allowed actions for inspection.
    /// </summary>
    public static IReadOnlyCollection<string> AllowedActionsList
    {
        get => AllowedActions;
    }

    /// <summary>
    ///     Validates whether an action can be performed given the current context.
    ///     Checks are ordered from fastest to slowest for early rejection.
    /// </summary>
    /// <param name="context">Validation context containing action details and game state.</param>
    /// <returns>ValidationResult indicating success or failure with reason.</returns>
    public ValidationResult Validate(ValidationContext context)
    {
        if (context == null)
        {
            return ValidationResult.Fail("Validation context cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(context.AgentId))
        {
            return ValidationResult.Fail("AgentId is required in validation context.");
        }

        if (string.IsNullOrWhiteSpace(context.Action))
        {
            return ValidationResult.Fail("Action is required in validation context.");
        }

        // 1. Whitelist check - fastest, O(1) hash lookup
        if (!IsActionAllowed(context.Action))
        {
            return ValidationResult.Fail($"Action '{context.Action}' is not in the allowed actions whitelist.");
        }

        // 2. State transition check - O(1) hash lookup
        if (!IsValidStateForAction(context.Action, context.CurrentState))
        {
            return ValidationResult.Fail(
                $"Action '{context.Action}' cannot be performed while in state '{context.CurrentState}'.");
        }

        // 3. Target existence check - O(1) hash lookup
        var targetValidation = ValidateTarget(context);
        if (!targetValidation.IsValid)
        {
            return targetValidation;
        }

        // 4. Distance check - simple math, fast
        var distanceValidation = ValidateDistance(context);
        if (!distanceValidation.IsValid)
        {
            return distanceValidation;
        }

        // 5. Capability check - O(n) set intersection, typically small
        var capabilityValidation = ValidateCapabilities(context);
        if (!capabilityValidation.IsValid)
        {
            return capabilityValidation;
        }

        // 6. Frequency/cooldown check - O(1) dictionary lookup
        var cooldownValidation = ValidateCooldown(context);
        if (!cooldownValidation.IsValid)
        {
            return cooldownValidation;
        }

        // Record the action time for cooldown tracking
        RecordActionTime(context);

        return ValidationResult.Success;
    }

    /// <summary>
    ///     Registers a new allowed action with validation rules.
    ///     Thread-safe. Can be called at runtime to add new action types.
    /// </summary>
    /// <param name="action">The action name (case-insensitive).</param>
    /// <param name="validStates">States from which this action can be performed.</param>
    /// <param name="maxDistance">Maximum allowed distance to target (use float.MaxValue for no limit).</param>
    /// <param name="requiredCapabilities">Capabilities required to perform this action.</param>
    /// <param name="cooldown">Cooldown duration between executions.</param>
    public void RegisterAction(
        string action,
        IEnumerable<AgentState> validStates,
        float maxDistance,
        IEnumerable<string>? requiredCapabilities = null,
        TimeSpan? cooldown = null)
    {
        lock (_registrationLock)
        {
            _ = AllowedActions.Add(action);
            ValidStatesForAction[action] = new HashSet<AgentState>(validStates);
            MaxDistanceForAction[action] = maxDistance;
            RequiredCapabilities[action] = new HashSet<string>(requiredCapabilities ?? Enumerable.Empty<string>());
            CooldownDurations[action] = cooldown ?? TimeSpan.Zero;
        }
    }

    /// <summary>
    ///     Checks if an action is in the allowed whitelist.
    /// </summary>
    /// <param name="action">The action name to check.</param>
    /// <returns>True if the action is allowed, false otherwise.</returns>
    public static bool IsActionAllowed(string action) => AllowedActions.Contains(action);

    /// <summary>
    ///     Checks if an action is valid from the given state.
    /// </summary>
    /// <param name="action">The action name.</param>
    /// <param name="state">The current agent state.</param>
    /// <returns>True if the action can be performed from the given state.</returns>
    public static bool IsValidStateForAction(string action, AgentState state) =>
        ValidStatesForAction.TryGetValue(action, out var validStates) && validStates.Contains(state);

    /// <summary>
    ///     Validates that the target exists in the current location or is the player.
    /// </summary>
    private static ValidationResult ValidateTarget(ValidationContext context)
    {
        // If no target is needed, it's valid
        if (!RequiresTarget(context.Action))
        {
            return ValidationResult.Success;
        }

        // Must have a target ID
        if (string.IsNullOrEmpty(context.TargetId))
        {
            return ValidationResult.Fail($"Action '{context.Action}' requires a target but none was specified.");
        }

        // Target is the player - always valid
        if (context.TargetId == context.PlayerId)
        {
            return ValidationResult.Success;
        }

        // Target must exist in current location
        return !context.TargetsInLocation.Contains(context.TargetId)
            ? ValidationResult.Fail(
                $"Target '{context.TargetId}' does not exist in location '{context.CurrentLocation}'.")
            : ValidationResult.Success;
    }

    /// <summary>
    ///     Determines if an action requires a target.
    /// </summary>
    private static bool RequiresTarget(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "follow" => true,
            "fight" => true,
            "talk" => true,
            "give_gift" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Validates that the agent is within allowed distance of the target.
    /// </summary>
    private static ValidationResult ValidateDistance(ValidationContext context)
    {
        if (!RequiresTarget(context.Action))
        {
            return ValidationResult.Success;
        }

        // If no target position, can't validate distance - reject for safety
        if (!context.TargetPosition.HasValue)
        {
            return ValidationResult.Fail(
                $"Action '{context.Action}' requires target position for distance validation.");
        }

        var maxDistance = GetMaxDistance(context.Action, context.MaxDistanceOverride);
        if (maxDistance == float.MaxValue)
        {
            return ValidationResult.Success;
        }

        var dx = context.AgentPosition.X - context.TargetPosition.Value.X;
        var dy = context.AgentPosition.Y - context.TargetPosition.Value.Y;
        var distance = MathF.Sqrt(dx * dx + dy * dy);

        return distance > maxDistance
            ? ValidationResult.Fail(
                $"Target is too far away ({distance:F1} units). Maximum distance for '{context.Action}' is {maxDistance} units.")
            : ValidationResult.Success;
    }

    /// <summary>
    ///     Gets the maximum allowed distance for an action.
    /// </summary>
    private static float GetMaxDistance(string action, float? overrideValue) => overrideValue ??
                                                                                (MaxDistanceForAction.TryGetValue(action, out var distance) ? distance : 10f);

    /// <summary>
    ///     Validates that the agent has all required capabilities for the action.
    /// </summary>
    private static ValidationResult ValidateCapabilities(ValidationContext context)
    {
        if (!RequiredCapabilities.TryGetValue(context.Action, out var required))
        {
            return ValidationResult.Success; // No requirements
        }

        var missing = required.Where(cap => !context.AgentCapabilities.Contains(cap)).ToList();
        return missing.Count > 0
            ? ValidationResult.Fail(
                $"Agent lacks required capabilities for '{context.Action}'. Missing: {string.Join(", ", missing)}.")
            : ValidationResult.Success;
    }

    /// <summary>
    ///     Validates that the action is not on cooldown.
    /// </summary>
    private ValidationResult ValidateCooldown(ValidationContext context)
    {
        var cooldown = GetCooldownDuration(context.Action);
        if (cooldown <= TimeSpan.Zero)
        {
            return ValidationResult.Success;
        }

        if (!_lastActionTimes.TryGetValue(context.AgentId, out var actionTimes))
        {
            return ValidationResult.Success;
        }

        if (!actionTimes.TryGetValue(context.Action, out var lastTime))
        {
            return ValidationResult.Success;
        }

        var elapsed = context.CurrentTime - lastTime;
        if (elapsed < cooldown)
        {
            var remaining = cooldown - elapsed;
            return ValidationResult.Fail(
                $"Action '{context.Action}' is on cooldown. Available in {remaining.TotalSeconds:F1} seconds.");
        }

        return ValidationResult.Success;
    }

    /// <summary>
    ///     Records the current time as the last execution time for an action.
    /// </summary>
    private void RecordActionTime(ValidationContext context)
    {
        var cooldown = GetCooldownDuration(context.Action);
        if (cooldown <= TimeSpan.Zero)
        {
            return;
        }

        var actionTimes = _lastActionTimes.GetOrAdd(context.AgentId, _ => new ConcurrentDictionary<string, DateTime>());
        actionTimes[context.Action] = context.CurrentTime;
    }

    /// <summary>
    ///     Gets the cooldown duration for an action.
    /// </summary>
    private static TimeSpan GetCooldownDuration(string action) =>
        CooldownDurations.TryGetValue(action, out var cooldown) ? cooldown : TimeSpan.Zero;
}