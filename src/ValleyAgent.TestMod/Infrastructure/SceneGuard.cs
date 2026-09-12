#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Brain;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     A record capturing all mutable game state that PrepareScene modifies,
///     so that Cleanup can atomically restore the world to its original condition.
/// </summary>
public record SceneToken(
    Point OriginalNpcTile,
    int OriginalTimeOfDay,
    string OriginalSeason,
    int OriginalDayOfMonth,
    int OriginalYear,
    AgentState OriginalAgentState,
    bool OriginalSuppressDecisions,
    Dictionary<Vector2, TerrainFeature> CleanedTerrainFeatures
);

/// <summary>
///     Configuration options for a scene prepare operation.
/// </summary>
public class SceneOptions
{
    /// <summary>Game time of day in Stardew format (e.g. 1000 = 10:00 AM).</summary>
    public int TimeOfDay { get; set; } = 1000;

    /// <summary>Season name (spring, summer, fall, winter).</summary>
    public string Season { get; set; } = "summer";

    /// <summary>Day of month (1-28).</summary>
    public int Day { get; set; } = 1;

    /// <summary>Game year.</summary>
    public int Year { get; set; } = 1;

    /// <summary>Whether to suppress LLM decision triggers during the scene.</summary>
    public bool SuppressDecisions { get; set; } = true;
}

/// <summary>
///     Provides deterministic scene initialisation and cleanup for automated tests.
///     PrepareScene enforces five rules:
///     1. Clean map  — removes terrain features, objects, and resource clumps from the target location
///     2. Validate NPC spawn — ensures the NPC tile is walkable; auto-corrects via FindWalkableTileNear
///     3. Fix time   — sets Game1.timeOfDay, currentSeason, dayOfMonth, year, isRaining
///     4. Reset agent — forces IDLE, restores HP, clears inventory, sets emotion to Neutral, clears memory
///     5. Control LLM — sets DebugFlags.SuppressDecisions
///     Cleanup restores every modified field to its original value.
/// </summary>
public static class SceneGuard
{
    /// <summary>
    ///     Prepares a scene for testing: cleans the location, validates NPC spawn,
    ///     fixes game time, resets the agent, and optionally suppresses LLM decisions.
    /// </summary>
    /// <param name="npc">The NPC to validate and position.</param>
    /// <param name="agent">The agent instance to reset.</param>
    /// <param name="location">The GameLocation to clean.</param>
    /// <param name="options">Scene configuration options.</param>
    /// <returns>A SceneToken that can be passed to Cleanup to restore the world.</returns>
    public static SceneToken PrepareScene(
        NPC npc,
        AgentInstance agent,
        GameLocation location,
        SceneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(location);
        options ??= new SceneOptions();

        // ── Rule 1: Clean map ─────────────────────────────────────────────────
        // 保存 terrain features 原始值用于恢复
        var cleanedTerrainFeatures = new Dictionary<Vector2, TerrainFeature>();

        // 保存并移除 HoeDirt terrain features
        var terrainToRemove = new List<Vector2>();
        foreach (var tile in location.terrainFeatures.Keys)
        {
            if (location.terrainFeatures[tile] is HoeDirt)
            {
                terrainToRemove.Add(tile);
            }
        }

        foreach (var tile in terrainToRemove)
        {
            // 保存原始 terrain feature 以便恢复
            cleanedTerrainFeatures[tile] = location.terrainFeatures[tile];
            _ = location.terrainFeatures.Remove(tile);
        }

        // 移除 objects（不保存原始值，因为无法恢复）
        var objectsToRemove = new List<Vector2>();
        foreach (var tile in location.objects.Keys)
        {
            objectsToRemove.Add(tile);
        }

        foreach (var tile in objectsToRemove)
        {
            _ = location.objects.Remove(tile);
        }

        // Remove resource clumps (boulders, stumps)
        var clumpsToRemove = new List<ResourceClump>();
        foreach (var clump in location.resourceClumps)
        {
            clumpsToRemove.Add(clump);
        }

        foreach (var clump in clumpsToRemove)
        {
            _ = location.resourceClumps.Remove(clump);
        }

        // ── Rule 2: Validate NPC spawn ───────────────────────────────────────
        var originalTile = npc.TilePoint;
        var desiredTile = new Vector2(originalTile.X, originalTile.Y);
        var anchorTile = desiredTile; // use NPC position as anchor

        var tileVec = new Vector2((int)desiredTile.X, (int)desiredTile.Y);
        if (!TestScenes.IsTileWalkable(location, tileVec))
        {
            var corrected = TestScenes.FindWalkableTileNear(location, desiredTile, anchorTile);
            npc.setTileLocation(new Vector2(corrected.X, corrected.Y));
        }

        // ── Rule 3: Fix time ─────────────────────────────────────────────────
        var originalTime = Game1.timeOfDay;
        var originalSeason = Game1.currentSeason;
        var originalDay = Game1.dayOfMonth;
        var originalYear = Game1.year;

        Game1.timeOfDay = options.TimeOfDay;
        Game1.currentSeason = options.Season;
        Game1.dayOfMonth = options.Day;
        Game1.year = options.Year;
        Game1.isRaining = false;

        // 日期被直接改写时同步 weatherIcon，避免残留"节日日"标记
        // （weatherIcon==1 会让原版十分钟更新去加载 Data/Festivals/<日>.xnb）
        Game1.updateWeatherIcon();

        // ── Rule 4: Reset agent ───────────────────────────────────────────────
        var originalState = agent.StateMachine.CurrentStateFlag;

        // Force IDLE
        _ = agent.StateMachine.ForceTransition(AgentState.IDLE);

        // Fully restore health
        agent.Health.Respawn();

        // Clear inventory
        agent.Inventory.Clear();

        // Set emotion to Neutral
        agent.Brain.SyncEmotion(NpcEmotion.Neutral, 1.0f, "Scene reset");

        // Clear short-term memory
        agent.Brain.ShortTermMemories.Clear();
        agent.Brain.CurrentGoal = string.Empty;
        agent.Brain.LastDecisionState = string.Empty;
        agent.Brain.LastDecisionReason = string.Empty;
        agent.Brain.LastDialogueResponse = string.Empty;

        // ── Rule 5: Control LLM ───────────────────────────────────────────────
        var originalSuppress = DebugFlags.SuppressDecisions;
        DebugFlags.SuppressDecisions = options.SuppressDecisions;

        return new SceneToken(
            originalTile,
            originalTime,
            originalSeason,
            originalDay,
            originalYear,
            originalState,
            originalSuppress,
            cleanedTerrainFeatures
        );
    }

