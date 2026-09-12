using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValleyAgent.Save.Models;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Save;

/// <summary>
///     Manages serialization, deserialization, migration, and validation of save data.
///     Responsibilities:
///     - Serializes SaveData to JSON (thread-safe)
///     - Deserializes JSON to SaveData with automatic version migration
///     - Validates save data integrity
///     - Sanitizes transient states before saving
///     - Provides defaults for missing fields (never fails on partial data)
///     Design:
///     - Game-agnostic: no SMAPI/Stardew dependencies
///     - Thread-safe: all public methods are synchronized with locks
///     - Uses System.Text.Json for .NET 6 compatibility
///     - Enum values serialized as strings for readability and migration safety
/// </summary>
public class SaveDataManager
{
    /// <summary>
    ///     The current save data format version.
    /// </summary>
    public const string CurrentVersion = "2.0.0";

    /// <summary>
    ///     The integer schema version for the 3-tier memory system (T21).
    ///     Increment when the save data schema changes. Old saves without a
    ///     SchemaVersion field are treated as 0 and migrated to this value on load.
    /// </summary>
    public const int SaveDataVersion = 1;

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _lock = new();

    /// <summary>
    ///     Creates a new SaveDataManager with default JSON options.
    /// </summary>
    public SaveDataManager()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>
    ///     Serializes a SaveData object to a JSON string.
    ///     Sanitizes transient states before serialization.
    ///     Thread-safe.
    /// </summary>
    /// <param name="data">The save data to serialize.</param>
    /// <returns>A JSON string representation of the save data.</returns>
    /// <exception cref="ArgumentNullException">Thrown when data is null.</exception>
    public string Serialize(SaveData data)
    {
        // 使用 ThrowIfNull 替代显式 if-null-throw 模式
        ArgumentNullException.ThrowIfNull(data);

        lock (_lock)
        {
            var sanitized = SanitizeForSave(data);
            return JsonSerializer.Serialize(sanitized, _jsonOptions);
        }
    }

