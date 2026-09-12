#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;
using ValleyAgent.Brain;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     Lightweight tick-snapshot recording with anomaly detection for test debugging.
///     Records one snapshot every 10 ticks (not every tick) to minimize overhead.
/// </summary>
public class BehaviorRecorder
{
    /// <summary>Record a snapshot every 10 ticks.</summary>
    public const int TickInterval = 10;

    /// <summary>State flicker threshold: less than 60 ticks between same state transitions.</summary>
    public const int StateFlickerThresholdTicks = 60;


    /// <summary>Controller storm threshold: > 20 PathFindController rebuilds.</summary>
    public const int ControllerStormThreshold = 20;

    /// <summary>Handler-level action events per NPC.</summary>
    private readonly Dictionary<string, List<HandlerAction>> _actions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Emotion changes tracked per NPC.</summary>
    private readonly Dictionary<string, List<EmotionChange>> _emotionChanges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last controller reference per NPC (for storm detection).</summary>
    private readonly Dictionary<string, PathFindController?> _lastControllers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last tick a snapshot was recorded per NPC.</summary>
    private readonly Dictionary<string, int> _lastSnapshotTicks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last recorded state per NPC (for transition detection).</summary>
    private readonly Dictionary<string, AgentState> _lastStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Count of PathFindController creations per NPC (for controller storm detection).</summary>
    private readonly Dictionary<string, int> _pathfindRebuildCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-NPC snapshots keyed by NPC name.</summary>
    private readonly Dictionary<string, List<TickSnapshot>> _snapshots = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>State transitions tracked per NPC.</summary>
    private readonly Dictionary<string, List<StateTransition>> _transitions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Records a tick snapshot for an NPC if the tick interval has elapsed.
    ///     Call this from test Update() loop.
    /// </summary>
    public void RecordSnapshotIfDue(string npcName, int currentTick, NPC npc, AgentInstance? agent)
    {
        ArgumentNullException.ThrowIfNull(npc);
        // Check tick interval - only record every TickInterval ticks
        if (_lastSnapshotTicks.TryGetValue(npcName, out var lastTick) && currentTick - lastTick < TickInterval)
        {
            return;
        }

        _lastSnapshotTicks[npcName] = currentTick;

        // Ensure lists exist
        if (!_snapshots.TryGetValue(npcName, out var snapshotList))
        {
            snapshotList = new List<TickSnapshot>();
            _snapshots[npcName] = snapshotList;
        }

        if (!_transitions.TryGetValue(npcName, out var transitionList))
        {
            transitionList = new List<StateTransition>();
            _transitions[npcName] = transitionList;
        }

        if (!_emotionChanges.TryGetValue(npcName, out var emotionList))
        {
            emotionList = new List<EmotionChange>();
            _emotionChanges[npcName] = emotionList;
        }

        // Get current state
        var currentState = agent?.StateMachine.CurrentStateFlag ?? AgentState.IDLE;

        // Detect state transition
        if (_lastStates.TryGetValue(npcName, out var lastState) && lastState != currentState)
        {
            _transitions[npcName].Add(new StateTransition
            {
                Tick = currentTick,
                FromState = lastState,
                ToState = currentState,
                Reason = $"State changed at tick {currentTick}"
            });

            _lastStates[npcName] = currentState;
        }
        else if (!_lastStates.ContainsKey(npcName))
        {
            _lastStates[npcName] = currentState;
        }

        // Detect emotion change
        if (agent != null)
        {
            var currentEmotion = agent.Brain.Emotion;

            // Check if we have a previous snapshot with emotion data
            if (_snapshots[npcName].Count > 0)
            {
                var lastSnapshot = _snapshots[npcName][^1];
                if (lastSnapshot.Emotion != currentEmotion)
                {
                    _emotionChanges[npcName].Add(new EmotionChange
                    {
                        Tick = currentTick,
                        FromEmotion = lastSnapshot.Emotion,
                        ToEmotion = currentEmotion
                    });
                }
            }
        }

        // Check for PathFindController rebuild (controller storm detection)
        var hasController = npc.controller is not null;
        if (hasController)
        {
            if (_lastControllers.TryGetValue(npcName, out var lastCtrl) && lastCtrl == null)
            {
                // Controller was recreated
                if (!_pathfindRebuildCounts.TryGetValue(npcName, out var rebuildCount))
                {
                    rebuildCount = 0;
                }

                _pathfindRebuildCounts[npcName] = rebuildCount + 1;
            }
        }

        _lastControllers[npcName] = npc.controller;

        // Build snapshot
        var snapshot = new TickSnapshot
        {
            Tick = currentTick,
            State = currentState,
            Position = npc.Tile,
            LocationName = npc.currentLocation?.Name ?? "Unknown",
            Health = agent?.Health.Health ?? 0,
            MaxHealth = agent?.Health.MaxHealth ?? 0,
            Emotion = agent?.Brain.Emotion ?? NpcEmotion.Neutral,
            EmotionIntensity = 1.0f, // Default intensity
            HasController = hasController,
            IsMoving = npc.isMoving()
        };

        _snapshots[npcName].Add(snapshot);
    }