    /// <summary>
    ///     Restores the world to its state before PrepareScene was called.
    /// </summary>
    /// <param name="npc">The NPC to restore to original position.</param>
    /// <param name="agent">The agent to restore to original state.</param>
    /// <param name="location">The GameLocation to restore objects into.</param>
    /// <param name="token">The token returned by PrepareScene.</param>
    public static void Cleanup(
        NPC npc,
        AgentInstance agent,
        GameLocation location,
        SceneToken token)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(location);
        // ── Restore NPC tile ─────────────────────────────────────────────────
        npc.setTileLocation(new Vector2(token.OriginalNpcTile.X, token.OriginalNpcTile.Y));

        // ── Restore time ────────────────────────────────────────────────────
        Game1.timeOfDay = token.OriginalTimeOfDay;
        Game1.currentSeason = token.OriginalSeason;
        Game1.dayOfMonth = token.OriginalDayOfMonth;
        Game1.year = token.OriginalYear;
        Game1.updateWeatherIcon();

        // ── Restore agent state ─────────────────────────────────────────────
        _ = agent.StateMachine.ForceTransition(token.OriginalAgentState);
        agent.Health.Respawn();

        // ── Restore LLM suppression flag ───────────────────────────────────
        DebugFlags.SuppressDecisions = token.OriginalSuppressDecisions;

        // ── 恢复 terrain features（使用保存的原始值）──────────────────────
        foreach (var kvp in token.CleanedTerrainFeatures)
        {
            if (!location.terrainFeatures.ContainsKey(kvp.Key))
            {
                location.terrainFeatures[kvp.Key] = kvp.Value;
            }
        }

        // 注意：不恢复 objects，因为无法知道原始内容，用石头占位无意义
    }
}