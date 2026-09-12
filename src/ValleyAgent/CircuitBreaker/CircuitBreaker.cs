using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ValleyAgent.Resilience;

/// <summary>
///     A thread-safe circuit breaker that monitors LLM health and provides fallback behavior.
///     Design:
///     - Game-agnostic core logic (no SMAPI/Stardew dependencies)
///     - Thread-safe using Interlocked and lock for state transitions
///     - Sliding window for response time tracking
///     - Configurable thresholds via CircuitBreakerConfig
///     - Standard circuit breaker pattern: CLOSED -> OPEN -> HALF_OPEN -> CLOSED
///     Usage:
///     - Wrap LLM calls with CanExecute() check before attempting
///     - Call RecordSuccess() / RecordFailure() after each LLM call
///     - Call RecordResponseTime(elapsed) to track performance
///     - Subscribe to OnStateChanged and OnFallbackActivated for monitoring
///     Constraints:
///     - Does NOT retry failed calls (retry logic is in LLM providers)
///     - Does NOT attempt LLM calls in OPEN state (CanExecute returns false)
///     - Keeps NPC interaction working via fallback events
/// </summary>
public class CircuitBreaker
{
    private readonly Queue<ResponseTimeSample> _responseTimeSamples;
    private readonly object _stateLock = new();
    private int _consecutiveFailures;

    private CircuitBreakerState _currentState;
    private int _halfOpenCallCount;
    private DateTime _lastStateChangeAt;
    private DateTime _openedAt;

    /// <summary>
    ///     Creates a new CircuitBreaker with default configuration.
    /// </summary>
    public CircuitBreaker()
        : this(new CircuitBreakerConfig())
    {
    }

    /// <summary>
    ///     Creates a new CircuitBreaker with custom configuration.
    /// </summary>
    public CircuitBreaker(CircuitBreakerConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _responseTimeSamples = new Queue<ResponseTimeSample>();
        _currentState = CircuitBreakerState.CLOSED;
        _consecutiveFailures = 0;
        _halfOpenCallCount = 0;
        _openedAt = DateTime.MinValue;
        _lastStateChangeAt = DateTime.UtcNow;
    }

    /// <summary>
    ///     Current state of the circuit breaker.
    /// </summary>
    public CircuitBreakerState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>
    ///     Number of consecutive failures recorded.
    /// </summary>
    public int ConsecutiveFailures
    {
        get => Interlocked.CompareExchange(ref _consecutiveFailures, 0, 0);
    }

    /// <summary>
    ///     Current average response time in seconds over the configured window.
    ///     Returns 0 if no samples.
    /// </summary>
    public double AverageResponseTimeSeconds
    {
        get
        {
            lock (_stateLock)
            {
                CleanupExpiredSamples();
                return CalculateAverageResponseTime();
            }
        }
    }

    /// <summary>
    ///     Number of response time samples in the current window.
    /// </summary>
    public int ResponseTimeSampleCount
    {
        get
        {
            lock (_stateLock)
            {
                CleanupExpiredSamples();
                return _responseTimeSamples.Count;
            }
        }
    }

    /// <summary>
    ///     When the circuit entered OPEN state, or DateTime.MinValue if never.
    /// </summary>
    public DateTime OpenedAt
    {
        get
        {
            lock (_stateLock)
            {
                return _openedAt;
            }
        }
    }

    /// <summary>
    ///     When the last state change occurred.
    /// </summary>
    public DateTime LastStateChangeAt
    {
        get
        {
            lock (_stateLock)
            {
                return _lastStateChangeAt;
            }
        }
    }

