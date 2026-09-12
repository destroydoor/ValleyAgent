using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using ValleyAgent.RAG.Models;

namespace ValleyAgent.RAG;

/// <summary>
///     Static JSON-based knowledge base for Stardew Valley game data.
///     Loads data on GameLaunched and provides O(1) case-insensitive lookups.
/// </summary>
public class RAGKnowledgeBase
{
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    // Reverse lookup: season -> list of festivals
    private readonly Dictionary<string, List<FestivalData>> _festivalsBySeason = new(StringComparer.OrdinalIgnoreCase);

    // Reverse lookup: item name -> list of NPCs who love/like/etc. that item
    private readonly Dictionary<string, Dictionary<GiftTaste, List<string>>> _itemGiftPreferences =
        new(StringComparer.OrdinalIgnoreCase);

    // Reverse lookup: location name -> list of NPCs who live there
    private readonly Dictionary<string, List<string>> _locationResidents = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor _monitor;

    // Reverse lookup: NPC name -> list of items they love/like/etc.
    private readonly Dictionary<string, Dictionary<GiftTaste, List<string>>> _npcGiftPreferences =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, FestivalData> _festivals = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ItemData> _itemsById = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ItemData> _itemsByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LocationData> _locations = new(StringComparer.OrdinalIgnoreCase);
    private GameMechanics? _mechanics;

    // Case-insensitive dictionaries for O(1) lookup by name
    private Dictionary<string, NpcData> _npcs = new(StringComparer.OrdinalIgnoreCase);

    public RAGKnowledgeBase(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>Whether the knowledge base has been loaded.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    ///     Loads all JSON data files from the mod's RAG directory.
    ///     Should be called in the GameLaunched event.
    /// </summary>
    public void Load(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);

        try
        {
            var ragDir = Path.Combine(helper.DirectoryPath, "RAG");

            // 事务式加载：先加载到临时变量，全部成功后再赋值
            var npcs = LoadJson<List<NpcData>>(Path.Combine(ragDir, "npcs.json"));
            var items = LoadJson<List<ItemData>>(Path.Combine(ragDir, "items.json"));
            var locations = LoadJson<List<LocationData>>(Path.Combine(ragDir, "locations.json"));
            var festivals = LoadJson<List<FestivalData>>(Path.Combine(ragDir, "festivals.json"));
            var mechanics = LoadJson<GameMechanics>(Path.Combine(ragDir, "mechanics.json"));

            // 全部加载成功，构建字典
            _npcs = npcs.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
            _itemsById = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _itemsByName = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!_itemsById.ContainsKey(item.Id))
                {
                    _itemsById[item.Id] = item;
                }
                else
                {
                    _monitor.LogOnce($"RAG items.json duplicate ID '{item.Id}' for '{item.Name}' skipped",
                        LogLevel.Warn);
                }

                if (!_itemsByName.ContainsKey(item.Name))
                {
                    _itemsByName[item.Name] = item;
                }
                else
                {
                    _monitor.LogOnce($"RAG items.json duplicate Name '{item.Name}' skipped", LogLevel.Warn);
                }
            }

            _locations = locations.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            _festivals = festivals.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            _mechanics = mechanics;

            BuildReverseLookups();

