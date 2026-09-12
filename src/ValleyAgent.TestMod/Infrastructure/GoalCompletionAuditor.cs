#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;
using ValleyAgent.LLM;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.Testing;
using CompletionVerdict = ValleyAgent.Testing.CompletionVerdict;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     Intent→Outcome verification auditor for FARM/FIGHT/MINE/FOLLOW states.
///     Passive — records data when called by test harness, does not monitor actively.
///     All verification is deterministic (no LLM).
/// </summary>
public class GoalCompletionAuditor
{
    /// <summary>All recorded goals, append-only. Keyed by NPC name for fast lookup.</summary>
    private readonly Dictionary<string, List<GoalRecord>> _records = new(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Record an LLM decision with an environmental snapshot.
    ///     Called by the test harness when a decision is made.
    /// </summary>
    public void RecordDecision(AgentInstance agent, AIDecision decision, int tick)
    {
        if (agent == null || decision == null)
        {
            return;
        }

        var loc = Game1.getCharacterFromName(agent.NpcName)?.currentLocation ?? Game1.currentLocation;
        var npcName = agent.NpcName;

        var crops = CountMatureCrops(loc);
        var monsters = CountMonsters(loc);
        var rocks = CountBreakableRocks(loc);

        var record = new GoalRecord
        {
            NpcName = npcName,
            StartTick = tick,
            TargetState = decision.GetAgentState(),
            Reason = decision.Reason,
            Thought = decision.Thought,
            NearbyCropsAtStart = crops,
            NearbyMonstersAtStart = monsters,
            NearbyRocksAtStart = rocks
        };

        if (!_records.TryGetValue(npcName, out var list))
        {
            list = new List<GoalRecord>();
            _records[npcName] = list;
        }

        list.Add(record);
    }

    /// <summary>
    ///     Mark the exit of a state for the latest unreviewed goal matching the NPC and state.
    ///     Called by the test harness when an agent transitions out of a state.
    /// </summary>
    public void AuditExit(AgentInstance agent, AgentState exitedState, int tick)
    {
        if (agent == null)
        {
            return;
        }

        var npcName = agent.NpcName;
        if (!_records.TryGetValue(npcName, out var list) || list.Count == 0)
        {
            return;
        }

        // Find the latest unreviewed goal for this NPC that matches the exited state
        GoalRecord? target = null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var goal = list[i];
            if (goal.TargetState == exitedState && !goal.Reviewed)
            {
                target = goal;
                break;
            }
        }

        if (target == null)
        {
            return;
        }

        target.DurationTicks = tick - target.StartTick;
        target.Reviewed = true;

        // Auto-verify on exit
        var (verdict, failureReason) = VerifyCompletion(agent, target);
        target.Completed = verdict == CompletionVerdict.Success;
        target.FailureReason = failureReason;
    }

    /// <summary>
    ///     Deterministically verify whether a goal was meaningfully completed.
    /// </summary>
    public static (CompletionVerdict Verdict, string? FailureReason) VerifyCompletion(AgentInstance? agent,
        GoalRecord goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (agent == null)
        {
            return (CompletionVerdict.Unclear, "Agent is null");
        }

        var loc = Game1.getCharacterFromName(agent.NpcName)?.currentLocation ?? Game1.currentLocation;

        return goal.TargetState switch
        {
            AgentState.FARM => VerifyFarm(goal, loc),
            AgentState.FIGHT => VerifyFight(goal, loc),
            AgentState.MINE => VerifyMine(goal, loc),
            AgentState.FOLLOW => VerifyFollow(agent, goal, loc),
            _ => (CompletionVerdict.Unclear, null)
        };
    }

    /// <summary>
    ///     Generate a human-readable report of all recorded goals and their verdicts.
    /// </summary>
    public string GenerateReport()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("=== GoalCompletionAuditor Report ===");
        _ = sb.AppendLine();

        if (_records.Count == 0)
        {
            _ = sb.AppendLine("No goals recorded.");
            return sb.ToString();
        }

        // Per-state aggregation
        var stateGroups = _records.SelectMany(kvp => kvp.Value)
            .GroupBy(g => g.TargetState)
            .OrderBy(g => g.Key.ToString());

