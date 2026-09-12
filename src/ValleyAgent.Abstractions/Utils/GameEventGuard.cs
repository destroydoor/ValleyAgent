using System;
using StardewValley;

namespace ValleyAgent.Utils
{
    /// <summary>
    /// D2 守卫表达式单一来源。判断当前是否处于事件/节日中，
    /// 期间禁止 NPC 跨图旅行的发起与重定向。
    /// 表达式复用自 NPCDialoguePatch.cs:27-35 的既有守卫。
    /// 放在 Abstractions 项目以同时被主 mod 与 Abstractions 引用
    /// （Abstractions 不能反向引用主 mod，见 ValleyAgent.Abstractions.csproj 注释）。
    /// </summary>
    public static class GameEventGuard
    {
        /// <summary>可注入谓词，TestMod 可替换以模拟事件状态。null 时用真实游戏状态。</summary>
        public static Func<bool>? EventUpOverride { get; set; }
        public static Func<bool>? CurrentEventOverride { get; set; }
        public static Func<bool>? FestivalOverride { get; set; }

        /// <summary>
        /// 当前是否处于事件/节日中。任一条件为 true 即返回 true。
        /// TestMod 可通过设置 Override 谓词模拟事件状态（无需真的进入节日）。
        /// </summary>
        public static bool IsEventOrFestivalActive
        {
            get
            {
                bool eventUp = EventUpOverride?.Invoke() ?? Game1.eventUp;
                bool currentEvent = CurrentEventOverride?.Invoke() ?? (Game1.CurrentEvent != null);
                bool festival = FestivalOverride?.Invoke() ?? Game1.isFestival();
                return eventUp || currentEvent || festival;
            }
        }

        /// <summary>测试后必须调用，清除所有 override。</summary>
        public static void ResetOverrides()
        {
            EventUpOverride = null;
            CurrentEventOverride = null;
            FestivalOverride = null;
        }
    }
}
