using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleyAgent.Navigation
{
    /// <summary>
    /// Builds and queries a graph of map connections (warps and doors)
    /// for cross-map pathfinding.
    ///
    /// Fix #7: doors now generate bidirectional connections.
    /// Fix #8: door ToTile is inferred from the target location's warps/doors
    /// that point back to the source, or from the target location's default warp point.
    /// Fix #6: BFS uses predecessor map instead of storing full paths in the queue.
    /// </summary>
    public class LocationGraph
    {
        /// <summary>
        /// Represents a single directional connection between two maps.
        /// </summary>
        public class Connection
        {
            /// <summary>Source map name.</summary>
            public string From { get; set; } = string.Empty;

            /// <summary>Destination map name.</summary>
            public string To { get; set; } = string.Empty;

            /// <summary>Tile in the source map where the connection starts.</summary>
            public Point FromTile { get; set; }

            /// <summary>Tile in the destination map where the traveller appears.</summary>
            public Point ToTile { get; set; }
        }

        /// <summary>
        /// Result of a path query between two maps.
        /// </summary>
        public class PathResult
        {
            /// <summary>The sequence of hops from source to destination.</summary>
            public List<Connection> Hops { get; set; } = new();

            /// <summary>Number of map hops.</summary>
            public int HopCount => Hops.Count;

            /// <summary>True if a path was found.</summary>
            public bool Found => Hops.Count > 0;
        }

        private readonly Dictionary<string, List<Connection>> _graph = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Scans all loaded GameLocations and builds the connection graph.
        /// Should be called once per save load (after locations are initialized).
        /// </summary>
        public void Build()
        {
            _graph.Clear();

            if (Game1.locations == null)
            {
                return;
            }

            // Phase 1: scan all warps (bidirectional)
            foreach (var location in Game1.locations)
            {
                if (location == null)
                {
                    continue;
                }

                var fromName = location.NameOrUniqueName;
                if (string.IsNullOrWhiteSpace(fromName))
                {
                    continue;
                }

                if (!_graph.TryGetValue(fromName, out var value))
                {
                    value = new List<Connection>();
                    _graph.Add(fromName, value);
                }

                // Scan warps — these are naturally bidirectional
                var warps = location.warps;
                if (warps != null)
                {
                    foreach (var warp in warps)
                    {
                        if (warp == null)
                        {
                            continue;
                        }

                        var toName = warp.TargetName;
                        if (string.IsNullOrWhiteSpace(toName))
                        {
                            continue;
                        }

                        // Forward connection
                        var forward = new Connection
                        {
                            From = fromName,
                            To = toName,
                            FromTile = new Point(warp.X, warp.Y),
                            ToTile = new Point(warp.TargetX, warp.TargetY)
                        };
                        value.Add(forward);

                        // Ensure reverse node exists
                        if (!_graph.TryGetValue(toName, out var toValue))
                        {
                            toValue = new List<Connection>();
                            _graph.Add(toName, toValue);
                        }

                        // Reverse connection (for bidirectional traversal)
                        var reverse = new Connection
                        {
                            From = toName,
                            To = fromName,
                            FromTile = new Point(warp.TargetX, warp.TargetY),
                            ToTile = new Point(warp.X, warp.Y)
                        };
                        toValue.Add(reverse);
                    }
                }
            }

            // Phase 2: scan all doors (bidirectional with ToTile inference)
            // Fix #7 & #8: doors generate reverse connections with inferred ToTile
            foreach (var location in Game1.locations)
            {
                if (location == null)
                {
                    continue;
                }

                var fromName = location.NameOrUniqueName;
                if (string.IsNullOrWhiteSpace(fromName))
                {
                    continue;
                }

                var doors = location.doors;
                if (doors == null)
                {
                    continue;
                }

                foreach (var key in doors.Keys)
                {
                    var toName = doors[key];
                    if (string.IsNullOrWhiteSpace(toName))
                    {
                        continue;
                    }

                    // Forward door connection
                    var inferredToTile = InferDoorToTile(fromName, toName);
                    var conn = new Connection
                    {
                        From = fromName,
                        To = toName,
                        FromTile = key,
                        ToTile = inferredToTile
                    };
                    if (!_graph.TryGetValue(fromName, out var fromConns))
                    {
                        fromConns = new List<Connection>();
                        _graph.Add(fromName, fromConns);
                    }

                    fromConns.Add(conn);

                    // Fix #7: reverse door connection
                    var reverseToTile = key; // The door tile in the source map is where NPC appears when coming back
                    var reverseConn = new Connection
                    {
                        From = toName,
                        To = fromName,
                        FromTile = inferredToTile,
                        ToTile = reverseToTile
                    };
                    if (!_graph.TryGetValue(toName, out var toConns))
                    {
                        toConns = new List<Connection>();
                        _graph.Add(toName, toConns);
                    }

                    toConns.Add(reverseConn);
                }
            }
        }

        /// <summary>
        /// Finds the shortest path between two maps using BFS.
        /// Fix #6: uses predecessor map instead of storing full paths in queue.
        /// </summary>
        public PathResult FindPath(string from, string to)
        {
            var result = new PathResult();

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return result;
            }

            if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            if (!_graph.ContainsKey(from) || !_graph.ContainsKey(to))
            {
                return result;
            }

            // BFS with predecessor map — O(V+E) space instead of O(V*D*pathLen)
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
            var predecessor = new Dictionary<string, (string fromLoc, Connection conn)>(StringComparer.OrdinalIgnoreCase);

            var queue = new Queue<string>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (!_graph.TryGetValue(current, out var connections))
                {
                    continue;
                }

                foreach (var conn in connections)
                {
                    if (visited.Contains(conn.To))
                    {
                        continue;
                    }

                    _ = visited.Add(conn.To);
                    predecessor[conn.To] = (current, conn);

                    if (conn.To.Equals(to, StringComparison.OrdinalIgnoreCase))
                    {
                        // Reconstruct path by backtracking through predecessors
                        var path = new List<Connection>();
                        var step = to;
                        while (predecessor.TryGetValue(step, out var pred))
                        {
                            path.Add(pred.conn);
                            step = pred.fromLoc;
                        }
                        path.Reverse();
                        result.Hops = path;
                        return result;
                    }

                    queue.Enqueue(conn.To);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns all known map names in the graph.
        /// </summary>
        public IReadOnlyCollection<string> KnownLocations => _graph.Keys;

        /// <summary>
        /// Fix #8: Infers the ToTile for a door connection by searching the target
        /// location's warps/doors for a connection that points back to the source.
        /// If no back-connection is found, uses the target location's default entry point.
        /// </summary>
        private static Point InferDoorToTile(string fromLocation, string toLocation)
        {
            var targetLoc = Game1.getLocationFromName(toLocation);
            if (targetLoc == null)
            {
                return Point.Zero;
            }

            // Strategy 1: find a warp in the target that points back to the source
            if (targetLoc.warps != null)
            {
                foreach (var warp in targetLoc.warps)
                {
                    if (warp != null &&
                        string.Equals(warp.TargetName, fromLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        // The warp's (TargetX, TargetY) is where the player appears in the source map.
                        // The warp's (X, Y) is where the warp IS in the target map — that's our ToTile.
                        return new Point(warp.X, warp.Y);
                    }
                }
            }

            // Strategy 2: find a door in the target that points back to the source
            if (targetLoc.doors != null)
            {
                foreach (var key in targetLoc.doors.Keys)
                {
                    if (string.Equals(targetLoc.doors[key], fromLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        // This door tile in the target map is where NPC should appear
                        return key;
                    }
                }
            }

            // Strategy 3: use the target location's first warp as entry point
            if (targetLoc.warps != null && targetLoc.warps.Count > 0)
            {
                var firstWarp = targetLoc.warps[0];
                if (firstWarp != null)
                {
                    return new Point(firstWarp.X, firstWarp.Y);
                }
            }

            // Fallback: Point.Zero — caller must handle (AgentNavigator has fallback logic)
            return Point.Zero;
        }
    }
}