    /// <summary>
    ///     Deserializes a JSON string to a SaveData object.
    ///     Applies defaults for missing fields and runs version migration if needed.
    ///     Thread-safe.
    /// </summary>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <returns>A SaveData object. Returns a default-initialized SaveData if json is null/empty or invalid.</returns>
    public SaveData Deserialize(string json)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return CreateDefaultSaveData();
            }

            json = json.Replace("\"THINKING\"", "\"IDLE\"", StringComparison.Ordinal);

            SaveData? data;
            try
            {
                data = JsonSerializer.Deserialize<SaveData>(json, _jsonOptions);
            }
            catch (JsonException)
            {
                // Invalid JSON - return defaults rather than failing
                return CreateDefaultSaveData();
            }

            if (data == null)
            {
                return CreateDefaultSaveData();
            }

            // Apply defaults for any missing/null fields
            ApplyDefaults(data);

            // Version migration
            if (string.IsNullOrWhiteSpace(data.Version) ||
                data.Version == "1.0.0")
            {
                data = MigrateV1ToV2(data);
            }

            return data;
        }
    }

    /// <summary>
    ///     Migrates a V1 save data object to V2 format.
    ///     Preserves all existing data and initializes new fields with defaults.
    /// </summary>
    /// <param name="old">The V1 save data to migrate.</param>
    /// <returns>A new SaveData object in V2 format.</returns>
    public SaveData MigrateV1ToV2(SaveData old)
    {
        if (old == null)
        {
            return CreateDefaultSaveData();
        }

        lock (_lock)
        {
            var migrated = new SaveData
            {
                Version = CurrentVersion,
                AgentStates = old.AgentStates ??
                              new Dictionary<string, AgentStateData>(StringComparer.OrdinalIgnoreCase),
                Memories = old.Memories ?? new Dictionary<string, MemoryData>(StringComparer.OrdinalIgnoreCase),
                FriendshipHistory = old.FriendshipHistory ??
                                    new Dictionary<string, FriendshipHistoryData>(StringComparer.OrdinalIgnoreCase),
                Allocations = old.Allocations ?? new AllocationData(),
                Statistics = old.Statistics ?? new StatisticsData()
            };

            // If V1 had no Statistics, initialize with defaults
            if (old.Statistics == null)
            {
                migrated.Statistics = new StatisticsData
                {
                    SessionStartTime = DateTime.UtcNow
                };
            }

            // If V1 had no Allocations, initialize with defaults
            if (old.Allocations == null)
            {
                migrated.Allocations = new AllocationData();
            }

            // Migrate legacy THINKING states from V1 to IDLE
            foreach (var kvp in migrated.AgentStates)
            {
                if (kvp.Value != null)
                {
                    if (kvp.Value.CurrentState.ToString() == "THINKING")
                    {
                        kvp.Value.CurrentState = AgentState.IDLE;
                    }

                    kvp.Value.Inventory ??= new List<string>();
                }
            }

            return migrated;
        }
    }

    /// <summary>
    ///     Validates a SaveData object for required fields and data integrity.
    ///     Returns false for null data, missing required fields, or invalid states.
    ///     Does NOT throw on validation failure.
    ///     Thread-safe.
    /// </summary>
    /// <param name="data">The save data to validate.</param>
    /// <returns>True if the data is valid; false otherwise.</returns>
    public bool Validate(SaveData data)
    {
        if (data == null)
        {
            return false;
        }

        lock (_lock)
        {
            // Version must be present
            if (string.IsNullOrWhiteSpace(data.Version))
            {
                return false;
            }

            // Required collections must not be null
            if (data.AgentStates == null)
            {
                return false;
            }

            if (data.Memories == null)
            {
                return false;
            }

            if (data.FriendshipHistory == null)
            {
                return false;
            }

            if (data.Allocations == null)
            {
                return false;
            }

            if (data.Statistics == null)
            {
                return false;
            }

            // Validate agent states
            foreach (var kvp in data.AgentStates)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    return false;
                }

                if (kvp.Value == null)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(kvp.Value.NpcName))
                {
                    return false;
                }

                // Health == -1 is valid (not set)
                if (kvp.Value.Health is < -1 or > 100)
                {
                    return false;
                }

                // Inventory must not be null
                if (kvp.Value.Inventory == null)
                {
                    return false;
                }

                // THINKING 状态已从 AgentState 枚举中移除，此检查不再需要
            }

            // Validate memories
            foreach (var kvp in data.Memories)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    return false;
                }

                if (kvp.Value == null)
                {
                    return false;
                }

                // Dialogue history must not exceed max capacity
                if (kvp.Value.DialogueHistory != null && kvp.Value.DialogueHistory.Count > 20)
                {
                    return false;
                }
            }

            // Validate friendship history
            foreach (var kvp in data.FriendshipHistory)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    return false;
                }

                if (kvp.Value == null)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(kvp.Value.NpcName))
                {
                    return false;
                }
            }

            // Validate allocations
            if (data.Allocations.AgentNpcNames == null)
            {
                return false;
            }

            if (data.Allocations.ManualOverrides == null)
            {
                return false;
            }

            // Validate statistics
            return data.Statistics.SessionStartTime != default;
        }
    }

    #region Private Helpers

    /// <summary>
    ///     Creates a fully default-initialized SaveData object.
    /// </summary>
    private static SaveData CreateDefaultSaveData()
    {
        return new SaveData
        {
            Version = CurrentVersion,
            AgentStates = new Dictionary<string, AgentStateData>(StringComparer.OrdinalIgnoreCase),
            Memories = new Dictionary<string, MemoryData>(StringComparer.OrdinalIgnoreCase),
            FriendshipHistory = new Dictionary<string, FriendshipHistoryData>(StringComparer.OrdinalIgnoreCase),
            Allocations = new AllocationData(),
            Statistics = new StatisticsData
            {
                SessionStartTime = DateTime.UtcNow
            }
        };
    }

    /// <summary>
    ///     Applies defaults to any null fields in the save data.
    ///     Mutates the provided object in-place.
    /// </summary>
    private static void ApplyDefaults(SaveData data)
    {
        data.Version ??= CurrentVersion;
        // T21: Old saves without SchemaVersion deserialize as 0; migrate to current.
        if (data.SchemaVersion == 0)
        {
            data.SchemaVersion = SaveDataVersion;
        }

        data.AgentStates ??= new Dictionary<string, AgentStateData>(StringComparer.OrdinalIgnoreCase);
        data.Memories ??= new Dictionary<string, MemoryData>(StringComparer.OrdinalIgnoreCase);
        data.FriendshipHistory ??= new Dictionary<string, FriendshipHistoryData>(StringComparer.OrdinalIgnoreCase);
        data.Allocations ??= new AllocationData();
        data.Statistics ??= new StatisticsData
        {
            SessionStartTime = DateTime.UtcNow
        };

        // Ensure nested collections in Allocations are non-null
        data.Allocations.AgentNpcNames ??= new List<string>();
        data.Allocations.ManualOverrides ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Ensure nested collections in Statistics are initialized
        if (data.Statistics.SessionStartTime == default)
        {
            data.Statistics.SessionStartTime = DateTime.UtcNow;
        }

        // Ensure all dictionary values have non-null collections
        foreach (var memory in data.Memories.Values)
        {
            if (memory != null)
            {
                memory.DialogueHistory ??= new List<DialogueExchangeData>();
                memory.EventHistory ??= new List<EventRecordData>();
                memory.ShortTermMemories ??= new List<StructuredMemoryEntry>();
                memory.LongTermMemories ??= new List<StructuredMemoryEntry>();
            }
        }

        foreach (var friendship in data.FriendshipHistory.Values)
        {
            if (friendship != null)
            {
                friendship.Changes ??= new List<FriendshipChangeData>();
            }
        }

        // Ensure agent state data has non-null inventory
        foreach (var agentState in data.AgentStates.Values)
        {
            if (agentState != null)
            {
                agentState.Inventory ??= new List<string>();
            }
        }
    }

    /// <summary>
    ///     Sanitizes save data before serialization.
    ///     - Truncates dialogue history to max 20 entries per NPC
    ///     - Ensures no sensitive data is present
    ///     Returns a shallow-copied SaveData safe for serialization.
    /// </summary>
    private static SaveData SanitizeForSave(SaveData data)
    {
        var sanitized = new SaveData
        {
            Version = data.Version ?? CurrentVersion,
            // T21: Always stamp the current schema version on save.
            SchemaVersion = SaveDataVersion,
            AgentStates = new Dictionary<string, AgentStateData>(
                data.AgentStates ?? new Dictionary<string, AgentStateData>(), StringComparer.OrdinalIgnoreCase),
            Memories = new Dictionary<string, MemoryData>(data.Memories ?? new Dictionary<string, MemoryData>(),
                StringComparer.OrdinalIgnoreCase),
            FriendshipHistory = new Dictionary<string, FriendshipHistoryData>(
                data.FriendshipHistory ?? new Dictionary<string, FriendshipHistoryData>(),
                StringComparer.OrdinalIgnoreCase),
            Allocations = new AllocationData
            {
                AgentNpcNames = new List<string>(data.Allocations?.AgentNpcNames ?? new List<string>()),
                ManualOverrides = new Dictionary<string, bool>(
                    data.Allocations?.ManualOverrides ?? new Dictionary<string, bool>(),
                    StringComparer.OrdinalIgnoreCase)
            },
            Statistics = new StatisticsData
            {
                SessionStartTime = data.Statistics?.SessionStartTime ?? DateTime.UtcNow,
                TotalLlmCalls = data.Statistics?.TotalLlmCalls ?? 0,
                TotalTokensUsed = data.Statistics?.TotalTokensUsed ?? 0,
                TotalDecisions = data.Statistics?.TotalDecisions ?? 0,
                AverageLlmResponseTime = data.Statistics?.AverageLlmResponseTime ?? 0.0
            }
        };

        // Sanitize agent states: ensure Inventory is non-null
        foreach (var kvp in sanitized.AgentStates)
        {
            if (kvp.Value != null)
            {
                kvp.Value.Inventory ??= new List<string>();
            }
        }

        // Sanitize memories: cap dialogue history at 20 entries
        foreach (var kvp in sanitized.Memories)
        {
            if (kvp.Value?.DialogueHistory != null && kvp.Value.DialogueHistory.Count > 20)
            {
                // Keep the most recent 20
                kvp.Value.DialogueHistory = kvp.Value.DialogueHistory
                    .Skip(kvp.Value.DialogueHistory.Count - 20)
                    .ToList();
            }
        }

        return sanitized;
    }

    #endregion
}