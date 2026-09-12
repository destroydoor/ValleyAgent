using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using ValleyAgent.Brain;

namespace ValleyAgent.RAG.Models
{
    /// <summary>
    /// Full NPC bio data from ValleyTalk's bio/*.json files.
    /// Much richer than the flat NpcData model — contains detailed biography,
    /// structured relationships, personality traits with descriptions, preoccupations,
    /// sample dialogue, and behavioral metadata.
    /// </summary>
    public class ValleyTalkBioData
    {
        /// <summary>Internal NPC name (e.g., "Abigail").</summary>
        [JsonIgnore]
        public string NpcName { get; set; } = string.Empty;

        /// <summary>Full narrative biography (1300+ words for major NPCs).</summary>
        public string Biography { get; set; } = string.Empty;

        /// <summary>Closing statement for the biography.</summary>
        public string BiographyEnd { get; set; } = string.Empty;

        /// <summary>Key relationships with other characters, each with heading and description.</summary>
        public Dictionary<string, BioRelationship> Relationships { get; set; } = new();

        /// <summary>Personality traits with detailed descriptions.</summary>
        public Dictionary<string, BioTrait> Traits { get; set; } = new();

        /// <summary>Topics the NPC is currently thinking about (used as conversation starters).</summary>
        public List<string> Preoccupations { get; set; } = new();

        /// <summary>Sample dialogue lines by day/time key (e.g., "Mon", "Tue_6", etc.).</summary>
        public Dictionary<string, string> Dialogue { get; set; } = new();

        /// <summary>Extra portrait keys for emotion display (e.g., "7": "shocked").</summary>
        public Dictionary<string, string> ExtraPortraits { get; set; } = new();

        /// <summary>A unique behavioral or visual quality (e.g., "looking grumpy").</summary>
        public string Unique { get; set; } = string.Empty;

        /// <summary>Whether the NPC's home location includes a bed (meaning they sleep there).</summary>
        public bool HomeLocationBed { get; set; }

        /// <summary>
        /// Phase-specific prompt overrides from ValleyTalk bio data.
        /// Keys like "nonSpouseFriendshipFirstConversation", "nonSpouseFreindshipStrangers", etc.
        /// </summary>
        public Dictionary<string, string>? PromptOverrides { get; set; }

        private static readonly string[] _turnPointKeywords = { "However", "Despite", "But", "Beneath", "Underneath", "though", "although" };

        private static readonly Dictionary<string, FriendshipPhase> _overridePhaseMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "nonSpouseFriendshipFirstConversation", FriendshipPhase.Stranger },
            { "nonSpouseFreindshipStrangers", FriendshipPhase.Stranger },
            { "nonSpouseFriendshipAcquaintances", FriendshipPhase.Acquaintance },
            { "nonSpouseFriendshipFriends", FriendshipPhase.Friend },
            { "nonSpouseFriendshipCloseFriends", FriendshipPhase.Close },
        };

        /// <summary>
        /// Get a phase-appropriate biography summary.
        /// Phase 1-2: first 2 sentences (surface).
        /// Phase 3-4: first 2 sentences + turning-point sentence.
        /// Phase 5: first 2 sentences + turning-point sentence + BiographyEnd.
        /// </summary>
        public string GetPhaseSummary(FriendshipPhase phase)
        {
            if (string.IsNullOrWhiteSpace(Biography))
            {
                return string.Empty;
            }

            var sentences = Biography.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (sentences.Count == 0)
            {
                return string.Empty;
            }

            var count = Math.Min(2, sentences.Count);
            var summary = string.Join(". ", sentences.Take(count)).Trim();
            if (!summary.EndsWith('.'))
            {
                summary += ".";
            }

            if (phase >= FriendshipPhase.Friend)
            {
                var turnSentence = sentences.Skip(count).FirstOrDefault(s =>
                    _turnPointKeywords.Any(kw => s.Contains(kw, StringComparison.OrdinalIgnoreCase)));
                if (turnSentence != null)
                {
                    summary += " " + turnSentence.Trim();
                    if (!summary.EndsWith('.'))
                    {
                        summary += ".";
                    }
                }
            }

            if (phase >= FriendshipPhase.Partner && !string.IsNullOrWhiteSpace(BiographyEnd))
            {
                summary += " " + BiographyEnd.Trim();
            }

            return summary;
        }

        /// <summary>
        /// Get a prompt override for the current friendship phase.
        /// Returns null if no override exists for this phase.
        /// </summary>
        public string? GetPromptOverride(FriendshipPhase phase)
        {
            if (PromptOverrides == null || PromptOverrides.Count == 0)
            {
                return null;
            }

            foreach (var kvp in _overridePhaseMap)
            {
                if (kvp.Value == phase && PromptOverrides.TryGetValue(kvp.Key, out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        /// <summary>Concise summary for prompt injection (biography condensed to 2-3 sentences).</summary>
        [JsonIgnore]
        public string PromptSummary
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Biography))
                {
                    return string.Empty;
                }

                // Take first 2 sentences from the biography as a concise summary
                var sentences = Biography.Split('.');
                var count = Math.Min(2, sentences.Length);
                return string.Join(". ", sentences, 0, count).Trim() + ".";
            }
        }

        /// <summary>All trait names joined for prompt injection.</summary>
        [JsonIgnore]
        public string TraitNames
        {
            get
            {
                if (Traits.Count == 0)
                {
                    return string.Empty;
                }

                var names = new List<string>();
                foreach (var trait in Traits.Values)
                {
                    if (!string.IsNullOrWhiteSpace(trait.Heading))
                    {
                        names.Add(trait.Heading);
                    }
                }
                return string.Join(", ", names);
            }
        }

        /// <summary>All relationship summaries for prompt injection.</summary>
        [JsonIgnore]
        public string RelationshipSummary
        {
            get
            {
                if (Relationships.Count == 0)
                {
                    return string.Empty;
                }

                var parts = new List<string>();
                foreach (var kvp in Relationships)
                {
                    var rel = kvp.Value;
                    if (!string.IsNullOrWhiteSpace(rel.Description))
                    {
                        parts.Add($"{rel.Heading}: {rel.Description}");
                    }
                    else if (!string.IsNullOrWhiteSpace(rel.Heading))
                    {
                        parts.Add(rel.Heading);
                    }
                }
                return string.Join("; ", parts);
            }
        }

        /// <summary>Concise description of the NPC's unique quality for prompt injection.</summary>
        [JsonIgnore]
        public string UniqueHint => string.IsNullOrWhiteSpace(Unique) ? string.Empty : $"Distinctive trait: {Unique}.";
    }

    /// <summary>
    /// A relationship entry from the bio file, with heading and description.
    /// </summary>
    public class BioRelationship
    {
        public string Id { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// A personality trait entry from the bio file, with heading and detailed description.
    /// </summary>
    public class BioTrait
    {
        public string Id { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
