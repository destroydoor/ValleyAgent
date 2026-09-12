using System;

namespace ValleyAgent.Brain
{
    /// <summary>
    ///     Continuous emotion state with intensity and source tracking.
    ///     Affects decision-making thresholds and behavior via RuleBasedDecisionEngine.
    ///     Replaces the old single-enum approach with a richer continuous representation.
    /// </summary>
    public struct EmotionState
    {
        /// <summary>The primary emotion type.</summary>
        public NpcEmotion PrimaryEmotion { get; set; }

        /// <summary>Intensity of the emotion (0.0 = neutral/minimal, 1.0 = maximum).</summary>
        public float Intensity { get; set; }

        /// <summary>When this emotion was last updated.</summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>What caused this emotion (event name, dialogue keyword, etc.).</summary>
        public string? Source { get; set; }

        /// <summary>
        ///     Creates a new emotion state with default neutral intensity 1.0.
        /// </summary>
        public EmotionState(NpcEmotion primaryEmotion, float intensity = 1.0f, string? source = null)
        {
            PrimaryEmotion = primaryEmotion;
            Intensity = Math.Clamp(intensity, 0f, 1f);
            LastUpdated = DateTime.UtcNow;
            Source = source;
        }

        /// <summary>
        ///     Creates an EmotionState for Neutral with given intensity.
        /// </summary>
        public static EmotionState Neutral(float intensity = 1.0f) =>
            new(NpcEmotion.Neutral, intensity, "natural_decay");

        /// <summary>
        ///     Blend two emotion states, weighted toward the primary.
        /// </summary>
        public static EmotionState Blend(EmotionState a, EmotionState b, float weight = 0.5f)
        {
            var clampedWeight = Math.Clamp(weight, 0f, 1f);
            var blendedIntensity = (a.Intensity * clampedWeight) + (b.Intensity * (1f - clampedWeight));
            return new EmotionState(clampedWeight >= 0.5f ? a.PrimaryEmotion : b.PrimaryEmotion, blendedIntensity, a.Source ?? b.Source);
        }

        public override readonly bool Equals(object? obj) =>
            obj is EmotionState other && PrimaryEmotion == other.PrimaryEmotion
                                     && Math.Abs(Intensity - other.Intensity) < 0.001f;

        public override readonly int GetHashCode() => HashCode.Combine(PrimaryEmotion, Intensity);

        public static bool operator ==(EmotionState left, EmotionState right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(EmotionState left, EmotionState right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    ///     EmotionState 工具辅助类。
    /// </summary>
    public static class EmoStateHelper
    {
        /// <summary>
        ///     低强度时的弱化描述（intensity &lt; 0.3），用于 prompt 注入。
        /// </summary>
        public static string GetLowIntensityLabel(NpcEmotion emotion)
        {
            return emotion switch
            {
                NpcEmotion.Happy => "slightly upbeat",
                NpcEmotion.Angry => "mildly irritated",
                NpcEmotion.Sad => "a bit blue",
                NpcEmotion.Worried => "vaguely uneasy",
                NpcEmotion.Excited => "somewhat eager",
                NpcEmotion.Tired => "a little weary",
                NpcEmotion.Grateful => "mildly appreciative",
                _ => "calm",
            };
        }
    }
}
