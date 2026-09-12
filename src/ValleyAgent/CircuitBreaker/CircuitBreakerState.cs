namespace ValleyAgent.Resilience;

/// <summary>
///     Represents the possible states of a circuit breaker.
/// </summary>
public enum CircuitBreakerState
{
    /// <summary>
    ///     Normal operation. LLM calls are allowed.
    ///     Transitions to OPEN on failure threshold exceeded.
    /// </summary>
    CLOSED,

    /// <summary>
    ///     Failure threshold exceeded. LLM calls are blocked.
    ///     Uses fallback behavior (template dialogue, predefined rules).
    ///     Transitions to HALF_OPEN after cooldown period.
    /// </summary>
    OPEN,

    /// <summary>
    ///     Testing if service has recovered. Allows exactly 1 LLM call.
    ///     Success -> CLOSED, Failure -> OPEN.
    /// </summary>
    HALF_OPEN
}