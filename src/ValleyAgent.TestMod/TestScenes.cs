using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace ValleyAgent.TestMod;

/// <summary>
///     Spawns test entities (monsters, crops, rocks, forageables) into the game world
///     so that ValleyAgent behaviours can be exercised and observed.
/// </summary>
public static class TestScenes
{
    private static readonly List<Monster> _spawnedMonsters = new();
    private static readonly List<Vector2> _spawnedObjectTiles = new();
    private static readonly List<Vector2> _spawnedTerrainTiles = new();

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Spawn monsters near the player and remember them for later cleanup.</summary>
    public static IReadOnlyList<Monster> SpawnFightScene(IModHelper helper, IMonitor monitor, int count = 3)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        var playerTile = Game1.player.Tile;
        monitor.Log($"Spawning {count} slimes near player ({playerTile})...", LogLevel.Info);

        var spawned = new List<Monster>();
        for (var i = 0; i < count; i++)
        {
            var tile = playerTile + new Vector2(i - 1, 1);
            var pos = tile * 64f;
            var slime = new GreenSlime(pos);
            loc.characters.Add(slime);
            _spawnedMonsters.Add(slime);
            spawned.Add(slime);
        }

        return spawned;
    }

    /// <summary>Spawn mature crops near the player on walkable tiles.</summary>
    public static IReadOnlyList<Vector2> SpawnFarmScene(IModHelper helper, IMonitor monitor, int count = 3)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        var playerTile = Game1.player.Tile;
        monitor.Log($"Spawning {count} mature crops near player...", LogLevel.Info);

        var spawned = new List<Vector2>();
        string[] cropIds = { "472", "473", "474" }; // Parsnip Seeds, Green Bean Seeds, Cauliflower Seeds
        for (var i = 0; i < count; i++)
        {
            var tile = playerTile + new Vector2(i - 1, 1);
            // Ensure the tile is walkable before spawning
            tile = FindWalkableTileNear(loc, tile, playerTile);
            if (loc.terrainFeatures.ContainsKey(tile))
            {
                continue;
            }

            var dirt = new HoeDirt();
            var seedId = cropIds[i % cropIds.Length];
            dirt.crop = new Crop(seedId, (int)tile.X, (int)tile.Y, loc);
            dirt.crop.growCompletely(); // instantly mature
            loc.terrainFeatures[tile] = dirt;
            _spawnedTerrainTiles.Add(tile);
            spawned.Add(tile);
        }

        return spawned;
    }

    /// <summary>Spawn breakable rocks near the player on walkable tiles.</summary>
    public static IReadOnlyList<Vector2> SpawnMineScene(IModHelper helper, IMonitor monitor, int count = 3)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        var playerTile = Game1.player.Tile;
        monitor.Log($"Spawning {count} rocks near player...", LogLevel.Info);

        var spawned = new List<Vector2>();
        for (var i = 0; i < count; i++)
        {
            var tile = playerTile + new Vector2(i - 1, 1);
            // Ensure the tile is walkable before spawning
            tile = FindWalkableTileNear(loc, tile, playerTile);
            if (loc.objects.ContainsKey(tile))
            {
                continue;
            }

            var stone = ItemRegistry.Create<SObject>("(O)343");
            loc.objects[tile] = stone;
            _spawnedObjectTiles.Add(tile);
            spawned.Add(tile);
        }

        return spawned;
    }

    /// <summary>Spawn forageables near the player on walkable tiles.</summary>
    public static IReadOnlyList<Vector2> SpawnForageScene(IModHelper helper, IMonitor monitor, int count = 3)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        var playerTile = Game1.player.Tile;
        monitor.Log($"Spawning {count} forageables near player...", LogLevel.Info);

        var spawned = new List<Vector2>();
        string[] forageIds = { "(O)16", "(O)18", "(O)20" }; // Wild Horseradish, Daffodil, Leek
        for (var i = 0; i < count; i++)
        {
            var tile = playerTile + new Vector2(i - 1, 1);
            // Ensure the tile is walkable before spawning
            tile = FindWalkableTileNear(loc, tile, playerTile);
            if (loc.objects.ContainsKey(tile))
            {
                continue;
            }

            var id = forageIds[i % forageIds.Length];
            var forage = ItemRegistry.Create<SObject>(id);
            forage.IsSpawnedObject = true;
            forage.CanBeGrabbed = true;
            loc.objects[tile] = forage;
            _spawnedObjectTiles.Add(tile);
            spawned.Add(tile);
        }

        return spawned;
    }

    /// <summary>Spawn breakable rocks near the specified NPC tile on walkable tiles.</summary>
    public static IReadOnlyList<Vector2> SpawnMineSceneNearNpc(IModHelper helper, IMonitor monitor, Point npcTile,
        int count = 3)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        monitor.Log($"Spawning {count} rocks near NPC at {npcTile}...", LogLevel.Info);

        var spawned = new List<Vector2>();
        var npcPos = new Vector2(npcTile.X, npcTile.Y);
        for (var i = 0; i < count; i++)
        {
            // Spawn 2-3 tiles away from NPC in various directions
            var offset = i switch
            {
                0 => new Vector2(2, 0),
                1 => new Vector2(0, 2),
                2 => new Vector2(-2, 0),
                _ => new Vector2(2, 2)
            };
            var desiredTile = npcPos + offset;
            // Ensure the tile is walkable before spawning
            var tile = FindWalkableTileNear(loc, desiredTile, npcPos);
            if (loc.objects.ContainsKey(tile))
            {
                continue;
            }

            var stone = ItemRegistry.Create<SObject>("(O)343");
            loc.objects[tile] = stone;
            _spawnedObjectTiles.Add(tile);
            spawned.Add(tile);
        }

        return spawned;
    }

    /// <summary>Spawn forageables near the specified NPC tile on walkable tiles.</summary>
    public static List<Vector2> SpawnForageSceneNearNpc(IModHelper helper, IMonitor monitor, Point npcTile,
        int count = 3)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        monitor.Log($"Spawning {count} forageables near NPC at {npcTile}...", LogLevel.Info);

        var spawned = new List<Vector2>();
        string[] forageIds = { "(O)16", "(O)18", "(O)20" }; // Wild Horseradish, Daffodil, Leek
        var npcPos = new Vector2(npcTile.X, npcTile.Y);
        for (var i = 0; i < count; i++)
        {
            // Spawn 2-3 tiles away from NPC in various directions
            var offset = i switch
            {
                0 => new Vector2(2, 0),
                1 => new Vector2(0, 2),
                2 => new Vector2(-2, 0),
                _ => new Vector2(2, 2)
            };
            var desiredTile = npcPos + offset;
            // Ensure the tile is walkable before spawning
            var tile = FindWalkableTileNear(loc, desiredTile, npcPos);
            if (loc.objects.ContainsKey(tile))
            {
                continue;
            }

            var id = forageIds[i % forageIds.Length];
            var forage = ItemRegistry.Create<SObject>(id);
            forage.IsSpawnedObject = true;
            forage.CanBeGrabbed = true;
            loc.objects[tile] = forage;
            _spawnedObjectTiles.Add(tile);
            spawned.Add(tile);
        }

        return spawned;
    }

    /// <summary>Remove objects and terrain features (keeps monsters for multi-phase tests).</summary>
    public static void ClearObjectsAndTerrain(IModHelper helper, IMonitor monitor)
    {
        var loc = Game1.currentLocation;

        foreach (var tile in _spawnedObjectTiles.ToList())
        {
            if (loc.objects.ContainsKey(tile))
            {
                _ = loc.objects.Remove(tile);
            }
        }

        _spawnedObjectTiles.Clear();

        foreach (var tile in _spawnedTerrainTiles.ToList())
        {
            if (loc.terrainFeatures.ContainsKey(tile))
            {
                _ = loc.terrainFeatures.Remove(tile);
            }
        }

        _spawnedTerrainTiles.Clear();
    }

    /// <summary>Remove every entity created by this helper.</summary>
    public static void ClearAll(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;

        // Remove spawned monsters
        foreach (var monster in _spawnedMonsters.ToList())
        {
            if (monster?.currentLocation == loc && loc.characters.Contains(monster))
            {
                _ = loc.characters.Remove(monster);
            }
        }

        _spawnedMonsters.Clear();

        ClearObjectsAndTerrain(helper, monitor);
        monitor.Log("Cleared all test entities.", LogLevel.Info);
    }

    // ------------------------------------------------------------------
    // Query helpers used by assertions
    // ------------------------------------------------------------------

    public static IReadOnlyList<Monster> GetSpawnedMonsters() => _spawnedMonsters;
    public static IReadOnlyList<Vector2> GetSpawnedObjectTiles() => _spawnedObjectTiles;
    public static IReadOnlyList<Vector2> GetSpawnedTerrainTiles() => _spawnedTerrainTiles;

    // ------------------------------------------------------------------
    // Inventory flood scene (for overflow testing)
    // ------------------------------------------------------------------

    /// <summary>
    ///     Spawns enough forageables to overflow a 12-slot backpack on walkable tiles.
    ///     Call after filling inventory via FillNpcInventory API.
    /// </summary>
    public static IReadOnlyList<Vector2> SpawnForageOverflowScene(IModHelper helper, IMonitor monitor, int count = 15)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var loc = Game1.currentLocation;
        var playerTile = Game1.player.Tile;
        monitor.Log($"Spawning {count} forageables for overflow test near player...", LogLevel.Info);

        var spawned = new List<Vector2>();
        string[] forageIds =
            { "(O)16", "(O)18", "(O)20", "(O)22", "(O)399" }; // Horseradish, Daffodil, Leek, Dandelion, Salmonberry
        for (var i = 0; i < count && i < 30; i++)
        {
            var col = i % 5 - 2;
            var row = i / 5 + 1;
            var desiredTile = playerTile + new Vector2(col, row);
            // Ensure the tile is walkable before spawning
            var tile = FindWalkableTileNear(loc, desiredTile, playerTile);
            if (loc.objects.ContainsKey(tile))
            {
                continue;
            }

            var forage = ItemRegistry.Create<SObject>(forageIds[i % forageIds.Length]);
            forage.IsSpawnedObject = true;
            forage.CanBeGrabbed = true;
            loc.objects[tile] = forage;
            _spawnedObjectTiles.Add(tile);
            spawned.Add(tile);
        }

        return spawned;
    }

    // ------------------------------------------------------------------
    // Player inventory flood (for gift-when-full testing)
    // ------------------------------------------------------------------

    /// <summary>
    ///     Fill the player's inventory to the maximum slot count using cheap filler items.
    ///     Returns the number of slots filled.
    /// </summary>
    public static int FillPlayerInventoryToMax(IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var maxSlots = Game1.player.MaxItems;
        var filled = 0;
        for (var i = 0; i < maxSlots; i++)
        {
            if (Game1.player.Items[i] == null)
            {
                Game1.player.Items[i] = ItemRegistry.Create<SObject>("(O)390"); // Stone — cheap, stackable
                filled++;
            }
        }

        monitor.Log($"Filled {filled}/{maxSlots} player inventory slots to max capacity.", LogLevel.Info);
        return filled;
    }

    /// <summary>Clear filler items from player inventory. Only removes Stone (ID 390).</summary>
    public static void ClearPlayerFillerItems(IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var cleared = 0;
        for (var i = 0; i < Game1.player.MaxItems; i++)
        {
            if (Game1.player.Items[i] is SObject obj && obj.ItemId == "390")
            {
                Game1.player.Items[i] = null;
                cleared++;
            }
        }

        monitor.Log($"Cleared {cleared} filler items from player inventory.", LogLevel.Info);
    }

    // ------------------------------------------------------------------
    // Walkable tile helper
    // ------------------------------------------------------------------

    /// <summary>
    ///     Finds a walkable tile near the desired position.
    ///     Searches in an expanding spiral (radius 0→maxRadius) and returns the first passable tile.
    ///     If the desired tile itself is walkable, returns it immediately.
    ///     Prefers tiles closer to the anchor (e.g., player or NPC position).
    /// </summary>
    public static Vector2 FindWalkableTileNear(GameLocation location, Vector2 desiredTile, Vector2 anchorTile,
        int maxRadius = 5)
    {
        if (location == null)
        {
            return desiredTile;
        }

        // If desired tile is already walkable, use it
        if (IsTileWalkable(location, desiredTile))
        {
            return desiredTile;
        }

        // Search in expanding spiral for a walkable tile
        Vector2? bestTile = null;
        var bestDist = float.MaxValue;

        for (var radius = 1; radius <= maxRadius; radius++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    // Only check the perimeter of the current radius
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    var candidate = new Vector2(desiredTile.X + dx, desiredTile.Y + dy);

                    // Skip out-of-bounds tiles
                    if (candidate.X < 0 || candidate.Y < 0)
                    {
                        continue;
                    }

                    if (location.Map?.Layers.Count > 0)
                    {
                        var layer = location.Map.Layers[0];
                        if (candidate.X >= layer.LayerWidth || candidate.Y >= layer.LayerHeight)
                        {
                            continue;
                        }
                    }

                    if (IsTileWalkable(location, candidate))
                    {
                        var dist = Vector2.Distance(candidate, anchorTile);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestTile = candidate;
                        }
                    }
                }
            }

            // If we found a walkable tile at this radius, return it
            if (bestTile.HasValue)
            {
                return bestTile.Value;
            }
        }

        // Fallback: return the desired tile even if not walkable
        // (let the caller handle the error)
        return desiredTile;
    }

    /// <summary>Checks if a tile is walkable (not blocked by buildings, water, etc.).</summary>
    public static bool IsTileWalkable(GameLocation location, Vector2 tile)
    {
        if (location == null)
        {
            return false;
        }

        // Check map bounds
        if (tile.X < 0 || tile.Y < 0)
        {
            return false;
        }

        if (location.Map?.Layers.Count > 0)
        {
            var layer = location.Map.Layers[0];
            if (tile.X >= layer.LayerWidth || tile.Y >= layer.LayerHeight)
            {
                return false;
            }
        }

        // Check if tile is passable (no collision)
        var bounds = new Rectangle(
            (int)(tile.X * 64f),
            (int)(tile.Y * 64f),
            64, 64);
        var isPassable = !location.isCollidingPosition(bounds, Game1.viewport, false, 0, false, null);

        return isPassable;
    }
}