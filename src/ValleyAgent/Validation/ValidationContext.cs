using System;
using System.Collections.Generic;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Validation;

/// <summary>
///     Context information required to validate an action.
///     Game-agnostic - uses primitive types only, no SMAPI dependencies.
/// </summary>
public class ValidationContext
{
    /// <summary>
    ///     Unique identifier of the agent performing the action.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    ///     The action being requested (e.g., "follow", "fight", "give_gift").
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    ///     The agent's current state.
    /// </summary>
    public AgentState CurrentState { get; set; }

    /// <summary>
    ///     Unique identifier of the target entity (NPC, player, object).
    ///     Null if the action has no target.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    ///     The agent's current position in the game world (X, Y).
    /// </summary>
    public (float X, float Y) AgentPosition { get; set; }

    /// <summary>
    ///     The target's position in the game world. Null if target is not in the world or action has no target.
    /// </summary>
    public (float X, float Y)? TargetPosition { get; set; }

    /// <summary>
    ///     Identifier of the current location/map (e.g., "Farm", "Town").
    /// </summary>
    public string CurrentLocation { get; set; } = string.Empty;

    /// <summary>
    ///     The current game time (used for cooldown/frequency validation).
    /// </summary>
    public DateTime CurrentTime { get; set; }

    /// <summary>
    ///     Set of capabilities the agent possesses (e.g., "can_fight", "can_mine").
    /// </summary>
    public HashSet<string> AgentCapabilities { get; set; } = new();

    /// <summary>
    ///     Set of entity IDs present in the current location.
    ///     Used for target existence validation.
    /// </summary>
    public HashSet<string> TargetsInLocation { get; set; } = new();

    /// <summary>
    ///     The player entity ID. Used to identify the player target.
    /// </summary>
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>
    ///     Maximum allowed distance for the action (optional override).
    ///     If not set, uses the default for the action type.
    /// </summary>
    public float? MaxDistanceOverride { get; set; }
}