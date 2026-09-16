using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;

namespace ValleyAgent.StateMachine;

/// <summary>
///     Static utility for checking whether a state transition is valid/feasible
///     given the NPC's current location and game state.
///     Extracted from ModEntry.IsTransitionValid to be reusable across the codebase.
/// </summary>
public static class StateFeasibility
{
    /// <summary>
    ///     Checks whether the given target state is feasible for the NPC at its current location.
    ///     Returns true if the state is valid (or if validation is impossible).
    ///     Returns false for states that can never work at this location:
    ///     - FARM requires Farm/FarmHouse or outdoors with HoeDirt
    ///     - MINE requires MineShaft/SkullCave/underground with stones
    ///     - FORAGE requires outdoors or mine with forageables
    ///     - FIGHT requires monsters present in the location
    ///     - TALK requires player in same location
    ///     - IDLE, FOLLOW are always valid
    /// </summary>
    public static bool IsValid(NPC npc, AgentState targetState, IMonitor? monitor = null)
    {
        var location = npc.currentLocation;
        if (location == null)
        {
            return true;
        }

        var locName = location.NameOrUniqueName;

        switch (targetState)
        {
            case AgentState.FARM:
                if (location is Farm or FarmHouse or FarmCave)
                {
                    return true;
                }

                // 排除已知非农场户外位置（即使有 HoeDirt 也不行）
                if (IsKnownNonFarmOutdoors(locName))
                {
                    monitor?.Log($"StateFeasibility: {npc.Name} cannot FARM at '{locName}'", LogLevel.Debug);
                    return false;
                }

                if (location.IsOutdoors && location.terrainFeatures.Values
                        .Any(tf => tf is HoeDirt))
                {
                    return true;
                }

                monitor?.Log($"StateFeasibility: {npc.Name} cannot FARM at '{locName}'", LogLevel.Debug);
                return false;

            case AgentState.MINE:
                if (location is MineShaft or VolcanoDungeon)
                {
                    return true;
                }

                if (locName.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (location.Objects.Values.Any(obj =>
                        (obj.Name?.Contains("Stone") ?? false) || (obj.Name?.Contains("Boulder") ?? false)))
                {
                    return true;
                }

                monitor?.Log($"StateFeasibility: {npc.Name} cannot MINE at '{locName}'", LogLevel.Debug);
                return false;

            case AgentState.FORAGE:
                if (location.IsOutdoors)
                {
                    return true;
                }

                if (location is MineShaft)
                {
                    return true;
                }

                if (location.Objects.Values.Any(obj => obj.IsSpawnedObject))
                {
                    return true;
                }

                monitor?.Log($"StateFeasibility: {npc.Name} cannot FORAGE at '{locName}'", LogLevel.Debug);
                return false;

            case AgentState.FIGHT:
                if (location.characters.Any(ch => ch is Monster))
                {
                    return true;
                }

                monitor?.Log($"StateFeasibility: {npc.Name} cannot FIGHT at '{locName}' — no monsters", LogLevel.Debug);
                return false;

            case AgentState.TALK:
                if (Game1.player?.currentLocation == location)
                {
                    return true;
                }

                monitor?.Log($"StateFeasibility: {npc.Name} cannot TALK at '{locName}' — player elsewhere", LogLevel.Debug);
                return false;

            // 这些状态始终有效
            case AgentState.IDLE:
            case AgentState.FOLLOW:
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    ///     获取当前 NPC 位置所有被禁用的状态名称列表（用逗号分隔）。
    ///     用于注入到 LLM decision prompt，让 LLM 提前知道哪些状态不能选。
    ///     如果没有被禁用的状态，返回 "none"。
    /// </summary>
    public static string GetBlockedStatesList(NPC npc)
    {
        var blocked = new List<string>();
        var states = new[] { AgentState.FARM, AgentState.MINE, AgentState.FORAGE, AgentState.FIGHT, AgentState.TALK };
        foreach (var st in states)
        {
            if (!IsValid(npc, st))
            {
                blocked.Add(st.ToString());
            }
        }

        return blocked.Count > 0 ? string.Join(", ", blocked) : "none";
    }

    // 与 FarmHandler.IsFarmLocation 的排除列表保持一致
    private static bool IsKnownNonFarmOutdoors(string name)
    {
        return name.Equals("Town", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Forest", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Mountain", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Beach", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Desert", StringComparison.OrdinalIgnoreCase)
               || name.Equals("BusStop", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Backwoods", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Railroad", StringComparison.OrdinalIgnoreCase);
    }
}