using System;

namespace ValleyAgent.Save.Models;

/// <summary>
///     A single dialogue exchange between player and NPC.
/// </summary>
public class DialogueExchangeData
{
    /// <summary>What the player said.</summary>
    public string PlayerInput { get; set; } = string.Empty;

    /// <summary>What the NPC responded.</summary>
    public string NpcResponse { get; set; } = string.Empty;

    /// <summary>When this exchange occurred (real world time).</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}