    /// <summary>
    ///     Records a handler-level action event for ReAct analysis.
    ///     Call this from tests to track Observe/Move/Act/ClosingRitual events.
    /// </summary>
    public void RecordAction(string npcName, string actionType, string target, string result)
    {
        if (!_actions.TryGetValue(npcName, out var actionList))
        {
            actionList = new List<HandlerAction>();
            _actions[npcName] = actionList;
        }

        actionList.Add(new HandlerAction
        {
            Tick = GetCurrentTick(npcName),
            ActionType = actionType,
            Target = target,
            Result = result
        });
    }

    /// <summary>
    ///     Gets the current tick for an NPC (from latest snapshot or 0 if none).
    /// </summary>
    private int GetCurrentTick(string npcName) =>
        _snapshots.TryGetValue(npcName, out var list) && list.Count > 0 ? list[^1].Tick : 0;

    /// <summary>
    ///     Analyzes ReAct pattern from recorded handler actions.
    ///     Counts Observe, Move, Act, and ClosingRitual events.
    /// </summary>
    internal ReActAnalysis AnalyzeReActPattern(string npcName)
    {
        var analysis = new ReActAnalysis();

        if (!_actions.TryGetValue(npcName, out var actions))
        {
            return analysis;
        }

        foreach (var action in actions)
        {
            switch (action.ActionType)
            {
                case "Observe":
                    analysis.ObserveCount++;
                    break;
                case "Move":
                    analysis.MoveCount++;
                    break;
                case "Act":
                    analysis.ActCount++;
                    break;
                case "ClosingRitual":
                    analysis.ClosingRitualCount++;
                    break;
            }
        }

        return analysis;
    }

