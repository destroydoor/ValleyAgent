using System;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Agents;

/// <summary>
///     Event arguments for agent allocation and deallocation notifications.
/// </summary>
public class AgentAllocationEventArgs : EventArgs
{
    public AgentAllocationEventArgs(string npcName, AgentState currentState, bool isManualOverride, string reason)
    {
        NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        CurrentState = currentState;
        IsManualOverride = isManualOverride;
        Reason = reason ?? string.Empty;
    }

    /// <summary>
    ///     The name of the NPC being allocated or deallocated.
    /// </summary>
    public string NpcName { get; }

    /// <summary>
    ///     The current state of the NPC at the time of the event.
    /// </summary>
    public AgentState CurrentState { get; }

    /// <summary>
    ///     Whether this allocation was a manual override by the player.
    /// </summary>
    public bool IsManualOverride { get; }

    /// <summary>
    ///     The reason for the allocation change.
    /// </summary>
    public string Reason { get; }
}