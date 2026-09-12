#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;

namespace ValleyAgent.TestMod;

/// <summary>
///     warp 落点可达性守卫：验证玩家传送落点是否"从地图入口 BFS 可达"，
///     而非仅单格可走（TileWalkability.IsTileWalkable）。
///     背景教训（F_FollowCrossMap 注释，2026-08-02 实测）：农场农田中心 (30,30) 单格可走，
///     但周围 NPCBarrier 树篱 + 开局 debris 形成硬隔离（bfsReachable=False），
///     玩家能 warp 进去而 NPC 物理上走不到，跟随/挖矿/战斗测试因此失去意义。
///     所有测试 warp 落点必须经 ResolveLandingTile 验证；不可达落点自动修正到最近可达瓦片。
/// </summary>
public static class WarpTargetGuard
{
    /// <summary>BFS 访问瓦片数上限，防止异常地图导致全图扫描拖慢测试。</summary>
    private const int MaxBfsVisited = 40000;

    /// <summary>落点自动修正的最大搜索半径（瓦片）。</summary>
    private const int MaxCorrectionRadius = 16;

    /// <summary>
    ///     从地图所有入口瓦片（其他地图指向本图的 warp 到达点）做 4 向 BFS，
    ///     返回入口可达瓦片集合。找不到入口时返回 null（调用方应降级为仅单格检查）。
    /// </summary>
    public static HashSet<Point>? ComputeEntranceReachableTiles(GameLocation loc)
    {
        if (loc == null)
        {
            return null;
        }

        var seeds = GetEntranceTiles(loc);
        if (seeds.Count == 0)
        {
            return null;
        }

        var reachable = new HashSet<Point>();
        var queue = new Queue<Point>();
        foreach (var seed in seeds)
        {
            // 入口瓦片本身可能踩在 warp 触发格上（可走），也可能紧邻障碍物；
            // 把入口及其 4 邻中可走的瓦片都作为 BFS 种子，避免种子被单格判定卡死。
            foreach (var candidate in Neighbors4(seed))
            {
                if (reachable.Contains(candidate))
                {
                    continue;
                }

                if (TileWalkability.IsTileWalkable(loc, candidate.X, candidate.Y))
                {
                    _ = reachable.Add(candidate);
                    queue.Enqueue(candidate);
                }
            }
        }

        while (queue.Count > 0 && reachable.Count < MaxBfsVisited)
        {
            var cur = queue.Dequeue();
            foreach (var next in Neighbors4(cur))
            {
                if (reachable.Contains(next))
                {
                    continue;
                }

                if (!TileWalkability.IsTileWalkable(loc, next.X, next.Y))
                {
                    continue;
                }

                _ = reachable.Add(next);
                queue.Enqueue(next);
            }
        }

        return reachable;
    }

    /// <summary>
    ///     目标瓦片是否从地图入口 BFS 可达（NPC 物理上能走到）。
    ///     reachable 可由 <see cref="ComputeEntranceReachableTiles" /> 预算后复用。
    /// </summary>
    public static bool IsReachableFromEntrance(GameLocation loc, Point tile, HashSet<Point>? reachable)
    {
        if (reachable == null)
        {
            // 无法确定入口（非常规地图）：降级为单格可走检查
            return TileWalkability.IsTileWalkable(loc, tile.X, tile.Y);
        }

        return reachable.Contains(tile);
    }

