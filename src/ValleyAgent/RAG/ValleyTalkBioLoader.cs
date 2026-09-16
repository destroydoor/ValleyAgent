using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using ValleyAgent.Brain;
using ValleyAgent.RAG.Models;

namespace ValleyAgent.RAG;

/// <summary>
///     Loads rich NPC bio data from ValleyTalk's bio/*.json files.
///     Supports both vanilla ([CP] ValleyTalk Base/assets/bio/) and SVE (ValleyTalk for SVE/assets/bio/) paths.
///     Files use Content Patcher format: {Changes:[{Entries:{Biography,Relationships,Traits,...}}]}.
///     Provides full personality profiles (biography, relationships, traits, preoccupations)
///     that are far richer than a flat NPC data file (RAGKnowledgeBase 已于 2026-09-12 删除).
/// </summary>
public class ValleyTalkBioLoader
{
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, ValleyTalkBioData> _bios = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor _monitor;
    private IModHelper? _helper;

    public ValleyTalkBioLoader(IMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    /// <summary>Whether bio data has been loaded.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Which bio source was loaded ("SVE" or "Base" or "Bundled").</summary>
    public string LoadedSource { get; private set; } = "None";

    /// <summary>
    ///     Initialize with the SMAPI helper so we can resolve paths relative to the mod.
    /// </summary>
    public void Initialize(IModHelper helper) => _helper = helper;

    /// <summary>
    ///     Load all bio/*.json files from the ValleyTalk mod directory.
    ///     Priority: SVE (ValleyTalk for SVE/assets/bio/) → Base ([CP] ValleyTalk Base/assets/bio/).
    ///     Both missing 时不做任何回退，直接空载（IsLoaded 保持 false）。
    /// </summary>
    public void Load()
    {
        if (_helper == null)
        {
            _monitor.Log("ValleyTalkBioLoader: helper not initialized, cannot load bios.", LogLevel.Warn);
            return;
        }

        try
        {
            var bioDir = ResolveBioDirectory();
            if (string.IsNullOrEmpty(bioDir) || !Directory.Exists(bioDir))
            {
                _monitor.Log("ValleyTalkBioLoader: bio directory not found (tried SVE and Base paths); " +
                             "no bio data will be loaded.", LogLevel.Warn);
                return;
            }

            var bioFiles = Directory.GetFiles(bioDir, "*.json");
            _monitor.Log(
                $"ValleyTalkBioLoader: loading {bioFiles.Length} bio files from '{bioDir}' (source={LoadedSource})...",
                LogLevel.Info);

            var loaded = 0;
            foreach (var filePath in bioFiles)
            {
                try
                {
                    var bio = LoadBioFile(filePath);
                    if (bio != null)
                    {
                        _bios[bio.NpcName] = bio;
                        loaded++;
                    }
                }
                catch (IOException ex)
                {
                    var fileName = Path.GetFileName(filePath);
                    _monitor.Log($"ValleyTalkBioLoader: failed to load '{fileName}': {ex}", LogLevel.Warn);
                }
                catch (JsonException ex)
                {
                    var fileName = Path.GetFileName(filePath);
                    _monitor.Log($"ValleyTalkBioLoader: failed to load '{fileName}': {ex}", LogLevel.Warn);
                }
            }

            IsLoaded = true;
            _monitor.Log(
                $"ValleyTalkBioLoader: loaded {loaded}/{bioFiles.Length} bio files successfully (source={LoadedSource}).",
                LogLevel.Info);
        }
        catch (IOException ex)
        {
            _monitor.Log($"ValleyTalkBioLoader: failed to load bios: {ex}", LogLevel.Error);
        }
        catch (JsonException ex)
        {
            _monitor.Log($"ValleyTalkBioLoader: failed to load bios: {ex}", LogLevel.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            _monitor.Log($"ValleyTalkBioLoader: failed to load bios: {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     Get the bio data for a specific NPC.
    ///     Returns null if not found.
    /// </summary>
    public ValleyTalkBioData? GetBio(string npcName)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(npcName))
        {
            return null;
        }

        _ = _bios.TryGetValue(npcName, out var bio);
        return bio;
    }

    /// <summary>
    ///     Check if bio data exists for an NPC.
    /// </summary>
    public bool HasBio(string npcName) => IsLoaded && _bios.ContainsKey(npcName);

    /// <summary>
    ///     Get all loaded bio names for inspection.
    /// </summary>
    public IReadOnlyList<string> GetBioNames() => _bios.Keys.ToList().AsReadOnly();

    /// <summary>
    ///     Inject the ValleyTalk bio data into an agent's brain.
    ///     Should be called right after agent creation.
    /// </summary>
    public void InjectBio(AgentBrain brain)
    {
        if (brain == null || !IsLoaded)
        {
            return;
        }

        var bio = GetBio(brain.NpcName);
        if (bio != null)
        {
            brain.Bio = bio;
        }
    }

    /// <summary>
    ///     Resolve bio directory with SVE priority, then Base fallback.
    ///     Returns the first existing directory, or empty string if none found.
    /// </summary>
    private string ResolveBioDirectory()
    {
        var modDir = _helper!.DirectoryPath;

        // Priority 1: SVE bio directory (ValleyTalk for SVE/assets/bio/)
        var svePath = Path.GetFullPath(Path.Combine(modDir, "..", "ValleyTalk for SVE", "assets", "bio"));
        if (Directory.Exists(svePath))
        {
            LoadedSource = "SVE";
            return svePath;
        }

        // Priority 2: Base bio directory ([CP] ValleyTalk Base/assets/bio/)
        // Note: the actual directory name may include locale suffix like "（中文翻译）"
        var baseDir = Path.GetFullPath(Path.Combine(modDir, "..", "[CP] ValleyTalk Base", "assets", "bio"));
        if (Directory.Exists(baseDir))
        {
            LoadedSource = "Base";
            return baseDir;
        }

        // Priority 3: Search for any [CP] ValleyTalk Base* directory (locale variants)
        var parentDir = Path.GetFullPath(Path.Combine(modDir, ".."));
        try
        {
            var candidates = Directory.GetDirectories(parentDir, "[CP] ValleyTalk Base*");
            foreach (var candidate in candidates)
            {
                var bioCandidate = Path.Combine(candidate, "assets", "bio");
                if (Directory.Exists(bioCandidate))
                {
                    LoadedSource = "Base";
                    return bioCandidate;
                }
            }
        }
        catch (IOException)
        {
            // Ignore directory enumeration errors
        }

        LoadedSource = "None";
        return string.Empty;
    }

    /// <summary>
    ///     Load a single bio file, auto-detecting Content Patcher format vs flat format.
    ///     CP format: {Changes:[{Entries:{Biography,Relationships,Traits,...}}]}
    ///     Flat format: {Biography,Relationships,Traits,...}
    /// </summary>
    private static ValleyTalkBioData? LoadBioFile(string filePath)
    {
        var json = File.ReadAllText(filePath);

        // Set NPC name from filename (e.g., "Abigail.json" → "Abigail")
        var npcName = Path.GetFileNameWithoutExtension(filePath);

        // Try Content Patcher format first (SVE and newer Base files)
        var cpBio = TryParseContentPatcherFormat(json, npcName);
        if (cpBio != null)
        {
            return cpBio;
        }

        // Fall back to flat format (older Base files)
        var flatBio = JsonSerializer.Deserialize<ValleyTalkBioData>(json, CachedJsonOptions);
        if (flatBio != null)
        {
            flatBio.NpcName = npcName;
        }

        return flatBio;
    }

    /// <summary>
    ///     Parse Content Patcher format: {Changes:[{Action, Target, Entries:{...}}]}.
    ///     Returns null if the JSON is not in CP format.
    /// </summary>
    private static ValleyTalkBioData? TryParseContentPatcherFormat(string json, string npcName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // CP format requires "Changes" array
            if (!root.TryGetProperty("Changes", out var changes) || changes.GetArrayLength() == 0)
            {
                return null;
            }

            var change = changes[0];
            if (!change.TryGetProperty("Entries", out var entries))
            {
                return null;
            }

            var bio = new ValleyTalkBioData { NpcName = npcName };

            // Biography
            if (entries.TryGetProperty("Biography", out var bioEl) && bioEl.ValueKind == JsonValueKind.String)
            {
                bio.Biography = bioEl.GetString() ?? string.Empty;
            }

            // BiographyEnd
            if (entries.TryGetProperty("BiographyEnd", out var bioEndEl) && bioEndEl.ValueKind == JsonValueKind.String)
            {
                bio.BiographyEnd = bioEndEl.GetString() ?? string.Empty;
            }

            // Relationships (nested object with id/Heading/Description per entry)
            if (entries.TryGetProperty("Relationships", out var relsEl) && relsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var relProp in relsEl.EnumerateObject())
                {
                    var rel = ParseRelationship(relProp.Value);
                    if (rel != null)
                    {
                        bio.Relationships[relProp.Name] = rel;
                    }
                }
            }

            // Traits (nested object with id/Heading/Description per entry)
            if (entries.TryGetProperty("Traits", out var traitsEl) && traitsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var traitProp in traitsEl.EnumerateObject())
                {
                    var trait = ParseTrait(traitProp.Value);
                    if (trait != null)
                    {
                        bio.Traits[traitProp.Name] = trait;
                    }
                }
            }

            // Preoccupations (array of strings)
            if (entries.TryGetProperty("Preoccupations", out var preoccEl) && preoccEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in preoccEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            bio.Preoccupations.Add(text);
                        }
                    }
                }
            }

            // Dialogue (object of key→string)
            if (entries.TryGetProperty("Dialogue", out var dlgEl) && dlgEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var dlgProp in dlgEl.EnumerateObject())
                {
                    if (dlgProp.Value.ValueKind == JsonValueKind.String)
                    {
                        bio.Dialogue[dlgProp.Name] = dlgProp.Value.GetString() ?? string.Empty;
                    }
                }
            }

            // ExtraPortraits
            if (entries.TryGetProperty("ExtraPortraits", out var epEl) && epEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var epProp in epEl.EnumerateObject())
                {
                    if (epProp.Value.ValueKind == JsonValueKind.String)
                    {
                        bio.ExtraPortraits[epProp.Name] = epProp.Value.GetString() ?? string.Empty;
                    }
                }
            }

            // Unique
            if (entries.TryGetProperty("Unique", out var uniqueEl) && uniqueEl.ValueKind == JsonValueKind.String)
            {
                bio.Unique = uniqueEl.GetString() ?? string.Empty;
            }

            // HomeLocationBed
            if (entries.TryGetProperty("HomeLocationBed", out var bedEl) && bedEl.ValueKind == JsonValueKind.True)
            {
                bio.HomeLocationBed = true;
            }

            // PromptOverrides
            if (entries.TryGetProperty("PromptOverrides", out var poEl) && poEl.ValueKind == JsonValueKind.Object)
            {
                bio.PromptOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var poProp in poEl.EnumerateObject())
                {
                    if (poProp.Value.ValueKind == JsonValueKind.String)
                    {
                        bio.PromptOverrides[poProp.Name] = poProp.Value.GetString() ?? string.Empty;
                    }
                }
            }

            return bio;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BioRelationship? ParseRelationship(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rel = new BioRelationship();
        if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            rel.Id = idEl.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("Heading", out var headEl) && headEl.ValueKind == JsonValueKind.String)
        {
            rel.Heading = headEl.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("Description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            rel.Description = descEl.GetString() ?? string.Empty;
        }

        return rel;
    }

    private static BioTrait? ParseTrait(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var trait = new BioTrait();
        if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            trait.Id = idEl.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("Heading", out var headEl) && headEl.ValueKind == JsonValueKind.String)
        {
            trait.Heading = headEl.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("Description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            trait.Description = descEl.GetString() ?? string.Empty;
        }

        return trait;
    }
}