namespace ValleyAgent.Resilience;

/// <summary>
///     Configuration for the circuit breaker with configurable thresholds.
///     All times are in seconds for consistency.
/// </summary>
public class CircuitBreakerConfig
{
    /// <summary>
    ///     Creates a new CircuitBreakerConfig with default values.
    /// </summary>
    public CircuitBreakerConfig()
    {
    }

    /// <summary>
    ///     Creates a new CircuitBreakerConfig with custom values.
    /// </summary>
    public CircuitBreakerConfig(
        int consecutiveFailureThreshold,
        double slowResponseThresholdSeconds,
        int responseTimeWindowSeconds,
        int openCooldownSeconds,
        int halfOpenMaxCalls,
        int minResponseTimeSamples)
    {
        ConsecutiveFailureThreshold = consecutiveFailureThreshold;
        SlowResponseThresholdSeconds = slowResponseThresholdSeconds;
        ResponseTimeWindowSeconds = responseTimeWindowSeconds;
        OpenCooldownSeconds = openCooldownSeconds;
        HalfOpenMaxCalls = halfOpenMaxCalls;
        MinResponseTimeSamples = minResponseTimeSamples;
    }

    /// <summary>
    ///     Number of consecutive failures required to open the circuit. Default: 5.
    /// </summary>
    public int ConsecutiveFailureThreshold { get; set; } = 5;

    /// <summary>
    ///     Average response time threshold in seconds that triggers circuit open. Default: 10.
    /// </summary>
    public double SlowResponseThresholdSeconds { get; set; } = 10.0;

    /// <summary>
    ///     Time window in seconds for calculating average response time. Default: 60.
    /// </summary>
    public int ResponseTimeWindowSeconds { get; set; } = 60;

    /// <summary>
    ///     Cooldown period in seconds before transitioning from OPEN to HALF_OPEN. Default: 30.
    /// </summary>
    public int OpenCooldownSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum number of calls allowed in HALF_OPEN state before reverting to OPEN on failure. Default: 1.
    /// </summary>
    public int HalfOpenMaxCalls { get; set; } = 1;

    /// <summary>
    ///     Minimum number of response time samples required before evaluating slow response threshold. Default: 3.
    /// </summary>
    public int MinResponseTimeSamples { get; set; } = 3;
}