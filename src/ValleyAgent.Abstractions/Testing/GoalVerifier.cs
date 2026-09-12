
namespace ValleyAgent.Testing
{
    /// <summary>
    /// 目标完成验证结果。
    /// </summary>
    public enum CompletionVerdict
    {
        /// <summary>目标已达成（环境变化符合预期）。</summary>
        Success,

        /// <summary>目标未达成（环境无有意义的变化）。</summary>
        Failure,

        /// <summary>无法判定（基线不清、无变化可能等）。</summary>
        Unclear,
    }

    /// <summary>
    /// 纯验证逻辑，不依赖任何游戏类型。
    /// 从 GoalCompletionAuditor 中提取，供游戏内测试和 xUnit 单元测试共享。
    /// </summary>
    public static class GoalVerifier
    {
        /// <summary>
        /// 验证 FARM 目标：当前作物数 < 起始作物数 → Success。
        /// </summary>
        public static (CompletionVerdict Verdict, string? Reason) VerifyFarm(int currentCrops, int startCrops)
        {
            if (currentCrops < startCrops)
            {
                return (CompletionVerdict.Success, null);
            }

            if (startCrops == 0)
            {
                return (CompletionVerdict.Unclear, "起始无作物 — 基线不清");
            }

            return (CompletionVerdict.Failure, $"FARM: 期望作物数 < {startCrops}，实际 {currentCrops}");
        }

        /// <summary>
        /// 验证 FIGHT 目标：当前怪物数 < 起始怪物数 → Success。
        /// </summary>
        public static (CompletionVerdict Verdict, string? Reason) VerifyFight(int currentMonsters, int startMonsters)
        {
            if (currentMonsters < startMonsters)
            {
                return (CompletionVerdict.Success, null);
            }

            if (startMonsters == 0)
            {
                return (CompletionVerdict.Unclear, "起始无怪物 — 基线不清");
            }

            return (CompletionVerdict.Failure, $"FIGHT: 期望怪物数 < {startMonsters}，实际 {currentMonsters}");
        }

        /// <summary>
        /// 验证 MINE 目标：当前石头数 < 起始石头数 → Success。
        /// </summary>
        public static (CompletionVerdict Verdict, string? Reason) VerifyMine(int currentRocks, int startRocks)
        {
            if (currentRocks < startRocks)
            {
                return (CompletionVerdict.Success, null);
            }

            if (startRocks == 0)
            {
                return (CompletionVerdict.Unclear, "起始无石头 — 基线不清");
            }

            return (CompletionVerdict.Failure, $"MINE: 期望石头数 < {startRocks}，实际 {currentRocks}");
        }

        /// <summary>
        /// 验证 FORAGE 目标：当前采集物数 < 起始采集物数 → Success。
        /// </summary>
        public static (CompletionVerdict Verdict, string? Reason) VerifyForage(int currentForageables, int startForageables)
        {
            if (currentForageables < startForageables)
            {
                return (CompletionVerdict.Success, null);
            }

            if (startForageables == 0)
            {
                return (CompletionVerdict.Unclear, "起始无采集物 — 基线不清");
            }

            return (CompletionVerdict.Failure, $"FORAGE: 期望采集物数 < {startForageables}，实际 {currentForageables}");
        }

        /// <summary>
        /// 验证 FOLLOW 目标：距离 < maxDistance → Success。
        /// </summary>
        public static (CompletionVerdict Verdict, string? Reason) VerifyFollow(float distance, float maxDistance = 5f)
        {
            if (distance < maxDistance)
            {
                return (CompletionVerdict.Success, null);
            }

            return (CompletionVerdict.Failure, $"FOLLOW: 距离 {distance:F1} ≥ {maxDistance:F1} 格");
        }

        /// <summary>
        /// 验证 IDLE：总是 Success。
        /// </summary>
        public static (CompletionVerdict Verdict, string? Reason) VerifyIdle() => (CompletionVerdict.Success, null);
    }
}