    /// <summary>
    ///     Detects anomalies in the recorded behavior.
    ///     Returns a list of anomalies found.
    /// </summary>
    internal IReadOnlyList<Anomaly> DetectAnomalies(string npcName)
    {
        var anomalies = new List<Anomaly>();

        if (!_snapshots.TryGetValue(npcName, out var snapshots) || snapshots.Count < 2)
        {
            return anomalies;
        }

        // Check 1: State flicker (rapid back-and-forth transitions)
        var stateOccurrences = new Dictionary<AgentState, List<int>>();
        foreach (var snap in snapshots)
        {
            if (!stateOccurrences.TryGetValue(snap.State, out var occList))
            {
                occList = new List<int>();
                stateOccurrences[snap.State] = occList;
            }

            stateOccurrences[snap.State].Add(snap.Tick);
        }

        foreach (var kvp in stateOccurrences)
        {
            var ticks = kvp.Value;
            for (var i = 1; i < ticks.Count; i++)
            {
                var interval = ticks[i] - ticks[i - 1];
                if (interval is < StateFlickerThresholdTicks and > 0)
                {
                    anomalies.Add(new Anomaly
                    {
                        Type = "StateFlicker",
                        Tick = ticks[i],
                        Description =
                            $"State '{kvp.Key}' flickered: returned within {interval} ticks (threshold: {StateFlickerThresholdTicks})"
                    });
                }
            }
        }

        // Check 2: Controller storm (> 20 PathFindController rebuilds)
        if (_pathfindRebuildCounts.TryGetValue(npcName, out var rebuildCount))
        {
            if (rebuildCount > ControllerStormThreshold)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "ControllerStorm",
                    Tick = GetCurrentTick(npcName),
                    Description =
                        $"PathFindController rebuilt {rebuildCount} times (threshold: {ControllerStormThreshold})"
                });
            }
        }

        return anomalies;
    }

    /// <summary>
    ///     Generates a human-readable report for the given NPC.
    /// </summary>
    public string GenerateReport(string npcName)
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine($"=== Behavior Report for {npcName} ===");
        _ = sb.AppendLine();

        // snapshots summary
        if (_snapshots.TryGetValue(npcName, out var snapshots) && snapshots.Count > 0)
        {
            _ = sb.AppendLine($"Total Snapshots: {snapshots.Count}");
            _ = sb.AppendLine($"Tick Range: {snapshots[0].Tick} - {snapshots[^1].Tick}");
            _ = sb.AppendLine();
        }
        else
        {
            _ = sb.AppendLine("No snapshots recorded.");
            return sb.ToString();
        }

        // State timeline
        _ = sb.AppendLine("--- State Timeline ---");
        if (_transitions.TryGetValue(npcName, out var transitions) && transitions.Count > 0)
        {
            foreach (var t in transitions)
            {
                _ = sb.AppendLine($"  [tick {t.Tick}] {t.FromState} → {t.ToState}");
            }
        }
        else
        {
            _ = sb.AppendLine("  No state transitions recorded.");
        }

        _ = sb.AppendLine();

        // Emotion timeline
        _ = sb.AppendLine("--- Emotion Timeline ---");
        if (_emotionChanges.TryGetValue(npcName, out var emotionChanges) && emotionChanges.Count > 0)
        {
            foreach (var e in emotionChanges)
            {
                _ = sb.AppendLine($"  [tick {e.Tick}] {e.FromEmotion} → {e.ToEmotion}");
            }
        }
        else
        {
            _ = sb.AppendLine("  No emotion changes recorded.");
        }

        _ = sb.AppendLine();

        // ReAct analysis
        _ = sb.AppendLine("--- ReAct Pattern Analysis ---");
        var react = AnalyzeReActPattern(npcName);
        _ = sb.AppendLine($"  Observe: {react.ObserveCount}");
        _ = sb.AppendLine($"  Move: {react.MoveCount}");
        _ = sb.AppendLine($"  Act: {react.ActCount}");
        _ = sb.AppendLine($"  ClosingRitual: {react.ClosingRitualCount}");
        _ = sb.AppendLine();

        // Anomalies
        _ = sb.AppendLine("--- Anomalies ---");
        var anomalies = DetectAnomalies(npcName);
        if (anomalies.Count > 0)
        {
            foreach (var a in anomalies)
            {
                _ = sb.AppendLine($"  [{a.Type}] tick {a.Tick}: {a.Description}");
            }
        }
        else
        {
            _ = sb.AppendLine("  No anomalies detected.");
        }

        _ = sb.AppendLine();

        // PathFindController rebuild count
        if (_pathfindRebuildCounts.TryGetValue(npcName, out var rebuildCount))
        {
            _ = sb.AppendLine($"PathFindController Rebuilds: {rebuildCount}");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Gets all snapshots for an NPC.
    /// </summary>
    internal IReadOnlyList<TickSnapshot> GetSnapshots(string npcName) => _snapshots.TryGetValue(npcName, out var list)
        ? list.AsReadOnly()
        : Array.Empty<TickSnapshot>();

    /// <summary>
    ///     Gets all state transitions for an NPC.
    /// </summary>
    internal IReadOnlyList<StateTransition> GetTransitions(string npcName) =>
        _transitions.TryGetValue(npcName, out var list) ? list.AsReadOnly() : Array.Empty<StateTransition>();

    /// <summary>
    ///     Gets all emotion changes for an NPC.
    /// </summary>
    internal IReadOnlyList<EmotionChange> GetEmotionChanges(string npcName) =>
        _emotionChanges.TryGetValue(npcName, out var list) ? list.AsReadOnly() : Array.Empty<EmotionChange>();

    /// <summary>
    ///     Gets all handler actions for an NPC.
    /// </summary>
    internal IReadOnlyList<HandlerAction> GetActions(string npcName) => _actions.TryGetValue(npcName, out var list)
        ? list.AsReadOnly()
        : Array.Empty<HandlerAction>();

    /// <summary>
    ///     Clears all recorded data for an NPC.
    /// </summary>
    public void Clear(string npcName)
    {
        _ = _snapshots.Remove(npcName);
        _ = _transitions.Remove(npcName);
        _ = _emotionChanges.Remove(npcName);
        _ = _actions.Remove(npcName);
        _ = _pathfindRebuildCounts.Remove(npcName);
        _ = _lastStates.Remove(npcName);
        _ = _lastSnapshotTicks.Remove(npcName);
        _ = _lastControllers.Remove(npcName);
    }

    /// <summary>
    ///     Clears all recorded data for all NPCs.
    /// </summary>
    public void ClearAll()
    {
        _snapshots.Clear();
        _transitions.Clear();
        _emotionChanges.Clear();
        _actions.Clear();
        _pathfindRebuildCounts.Clear();
        _lastStates.Clear();
        _lastSnapshotTicks.Clear();
        _lastControllers.Clear();
    }

    /// <summary>
    ///     A single tick snapshot recording agent state at a point in time.
    /// </summary>
    internal sealed class TickSnapshot
    {
        public int Tick { get; set; }
        public AgentState State { get; set; }
        public Vector2 Position { get; set; }
        public string LocationName { get; set; } = string.Empty;
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public NpcEmotion Emotion { get; set; }
        public float EmotionIntensity { get; set; }
        public bool HasController { get; set; }
        public bool IsMoving { get; set; }
    }

    /// <summary>
    ///     Records a state transition event.
    /// </summary>
    internal sealed class StateTransition
    {
        public int Tick { get; set; }
        public AgentState FromState { get; set; }
        public AgentState ToState { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    ///     Records an emotion change event.
    /// </summary>
    internal sealed class EmotionChange
    {
        public int Tick { get; set; }
        public NpcEmotion FromEmotion { get; set; }
        public NpcEmotion ToEmotion { get; set; }
    }

    /// <summary>
    ///     Handler-level action event for ReAct analysis.
    /// </summary>
    internal sealed class HandlerAction
    {
        public int Tick { get; set; }
        public string ActionType { get; set; } = string.Empty; // Observe, Move, Act, ClosingRitual
        public string Target { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }

    /// <summary>
    ///     An anomaly detected during recording or analysis.
    /// </summary>
    internal sealed class Anomaly
    {
        public string Type { get; set; } = "";
        public int Tick { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    ///     ReAct pattern analysis result.
    /// </summary>
    internal sealed class ReActAnalysis
    {
        public int ObserveCount { get; set; }
        public int MoveCount { get; set; }
        public int ActCount { get; set; }
        public int ClosingRitualCount { get; set; }
    }
}