using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace ValleyAgent.Navigation
{
    /// <summary>
    /// Pure, testable pathfinding utilities with no Stardew Valley dependencies.
    /// All walkability checks are passed as <c>Func&lt;int,int,bool&gt;</c> delegates,
    /// enabling unit testing with mock grid data.
    /// </summary>
    public static class PathfindingUtility
    {
        // 8方向：4 cardinal + 4 diagonal
        private static readonly Point[] CardinalDirs = new[]
        {
            new Point(0, -1), new Point(0, 1), new Point(-1, 0), new Point(1, 0)
        };

        private static readonly Point[] DiagonalDirs = new[]
        {
            new Point(-1, -1), new Point(1, -1), new Point(-1, 1), new Point(1, 1)
        };

        // ─── Walkability ───────────────────────────────────────────────────

        public static bool IsTileWalkable(int x, int y, Func<int, int, bool> isPassable) => isPassable(x, y);

        // ─── Approach Tile (8-direction, nearest-first) ─────────────────────

        /// <summary>
        /// Finds the nearest walkable tile around the target in 8 directions.
        /// Cardinal directions are checked first (distance=1), then diagonals (distance=√2).
        /// Fix #15: added diagonal directions so diagonal approach positions are not skipped.
        /// </summary>
        public static Point GetApproachTile(Point target, Func<int, int, bool> isWalkable)
        {
            // Cardinal first (distance = 1.0)
            foreach (var dir in CardinalDirs)
            {
                var check = new Point(target.X + dir.X, target.Y + dir.Y);
                if (isWalkable(check.X, check.Y))
                {
                    return check;
                }
            }

            // Diagonal second (distance = √2 ≈ 1.41)
            foreach (var dir in DiagonalDirs)
            {
                var check = new Point(target.X + dir.X, target.Y + dir.Y);
                if (isWalkable(check.X, check.Y))
                {
                    return check;
                }
            }

            // 8邻域全失败，扩展搜索范围
            var expanded = FindWalkableTileNear(target, isWalkable, int.MaxValue, int.MaxValue, maxRadius: 5);
            if (expanded.X != target.X || expanded.Y != target.Y)
            {
                return expanded;
            }

            return target;
        }

        // ─── Alternative Tile (deterministic, for retry) ────────────────────

        /// <summary>
        /// Returns a deterministic walkable alternative tile near the target.
        /// Fix #12: uses enhanced hash with more entropy to reduce seed collisions.
        /// Searches in an expanding spiral (radius 1→3) with a deterministic rotation.
        /// </summary>
        public static Point FindAlternativeTile(
            Point target,
            Func<int, int, bool> isWalkable,
            int mapWidth = int.MaxValue,
            int mapHeight = int.MaxValue)
        {
            // Fix #12: better hash — add prime multipliers and mixing to reduce collisions
            var hash = (uint)(target.X * 73856093) ^ (uint)(target.Y * 19349663);
            hash ^= hash >> 16;
            hash *= 0x45d9f3b;
            hash ^= hash >> 16;
            var rotation = (int)(hash % 8);

            for (var radius = 1; radius <= 3; radius++)
            {
                var perimeter = BuildPerimeter(radius);

                var startIdx = rotation % perimeter.Count;
                for (var i = 0; i < perimeter.Count; i++)
                {
                    var (dx, dy) = perimeter[(startIdx + i) % perimeter.Count];
                    var candidate = new Point(target.X + dx, target.Y + dy);

                    if (!IsInBounds(candidate, mapWidth, mapHeight))
                    {
                        continue;
                    }

                    if (isWalkable(candidate.X, candidate.Y))
                    {
                        return candidate;
                    }
                }
            }

            // Last resort: try cardinal neighbors, pick first walkable
            foreach (var dir in CardinalDirs)
            {
                var fallback = new Point(target.X + dir.X, target.Y + dir.Y);
                if (IsInBounds(fallback, mapWidth, mapHeight) && isWalkable(fallback.X, fallback.Y))
                {
                    return fallback;
                }
            }

            return new Point(target.X + 1, target.Y);
        }

        // ─── Walkable Tile Near (spiral search, nearest-first) ─────────────

        /// <summary>
        /// Finds the nearest walkable tile to the anchor using an expanding spiral search.
        /// Fix #14: within each radius, candidates are sorted by Euclidean distance
        /// to guarantee the returned tile is the closest walkable one.
        /// </summary>
        public static Point FindWalkableTileNear(
            Point anchor,
            Func<int, int, bool> isWalkable,
            int mapWidth = int.MaxValue,
            int mapHeight = int.MaxValue,
            int maxRadius = 15)
        {
            if (isWalkable(anchor.X, anchor.Y))
            {
                return anchor;
            }

            for (var radius = 1; radius <= maxRadius; radius++)
            {
                Point? bestTile = null;
                var bestDist = float.MaxValue;

                for (var dx = -radius; dx <= radius; dx++)
                {
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        {
                            continue;
                        }

                        var tx = anchor.X + dx;
                        var ty = anchor.Y + dy;

                        if (!IsInBounds(new Point(tx, ty), mapWidth, mapHeight))
                        {
                            continue;
                        }

                        if (!isWalkable(tx, ty))
                        {
                            continue;
                        }

                        var dist = Vector2.Distance(new Vector2(dx, dy), Vector2.Zero);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestTile = new Point(tx, ty);
                        }
                    }
                }

                if (bestTile.HasValue)
                {
                    return bestTile.Value;
                }
            }

            return anchor;
        }

        // ─── Walkable Tile Closer to Target ────────────────────────────────

        /// <summary>
        /// Finds a walkable tile that is strictly closer to the target than the current tile.
        /// Fix #14: sorts candidates by distance to find the globally closest option.
        /// </summary>
        public static Point? FindWalkableTileCloserToTarget(
            Point current,
            Point target,
            Func<int, int, bool> isWalkable,
            int mapWidth = int.MaxValue,
            int mapHeight = int.MaxValue,
            int maxRadius = 3)
        {
            var currentDist = Vector2.Distance(new Vector2(current.X, current.Y), new Vector2(target.X, target.Y));
            Point? bestTile = null;
            var bestDist = currentDist;

            for (var radius = 1; radius <= maxRadius; radius++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        {
                            continue;
                        }

                        var candidate = new Point(current.X + dx, current.Y + dy);

                        if (!IsInBounds(candidate, mapWidth, mapHeight))
                        {
                            continue;
                        }

                        if (!isWalkable(candidate.X, candidate.Y))
                        {
                            continue;
                        }

                        var dist = Vector2.Distance(new Vector2(candidate.X, candidate.Y), new Vector2(target.X, target.Y));
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestTile = candidate;
                        }
                    }
                }
            }

            return bestTile;
        }

        // ─── Step Direction (with diagonal support) ─────────────────────────

        /// <summary>
        /// Returns a single-tile step direction toward the target.
        /// Fix #13: when both dx and dy are non-zero, returns a diagonal step
        /// instead of only moving along the primary axis. This prevents zigzag
        /// paths in narrow corridors and diagonal obstacle scenarios.
        /// </summary>
        public static Point GetStepToward(Point from, Point to)
        {
            var dx = Math.Sign(to.X - from.X);
            var dy = Math.Sign(to.Y - from.Y);

            // Both axes have delta → diagonal step
            if (dx != 0 && dy != 0)
            {
                return new Point(dx, dy);
            }

            // Only one axis has delta → cardinal step
            return dx != 0 ? new Point(dx, 0) : dy != 0 ? new Point(0, dy) : Point.Zero;
        }

        // ─── Stuck Detection ───────────────────────────────────────────────

        public static bool IsStuck(
            int? stuckStartTick,
            int currentTick,
            float distanceToTarget,
            float distanceThreshold = 2.5f,
            int timeThresholdTicks = 120)
        {
            if (distanceToTarget > distanceThreshold)
            {
                return false;
            }

            if (stuckStartTick == null)
            {
                return false;
            }

            var elapsed = currentTick - stuckStartTick.Value;
            return elapsed >= timeThresholdTicks;
        }

        public static bool ShouldStartStuckTracking(float distanceToTarget, float distanceThreshold = 2.5f) => distanceToTarget <= distanceThreshold;

        // ─── Internal Helpers ──────────────────────────────────────────────

        /// <summary>
        /// Builds the perimeter cells for a given radius (ring, not filled square).
        /// </summary>
        private static List<(int dx, int dy)> BuildPerimeter(int radius)
        {
            var perimeter = new List<(int dx, int dy)>();
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    perimeter.Add((dx, dy));
                }
            }
            return perimeter;
        }

        /// <summary>
        /// Checks if a tile is within map bounds.
        /// </summary>
        private static bool IsInBounds(Point tile, int mapWidth, int mapHeight) => mapWidth >= int.MaxValue || mapHeight >= int.MaxValue || (tile.X >= 0 && tile.X < mapWidth && tile.Y >= 0 && tile.Y < mapHeight);
    }
}
