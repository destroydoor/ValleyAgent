using System;
using System.Collections.Generic;

namespace ValleyAgent.Brain
{
    public enum MemoryEntryType
    {
        Generic,
        Conversation,
        Decision,
        Emotion,
        Event,
        Combat,
        Gift,
        Farm,
        Task,
    }

    public class MemoryEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public double Importance { get; set; } = 1.0;
        public MemoryEntryType EntryType { get; set; } = MemoryEntryType.Generic;
        public string Location { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();

        public MemoryEntry() { }

        public MemoryEntry(string text, double importance = 1.0, MemoryEntryType entryType = MemoryEntryType.Generic, string location = "", List<string>? tags = null)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Timestamp = DateTime.UtcNow;
            Importance = Math.Clamp(importance, 0.0, 10.0);
            EntryType = entryType;
            Location = location;
            Tags = tags ?? new List<string>();
        }

        public double GetRecencyScore()
        {
            var ageSeconds = (DateTime.UtcNow - Timestamp).TotalSeconds;
            return Math.Max(0, 1.0 - (ageSeconds / 86400.0));
        }

        public double GetCompositeScore() => Importance + (GetRecencyScore() * 2.0);

        public MemoryEntryData ToSaveData()
        {
            return new MemoryEntryData
            {
                Text = Text,
                Timestamp = Timestamp,
                Importance = Importance,
                EntryType = EntryType.ToString(),
                Location = Location,
                Tags = Tags,
            };
        }

        public static MemoryEntry FromSaveData(MemoryEntryData data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var entry = new MemoryEntry
            {
                Text = data.Text,
                Timestamp = data.Timestamp,
                Importance = data.Importance,
                Location = data.Location,
                Tags = data.Tags ?? new List<string>(),
            };
            if (Enum.TryParse<MemoryEntryType>(data.EntryType, true, out var type))
            {
                entry.EntryType = type;
            }

            return entry;
        }
    }

    public class MemoryEntryData
    {
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public double Importance { get; set; } = 1.0;
        public string EntryType { get; set; } = "Generic";
        public string Location { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
    }

    public enum MemoryTier
    {
        Strong,
        Medium,
        Weak
    }

    public class TieredMemoryEntry
    {
        public string Text { get; set; } = string.Empty;
        public MemoryTier Tier { get; set; } = MemoryTier.Medium;
        public double Timestamp { get; set; }
        public double LastAccessed { get; set; }
        public string EntryType { get; set; } = "generic";
        public string Location { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();

        public TieredMemoryEntry()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Timestamp = now;
            LastAccessed = now;
        }

        public double BaseScore => Tier switch
        {
            MemoryTier.Strong => 100.0,
            MemoryTier.Medium => 50.0,
            MemoryTier.Weak => 10.0,
            _ => 50.0
        };

        public double Score
        {
            get
            {
                var hoursSinceAccess = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - LastAccessed) / 3600.0;
                return BaseScore * Math.Pow(0.995, hoursSinceAccess);
            }
        }

        public void Touch()
        {
            LastAccessed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
