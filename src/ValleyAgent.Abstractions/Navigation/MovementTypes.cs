using System;
using Microsoft.Xna.Framework;

namespace ValleyAgent.Navigation
{
    // ═══════════════════════════════════════════════════════════════════════
    // MovementMode
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Controls the cooldown applied when <see cref="IMovementService"/> creates
    /// a <see cref="StardewValley.Pathfinding.PathFindController"/> for an NPC.
    ///
    /// Each mode enforces a different minimum delay between successive
    /// <c>PathFindController</c> creations to prevent destroy-create storms
    /// when multiple components compete for <c>npc.controller</c>.
    ///
    /// Concrete tick values are defined in <see cref="MovementConstants"/>.
    /// </summary>
    public enum MovementMode
    {
        /// <summary>
        /// Short 5-tick cooldown (<see cref="MovementConstants.ShortRangeCooldownTicks"/>).
        /// Used by handler Update loops for responsive target-switching:
        /// farming, mining, foraging, fighting, and talking.
        ///
        /// At 60 fps, a 5-tick cooldown is ~0.08 seconds — fast enough to feel
        /// immediate to the player while preventing per-tick recreation spam
        /// when the game engine destroys an invalid path between frames.
        /// </summary>
        ShortRange = 0,

        /// <summary>
        /// Longer 30-tick cooldown (<see cref="MovementConstants.LongRangeCooldownTicks"/>).
        /// Used for FOLLOW state where the target (player) moves continuously
        /// and the game engine's native schedule system is more aggressive about
        /// nullifying <c>npc.controller</c>.
        ///
        /// At 60 fps, a 30-tick cooldown is 0.5 seconds.
        /// </summary>
        LongRange = 1,

        /// <summary>
        /// No cooldown (<see cref="MovementConstants.ImmediateCooldownTicks"/>).
        /// Used only for emergency situations where any delay in movement
        /// is unacceptable:
        /// <list type="bullet">
        ///   <item>Monster appears within detection range — immediate FIGHT response.</item>
        ///   <item>NPC health drops below flee threshold — immediate retreat.</item>
        ///   <item>Emergency state override from <c>CheckEmergencyState</c>.</item>
        /// </list>
        /// </summary>
        Immediate = 2,
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MoveResult
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of a movement request issued to
    /// <see cref="IMovementService.MoveTo(StardewValley.NPC, Microsoft.Xna.Framework.Vector2, MovementMode, int)"/>
    /// or <see cref="IMovementService.Follow"/>.
    ///
    /// Callers <b>must</b> inspect this result and branch accordingly:
    /// retry on <see cref="BlockedByCooldown"/>, wait on <see cref="AlreadyMoving"/>,
    /// exit the current state on <see cref="NoPathFound"/>.
    /// </summary>
    public enum MoveResult
    {
        /// <summary>
        /// A <see cref="StardewValley.Pathfinding.PathFindController"/> was created
        /// and the NPC will begin navigating on the next game tick.
        /// </summary>
        Success = 0,

        /// <summary>
        /// The per-mode creation cooldown has not elapsed since the last
        /// <c>PathFindController</c> was created for this NPC.
        /// Callers should retry on a subsequent tick.
        /// </summary>
        BlockedByCooldown = 1,

        /// <summary>
        /// The target tile is unwalkable or unreachable from the NPC's current
        /// position. No path exists on the current map.
        /// Handlers should treat this as "target exhausted" and exit their state.
        /// </summary>
        NoPathFound = 2,

        /// <summary>
        /// The NPC already has an active <c>PathFindController</c> navigating
        /// toward a different target. Callers should either wait or call
        /// <see cref="IMovementService.Stop(StardewValley.NPC, string)"/> to cancel
        /// the existing movement before issuing a new request.
        /// </summary>
        AlreadyMoving = 3,

