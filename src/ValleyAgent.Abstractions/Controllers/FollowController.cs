using System;
using Microsoft.Xna.Framework;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Controllers
{
    /// <summary>
    /// NPC behavior controller that handles following the player.
    /// Game-agnostic: uses Vector2 coordinates and exposes a simple
    /// "should follow" boolean for the game integration layer.
    /// Actual pathfinding and movement is handled by AgentNavigator / PathFindController.
    /// </summary>
    public class FollowController : IAgentController
    {
        // ─── Dependencies ──────────────────────────────────────────────────

        private readonly Func<Vector2> _getPlayerPosition;

        // ─── Configurable Thresholds ───────────────────────────────────────

        /// <summary>Distance (in tiles) below which the NPC stops following.</summary>
        public float CloseDistanceThreshold { get; set; } = 2.0f;

        /// <summary>Distance (in tiles) above which the NPC begins moving toward the player.</summary>
        public float FarDistanceThreshold { get; set; } = 5.0f;

        // ─── State ─────────────────────────────────────────────────────────

        private Vector2 _lastKnownPlayerPosition;

        // ─── Output (read by game integration layer) ───────────────────────

        /// <summary>True when the controller wants the NPC to actively move toward the player.</summary>
        public bool IsMoving { get; private set; }

        /// <summary>The NPC's current tile position. Updated by integration layer each tick.</summary>
        public Vector2 CurrentPosition { get; set; }

        // ─── IAgentController Implementation ───────────────────────────────

        /// <inheritdoc />
        public string ControllerName => nameof(FollowController);

        /// <inheritdoc />
        public bool IsActive { get; private set; }

        /// <inheritdoc />
        public event EventHandler<string>? OnActionStarted;

        /// <inheritdoc />
        public event EventHandler<string>? OnActionCompleted;

        /// <inheritdoc />
#pragma warning disable CS0067 // 事件由接口定义，此控制器不主动触发
        public event EventHandler<string>? OnActionFailed;
#pragma warning restore CS0067

        /// <summary>
        /// Creates a new FollowController.
        /// </summary>
        /// <param name="getPlayerPosition">Callback that returns the player's current tile position.</param>
        public FollowController(Func<Vector2> getPlayerPosition)
        {
            _getPlayerPosition = getPlayerPosition ?? throw new ArgumentNullException(nameof(getPlayerPosition));
        }

        /// <inheritdoc />
        public bool CanHandle(AgentState state) => state == AgentState.FOLLOW;

        /// <inheritdoc />
        public void Start()
        {
            if (IsActive)
            {
                return;
            }

            _lastKnownPlayerPosition = _getPlayerPosition();
            IsActive = true;
            OnActionStarted?.Invoke(this, $"Started following player at {_lastKnownPlayerPosition}");
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!IsActive)
            {
                return;
            }

            IsActive = false;
            IsMoving = false;
            OnActionCompleted?.Invoke(this, "Stopped following player");
        }

        /// <inheritdoc />
        public void Update(int ticks)
        {
            if (!IsActive)
            {
                return;
            }

            var playerPos = _getPlayerPosition();
            _lastKnownPlayerPosition = playerPos;

            var distance = EuclideanDistance(CurrentPosition, playerPos);

            // Simple hysteresis: stop when close, start when far
            if (distance < CloseDistanceThreshold)
            {
                IsMoving = false;
            }
            else if (distance > FarDistanceThreshold)
            {
                IsMoving = true;
            }
            // In the dead zone (Close..Far), maintain previous state to avoid flicker
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private static float EuclideanDistance(Vector2 a, Vector2 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return MathF.Sqrt((dx * dx) + (dy * dy));
        }
    }
}
