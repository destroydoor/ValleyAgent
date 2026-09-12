using System.Collections.Generic;

namespace ValleyAgent.RAG.Models;

/// <summary>
///     Represents a Stardew Valley item (crop, mineral, fish, forage, cooked dish, etc.).
/// </summary>
public class ItemData
{
    /// <summary>Internal item ID (matches game Data/Objects key).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name of the item.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Item category (e.g., "Crop", "Mineral", "Fish", "Forage", "Cooking", "Animal Product").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Sub-category for more specific classification.</summary>
    public string SubType { get; set; } = string.Empty;

    /// <summary>Seasons this item is available (empty for year-round).</summary>
    public List<string> Seasons { get; set; } = new();

    /// <summary>Sell price in gold.</summary>
    public int SellPrice { get; set; }

    /// <summary>Whether this is a universally loved/liked item.</summary>
    public string UniversalGiftTaste { get; set; } = string.Empty;

    /// <summary>Additional description or notes.</summary>
    public string Description { get; set; } = string.Empty;
}