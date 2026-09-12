using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleyAgent.Navigation
{
    /// <summary>
    /// Single-owner service for all NPC movement operations in ValleyAgent.
    ///
    /// <b>Problem this solves:</b> The Stardew Valley engine stores NPC pathfinding
    /// in a single field — <c>NPC.controller</c> (type <c>PathFindController?</c>).
    /// Currently <b>8 different components</b> write to this field independently
    /// (<c>AgentTickLoop</c>, <c>ModEntry</c>, <c>AgentNavigator</c>, and all
    /// five handlers — <c>FarmHandler</c>, <c>MineHandler</c>, <c>ForageHandler</c>,
    /// <c>FightHandler</c>, <c>TalkHandler</c>), each with its own cooldown
    /// dictionary and stuck-detection logic. This causes a <b>destroy-create storm</b>
    /// where Component A nullifies a <c>PathFindController</c> that Component B
    /// just created on the previous tick.
    ///
    /// <see cref="IMovementService"/> is the <b>single owner</b> of
    /// <c>npc.controller</c>. All other components MUST route movement
    /// requests through this interface and MUST NOT set <c>npc.controller</c>
    /// directly.
    ///
    /// <b>Key behaviors:</b>
    /// <list type="bullet">
    ///   <item>Per-mode cooldown enforcement (<see cref="MovementMode"/>).</item>
    ///   <item>Stuck detection with controller recreation.</item>
    ///   <item>Freeze/unfreeze for position lock (zero speed).</item>
    ///   <item>Anti-WanderingSpouses integration (<c>followSchedule = false</c>).</item>
    ///   <item>Diagnostic state queries via <see cref="GetMovementState"/>.</item>
    /// </list>
    /// </summary>
    public interface IMovementService
    {
        // ═══════════════════════════════════════════════════════════════════
        // Movement Commands
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Commands the NPC to navigate toward a specific tile using
        /// Stardew Valley's built-in <see cref="StardewValley.Pathfinding.PathFindController"/>.
        ///
        /// This is the <b>primary movement method</b> for all handler states
        /// (FARM, MINE, FORAGE, FIGHT, TALK). The cooldown mode determines
        /// how aggressively the <c>PathFindController</c> can be recreated
        /// between successive calls.
        /// </summary>
        /// <param name="npc">
        ///     The NPC to move. Must not be null and must exist in the game world.
        ///     If null, returns <see cref="MoveResult.InvalidNpc"/>.
        /// </param>
        /// <param name="targetTile">
        ///     The destination tile in tile coordinates.
        ///     The implementation is responsible for walkability validation.
        /// </param>
        /// <param name="mode">
        ///     Controls the cooldown between successive <c>PathFindController</c> creations:
        ///     <list type="bullet">
        ///       <item><see cref="MovementMode.ShortRange"/> — 5-tick cooldown for handler tasks.</item>
        ///       <item><see cref="MovementMode.LongRange"/> — 30-tick cooldown for FOLLOW state.</item>
        ///       <item><see cref="MovementMode.Immediate"/> — no cooldown for emergency movement.</item>
        ///     </list>
        /// </param>
        /// <param name="currentTick">
        ///     The current game tick (from <c>Game1.ticks</c> or equivalent)
        ///     used for cooldown and stuck-detection timing.
        /// </param>
        /// <returns>
        ///     <see cref="MoveResult.Success"/> — <c>PathFindController</c> was created.
        ///     <see cref="MoveResult.BlockedByCooldown"/> — per-mode cooldown has not elapsed; retry next tick.
        ///     <see cref="MoveResult.Frozen"/> — NPC is frozen; call <see cref="Unfreeze"/> first.
        ///     <see cref="MoveResult.AlreadyMoving"/> — a movement to a different target is in progress.
        ///     <see cref="MoveResult.NoPathFound"/> — target tile is unwalkable or unreachable.
        ///     <see cref="MoveResult.InvalidNpc"/> — NPC reference is null or not in the game world.
        /// </returns>
        public MoveResult MoveTo(NPC npc, Vector2 targetTile, MovementMode mode, int currentTick);

        /// <summary>
        /// Convenience overload that accepts a <see cref="Point"/> target.
        /// Equivalent to <c>MoveTo(npc, new Vector2(targetTile.X, targetTile.Y), mode, currentTick)</c>.
        /// </summary>
        public MoveResult MoveTo(NPC npc, Point targetTile, MovementMode mode, int currentTick);

        /// <summary>
        /// Commands the NPC to follow the specified farmer character.
        ///
        /// The farmer's current tile position is sampled each tick and the
        /// NPC's <c>PathFindController</c> is updated accordingly.
        /// Uses <see cref="MovementMode.LongRange"/> cooldown internally.
        ///
        /// This method is intended for the FOLLOW state and replaces the
        /// current <c>AgentNavigator.Update(npc, follow, currentTick)</c> call.
        /// Cross-map travel (warp delay simulation) is managed internally.
        /// </summary>
        /// <param name="npc">The NPC that should follow.</param>
        /// <param name="target">The farmer to follow (typically <c>Game1.player</c>).</param>
        /// <returns>
        ///     <see cref="MoveResult.Success"/> — following was initiated.
        ///     <see cref="MoveResult.Frozen"/> — NPC is frozen; call <see cref="Unfreeze"/> first.
        ///     <see cref="MoveResult.AlreadyMoving"/> — NPC is already following.
        ///     <see cref="MoveResult.InvalidNpc"/> — NPC or target is null.
        /// </returns>
        public MoveResult Follow(NPC npc, Farmer target);

        /// <summary>
        /// Immediately stops all movement for the NPC.
        ///
        /// Nullifies the <c>PathFindController</c>, calls <c>npc.Halt()</c>,
        /// resets all per-NPC tracking state (cooldown timers, stuck detection,
        /// movement targets), and logs the stop reason for diagnostics.
        ///
        /// This is idempotent — calling it on an already-stopped NPC is safe.
        /// </summary>
        /// <param name="npc">The NPC to stop.</param>
        /// <param name="reason">
        ///     Human-readable reason for the stop. Examples:
        ///     "target reached", "task complete", "state transition to IDLE",
        ///     "player left the map", "no valid targets in range".
        /// </param>
        public void Stop(NPC npc, string reason);

        // ═══════════════════════════════════════════════════════════════════
        // Freeze / Unfreeze
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Freezes the NPC in place.
        ///
        /// Applies the following transformations atomically:
        /// <list type="number">
        ///   <item><c>npc.controller = null</c> — destroy any active pathfinding.</item>
        ///   <item><c>npc.Speed = 0</c> — prevent any movement drift.</item>
        ///   <item><c>npc.Halt()</c> — clear movement buffers.</item>
        ///   <item><c>npc.Position = currentTile * 64f</c> — lock pixel position to tile center.</item>
        ///   <item><c>npc.followSchedule = false</c> — block WanderingSpouses interference.</item>
        ///   <item><c>npc.ignoreScheduleToday = true</c> — prevent schedule override.</item>
        /// </list>
        ///
        /// Frozen NPCs reject all <see cref="MoveTo(StardewValley.NPC, Microsoft.Xna.Framework.Vector2, MovementMode, int)"/> and <see cref="Follow"/> calls
        /// with <see cref="MoveResult.Frozen"/> until <see cref="Unfreeze"/> is called.
        /// </summary>
        /// <param name="npc">The NPC to freeze.</param>
        public void Freeze(NPC npc);

        /// <summary>
        /// Releases a frozen NPC, allowing movement commands to be accepted again.
        ///
        /// Clears the frozen flag but does <b>not</b> automatically start any movement.
        /// The caller must issue a new <see cref="MoveTo(StardewValley.NPC, Microsoft.Xna.Framework.Vector2, MovementMode, int)"/> or <see cref="Follow"/>
        /// request after unfreezing.
        ///
        /// Does <b>not</b> restore the NPC's speed — callers should set
        /// <c>npc.Speed = MovementConstants.DefaultPathfindSpeed</c> if resuming
        /// pathfinding immediately.
        /// </summary>
        /// <param name="npc">The NPC to unfreeze.</param>
        public void Unfreeze(NPC npc);

        // ═══════════════════════════════════════════════════════════════════
        // Queries
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns whether the specified NPC has an active
        /// <see cref="StardewValley.Pathfinding.PathFindController"/> and
        /// is currently navigating toward a target.
        /// </summary>
        /// <param name="npcName">The NPC's name (case-insensitive).</param>
        public bool IsMoving(string npcName);

        /// <summary>
        /// Returns whether the specified NPC is currently frozen.
        /// Frozen NPCs have no controller, zero speed, and reject all
        /// movement commands.
        /// </summary>
        /// <param name="npcName">The NPC's name (case-insensitive).</param>
        public bool IsFrozen(string npcName);

        /// <summary>
        /// Returns whether the specified NPC is in a state that can
        /// accept movement commands: not frozen, not dead, and the
        /// NPC reference is valid.
        ///
        /// Convenience query — equivalent to
        /// <c>!IsFrozen(npcName) &amp;&amp; npc != null &amp;&amp; !npc.IsDead</c>.
        /// </summary>
        /// <param name="npcName">The NPC's name (case-insensitive).</param>
        public bool CanMove(string npcName);

        /// <summary>
        /// Returns a read-only snapshot of the NPC's current movement state.
        /// Useful for diagnostic logging, Debug HUD rendering, and stuck-detection
        /// integration in callers.
        /// </summary>
        /// <param name="npcName">The NPC's name (case-insensitive).</param>
        /// <returns>
        ///     A <see cref="MovementState"/> struct. If the NPC is unknown to the
        ///     service, returns a default state with <c>IsMoving = false</c>.
        /// </returns>
        public MovementState GetMovementState(string npcName);

        // ═══════════════════════════════════════════════════════════════════
        // Stuck Detection
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Detects and handles stuck NPCs.
        ///
        /// If the NPC is within <see cref="MovementConstants.StuckDistanceThreshold"/>
        /// of the target but hasn't moved significantly for
        /// <see cref="MovementConstants.StuckTimeThresholdTicks"/>, the current
        /// <c>PathFindController</c> is nullified so pathfinding can be retried
        /// with an offset target on the next tick.
        ///
        /// This centralizes the pattern currently duplicated in all five handlers:
        /// <code>
        /// if (dist &lt;= 2.5f) {
        ///     if (_stuckStartTick not set) _stuckStartTick = currentTick;
        ///     else if (currentTick - stuckStart > 120) { npc.controller = null; npc.Halt(); }
        /// }
        /// </code>
        /// </summary>
        /// <param name="npc">The NPC to check.</param>
        /// <param name="targetTile">The tile the NPC was trying to reach.</param>
        /// <param name="currentTick">The current game tick.</param>
        /// <returns>
        ///     <c>true</c> if the NPC was stuck and the controller was cleared;
        ///     <c>false</c> otherwise.
        /// </returns>
        public bool HandleStuck(NPC npc, Vector2 targetTile, int currentTick);

        // ═══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes all per-NPC state (cooldown timers, stuck detection,
        /// freeze flags, movement tracking) for the given NPC.
        ///
        /// Should be called when an agent is removed from the system
        /// (e.g., the player disables that NPC's agent, or on save unload).
        /// </summary>
        /// <param name="npcName">The NPC's name (case-insensitive).</param>
        public void RemoveNpc(string npcName);
    }
}