            IsLoaded = true;
            _monitor.Log($"RAG Knowledge Base loaded: {_npcs.Count} NPCs, {items.Count} items, " +
                         $"{_locations.Count} locations, {_festivals.Count} festivals", LogLevel.Info);
        }
        catch (IOException ex)
        {
            _npcs = new Dictionary<string, NpcData>(StringComparer.OrdinalIgnoreCase);
            _itemsById = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _itemsByName = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _locations = new Dictionary<string, LocationData>(StringComparer.OrdinalIgnoreCase);
            _festivals = new Dictionary<string, FestivalData>(StringComparer.OrdinalIgnoreCase);
            _mechanics = new GameMechanics();
            IsLoaded = false;
            _monitor.Log($"Failed to load RAG Knowledge Base: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.StackTrace ?? "No stack trace available");
        }
        catch (JsonException ex)
        {
            _npcs = new Dictionary<string, NpcData>(StringComparer.OrdinalIgnoreCase);
            _itemsById = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _itemsByName = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _locations = new Dictionary<string, LocationData>(StringComparer.OrdinalIgnoreCase);
            _festivals = new Dictionary<string, FestivalData>(StringComparer.OrdinalIgnoreCase);
            _mechanics = new GameMechanics();
            IsLoaded = false;
            _monitor.Log($"Failed to load RAG Knowledge Base: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.StackTrace ?? "No stack trace available");
        }
        catch (InvalidOperationException ex)
        {
            _npcs = new Dictionary<string, NpcData>(StringComparer.OrdinalIgnoreCase);
            _itemsById = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _itemsByName = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            _locations = new Dictionary<string, LocationData>(StringComparer.OrdinalIgnoreCase);
            _festivals = new Dictionary<string, FestivalData>(StringComparer.OrdinalIgnoreCase);
            _mechanics = new GameMechanics();
            IsLoaded = false;
            _monitor.Log($"Failed to load RAG Knowledge Base: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.StackTrace ?? "No stack trace available");
        }
    }

    private static T LoadJson<T>(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<T>(json, CachedJsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize {filePath}");
    }

    private void BuildReverseLookups()
    {
        // Build NPC gift preference lookups
        foreach (var npc in _npcs.Values)
        {
            var prefs = new Dictionary<GiftTaste, List<string>>
            {
                [GiftTaste.Love] = npc.LovedGifts,
                [GiftTaste.Like] = npc.LikedGifts,
                [GiftTaste.Neutral] = npc.NeutralGifts,
                [GiftTaste.Dislike] = npc.DislikedGifts,
                [GiftTaste.Hate] = npc.HatedGifts
            };
            _npcGiftPreferences[npc.Name] = prefs;

            // Build reverse: item -> NPCs who prefer it (所有五种 GiftTaste)
            var allGifts = new[]
            {
                (GiftTaste.Love, npc.LovedGifts),
                (GiftTaste.Like, npc.LikedGifts),
                (GiftTaste.Neutral, npc.NeutralGifts),
                (GiftTaste.Dislike, npc.DislikedGifts),
                (GiftTaste.Hate, npc.HatedGifts)
            };
            foreach (var (taste, items) in allGifts)
            {
                foreach (var item in items)
                {
                    if (!_itemGiftPreferences.TryGetValue(item, out var dict))
                    {
                        dict = new Dictionary<GiftTaste, List<string>>();
                        _itemGiftPreferences[item] = dict;
                    }

                    if (!dict.TryGetValue(taste, out var list))
                    {
                        list = new List<string>();
                        dict[taste] = list;
                    }

                    list.Add(npc.Name);
                }
            }
        }

        // Build location -> residents lookup
        foreach (var location in _locations.Values)
        {
            if (location.Residents.Count > 0)
            {
                _locationResidents[location.Name] = location.Residents;
            }
        }

        // Build season -> festivals lookup
        foreach (var festival in _festivals.Values)
        {
            if (!_festivalsBySeason.TryGetValue(festival.Season, out var list))
            {
                list = new List<FestivalData>();
                _festivalsBySeason[festival.Season] = list;
            }

            list.Add(festival);
        }
    }

    // ─── NPC Queries ────────────────────────────────────────────────────

    /// <summary>
    ///     Gets NPC data by name (case-insensitive).
    ///     Returns null if not found.
    /// </summary>
    public NpcData? GetNpcPreferences(string name) => _npcs.TryGetValue(name, out var npc) ? npc : null;

    /// <summary>
    ///     Gets all NPC names in the knowledge base.
    /// </summary>
    public IEnumerable<string> GetAllNpcNames() => _npcs.Keys;

    /// <summary>
    ///     Gets the gift taste (love/like/neutral/dislike/hate) for a specific NPC and item.
    /// </summary>
    public GiftTaste? GetGiftTaste(string npcName, string itemName)
    {
        if (!_npcGiftPreferences.TryGetValue(npcName, out var prefs))
        {
            return null;
        }

        if (prefs[GiftTaste.Love].Any(i => i.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
        {
            return GiftTaste.Love;
        }

        if (prefs[GiftTaste.Like].Any(i => i.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
        {
            return GiftTaste.Like;
        }

        // 使用条件表达式替代 if-return 链
        return prefs[GiftTaste.Neutral].Any(i => i.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            ? GiftTaste.Neutral
            : prefs[GiftTaste.Dislike].Any(i => i.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                ? GiftTaste.Dislike
                : prefs[GiftTaste.Hate].Any(i => i.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                    ? GiftTaste.Hate
                    : null;
    }

    /// <summary>
    ///     Gets all NPCs who love a specific item.
    /// </summary>
    public List<string> GetNpcsWhoLove(string itemName) => GetNpcsByGiftTaste(itemName, GiftTaste.Love);

    /// <summary>
    ///     Gets all NPCs who like a specific item.
    /// </summary>
    public List<string> GetNpcsWhoLike(string itemName) => GetNpcsByGiftTaste(itemName, GiftTaste.Like);

    /// <summary>
    ///     Gets NPCs by their gift taste for an item.
    /// </summary>
    public List<string> GetNpcsByGiftTaste(string itemName, GiftTaste taste)
    {
        return _itemGiftPreferences.TryGetValue(itemName, out var dict)
               && dict.TryGetValue(taste, out var list)
            ? list
            : new List<string>();
    }

    /// <summary>
    ///     Gets NPCs who live at a specific location.
    /// </summary>
    public List<string> GetNpcsAtLocation(string locationName)
    {
        return _locationResidents.TryGetValue(locationName, out var residents)
            ? residents
            : new List<string>();
    }

    // ─── Item Queries ───────────────────────────────────────────────────

    /// <summary>
    ///     Gets item data by name (case-insensitive).
    ///     Returns null if not found.
    /// </summary>
    public ItemData? GetItemInfo(string nameOrId) => _itemsByName.TryGetValue(nameOrId, out var byName) ? byName :
        _itemsById.TryGetValue(nameOrId, out var byId) ? byId : null;

    /// <summary>
    ///     Gets all items of a specific type (e.g., "Crop", "Fish", "Mineral").
    /// </summary>
    public IEnumerable<ItemData> GetItemsByType(string type)
    {
        return _itemsByName.Values.Where(i =>
            i.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Gets all items available in a specific season.
    /// </summary>
    public IEnumerable<ItemData> GetItemsBySeason(string season)
    {
        return _itemsByName.Values.Where(i =>
            i.Seasons.Contains(season, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Gets all items that are universally loved/liked/etc.
    /// </summary>
    public IEnumerable<ItemData> GetUniversalGiftItems(string taste)
    {
        return _itemsByName.Values.Where(i =>
            i.UniversalGiftTaste.Equals(taste, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Location Queries ───────────────────────────────────────────────

    /// <summary>
    ///     Gets location data by name (case-insensitive).
    ///     Returns null if not found.
    /// </summary>
    public LocationData? GetLocationInfo(string name) =>
        _locations.TryGetValue(name, out var location) ? location : null;

    /// <summary>
    ///     Gets all locations in a specific region.
    /// </summary>
    public IEnumerable<LocationData> GetLocationsByRegion(string region)
    {
        return _locations.Values.Where(l =>
            l.Region.Equals(region, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Gets forage items available at a location in a specific season.
    /// </summary>
    public List<string> GetForageAtLocation(string locationName, string season)
    {
        var location = GetLocationInfo(locationName);
        return location == null
            ? new List<string>()
            : location.ForageBySeason.TryGetValue(season, out var forage)
                ? forage
                : new List<string>();
    }

    // ─── Festival Queries ────────────────────────────────────────────────

    /// <summary>
    ///     Gets festival data by name (case-insensitive).
    ///     Returns null if not found.
    /// </summary>
    public FestivalData? GetFestivalInfo(string name) =>
        _festivals.TryGetValue(name, out var festival) ? festival : null;

    /// <summary>
    ///     Gets all festivals in a specific season.
    /// </summary>
    public IEnumerable<FestivalData> GetFestivalsBySeason(string season)
    {
        return _festivalsBySeason.TryGetValue(season, out var list)
            ? list
            : new List<FestivalData>();
    }

    /// <summary>
    ///     Gets the festival happening on a specific date (season + day).
    ///     Returns null if no festival on that date.
    /// </summary>
    public FestivalData? GetFestivalByDate(string season, int day)
    {
        return _festivals.Values.FirstOrDefault(f =>
            f.Season.Equals(season, StringComparison.OrdinalIgnoreCase) && f.Day == day);
    }

    // ─── Game Mechanics Queries ──────────────────────────────────────────

    /// <summary>
    ///     Gets the game mechanics data.
    ///     Returns null if not loaded.
    /// </summary>
    public GameMechanics? GetMechanics() => _mechanics;

    /// <summary>
    ///     Gets the friendship points required for a specific heart level.
    /// </summary>
    public int GetPointsForHearts(int hearts) => hearts * (_mechanics?.Friendship.PointsPerHeart ?? 250);

    /// <summary>
    ///     Gets the heart level for a given number of friendship points.
    /// </summary>
    public int GetHeartsForPoints(int points)
    {
        var pointsPerHeart = _mechanics?.Friendship.PointsPerHeart ?? 250;
        return Math.Min(points / pointsPerHeart, 14);
    }

    /// <summary>
    ///     Calculates the friendship points gained from giving a gift,
    ///     accounting for gift taste and birthday multiplier.
    /// </summary>
    public int CalculateGiftPoints(GiftTaste taste, bool isBirthday = false)
    {
        if (_mechanics == null)
        {
            return 0;
        }

        var tasteKey = taste.ToString();
        var basePoints = _mechanics.Gifts.TasteMultipliers.TryGetValue(tasteKey, out var points)
            ? points
            : 0;

        if (isBirthday)
        {
            basePoints *= _mechanics.Gifts.BirthdayMultiplier;
        }

        return basePoints;
    }

    /// <summary>
    ///     Checks if an NPC can be romanced.
    /// </summary>
    public bool IsNpcRomanceable(string npcName) => _npcs.TryGetValue(npcName, out var npc) && npc.IsRomanceable;

    /// <summary>
    ///     Gets the birthday of an NPC in "Season Day" format.
    ///     Returns null if NPC not found.
    /// </summary>
    public string? GetNpcBirthday(string npcName) => _npcs.TryGetValue(npcName, out var npc) ? npc.Birthday : null;

    /// <summary>
    ///     Gets all NPCs whose birthday falls on a specific season and day.
    /// </summary>
    public IEnumerable<NpcData> GetNpcsByBirthday(string season, int day)
    {
        return _npcs.Values.Where(n =>
        {
            var parts = n.Birthday.Split(' ');
            return parts.Length == 2
                   && parts[0].Equals(season, StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(parts[1], out var bday) && bday == day;
        });
    }

    /// <summary>
    ///     Gets all romanceable NPCs.
    /// </summary>
    public IEnumerable<NpcData> GetRomanceableNpcs() => _npcs.Values.Where(n => n.IsRomanceable);

    /// <summary>
    ///     Gets all NPCs who attend a specific festival.
    /// </summary>
    public List<string> GetFestivalAttendees(string festivalName)
    {
        return _festivals.TryGetValue(festivalName, out var festival)
            ? festival.ParticipatingNpcs
            : new List<string>();
    }

    /// <summary>
    ///     Gets all festivals an NPC attends.
    /// </summary>
    public IEnumerable<FestivalData> GetFestivalsForNpc(string npcName)
    {
        return _festivals.Values.Where(f =>
            f.ParticipatingNpcs.Contains(npcName, StringComparer.OrdinalIgnoreCase));
    }
}