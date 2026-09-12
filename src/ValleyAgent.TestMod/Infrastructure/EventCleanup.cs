#nullable enable
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod;

/// <summary>
///     共享的剧情/事件清理辅助（B 任务基础设施）。
///     背景：玩家首次进矿洞（Mine）会触发 Marlon 剧情（Game1.eventUp=true）。
///     事件残留有两个已实测的破坏路径：
///     1. SDV warp 完成逻辑（Game1.cs:6239 if(!eventUp)）跳过玩家 Position 设置
///     → 玩家 Tile 卡在旧值，后续测试的 dist/位置断言全部失败（F6_SceneSwitch 根因）。
///     2. GameEventGuard.IsEventOrFestivalActive 恒 true → AgentNavigator 跨图旅行被阻断。
///     清理方式刻意只做空引用赋值（eventUp=false / currentEvent=null），
///     绝不调用 event.onEventFinished() 或 Game1 的事件结束流程——
///     半成品事件的 onEventFinished 清理会抛 NullReference（V3TestRunner.cs:468 注释记录的坑）。
/// </summary>
public static class EventCleanup
{
    /// <summary>当前是否仍有活跃事件（真实状态，不看 Override）。供测试轮询/断言。</summary>
    public static bool IsEventActive
    {
        get => Game1.eventUp || Game1.currentLocation?.currentEvent != null || Game1.CurrentEvent != null;
    }

    /// <summary>
    ///     清掉真实事件状态：Game1.eventUp + 当前地图及所有已加载地图的 currentEvent。
    ///     直接置空字段，不走 SDV 事件结束流程（见类注释的 NRE 坑）。
    ///     返回是否有实际清理动作（供测试断言"事件确实被清过"）。
    /// </summary>
    public static bool ClearActiveEvents(IMonitor? monitor, string context)
    {
        var cleared = false;

        if (Game1.eventUp)
        {
            Game1.eventUp = false;
            cleared = true;
        }

        if (Game1.currentLocation?.currentEvent != null)
        {
            Game1.currentLocation.currentEvent = null;
            cleared = true;
        }

        foreach (var loc in Game1.locations)
        {
            if (loc?.currentEvent != null)
            {
                loc.currentEvent = null;
                cleared = true;
            }
        }

        if (cleared)
        {
            monitor?.Log($"[EventCleanup] {context}: cleared lingering event state", LogLevel.Info);
        }

        return cleared;
    }

    /// <summary>
    ///     强制 GameEventGuard 测试 Override 为"无事件/节日"，保证 AgentNavigator
    ///     跨图旅行不被事件守卫拦截。Teardown 必须调用 <see cref="ResetGuardOverrides" /> 恢复。
    /// </summary>
    public static void SuppressEventGuards()
    {
        GameEventGuard.EventUpOverride = () => false;
        GameEventGuard.CurrentEventOverride = () => false;
        GameEventGuard.FestivalOverride = () => false;
    }

    /// <summary>恢复 GameEventGuard 的真实状态判定。</summary>
    public static void ResetGuardOverrides() => GameEventGuard.ResetOverrides();
}