    /// <summary>
    ///     Whether the circuit is currently allowing LLM calls.
    ///     Returns true for CLOSED and HALF_OPEN (within call limit), false for OPEN.
    /// </summary>
    public bool CanExecute
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState switch
                {
                    CircuitBreakerState.CLOSED => true,
                    CircuitBreakerState.HALF_OPEN => _halfOpenCallCount < Config.HalfOpenMaxCalls,
                    CircuitBreakerState.OPEN => false,
                    _ => false
                };
            }
        }
    }

    /// <summary>
    ///     Current configuration (read-only reference).
    /// </summary>
    public CircuitBreakerConfig Config { get; }

    /// <summary>
    ///     Fired when the circuit breaker changes state (CLOSED -> OPEN, OPEN -> HALF_OPEN, etc.).
    /// </summary>
    public event EventHandler<CircuitBreakerStateChangedEventArgs>? OnStateChanged;

    /// <summary>
    ///     Fired when the circuit breaker activates fallback behavior (OPEN state entered).
    /// </summary>
    public event EventHandler<CircuitBreakerFallbackEventArgs>? OnFallbackActivated;

    #region Internal Types

    private readonly struct ResponseTimeSample
    {
        public TimeSpan Duration { get; }
        public DateTime Timestamp { get; }

        public ResponseTimeSample(TimeSpan duration, DateTime timestamp)
        {
            Duration = duration;
            Timestamp = timestamp;
        }
    }

    #endregion

    #region Public API

    /// <summary>
    ///     Records a successful LLM call.
    ///     In CLOSED: resets consecutive failures.
    ///     In HALF_OPEN: transitions to CLOSED if success threshold met.
    ///     In OPEN: no-op (should not be called in OPEN state).
    /// </summary>
    public void RecordSuccess()
    {
        lock (_stateLock)
        {
            switch (_currentState)
            {
                case CircuitBreakerState.CLOSED:
                    _ = Interlocked.Exchange(ref _consecutiveFailures, 0);
                    break;

                case CircuitBreakerState.HALF_OPEN:
                    // Success in HALF_OPEN transitions back to CLOSED
                    TransitionTo(CircuitBreakerState.CLOSED, "LLM call succeeded in HALF_OPEN state");
                    _ = Interlocked.Exchange(ref _consecutiveFailures, 0);
                    _halfOpenCallCount = 0;
                    break;

                case CircuitBreakerState.OPEN:
                    // Should not happen, but handle gracefully
                    break;
            }
        }
    }

    /// <summary>
    ///     Records a failed LLM call.
    ///     In CLOSED: increments failures, may transition to OPEN if threshold exceeded.
    ///     In HALF_OPEN: transitions back to OPEN immediately.
    ///     In OPEN: no-op.
    /// </summary>
    /// <param name="failureType">Type of failure for logging (timeout, error, invalid_json).</param>
    public void RecordFailure(string failureType = "error")
    {
        lock (_stateLock)
        {
            switch (_currentState)
            {
                case CircuitBreakerState.CLOSED:
                    var failures = Interlocked.Increment(ref _consecutiveFailures);
                    if (failures >= Config.ConsecutiveFailureThreshold)
                    {
                        TransitionTo(CircuitBreakerState.OPEN,
                            $"Consecutive failure threshold exceeded ({failures}/{Config.ConsecutiveFailureThreshold}): {failureType}");
                        FireFallbackEvent($"{failureType} - consecutive failures: {failures}");
                    }

                    break;

                case CircuitBreakerState.HALF_OPEN:
                    // Failure in HALF_OPEN immediately reverts to OPEN
                    TransitionTo(CircuitBreakerState.OPEN,
                        $"LLM call failed in HALF_OPEN state: {failureType}");
                    _ = Interlocked.Exchange(ref _consecutiveFailures, Config.ConsecutiveFailureThreshold);
                    _halfOpenCallCount = 0;
                    FireFallbackEvent($"{failureType} - HALF_OPEN test failed");
                    break;

                case CircuitBreakerState.OPEN:
                    // No-op, already open
                    break;
            }
        }
    }

    /// <summary>
    ///     Records the response time of an LLM call.
    ///     Only records for successful calls to avoid skewing averages with timeouts.
    ///     Automatically evaluates slow response threshold and may open circuit.
    /// </summary>
    /// <param name="elapsed">Time taken for the LLM call.</param>
    public void RecordResponseTime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return;
        }

        lock (_stateLock)
        {
            // Only track response times in CLOSED state
            if (_currentState != CircuitBreakerState.CLOSED)
            {
                return;
            }

            _responseTimeSamples.Enqueue(new ResponseTimeSample(elapsed, DateTime.UtcNow));
            CleanupExpiredSamples();

            // Check slow response threshold
            var avg = CalculateAverageResponseTime();
            if (_responseTimeSamples.Count >= Config.MinResponseTimeSamples &&
                avg > Config.SlowResponseThresholdSeconds)
            {
                TransitionTo(CircuitBreakerState.OPEN,
                    $"Average response time ({avg:F2}s) exceeded threshold ({Config.SlowResponseThresholdSeconds}s) over {Config.ResponseTimeWindowSeconds}s window");
                FireFallbackEvent($"slow_response - avg: {avg:F2}s, threshold: {Config.SlowResponseThresholdSeconds}s");
            }
        }
    }

    /// <summary>
    ///     Attempts to acquire permission to execute an LLM call.
    ///     In CLOSED: always allows.
    ///     In HALF_OPEN: allows up to HalfOpenMaxCalls.
    ///     In OPEN: checks if cooldown has elapsed, transitions to HALF_OPEN if so.
    ///     Call this BEFORE making an LLM call. If it returns false, use fallback behavior.
    /// </summary>
    /// <returns>True if the LLM call should be attempted, false to use fallback.</returns>
    public bool TryAcquireExecution()
    {
        lock (_stateLock)
        {
            switch (_currentState)
            {
                case CircuitBreakerState.CLOSED:
                    return true;

                case CircuitBreakerState.HALF_OPEN:
                    if (_halfOpenCallCount < Config.HalfOpenMaxCalls)
                    {
                        _halfOpenCallCount++;
                        return true;
                    }

                    return false;

                case CircuitBreakerState.OPEN:
                    // Check if cooldown has elapsed
                    var elapsed = DateTime.UtcNow - _openedAt;
                    if (elapsed >= TimeSpan.FromSeconds(Config.OpenCooldownSeconds))
                    {
                        TransitionTo(CircuitBreakerState.HALF_OPEN,
                            $"Cooldown period elapsed ({elapsed.TotalSeconds:F0}s / {Config.OpenCooldownSeconds}s)");
                        _halfOpenCallCount = 1; // Allow the current call
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    ///     Manually resets the circuit breaker to CLOSED state.
    ///     Clears all failure counts and response time samples.
    /// </summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            var previousState = _currentState;
            _currentState = CircuitBreakerState.CLOSED;
            _ = Interlocked.Exchange(ref _consecutiveFailures, 0);
            _halfOpenCallCount = 0;
            _responseTimeSamples.Clear();
            _openedAt = DateTime.MinValue;
            _lastStateChangeAt = DateTime.UtcNow;

            if (previousState != CircuitBreakerState.CLOSED)
            {
                OnStateChanged?.Invoke(this, new CircuitBreakerStateChangedEventArgs(
                    previousState, CircuitBreakerState.CLOSED, "Manual reset"));
            }
        }
    }

    /// <summary>
    ///     Returns a snapshot of the circuit breaker state for monitoring and debugging.
    /// </summary>
    public CircuitBreakerStatus GetStatus()
    {
        lock (_stateLock)
        {
            CleanupExpiredSamples();
            return new CircuitBreakerStatus
            {
                State = _currentState,
                ConsecutiveFailures = _consecutiveFailures,
                AverageResponseTimeSeconds = CalculateAverageResponseTime(),
                ResponseTimeSampleCount = _responseTimeSamples.Count,
                HalfOpenCallCount = _halfOpenCallCount,
                OpenedAt = _openedAt,
                LastStateChangeAt = _lastStateChangeAt,
                CanExecute = CanExecute,
                Config = Config
            };
        }
    }

    #endregion

    #region Private Methods

    private void TransitionTo(CircuitBreakerState newState, string reason)
    {
        if (_currentState == newState)
        {
            return;
        }

        var previousState = _currentState;
        _currentState = newState;
        _lastStateChangeAt = DateTime.UtcNow;

        if (newState == CircuitBreakerState.OPEN)
        {
            _openedAt = DateTime.UtcNow;
            _halfOpenCallCount = 0;
        }
        else if (newState == CircuitBreakerState.CLOSED)
        {
            _openedAt = DateTime.MinValue;
            _halfOpenCallCount = 0;
            _responseTimeSamples.Clear();
        }

        OnStateChanged?.Invoke(this, new CircuitBreakerStateChangedEventArgs(
            previousState, newState, reason));
    }

    private void FireFallbackEvent(string reason)
    {
        var avg = CalculateAverageResponseTime();
        OnFallbackActivated?.Invoke(this, new CircuitBreakerFallbackEventArgs(
            reason, _currentState, _consecutiveFailures, avg));
    }

    private void CleanupExpiredSamples()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(Config.ResponseTimeWindowSeconds);
        while (_responseTimeSamples.Count > 0 && _responseTimeSamples.Peek().Timestamp < cutoff)
        {
            _ = _responseTimeSamples.Dequeue();
        }
    }

    private double CalculateAverageResponseTime()
    {
        if (_responseTimeSamples.Count == 0)
        {
            return 0.0;
        }

        var totalSeconds = _responseTimeSamples.Sum(s => s.Duration.TotalSeconds);
        return totalSeconds / _responseTimeSamples.Count;
    }

    #endregion
}

/// <summary>
///     Immutable snapshot of circuit breaker status for monitoring.
/// </summary>
public class CircuitBreakerStatus
{
    /// <summary>
    ///     Current circuit breaker state.
    /// </summary>
    public CircuitBreakerState State { get; set; }

    /// <summary>
    ///     Number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    ///     Average response time in seconds.
    /// </summary>
    public double AverageResponseTimeSeconds { get; set; }

    /// <summary>
    ///     Number of response time samples.
    /// </summary>
    public int ResponseTimeSampleCount { get; set; }

    /// <summary>
    ///     Number of calls made in HALF_OPEN state.
    /// </summary>
    public int HalfOpenCallCount { get; set; }

    /// <summary>
    ///     When the circuit was opened, or DateTime.MinValue.
    /// </summary>
    public DateTime OpenedAt { get; set; }

    /// <summary>
    ///     When the last state change occurred.
    /// </summary>
    public DateTime LastStateChangeAt { get; set; }

    /// <summary>
    ///     Whether the circuit currently allows execution.
    /// </summary>
    public bool CanExecute { get; set; }

    /// <summary>
    ///     Current configuration.
    /// </summary>
    public CircuitBreakerConfig Config { get; set; } = new();
}