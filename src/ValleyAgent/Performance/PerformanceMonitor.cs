using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ValleyAgent.Performance;

/// <summary>
///     Tracks a single LLM call metric sample for sliding window analysis.
/// </summary>
public readonly struct LlmLatencySample
{
    /// <summary>Duration of the LLM call.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Tokens used in this call.</summary>
    public int Tokens { get; }

    /// <summary>When the call was made.</summary>
    public DateTime Timestamp { get; }

    public LlmLatencySample(TimeSpan duration, int tokens)
    {
        Duration = duration;
        Tokens = tokens;
        Timestamp = DateTime.UtcNow;
    }
}

/// <summary>
///     Tracks decision frequency for a single NPC.
/// </summary>
public class NpcDecisionMetrics
{
    private long _decisionCount;

    public NpcDecisionMetrics(string npcName)
    {
        NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        TrackingStarted = DateTime.UtcNow;
    }

    /// <summary>The NPC name.</summary>
    public string NpcName { get; }

    /// <summary>Total decisions made by this NPC.</summary>
    public long DecisionCount
    {
        get => Interlocked.Read(ref _decisionCount);
    }

    /// <summary>When tracking started for this NPC.</summary>
    public DateTime TrackingStarted { get; }

    /// <summary>
    ///     Increments the decision count.
    ///     Thread-safe.
    /// </summary>
    public void IncrementDecisions() => _ = Interlocked.Increment(ref _decisionCount);
}

/// <summary>
///     Comprehensive performance metrics snapshot.
/// </summary>
public class PerformanceMetricsReport
{
    /// <summary>Total LLM calls recorded.</summary>
    public int TotalLlmCalls { get; set; }

    /// <summary>Minimum LLM call latency.</summary>
    public TimeSpan MinLatency { get; set; }

    /// <summary>Maximum LLM call latency.</summary>
    public TimeSpan MaxLatency { get; set; }

    /// <summary>Average LLM call latency.</summary>
    public TimeSpan AvgLatency { get; set; }

    /// <summary>95th percentile LLM call latency.</summary>
    public TimeSpan P95Latency { get; set; }

    /// <summary>Total tokens across all recorded calls.</summary>
    public long TotalTokens { get; set; }

    /// <summary>Total state transitions recorded.</summary>
    public long TotalStateTransitions { get; set; }

    /// <summary>Decision counts per NPC.</summary>
    public Dictionary<string, long> DecisionsByNpc { get; set; } = new();

    /// <summary>When tracking started.</summary>
    public DateTime TrackingStarted { get; set; }
}

/// <summary>
///     Monitors performance metrics for the ValleyAgent AI system.
///     Tracks:
///     - LLM call latency (min/max/avg/p95) with sliding window
///     - Decision frequency per NPC
///     - State transition counts
///     Design:
///     - Thread-safe using Interlocked and ConcurrentDictionary
///     - Sliding window of last 100 LLM calls (configurable)
///     - Minimal overhead: atomic operations, no locks on hot paths
///     - Memory capped: max 100 samples, max 100 NPCs tracked
/// </summary>
public class PerformanceMonitor
{
    private const int DefaultMaxSamples = 100;
    private const int DefaultMaxNpcs = 100;

    private readonly Queue<LlmLatencySample> _latencySamples;
    private readonly int _maxNpcs;
    private readonly int _maxSamples;
    private readonly object _npcLock = new();
    private readonly Dictionary<string, NpcDecisionMetrics> _npcMetrics;
    private readonly object _samplesLock = new();
    private readonly DateTime _trackingStarted;
    private long _stateTransitionCount;
    private long _totalLlmCalls;

    /// <summary>
    ///     Creates a new PerformanceMonitor.
    /// </summary>
    /// <param name="maxSamples">Maximum LLM call samples to retain in sliding window. Default: 100.</param>
    /// <param name="maxNpcs">Maximum NPCs to track individually. Default: 100.</param>
    public PerformanceMonitor(int maxSamples = DefaultMaxSamples, int maxNpcs = DefaultMaxNpcs)
    {
        _maxSamples = maxSamples > 0 ? maxSamples : DefaultMaxSamples;
        _maxNpcs = maxNpcs > 0 ? maxNpcs : DefaultMaxNpcs;
        _latencySamples = new Queue<LlmLatencySample>(_maxSamples);
        _npcMetrics = new Dictionary<string, NpcDecisionMetrics>(StringComparer.OrdinalIgnoreCase);
        _trackingStarted = DateTime.UtcNow;
    }

    /// <summary>
    ///     Records an LLM call with its duration and token usage.
    ///     Thread-safe.
    /// </summary>
    /// <param name="duration">Time taken for the LLM call.</param>
    /// <param name="tokens">Tokens used (prompt + completion).</param>
    public void RecordLlmCall(TimeSpan duration, int tokens)
    {
        _ = Interlocked.Increment(ref _totalLlmCalls);

        var sample = new LlmLatencySample(duration, tokens > 0 ? tokens : 0);

        lock (_samplesLock)
        {
            _latencySamples.Enqueue(sample);
            while (_latencySamples.Count > _maxSamples)
            {
                _ = _latencySamples.Dequeue();
            }
        }
    }

