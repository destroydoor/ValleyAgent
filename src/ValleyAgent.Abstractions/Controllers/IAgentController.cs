using System;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Controllers
{
    /// <summary>
    /// Interface for all NPC behavior controllers (Follow, Fight, Farm, Forage, Dialogue, Idle).
    /// Controllers are activated by the state machine when entering their associated state
    /// and deactivated when exiting.
    /// 
    /// Design principles:
    /// - Game-agnostic: Uses string identifiers, not SMAPI types.
    /// - State-driven: Each controller handles exactly one <see cref="AgentState"/>.
    /// - Event-driven: Reports lifecycle events for logging and orchestration.
    /// - Tick-based update: Synchronized with the game's update loop.
    /// </summary>
    public interface IAgentController
    {
        /// <summary>
        /// Human-readable name for this controller (for logging/debugging).
        /// </summary>
        public string ControllerName { get; }

        /// <summary>
        /// Whether this controller is currently active (has been started but not stopped).
        /// </summary>
        public bool IsActive { get; }

        /// <summary>
        /// Determines whether this controller can handle the given agent state.
        /// Typically returns true for exactly one state (the one this controller manages).
        /// </summary>
        /// <param name="state">The agent state to check.</param>
        /// <returns>True if this controller handles the given state; otherwise, false.</returns>
        public bool CanHandle(AgentState state);

        /// <summary>
        /// Starts the controller. Called when the state machine enters the associated state.
        /// Use for initialization logic (e.g., pathfinding setup, target acquisition).
        /// </summary>
        public void Start();

        /// <summary>
        /// Stops the controller. Called when the state machine exits the associated state.
        /// Use for cleanup logic (e.g., releasing resources, stopping movement).
        /// </summary>
        public void Stop();

        /// <summary>
        /// Called every game tick while this controller is active.
        /// </summary>
        /// <param name="ticks">Number of ticks since the game started (or since controller activation).</param>
        public void Update(int ticks);

        /// <summary>
        /// Fired when the controller starts executing its primary action.
        /// The event argument provides a description of the action being started.
        /// </summary>
        public event EventHandler<string>? OnActionStarted;

        /// <summary>
        /// Fired when the controller successfully completes its primary action.
        /// The event argument provides a description of the completed action.
        /// </summary>
        public event EventHandler<string>? OnActionCompleted;

        /// <summary>
        /// Fired when the controller fails to execute or complete its primary action.
        /// The event argument provides a reason or error message for the failure.
        /// </summary>
        public event EventHandler<string>? OnActionFailed;
    }
}