        /// <summary>
        /// The NPC is frozen (via an explicit
        /// <see cref="IMovementService.Freeze(StardewValley.NPC)"/> call).
        /// Frozen NPCs reject all movement commands until
        /// <see cref="IMovementService.Unfreeze(StardewValley.NPC)"/> is called.
        /// </summary>
        Frozen = 4,

        /// <summary>
        /// The NPC reference is null, has been removed from the game world,
        /// or its current location is null. No movement can be initiated.
        /// </summary>
        InvalidNpc = 5,

        /// <summary>
        /// The target tile is the same as (or within 0.5 tiles of) the NPC's current tile.
        /// SDV's PathFindController considers a zero-length path invalid and destroys itself
        /// immediately, so movement is skipped to avoid controller creation storms.
        /// </summary>
        AlreadyAtTarget = 6,
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MovementState
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read-only snapshot of an NPC's current movement status.
    ///
    /// Returned by <see cref="IMovementService.GetMovementState(string)"/>
    /// so callers can inspect movement state without accessing the
    /// service's internal tracking dictionaries.
    ///
    /// This is a value type (<see langword="struct"/>) to avoid
    /// garbage-collection pressure on the per-tick hot path.
    /// </summary>
    public readonly struct MovementState
    {
        /// <summary>
        /// Whether the NPC currently has an active
        /// <see cref="StardewValley.Pathfinding.PathFindController"/> and is
        /// navigating toward <see cref="TargetTile"/>.
        /// </summary>
        public bool IsMoving { get; init; }

        /// <summary>
        /// The tile the NPC is currently navigating toward, in tile coordinates.
        /// Returns <see cref="Vector2.Zero"/> when <see cref="IsMoving"/> is <c>false</c>.
        /// </summary>
        public Vector2 TargetTile { get; init; }

        /// <summary>
        /// The <see cref="MovementMode"/> that was used when the current
        /// <c>PathFindController</c> was created.
        /// Returns <see cref="MovementMode.ShortRange"/> when <see cref="IsMoving"/> is <c>false</c>.
        /// </summary>
        public MovementMode CurrentMode { get; init; }

        /// <summary>
        /// Whether the NPC is frozen. Frozen NPCs have no controller,
        /// zero speed, and reject all <c>MoveTo</c>/<c>Follow</c> requests
        /// with <see cref="MoveResult.Frozen"/>.
        /// </summary>
        public bool IsFrozen { get; init; }

        /// <summary>
        /// The game tick when the current movement was initiated
        /// (i.e., when the last <c>PathFindController</c> was created).
        /// Returns 0 when <see cref="IsMoving"/> is <c>false</c>.
        ///
        /// Callers can use this for stuck detection:
        /// <c>if (Game1.ticks - state.TickStarted > MovementConstants.StuckTimeThresholdTicks)</c>
        /// </summary>
        public int TickStarted { get; init; }

        /// <summary>
        /// When stuck detection began for the current movement target,
        /// or <c>null</c> if the NPC is not stuck.
        /// Used internally by <see cref="IMovementService.HandleStuck(StardewValley.NPC, Microsoft.Xna.Framework.Vector2, int)"/>.
        /// </summary>
        public int? StuckStartTick { get; init; }

        /// <summary>
        /// Returns a compact debug-friendly representation of this movement state.
        /// </summary>
        public override string ToString()
            => IsFrozen
                ? "Frozen"
                : IsMoving
                    ? $"Moving → ({TargetTile.X:F0}, {TargetTile.Y:F0}) [{CurrentMode}] @ t={TickStarted}"
                    : "Idle";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MovementConstants
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Public constants for <see cref="IMovementService"/> timing constraints.
    ///
    /// Exposed so callers can understand cooldown windows, stuck thresholds,
    /// and default movement parameters without hardcoding magic numbers.
    /// </summary>
    public static class MovementConstants
    {
        /// <summary>
        /// Cooldown ticks for <see cref="MovementMode.ShortRange"/>.
        /// Handlers: farming, mining, foraging, fighting, talking.
        /// At 60 fps: 5 ticks = ~0.08 seconds.
        /// </summary>
        public const int ShortRangeCooldownTicks = 5;

        /// <summary>
        /// Cooldown ticks for <see cref="MovementMode.LongRange"/>.
        /// FOLLOW state and cross-map navigation.
        /// At 60 fps: 30 ticks = 0.5 seconds.
        /// </summary>
        public const int LongRangeCooldownTicks = 30;

        /// <summary>
        /// Cooldown ticks for <see cref="MovementMode.Immediate"/>.
        /// Emergency movement — no delay.
        /// </summary>
        public const int ImmediateCooldownTicks = 0;

        /// <summary>
        /// Maximum tile distance for stuck detection to activate.
        /// If the NPC is within this distance of the target but hasn't
        /// moved for <see cref="StuckTimeThresholdTicks"/>, the controller
        /// is cleared so pathfinding can retry.
        ///
        /// Matches the existing pattern in all handlers:
        /// <c>if (dist &lt;= 2.5f) { /* stuck detection */ }</c>
        /// </summary>
        public const float StuckDistanceThreshold = 2.5f;

        /// <summary>
        /// Number of ticks the NPC must be stuck (within
        /// <see cref="StuckDistanceThreshold"/> of target but not moving)
        /// before the controller is cleared for a retry.
        ///
        /// At 60 fps: 120 ticks = 2 seconds.
        /// </summary>
        public const int StuckTimeThresholdTicks = 120;

        /// <summary>
        /// NPC movement speed during pathfinding.
        /// Standard NPC speed is 2; increasing may cause visual glitches.
        /// </summary>
        public const int DefaultPathfindSpeed = 2;

        /// <summary>
        /// NPC movement speed when frozen.
        /// Must be 0 to prevent any drift.
        /// </summary>
        public const int FrozenSpeed = 0;

        // ─── 加速感知的有效值 ──────────────────────────────────────────
        // 当 DebugFlags.GameSpeedMultiplier > 1 时，tick 阈值按比例缩小，
        // 使 NPC 行为在加速模式下保持相同的游戏时间节奏。

        /// <summary>加速后的 ShortRange 冷却 tick 数（下限 1）。</summary>
        public static int EffectiveShortRangeCooldownTicks
            => Math.Max(1, ShortRangeCooldownTicks / Utils.DebugFlags.GameSpeedMultiplier);

        /// <summary>加速后的 LongRange 冷却 tick 数（下限 1）。</summary>
        public static int EffectiveLongRangeCooldownTicks
            => Math.Max(1, LongRangeCooldownTicks / Utils.DebugFlags.GameSpeedMultiplier);

        /// <summary>加速后的卡住检测阈值 tick 数（下限 10）。</summary>
        public static int EffectiveStuckTimeThresholdTicks
            => Math.Max(10, StuckTimeThresholdTicks / Utils.DebugFlags.GameSpeedMultiplier);

        /// <summary>加速后的寻路速度（乘以倍率，上限 20）。</summary>
        public static int EffectivePathfindSpeed
            => Math.Min(20, DefaultPathfindSpeed * Utils.DebugFlags.GameSpeedMultiplier);

        // ─── E2-1 动态速度：距离分段 ──────────────────────────────────────
        // NPC 距目标越远移动越快（默认 >8 格 2×，3–8 格 1.5×，<3 格 1×），
        // 走行动画帧间隔随倍率等比缩短，保证"无滑步感"。数值可在 ModConfig 覆盖。

        /// <summary>远距分段阈值（格）。距离超过该值 → 远距，速度 ×DynamicSpeedFarMultiplier。</summary>
        public const float DynamicSpeedFarThreshold = 8f;

        /// <summary>中距分段阈值（格）。距离小于该值 → 近距，速度 ×DynamicSpeedNearMultiplier。</summary>
        public const float DynamicSpeedMidThreshold = 3f;

        /// <summary>近距（&lt; MidThreshold）速度倍率。</summary>
        public const float DynamicSpeedNearMultiplier = 1.0f;

        /// <summary>中距（[MidThreshold, FarThreshold]）速度倍率。</summary>
        public const float DynamicSpeedMidMultiplier = 1.5f;

        /// <summary>远距（&gt; FarThreshold）速度倍率。</summary>
        public const float DynamicSpeedFarMultiplier = 2.0f;

        /// <summary>NPC 走行动画的基础帧间隔（毫秒/帧）。SDV AnimatedSprite.interval 默认 100。</summary>
        public const float BaseWalkAnimationIntervalMs = 100f;

        /// <summary>走行动画帧间隔下限（毫秒），防止远距高倍率下帧率过激导致闪帧。</summary>
        public const float MinWalkAnimationIntervalMs = 40f;

        // 运行时配置（默认即上方常量；由 ServiceInitializer 启动时从 ModConfig 覆盖）。
        private static bool _dynamicSpeedEnabled = true;
        private static float _dynamicFarThreshold = DynamicSpeedFarThreshold;
        private static float _dynamicMidThreshold = DynamicSpeedMidThreshold;
        private static float _dynamicNearMultiplier = DynamicSpeedNearMultiplier;
        private static float _dynamicMidMultiplier = DynamicSpeedMidMultiplier;
        private static float _dynamicFarMultiplier = DynamicSpeedFarMultiplier;

        /// <summary>
        /// 从 ModConfig 应用动态速度配置（ServiceInitializer 启动时调用一次）。
        /// 阈值/倍率被 ModConfig.Validate 钳制过；此处直接采用。
        /// </summary>
        public static void ApplyDynamicSpeedConfig(
            bool enabled, float farThreshold, float midThreshold,
            float nearMultiplier, float midMultiplier, float farMultiplier)
        {
            _dynamicSpeedEnabled = enabled;
            _dynamicFarThreshold = farThreshold;
            _dynamicMidThreshold = midThreshold;
            _dynamicNearMultiplier = nearMultiplier;
            _dynamicMidMultiplier = midMultiplier;
            _dynamicFarMultiplier = farMultiplier;
        }

        /// <summary>
        /// 根据到目标的距离返回动态速度倍率（确定性分段，零 LLM）。
        /// 边界语义：distance &gt; FarThreshold → 远距倍率；distance &lt; MidThreshold → 近距倍率；
        /// 其余（含恰在边界上：== FarThreshold / == MidThreshold）→ 中距倍率。禁用时恒为近距倍率。
        /// </summary>
        public static float GetDynamicSpeedMultiplier(float distanceTiles)
        {
            if (!_dynamicSpeedEnabled || distanceTiles < _dynamicMidThreshold)
            {
                return _dynamicNearMultiplier; // 近距（< MidThreshold）或功能禁用
            }

            if (distanceTiles > _dynamicFarThreshold)
            {
                return _dynamicFarMultiplier; // 远距（> FarThreshold）
            }

            return _dynamicMidMultiplier; // 中距（[MidThreshold, FarThreshold]，含边界）
        }

        /// <summary>
        /// 应用动态速度倍率后的寻路速度（int，继承 GameSpeedMultiplier 缩放，上限 20）。
        /// </summary>
        public static int GetEffectiveDynamicSpeed(float distanceTiles)
            => Math.Min(20, (int)MathF.Round(EffectivePathfindSpeed * GetDynamicSpeedMultiplier(distanceTiles)));

        /// <summary>
        /// 走行动画帧间隔（毫秒），随速度倍率等比缩短：间隔 = 基础间隔 / 倍率。
        /// 让脚步动画与加快的移动速度同步，避免加速时的"滑步"观感（无滑步感）。
        /// </summary>
        public static float GetEffectiveWalkAnimationIntervalMs(float distanceTiles)
            => MathF.Max(MinWalkAnimationIntervalMs, BaseWalkAnimationIntervalMs / GetDynamicSpeedMultiplier(distanceTiles));
    }
}