    /// <summary>
    ///     Records a decision made by an NPC.
    ///     Thread-safe.
    /// </summary>
    /// <param name="npcName">The name of the NPC that made the decision.</param>
    public void RecordDecision(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return;
        }

        NpcDecisionMetrics? metrics;
        lock (_npcLock)
        {
            if (!_npcMetrics.TryGetValue(npcName, out metrics))
            {
                // Enforce max NPCs: remove oldest if at capacity
                if (_npcMetrics.Count >= _maxNpcs)
                {
                    var oldest = _npcMetrics.OrderBy(kvp => kvp.Value.TrackingStarted).FirstOrDefault().Key;
                    if (oldest != null)
                    {
                        _ = _npcMetrics.Remove(oldest);
                    }
                }

                metrics = new NpcDecisionMetrics(npcName);
                _npcMetrics[npcName] = metrics;
            }
        }

        metrics?.IncrementDecisions();
    }

    /// <summary>
    ///     Records a state transition occurrence.
    ///     Thread-safe.
    /// </summary>
    public void RecordStateTransition() => _ = Interlocked.Increment(ref _stateTransitionCount);

    /// <summary>
    ///     Gets a comprehensive metrics report.
    ///     Thread-safe: returns a snapshot of current metrics.
    /// </summary>
    public PerformanceMetricsReport GetMetricsReport()
    {
        var report = new PerformanceMetricsReport
        {
            TotalLlmCalls = (int)Interlocked.Read(ref _totalLlmCalls),
            TotalStateTransitions = Interlocked.Read(ref _stateTransitionCount),
            TrackingStarted = _trackingStarted
        };

        // Copy samples for analysis
        LlmLatencySample[] samples;
        lock (_samplesLock)
        {
            samples = _latencySamples.ToArray();
        }

        report.TotalLlmCalls = samples.Length;

        if (samples.Length > 0)
        {
            var durations = samples.Select(s => s.Duration).ToArray();
            var totalTokens = samples.Sum(s => (long)s.Tokens);

            report.MinLatency = durations.Min();
            report.MaxLatency = durations.Max();
            report.AvgLatency = TimeSpan.FromMilliseconds(durations.Average(d => d.TotalMilliseconds));
            report.P95Latency = CalculateP95(durations);
            report.TotalTokens = totalTokens;
        }

        // Copy NPC metrics
        lock (_npcLock)
        {
            foreach (var kvp in _npcMetrics)
            {
                report.DecisionsByNpc[kvp.Key] = kvp.Value.DecisionCount;
            }
        }

        return report;
    }

    /// <summary>
    ///     Gets a formatted metrics report string.
    /// </summary>
    public string GetFormattedReport()
    {
        var report = GetMetricsReport();
        var sb = new StringBuilder();

        _ = sb.AppendLine("=== Performance Metrics ===");
        _ = sb.AppendLine($"Tracking Duration: {DateTime.UtcNow - report.TrackingStarted:hh\\:mm\\:ss}");
        _ = sb.AppendLine($"Total LLM Calls: {report.TotalLlmCalls}");
        _ = sb.AppendLine($"Total State Transitions: {report.TotalStateTransitions}");
        _ = sb.AppendLine($"Total Tokens: {report.TotalTokens:N0}");

        if (report.TotalLlmCalls > 0)
        {
            _ = sb.AppendLine(
                $"Latency (min/avg/max/p95): {report.MinLatency.TotalMilliseconds:F0}ms / {report.AvgLatency.TotalMilliseconds:F0}ms / {report.MaxLatency.TotalMilliseconds:F0}ms / {report.P95Latency.TotalMilliseconds:F0}ms");
        }

        if (report.DecisionsByNpc.Count > 0)
        {
            _ = sb.AppendLine("Decisions by NPC:");
            foreach (var kvp in report.DecisionsByNpc.OrderByDescending(kvp => kvp.Value))
            {
                _ = sb.AppendLine($"  - {kvp.Key}: {kvp.Value}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Resets all metrics.
    ///     Thread-safe.
    /// </summary>
    public void Reset()
    {
        _ = Interlocked.Exchange(ref _totalLlmCalls, 0);
        _ = Interlocked.Exchange(ref _stateTransitionCount, 0);

        lock (_samplesLock)
        {
            _latencySamples.Clear();
        }

        lock (_npcLock)
        {
            _npcMetrics.Clear();
        }
    }

    private static TimeSpan CalculateP95(TimeSpan[] durations)
    {
        if (durations.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var sorted = durations.OrderBy(d => d.TotalMilliseconds).ToArray();
        var index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
        if (index < 0)
        {
            index = 0;
        }

        if (index >= sorted.Length)
        {
            index = sorted.Length - 1;
        }

        return sorted[index];
    }
}