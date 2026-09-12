using System.Collections.Generic;

namespace ValleyAgent.RAG.Models;

/// <summary>
///     Represents a Stardew Valley location with residents, resources, and forage.
/// </summary>
public class LocationData
{
    /// <summary>Internal location ID (e.g., "TheFarm", "PelicanTown").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name of the location.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Region this location belongs to (e.g., "Pelican Town", "Mountain").</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Description of the location.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>NPCs who live at or frequent this location.</summary>
    public List<string> Residents { get; set; } = new();

    /// <summary>Resources available at this location (e.g., "Copper Ore", "Oak Wood").</summary>
    public List<string> Resources { get; set; } = new();

    /// <summary>Forage items found here by season.</summary>
    public Dictionary<string, List<string>> ForageBySeason { get; set; } = new();

    /// <summary>Whether this location requires unlock conditions.</summary>
    public string UnlockCondition { get; set; } = string.Empty;
}