    /// <summary>
    ///     该瓦片是否是本图的 warp 触发格（玩家落上去会被自动弹回源地图，
    ///     2026-08-02 实测 BusStop (11,23)、Town (0,54) 弹回）。落点必须避开。
    /// </summary>
    public static bool IsWarpTriggerTile(GameLocation loc, Point tile)
    {
        if (loc.warps == null)
        {
            return false;
        }

        foreach (var warp in loc.warps)
        {
            if (warp.X == tile.X && warp.Y == tile.Y)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     在 desired 周围螺旋搜索最近的"可走 + 入口可达 + 非 warp 触发格"瓦片。
    ///     找不到返回 null。
    /// </summary>
    public static Point? FindNearestValidLandingTile(GameLocation loc, Point desired,
        HashSet<Point>? reachable, int maxRadius = MaxCorrectionRadius)
    {
        for (var radius = 0; radius <= maxRadius; radius++)
        {
            Point? best = null;
            var bestDist = int.MaxValue;
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    var candidate = new Point(desired.X + dx, desired.Y + dy);
                    if (!TileWalkability.IsTileWalkable(loc, candidate.X, candidate.Y))
                    {
                        continue;
                    }

                    if (IsWarpTriggerTile(loc, candidate))
                    {
                        continue;
                    }

                    if (!IsReachableFromEntrance(loc, candidate, reachable))
                    {
                        continue;
                    }

                    var dist = Math.Abs(dx) + Math.Abs(dy);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = candidate;
                    }
                }
            }

            if (best.HasValue)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>
    ///     解析 warp 落点：验证（可走 + 入口 BFS 可达 + 非 warp 触发格），
    ///     不可达时自动修正到最近可达瓦片并记录日志；无法修正时返回原坐标
    ///     （测试断言会暴露问题）并记 Warn。
    /// </summary>
    public static Point ResolveLandingTile(IMonitor monitor, string mapName, int x, int y, string context)
    {
        var desired = new Point(x, y);
        var loc = Game1.getLocationFromName(mapName);
        if (loc == null)
        {
            monitor?.Log($"[WarpGuard] {context}: location '{mapName}' not found, using raw ({x},{y})",
                LogLevel.Warn);
            return desired;
        }

        var reachable = ComputeEntranceReachableTiles(loc);
        if (reachable == null)
        {
            monitor?.Log($"[WarpGuard] {context}: no entrance warps into '{mapName}', " +
                         "falling back to walkable-only validation", LogLevel.Debug);
        }

        var walkable = TileWalkability.IsTileWalkable(loc, x, y);
        var onWarpTile = IsWarpTriggerTile(loc, desired);
        var entranceReachable = IsReachableFromEntrance(loc, desired, reachable);

        if (walkable && !onWarpTile && entranceReachable)
        {
            return desired;
        }

        var corrected = FindNearestValidLandingTile(loc, desired, reachable);
        if (corrected.HasValue)
        {
            monitor?.Log($"[WarpGuard] {context}: landing ({x},{y}) on '{mapName}' rejected " +
                         $"(walkable={walkable}, warpTile={onWarpTile}, bfsReachable={entranceReachable}) " +
                         $"→ corrected to ({corrected.Value.X},{corrected.Value.Y})", LogLevel.Info);
            return corrected.Value;
        }

        monitor?.Log($"[WarpGuard] {context}: landing ({x},{y}) on '{mapName}' rejected " +
                     $"(walkable={walkable}, warpTile={onWarpTile}, bfsReachable={entranceReachable}) " +
                     "and no reachable tile found nearby — using raw coordinates, assertions may fail",
            LogLevel.Warn);
        return desired;
    }

    /// <summary>
    ///     收集目标地图的入口瓦片：所有已加载地图中 TargetName 指向本图的 warp 的到达坐标
    ///     （玩家/旅行 NPC 进图的位置）。与 F_FollowCrossMap.GetArrivalTile 同源思路，
    ///     这里聚合全部入口而非单一来源。
    /// </summary>
    public static List<Point> GetEntranceTiles(GameLocation loc)
    {
        var result = new HashSet<Point>();
        foreach (var other in Game1.locations)
        {
            if (other?.warps == null)
            {
                continue;
            }

            foreach (var warp in other.warps)
            {
                if (string.Equals(warp.TargetName, loc.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(warp.TargetName, loc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    _ = result.Add(new Point(warp.TargetX, warp.TargetY));
                }
            }
        }

        return new List<Point>(result);
    }

    private static IEnumerable<Point> Neighbors4(Point p)
    {
        yield return p;
        yield return new Point(p.X + 1, p.Y);
        yield return new Point(p.X - 1, p.Y);
        yield return new Point(p.X, p.Y + 1);
        yield return new Point(p.X, p.Y - 1);
    }
}