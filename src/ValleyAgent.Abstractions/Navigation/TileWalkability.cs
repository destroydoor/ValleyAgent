using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleyAgent.Navigation
{
    /// <summary>
    /// SDV 瓦片可行走性与可破坏性检查工具类。
    /// 核心修复：使用完整地图视口替代 Game1.viewport，避免跨地图碰撞误判。
    /// </summary>
    public static class TileWalkability
    {
        /// <summary>
        /// 检查指定地图坐标是否可行走。
        /// 使用完整地图视口而非 Game1.viewport，修复 NPC 在非玩家所在地图时
        /// 因视口偏移导致 isCollidingPosition 误判为不可行走的 Bug。
        /// </summary>
        public static bool IsTileWalkable(GameLocation location, int x, int y, Character? ignore = null)
        {
            if (location == null)
                return false;

            var map = location.Map;
            if (map?.Layers == null || map.Layers.Count == 0)
                return false;

            int mapWidth = map.Layers[0].LayerWidth;
            int mapHeight = map.Layers[0].LayerHeight;
            if (mapWidth == 0 || mapHeight == 0)
                return false;

            // 越界瓦片永远不可走（pathfinding:true 下 isCollidingPosition 不做边界检查）
            if (x < 0 || y < 0 || x >= mapWidth || y >= mapHeight)
                return false;

            // 与原版 PathFindController.isPositionPassable 完全一致的判定：
            // - 62x62 内缩碰撞箱（避免贴边误判）
            // - pathfinding:true（忽略角色/怪物碰撞——角色会移动，把角色算作
            //   障碍物会把他人站着的可走瓦片误判为不可走，造成传送“纠错”误判）
            // - character 传被检查的 NPC 自己（ignore），其碰撞箱不计入
            var character = ignore ?? Game1.player;
            var bounds = new Rectangle(x * 64 + 1, y * 64 + 1, 62, 62);

            return !location.isCollidingPosition(bounds, Game1.viewport, character is Farmer, 0, glider: false, character, pathfinding: true);
        }

        /// <summary>
        /// 检查指定地图坐标是否存在可破坏物体（石头、矿石、杂草）。
        /// </summary>
        public static bool IsDestructible(GameLocation location, int x, int y)
        {
            if (location == null)
                return false;

            var tile = new Vector2(x, y);
            if (!location.objects.TryGetValue(tile, out var obj) || obj == null)
                return false;

            // 石头和矿石（使用 SDV 1.6 API）
            if (obj.IsBreakableStone())
                return true;

            // 杂草
            if (obj.Name?.Equals("Weeds", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return false;
        }

        /// <summary>
        /// 在 NPC 周围螺旋搜索最近的可破坏障碍物。
        /// 按半径从近到远搜索，同半径内取 Manhattan 距离最近者。
        /// </summary>
        public static Point? FindNearestDestructibleObstacle(GameLocation location, Point npcTile, int maxRadius = 5)
        {
            if (location == null)
                return null;

            Point? best = null;
            int bestDist = int.MaxValue;

            for (int r = 1; r <= maxRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        // 只检查当前半径的边界，跳过内层
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                            continue;

                        int tx = npcTile.X + dx;
                        int ty = npcTile.Y + dy;

                        if (IsDestructible(location, tx, ty))
                        {
                            int dist = Math.Abs(dx) + Math.Abs(dy);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                best = new Point(tx, ty);
                            }
                        }
                    }
                }

                // 当前半径内找到就返回最近的
                if (best.HasValue)
                    return best;
            }

            return null;
        }
    }
}
