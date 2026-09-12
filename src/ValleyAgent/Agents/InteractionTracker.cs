using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace ValleyAgent.Agents;

/// Tracks daily interaction scores for all villager NPCs.
/// Score formula: each dialogue +1 (max 3/day), decays -2 per day, floor=0.
public class InteractionTracker
{
    private readonly Dictionary<string, int> _scores = new();

    /// Call when player starts a dialogue with an NPC
    public void RecordDialogue(string npcName)
    {
        if (!_scores.TryGetValue(npcName, out var value))
        {
            value = 0;
        }

        if (value < 12)
        {
            _scores[npcName] = ++value;
        }
    }

    /// Call on DayStart — apply decay
    public void ApplyDecay()
    {
        var keys = _scores.Keys.ToList();
        foreach (var key in keys)
        {
            _scores[key] = Math.Max(0, _scores[key] - 2);
            // Remove if decayed to zero
            if (_scores[key] == 0)
            {
                _ = _scores.Remove(key);
            }
        }
    }

    /// Get top N NPCs by interaction score (weighted random selection)
    public List<string> GetTopNpcCandidates(int topN = 5)
    {
        return _scores
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// Weighted random: higher score = more likely, but any top-5 can be selected
    public string? PickWeightedRandom()
    {
        var candidates = GetTopNpcCandidates();
        if (candidates.Count == 0)
        {
            return null;
        }

        var highest = _scores[candidates[0]];
        if (highest > 12)
        {
            return candidates[0];
        }

        var weights = candidates.Select(n => (double)_scores[n]).ToList();
        var totalWeight = weights.Sum();
        var roll = RandomNumberGenerator.GetInt32(0, (int)totalWeight + 1);

        double cumulative = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative)
            {
                return candidates[i];
            }
        }

        return candidates[0];
    }

    public int GetScore(string npcName) => _scores.GetValueOrDefault(npcName, 0);

    // Save/Load support
    public Dictionary<string, int> GetAllScores() => new(_scores);

    public void SetScores(Dictionary<string, int> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        _scores.Clear();
        foreach (var kv in scores)
        {
            _scores[kv.Key] = kv.Value;
        }
    }
}