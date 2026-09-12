using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace ValleyAgent.Handlers;

/// <summary>
///     Helper for scanning wood/weed/twig resources in any location.
///     Used by BuildDecisionContext to provide exhaustive environmental awareness.
/// </summary>
public static class WoodScanHelper
{
    private const int DefaultRadius = 12;

    /// <summary>Returns a description of nearby trees, stumps, and weeds, or null if none.</summary>
    public static string? ScanEnvironment(NPC npc, int radius = DefaultRadius)
    {
        if (npc?.currentLocation == null)
        {
            return null;
        }

        var results = new List<string>();
        var npcTile = npc.Tile;

        // Scan terrain features (trees, stumps)
        if (npc.currentLocation.terrainFeatures != null)
        {
            foreach (var tileV in npc.currentLocation.terrainFeatures.Keys)
            {
                var feature = npc.currentLocation.terrainFeatures[tileV];
                if (feature == null)
                {
                    continue;
                }

                var dist = Vector2.Distance(npcTile, tileV);
                if (dist > radius)
                {
                    continue;
                }

                if (feature is Tree)
                {
                    results.Add($"Tree at tile {tileV}");
                }
                else if (feature is ResourceClump)
                {
                    // Stumps and logs — width/height can distinguish types
                    // 600 = stump (1x1), 602 = log (2x1), 604 = hollow log (2x2)
                    // Default behaviour when not sure: include anyway
                    results.Add($"Resource clump at tile {tileV}");
                }
            }
        }

        // Scan objects (weeds, twigs, branches, wood)
        if (npc.currentLocation.objects != null)
        {
            foreach (var tileV in npc.currentLocation.objects.Keys)
            {
                var obj = npc.currentLocation.objects[tileV];
                if (obj == null)
                {
                    continue;
                }

                var dist = Vector2.Distance(npcTile, tileV);
                if (dist > radius)
                {
                    continue;
                }

                var name = obj.Name ?? "";
                var displayName = obj.DisplayName ?? "";

                // Weed types
                if (name.Contains("Weed", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"Weed at tile {tileV}");
                    continue;
                }

                // Twig / branch
                if (name.Contains("Twig", StringComparison.OrdinalIgnoreCase) ||
                    displayName.Contains("Twig", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"Twig at tile {tileV}");
                    continue;
                }

                // Other debris / wood
                if (name.Contains("Wood", StringComparison.OrdinalIgnoreCase) ||
                    displayName.Contains("Wood", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"Wood debris at tile {tileV}");
                    continue;
                }

                // Leafy debris / fiber
                if (name.Contains("Leaf", StringComparison.OrdinalIgnoreCase) ||
                    displayName.Contains("Leaf", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"Leaves at tile {tileV}");
                }
            }
        }

        return results.Count > 0 ? string.Join("; ", results) : null;
    }
}