using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.RAG.Models;

namespace ValleyAgent.Brain;

public static class TraitPhaseMapper
{
    private static readonly Dictionary<string, FriendshipPhase> _traitPhaseMin = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Depressed", FriendshipPhase.Stranger },
        { "SelfDestructive", FriendshipPhase.Stranger },
        { "Self-Destructive", FriendshipPhase.Stranger },
        { "Cynical", FriendshipPhase.Stranger },
        { "Guarded", FriendshipPhase.Stranger },
        { "Vain", FriendshipPhase.Stranger },
        { "Fashion", FriendshipPhase.Stranger },
        { "Fashion Conscious", FriendshipPhase.Stranger },
        { "Introverted", FriendshipPhase.Stranger },
        { "Moody", FriendshipPhase.Stranger },
        { "Rebellious", FriendshipPhase.Stranger },
        { "Adventurous", FriendshipPhase.Stranger },
        { "Independent", FriendshipPhase.Stranger },
        { "Creative", FriendshipPhase.Stranger },
        { "Creative Spirit", FriendshipPhase.Stranger },

        { "Yearning", FriendshipPhase.Acquaintance },
        { "Yearning for Connection", FriendshipPhase.Acquaintance },
        { "Yearning for Something More", FriendshipPhase.Acquaintance },
        { "Vulnerable", FriendshipPhase.Acquaintance },
        { "Vulnerable Beneath the Surface", FriendshipPhase.Acquaintance },
        { "Struggles", FriendshipPhase.Acquaintance },

        { "Caring", FriendshipPhase.Friend },
        { "CaringUnderneath", FriendshipPhase.Friend },
        { "Caring Underneath", FriendshipPhase.Friend },
        { "LovesChickens", FriendshipPhase.Friend },
        { "Loves Chickens", FriendshipPhase.Friend },
        { "Growing", FriendshipPhase.Friend },
        { "Growing Appreciation", FriendshipPhase.Friend },
        { "Growing Appreciation for Pelican Town", FriendshipPhase.Friend },
        { "Empathetic", FriendshipPhase.Friend },
        { "Deep", FriendshipPhase.Friend },
        { "Deep Conversations", FriendshipPhase.Friend },
        { "Authenticity", FriendshipPhase.Friend },
        { "Appreciation", FriendshipPhase.Friend },

        { "Grateful", FriendshipPhase.Close },
        { "Redemption", FriendshipPhase.Close },
        { "Desire for Connection and Redemption", FriendshipPhase.Close }
    };

    private static readonly HashSet<string> _surfaceKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "beer", "wine", "ale", "alcohol", "drink", "drunk",
        "Jojamart", "joja",
        "fashion", "shopping", "mall", "clothes",
        "cynical", "depressed", "grumpy",
        "complaining", "avoiding", "hiding"
    };

    private static readonly HashSet<string> _deepKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "chicken", "chickens", "eggs", "breeds",
        "helping", "caring", "responsibility",
        "photography", "camera", "landscape", "nature",
        "Jas", "family", "love", "hope",
        "growing", "appreciation", "connection",
        "parents", "letter", "bunnies"
    };

    public static List<BioTrait> GetVisibleTraits(Dictionary<string, BioTrait> traits, FriendshipPhase phase)
    {
        if (traits == null || traits.Count == 0)
        {
            return new List<BioTrait>();
        }

        var result = new List<BioTrait>();
        foreach (var kvp in traits)
        {
            var traitKey = kvp.Key;
            var trait = kvp.Value;

            var minPhase = GetMinPhaseForTrait(traitKey, trait.Heading);
            if (phase >= minPhase)
            {
                result.Add(trait);
            }
        }

        return result;
    }

    public static string GetVisibleTraitNames(Dictionary<string, BioTrait> traits, FriendshipPhase phase)
    {
        var visible = GetVisibleTraits(traits, phase);
        return visible.Count == 0 ? string.Empty : string.Join(", ", visible.Select(t => t.Heading));
    }

    public static List<string> GetVisiblePreoccupations(List<string> preoccupations, FriendshipPhase phase)
    {
        if (preoccupations == null || preoccupations.Count == 0)
        {
            return new List<string>();
        }

        if (phase >= FriendshipPhase.Friend)
        {
            return preoccupations;
        }

        var result = new List<string>();
        foreach (var p in preoccupations)
        {
            if (IsSurfacePreoccupation(p) || !IsDeepPreoccupation(p))
            {
                result.Add(p);
            }
        }

        return result;
    }

    private static FriendshipPhase GetMinPhaseForTrait(string traitKey, string traitHeading)
    {
        if (_traitPhaseMin.TryGetValue(traitKey, out var phase))
        {
            return phase;
        }

        if (_traitPhaseMin.TryGetValue(traitHeading, out phase))
        {
            return phase;
        }

        foreach (var kvp in _traitPhaseMin)
        {
            if (traitKey.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                traitHeading.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return FriendshipPhase.Acquaintance;
    }

    private static bool IsSurfacePreoccupation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var kw in _surfaceKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDeepPreoccupation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var kw in _deepKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}