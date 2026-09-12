#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod;

/// <summary>
///     统一的测试传送入口：所有测试 warp 玩家都必须走这里（A+B 任务的汇合点）。
///     一次调用完成三件事：
///     1. warp 前清事件残留（eventUp 会让 SDV warp 完成逻辑跳过玩家 Position 设置）；
///     2. 落点经 WarpTargetGuard 验证（单格可走 + 从地图入口 BFS 可达 + 非 warp 触发格），
///     不可达自动修正到最近可达瓦片并记录日志；
///     3. warpFarmer 是异步队列，同步强制设置 currentLocation/Position，
///     保证调用结束时玩家 Tile 就是落点，不赌异步 warp 时序（F_FollowCrossMap 同款处理）。
///     注意：本方法不设置 GameEventGuard Override——需要"旅行不被事件守卫拦截"的测试
///     在 Setup 调 EventCleanup.SuppressEventGuards()，Teardown 调 ResetGuardOverrides()。
/// </summary>
public static class SafeWarp
{
    /// <summary>
    ///     传送玩家到指定地图的验证落点。返回实际落点（可能被修正）。
    /// </summary>
    public static Point Farmer(IMonitor monitor, string mapName, int x, int y, string context = "")
    {
        var tag = string.IsNullOrEmpty(context) ? "SafeWarp" : $"SafeWarp({context})";

        // 1. warp 前清事件：残留 eventUp 会让本次 warp 的 Position 设置被游戏跳过
        _ = EventCleanup.ClearActiveEvents(monitor, $"{tag} pre-warp");

        // 2. 落点可达性验证/修正
        var tile = WarpTargetGuard.ResolveLandingTile(monitor, mapName, x, y, tag);

        // 3. 发起 warp
        Game1.warpFarmer(mapName, tile.X, tile.Y, false);

        // 4. 同步强制就位：warpFarmer 只设 locationRequest，残留状态时读玩家 Tile 仍是旧值
        var loc = Game1.getLocationFromName(mapName);
        if (loc != null)
        {
            Game1.player.currentLocation = loc;
            Game1.player.Position = new Vector2(tile.X, tile.Y) * 64f;
        }

        // 5. warp 后再清一次：进入剧情地图（如首进 Mine 触发 Marlon 剧情）可能同 tick 起事件
        _ = EventCleanup.ClearActiveEvents(monitor, $"{tag} post-warp");

        return tile;
    }
}