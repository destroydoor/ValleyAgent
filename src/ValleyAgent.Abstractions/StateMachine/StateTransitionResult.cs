namespace ValleyAgent.StateMachine
{
    /// <summary>
    /// Result of a state transition attempt.
    /// </summary>
    public enum StateTransitionResult
    {
        /// <summary>Transition succeeded.</summary>
        Success,

        /// <summary>Transition failed (e.g., current state rejected it).</summary>
        Failed,

        /// <summary>Transition is not allowed between these states.</summary>
        InvalidTransition,
    }
}