using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;

namespace ValleyAgent.RAG;

/// <summary>
///     Loads ValleyTalk's GameSummary.json — comprehensive game world knowledge
///     (seasons, locations, festivals, villagers, farmer background).
///     Injects relevant world context into decision prompts so the LLM understands
///     the game world without needing to be a Stardew Valley expert.
///     Design (AGENTS.md P1):
///     - Season-specific crops and forageables
///     - Current location description
///     - Active festival awareness
///     - Villager relationships and descriptions
/// </summary>
public class GameSummaryLoader
{
    private readonly IMonitor _monitor;

    private GameSummaryData? _data;
    private IModHelper? _helper;

    public GameSummaryLoader(IMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    /// <summary>Whether the data has been loaded.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    ///     Initialize with the SMAPI helper so we can resolve paths relative to the mod.
    /// </summary>
    public void Initialize(IModHelper helper) => _helper = helper;

    /// <summary>
    ///     Load GameSummary.json from [CP] ValleyTalk Base/assets/.
    /// </summary>
    public void Load()
    {
        if (_helper == null)
        {
            _monitor.Log("GameSummaryLoader: helper not initialized, cannot load.", LogLevel.Warn);
            return;
        }

        try
        {
            var summaryPath = ResolveGameSummaryPath();
            if (!File.Exists(summaryPath))
            {
                _monitor.Log($"GameSummaryLoader: not found at '{summaryPath}'.", LogLevel.Warn);
                return;
            }

            var json = File.ReadAllText(summaryPath);
            _data = ParseGameSummary(json);

            if (_data != null)
            {
                IsLoaded = true;
                _monitor.Log($"GameSummaryLoader: loaded successfully. " +
                             $"Seasons={_data.Seasons.Count}, Locations={_data.Locations.Count}, " +
                             $"Festivals={_data.Festivals.Count}, Villagers={_data.Villagers.Count}",
                    LogLevel.Info);
            }
        }
        catch (IOException ex)
        {
            _monitor.Log($"GameSummaryLoader: failed to load: {ex.Message}", LogLevel.Error);
        }
        catch (JsonException ex)
        {
            _monitor.Log($"GameSummaryLoader: failed to load: {ex.Message}", LogLevel.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            _monitor.Log($"GameSummaryLoader: failed to load: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     Get season info for prompt injection — crops and forageables available.
    /// </summary>
    public string? GetSeasonInfo(string seasonName)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(seasonName))
        {
            return null;
        }

        var season = _data!.Seasons.Values.FirstOrDefault(s =>
            s.Name.Equals(seasonName, StringComparison.OrdinalIgnoreCase));

        if (season == null)
        {
            return null;
        }

        var parts = new List<string>();
        if (season.Crops?.Count > 0)
        {
            parts.Add($"Seasonal crops: {string.Join(", ", season.Crops)}");
        }

        if (season.Forage?.Count > 0)
        {
            parts.Add($"Forageables: {string.Join(", ", season.Forage)}");
        }

        return parts.Count > 0 ? string.Join("\n- ", parts) : null;
    }

    /// <summary>
    ///     Get the description of a specific location.
    /// </summary>
    public string? GetLocationDescription(string locationName)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(locationName))
        {
            return null;
        }

        // Try exact match first, then substring match
        var entry = _data!.Locations.Values.FirstOrDefault(l =>
            l.Id.Equals(locationName, StringComparison.OrdinalIgnoreCase));

        entry ??= _data.Locations.Values.FirstOrDefault(l =>
            l.Id.EndsWith(locationName, StringComparison.OrdinalIgnoreCase) ||
            l.Name.Contains(locationName, StringComparison.OrdinalIgnoreCase));

        return entry?.Description;
    }

    /// <summary>
    ///     Get the festival info for a specific date ("{season}{day}" e.g. "spring13").
    /// </summary>
    public string? GetFestivalForDate(string seasonName, int day)
    {
        if (!IsLoaded)
        {
            return null;
        }

        var key = $"{seasonName?.ToLowerInvariant()}{day}";
        return _data!.Festivals.TryGetValue(key, out var festival) ? $"{festival.Name}: {festival.Description}" : null;
    }

    /// <summary>
    ///     Get a villager description from GameSummary (lighter than bio/*.json).
    /// </summary>
    public string? GetVillagerDescription(string npcName)
    {
        return !IsLoaded || string.IsNullOrWhiteSpace(npcName)
            ? null
            : _data!.Villagers.TryGetValue(npcName, out var villager)
                ? villager.Description
                : null;
    }

    /// <summary>
    ///     Build a world knowledge summary for the current game context.
    ///     Injects: current season info, location description, active festival.
    ///     Returns null if nothing to inject.
    /// </summary>
    public string? BuildWorldKnowledgeSummary(string season, string location, int day)
    {
        if (!IsLoaded)
        {
            return null;
        }

        var parts = new List<string>();

        // Season info
        var seasonInfo = GetSeasonInfo(season);
        if (!string.IsNullOrWhiteSpace(seasonInfo))
        {
            parts.Add($"[{season}] {seasonInfo}");
        }

        // Location description
        var locDesc = GetLocationDescription(location);
        if (!string.IsNullOrWhiteSpace(locDesc))
        {
            parts.Add($"[Location: {location}] {locDesc}");
        }

        // Active festival
        var festival = GetFestivalForDate(season, day);
        if (!string.IsNullOrWhiteSpace(festival))
        {
            parts.Add($"[Festival Today] {festival}");
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    private string ResolveGameSummaryPath()
    {
        var modDir = _helper!.DirectoryPath;

        // Priority 1: SVE GameSummary (ValleyTalk for SVE/assets/GameSummary.json)
        var svePath = Path.GetFullPath(Path.Combine(modDir, "..", "ValleyTalk for SVE", "assets", "GameSummary.json"));
        if (File.Exists(svePath))
        {
            return svePath;
        }

        // Priority 2: Base GameSummary ([CP] ValleyTalk Base/assets/GameSummary.json)
        var basePath =
            Path.GetFullPath(Path.Combine(modDir, "..", "[CP] ValleyTalk Base", "assets", "GameSummary.json"));
        if (File.Exists(basePath))
        {
            return basePath;
        }

        // Priority 3: Search for any [CP] ValleyTalk Base* directory (locale variants)
        var parentDir = Path.GetFullPath(Path.Combine(modDir, ".."));
        try
        {
            var candidates = Directory.GetDirectories(parentDir, "[CP] ValleyTalk Base*");
            foreach (var candidate in candidates)
            {
                var gsCandidate = Path.Combine(candidate, "assets", "GameSummary.json");
                if (File.Exists(gsCandidate))
                {
                    return gsCandidate;
                }
            }
        }
        catch (IOException)
        {
            // Ignore directory enumeration errors
        }

        // Fallback: return base path even if not found (caller will handle missing file)
        return basePath;
    }

    private static GameSummaryData? ParseGameSummary(string json)
    {
        try
        {
            // GameSummary.json wraps data inside Content Patcher format:
            // { "Changes": [{ "Action": "EditData", "Target": "ValleyTalk/GameSummary", "Entries": { ... } }] }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Changes", out var changes) || changes.GetArrayLength() == 0)
            {
                return null;
            }

            var change = changes[0];
            if (!change.TryGetProperty("Entries", out var entries))
            {
                return null;
            }

            var data = new GameSummaryData();

            // SectionOrder
            if (entries.TryGetProperty("SectionOrder", out var sectionOrder))
            {
                foreach (var prop in sectionOrder.EnumerateObject())
                {
                    data.SectionOrder[prop.Name] = prop.Value.GetBoolean();
                }
            }

            // Seasons
            if (entries.TryGetProperty("Seasons", out var seasons) &&
                seasons.TryGetProperty("Entries", out var seasonEntries))
            {
                foreach (var prop in seasonEntries.EnumerateObject())
                {
                    var entry = ParseSectionEntry(prop.Value);
                    if (entry != null)
                    {
                        var season = new GameSeasonData
                        {
                            Id = entry.Id,
                            Name = entry.Name,
                            Description = entry.Description,
                            Crops = entry.GetStringList("Crops"),
                            Forage = entry.GetStringList("Forage")
                        };
                        data.Seasons[prop.Name] = season;
                    }
                }
            }

            // Locations
            if (entries.TryGetProperty("Locations", out var locations) &&
                locations.TryGetProperty("Entries", out var locationEntries))
            {
                foreach (var prop in locationEntries.EnumerateObject())
                {
                    var entry = ParseSectionEntry(prop.Value);
                    if (entry != null)
                    {
                        data.Locations[prop.Name] = new GameLocationData
                        {
                            Id = entry.Id,
                            Region = entry.GetString("Region"),
                            Name = entry.Name,
                            Description = entry.Description
                        };
                    }
                }
            }

            // Festivals
            if (entries.TryGetProperty("Festivals", out var festivals) &&
                festivals.TryGetProperty("Entries", out var festivalEntries))
            {
                foreach (var prop in festivalEntries.EnumerateObject())
                {
                    var entry = ParseSectionEntry(prop.Value);
                    if (entry != null)
                    {
                        data.Festivals[prop.Name] = new GameFestivalData
                        {
                            Id = entry.Id,
                            Name = entry.Name,
                            Description = entry.Description
                        };
                    }
                }
            }

            // Villagers
            if (entries.TryGetProperty("Villagers", out var villagers) &&
                villagers.TryGetProperty("Entries", out var villagerEntries))
            {
                foreach (var prop in villagerEntries.EnumerateObject())
                {
                    var entry = ParseSectionEntry(prop.Value);
                    if (entry != null)
                    {
                        data.Villagers[prop.Name] = new GameVillagerData
                        {
                            Id = entry.Id,
                            Name = entry.Name,
                            Description = entry.Description
                        };
                    }
                }
            }

            return data;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Parses a single section entry. Each entry is like:
    ///     { "id": "...", "Name": "...", "Description": "...", ...other fields... }
    /// </summary>
    private static SectionEntry? ParseSectionEntry(JsonElement element)
    {
        try
        {
            var entry = new SectionEntry();

            if (element.TryGetProperty("id", out var idProp))
            {
                entry.Id = idProp.GetString() ?? "";
            }

            if (element.TryGetProperty("Name", out var nameProp))
            {
                entry.Name = nameProp.GetString() ?? "";
            }

            if (element.TryGetProperty("Description", out var descProp))
            {
                entry.Description = descProp.GetString() ?? "";
            }

            // Store the raw element for fields parsed later per-section
            entry.Raw = element;

            return entry;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

#region Data Models

/// <summary>
///     Parsed GameSummary.json root data.
/// </summary>
internal class GameSummaryData
{
    public Dictionary<string, bool> SectionOrder { get; set; } = new();
    public Dictionary<string, GameSeasonData> Seasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GameLocationData> Locations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GameFestivalData> Festivals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GameVillagerData> Villagers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal class GameSeasonData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Crops { get; set; } = new();
    public List<string> Forage { get; set; } = new();
}

internal class GameLocationData
{
    public string Id { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

internal class GameFestivalData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

internal class GameVillagerData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
///     Parsed section entry from a GameSummary section.
/// </summary>
internal class SectionEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonElement Raw { get; set; }

    /// <summary>
    ///     Get a string property from the raw JSON element.
    /// </summary>
    public string GetString(string propertyName)
    {
        return Raw.ValueKind != JsonValueKind.Object
            ? string.Empty
            : Raw.TryGetProperty(propertyName, out var prop)
                ? prop.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>
    ///     Get a string list from the raw JSON element.
    /// </summary>
    public List<string> GetStringList(string propertyName)
    {
        var result = new List<string>();
        if (Raw.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (Raw.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                var str = item.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    result.Add(str);
                }
            }
        }

        return result;
    }
}

#endregion