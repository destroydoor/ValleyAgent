namespace ValleyAgent.Utils
{
    /// <summary>
    /// Centralized debug flags that can be set via the API and checked by handlers/ModEntry.
    /// Used primarily by the TestMod to control behavior during automated tests.
    /// </summary>
    public static class DebugFlags
    {
        /// <summary>When true, all LLM decision triggers (timer, dialogue-end, task-complete, gift) are suppressed.</summary>
        public static bool SuppressDecisions { get; set; }
        public static bool SuppressPathfindCooldown { get; set; }

        /// <summary>When true, MineHandler treats any location as valid for mining (skips navigation).</summary>
        public static bool ForceMiningLocation { get; set; }

        /// <summary>When true, ForageHandler treats any location as valid for foraging (skips navigation).</summary>
        public static bool ForceForageLocation { get; set; }

        /// <summary>
        /// 游戏加速倍率（仅测试用）。1 = 正常速度，5 = 5倍速。
        /// 影响：游戏时间流逝、决策间隔、移动阈值、NPC速度。
        /// </summary>
        public static int GameSpeedMultiplier { get; set; } = 1;
    }
}
