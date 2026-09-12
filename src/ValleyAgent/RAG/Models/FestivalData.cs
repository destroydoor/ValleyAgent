using System.Collections.Generic;

namespace ValleyAgent.RAG.Models;

/// <summary>
///     Represents a Stardew Valley festival with date, participants, and details.
/// </summary>
public class FestivalData
{
    /// <summary>Festival ID (e.g., "spring13").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name of the festival.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Season the festival occurs in.</summary>
    public string Season { get; set; } = string.Empty;

    /// <summary>Day of the season the festival occurs.</summary>
    public int Day { get; set; }

    /// <summary>Description of the festival.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>NPCs who attend this festival.</summary>
    public List<string> ParticipatingNpcs { get; set; } = new();

    /// <summary>Location where the festival takes place.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Start time in game format (e.g., "9:00 AM").</summary>
    public string StartTime { get; set; } = string.Empty;

    /// <summary>Special items or activities at this festival.</summary>
    public List<string> Activities { get; set; } = new();
}