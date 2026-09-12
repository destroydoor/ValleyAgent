using System;

namespace ValleyAgent.Resilience;

/// <summary>
///     Event arguments for circuit breaker state change notifications.
/// </summary>
public class CircuitBreakerStateChangedEventArgs : EventArgs
{
    public CircuitBreakerStateChangedEventArgs(CircuitBreakerState previousState, CircuitBreakerState newState,
        string reason)
    {
        PreviousState = previousState;
        NewState = newState;
        Reason = reason;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    ///     The previous circuit breaker state.
    /// </summary>
    public CircuitBreakerState PreviousState { get; }

    /// <summary>
    ///     The new circuit breaker state.
    /// </summary>
    public CircuitBreakerState NewState { get; }

    /// <summary>
    ///     The reason for the state change.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    ///     UTC timestamp when the state change occurred.
    /// </summary>
    public DateTime Timestamp { get; }
}

/// <summary>
///     Event arguments for when the circuit breaker activates fallback behavior.
/// </summary>
public class CircuitBreakerFallbackEventArgs : EventArgs
{
    public CircuitBreakerFallbackEventArgs(string reason, CircuitBreakerState circuitState, int consecutiveFailures,
        double averageResponseTimeSeconds)
    {
        Reason = reason;
        CircuitState = circuitState;
        ConsecutiveFailures = consecutiveFailures;
        AverageResponseTimeSeconds = averageResponseTimeSeconds;
    }

    /// <summary>
    ///     The reason fallback was activated.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    ///     Current circuit breaker state.
    /// </summary>
    public CircuitBreakerState CircuitState { get; }

    /// <summary>
    ///     Number of consecutive failures at time of fallback.
    /// </summary>
    public int ConsecutiveFailures { get; }

    /// <summary>
    ///     Current average response time in seconds, or 0 if no data.
    /// </summary>
    public double AverageResponseTimeSeconds { get; }
}