        foreach (var group in stateGroups)
        {
            var goals = group.ToList();
            var total = goals.Count;
            var success = goals.Count(g => g.Completed == true);
            var failure = goals.Count(g => g.Completed == false);
            var unclear = goals.Count(g => g.Completed == null);

            var rate = total > 0 ? (double)success / total * 100 : 0;
            _ = sb.AppendLine($"## {group.Key}  (completion rate: {rate:F1}%  {success}/{total} success)");
            _ = sb.AppendLine($"   Failed: {failure}  |  Unclear: {unclear}");
            _ = sb.AppendLine();

            foreach (var goal in goals)
            {
                var status = goal.Completed switch
                {
                    true => "✓ Success",
                    false => "✗ Failed",
                    null => "○ Unclear"
                };
                _ = sb.AppendLine(
                    $"  [{status}] {goal.NpcName}  tick {goal.StartTick}→{goal.StartTick + goal.DurationTicks}  ({goal.DurationTicks} ticks)");
                _ = sb.AppendLine($"           Reason: {goal.Reason}");
                if (!string.IsNullOrEmpty(goal.FailureReason))
                {
                    _ = sb.AppendLine($"           Failure: {goal.FailureReason}");
                }
            }

            _ = sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Clear all recorded goals.</summary>
    public void Clear() => _records.Clear();

    /// <summary>Get all goal records for an NPC.</summary>
    public IReadOnlyList<GoalRecord> GetRecords(string npcName) => _records.TryGetValue(npcName, out var list)
        ? list.AsReadOnly()
        : Array.Empty<GoalRecord>();

    // ─────────────────────────────────────────────────────────────────────────
    // Deterministic verification helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static (CompletionVerdict, string?) VerifyFarm(GoalRecord goal, GameLocation loc)
    {
        var current = CountMatureCrops(loc);
        return GoalVerifier.VerifyFarm(current, goal.NearbyCropsAtStart);
    }

    private static (CompletionVerdict, string?) VerifyFight(GoalRecord goal, GameLocation loc)
    {
        var current = CountMonsters(loc);
        return GoalVerifier.VerifyFight(current, goal.NearbyMonstersAtStart);
    }

    private static (CompletionVerdict, string?) VerifyMine(GoalRecord goal, GameLocation loc)
    {
        var current = CountBreakableRocks(loc);
        return GoalVerifier.VerifyMine(current, goal.NearbyRocksAtStart);
    }

    private static (CompletionVerdict, string?) VerifyFollow(AgentInstance agent, GoalRecord goal, GameLocation loc)
    {
        var player = Game1.player;
        if (player == null)
        {
            return (CompletionVerdict.Unclear, "Player not found");
        }

        if (loc.getCharacterFromName(agent.NpcName) is not NPC npc)
        {
            return (CompletionVerdict.Unclear, $"NPC {agent.NpcName} not found in location");
        }

        var dist = Vector2.Distance(npc.Tile, player.Tile);
        return GoalVerifier.VerifyFollow(dist);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Environmental counters (full-map, no radius limit for simplicity)
    // ─────────────────────────────────────────────────────────────────────────

    private static int CountMatureCrops(GameLocation loc)
    {
        return loc.terrainFeatures.Values
            .OfType<HoeDirt>()
            .Count(d => d.crop != null && d.crop.fullyGrown.Value);
    }

    private static int CountMonsters(GameLocation loc) => loc.characters.OfType<Monster>().Count();

    private static int CountBreakableRocks(GameLocation loc)
    {
        return loc.objects.Values.Count(o =>
            o.Name.Contains("Stone", StringComparison.OrdinalIgnoreCase) ||
            o.Name.Contains("Boulder", StringComparison.OrdinalIgnoreCase) ||
            o.ItemId == "343");
    }
}

/// <summary>
///     A single goal record captured at decision time.
/// </summary>
public class GoalRecord
{
    /// <summary>The NPC this goal belongs to.</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>Game tick when this decision was recorded.</summary>
    public int StartTick { get; set; }

    /// <summary>The target state chosen by LLM.</summary>
    public AgentState TargetState { get; set; }

    /// <summary>LLM-provided reason for the decision.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>LLM-provided inner thought at decision time.</summary>
    public string Thought { get; set; } = string.Empty;

    /// <summary>Duration in ticks from start to exit.</summary>
    public int DurationTicks { get; set; }

    /// <summary>Mature crop count at decision time (FARM).</summary>
    public int NearbyCropsAtStart { get; set; }

    /// <summary>Monster count at decision time (FIGHT).</summary>
    public int NearbyMonstersAtStart { get; set; }

    /// <summary>Breakable rock count at decision time (MINE).</summary>
    public int NearbyRocksAtStart { get; set; }

    /// <summary>Whether the goal was completed (null = unreviewed).</summary>
    public bool? Completed { get; set; }

    /// <summary>Whether this record has been reviewed by AuditExit.</summary>
    public bool Reviewed { get; set; }

    /// <summary>Reason for failure if verification failed.</summary>
    public string? FailureReason { get; set; }
}