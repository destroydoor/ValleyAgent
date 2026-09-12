using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ValleyAgent.RAG.Models;

/// <summary>
///     Represents a Stardew Valley NPC with personality, preferences, and relationship data.
/// </summary>
public class NpcData
{
    /// <summary>Internal game name (e.g., "Abigail").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display name, same as Name for vanilla NPCs.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Birthday in "Season Day" format (e.g., "Fall 13").</summary>
    public string Birthday { get; set; } = string.Empty;

    /// <summary>Whether this NPC can be romanced.</summary>
    public bool IsRomanceable { get; set; }

    /// <summary>Whether this NPC is datable (subset of romanceable).</summary>
    public bool IsDatable { get; set; }

    /// <summary>Home location name (e.g., "Pelican Town", "Desert").</summary>
    public string HomeLocation { get; set; } = string.Empty;

    /// <summary>Short personality description.</summary>
    public string Personality { get; set; } = string.Empty;

    /// <summary>Key personality traits.</summary>
    public List<string> Traits { get; set; } = new();

    /// <summary>Items this NPC loves receiving as gifts.</summary>
    public List<string> LovedGifts { get; set; } = new();

    /// <summary>Items this NPC likes receiving as gifts.</summary>
    public List<string> LikedGifts { get; set; } = new();

    /// <summary>Items this NPC is neutral about receiving.</summary>
    public List<string> NeutralGifts { get; set; } = new();

    /// <summary>Items this NPC dislikes receiving.</summary>
    public List<string> DislikedGifts { get; set; } = new();

    /// <summary>Items this NPC hates receiving.</summary>
    public List<string> HatedGifts { get; set; } = new();

    /// <summary>Universal gift categories this NPC loves (e.g., "All Universal Loves").</summary>
    public List<string> UniversalLoves { get; set; } = new();

    /// <summary>Universal gift categories this NPC likes.</summary>
    public List<string> UniversalLikes { get; set; } = new();

    /// <summary>Universal gift categories this NPC dislikes.</summary>
    public List<string> UniversalDislikes { get; set; } = new();

    /// <summary>Universal gift categories this NPC hates.</summary>
    public List<string> UniversalHates { get; set; } = new();

    /// <summary>Key relationships with other NPCs.</summary>
    public Dictionary<string, string> Relationships { get; set; } = new();

    /// <summary>Short description of the NPC.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
///     Gift preference level for an NPC.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GiftTaste
{
    Love,
    Like,
    Neutral,
    Dislike,
    Hate
}