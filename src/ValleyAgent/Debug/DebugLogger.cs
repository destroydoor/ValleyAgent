using System;
using System.Collections.Generic;
using System.Text;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Debug;

/// <summary>
///     Log severity levels for the debug logger.
/// </summary>
public enum LogLevel
{
    /// <summary>Most detailed logging, only available in DEBUG builds.</summary>
    Verbose,

    /// <summary>Diagnostic information useful during development.</summary>
    Debug,

    /// <summary>General operational information.</summary>
    Info,

    /// <summary>Unexpected conditions that don't prevent operation.</summary>
    Warn,

    /// <summary>Errors that prevent expected operation.</summary>
    Error
}

/// <summary>
///     Tracks token usage for a single LLM call.
/// </summary>
public readonly struct LlmCallMetrics
{
    /// <summary>The LLM provider name.</summary>
    public string Provider { get; }

    /// <summary>Length of the prompt in characters.</summary>
    public int PromptLength { get; }

    /// <summary>Response time for the call.</summary>
    public TimeSpan ResponseTime { get; }

    /// <summary>Total tokens used (prompt + completion).</summary>
    public int Tokens { get; }

    /// <summary>When the call was made.</summary>
    public DateTime Timestamp { get; }

    public LlmCallMetrics(string provider, int promptLength, TimeSpan responseTime, int tokens)
    {
        Provider = provider;
        PromptLength = promptLength;
        ResponseTime = responseTime;
        Tokens = tokens;
        Timestamp = DateTime.UtcNow;
    }
}

/// <summary>
///     Session-level token usage statistics.
/// </summary>
public class TokenUsageStats
{
    /// <summary>Total number of LLM calls made this session.</summary>
    public int TotalCalls { get; set; }

    /// <summary>Total tokens consumed this session.</summary>
    public int TotalTokens { get; set; }

    /// <summary>Average response time across all calls.</summary>
    public TimeSpan AverageResponseTime { get; set; }

    /// <summary>Calls grouped by provider name.</summary>
    public Dictionary<string, int> CallsByProvider { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the session started.</summary>
    public DateTime SessionStarted { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Game-agnostic debug logger for ValleyAgent.
///     Design:
///     - Thread-safe using Interlocked and locks
///     - Configurable minimum log level
///     - Verbose level is stripped in RELEASE builds via conditional compilation
///     - Delegates actual output to a configurable sink (ModEntry wires to IMonitor)
///     - Tracks session token usage for diagnostics
///     - Never logs sensitive data (API keys, personal info)
///     Usage:
///     - Set OutputSink to route logs (e.g., to SMAPI's IMonitor.Log)
///     - Configure MinimumLevel based on ModConfig.DebugMode
///     - Call specialized methods (LogDecision, LogLlmCall, LogError) for structured logging
/// </summary>
public class DebugLogger
{
    private readonly List<LlmCallMetrics> _llmCalls;
    private readonly object _statsLock = new();

    /// <summary>
    ///     Creates a new DebugLogger with the specified minimum log level.
    /// </summary>
    public DebugLogger(LogLevel minimumLevel = LogLevel.Info)
    {
        MinimumLevel = minimumLevel;
        _llmCalls = new List<LlmCallMetrics>();
    }

    /// <summary>
    ///     Sink for log output. ModEntry should set this to route to IMonitor.Log.
    ///     Signature: (message, level) => { }
    /// </summary>
    public Action<string, LogLevel>? OutputSink { get; set; }

    /// <summary>
    ///     Current minimum log level. Messages below this level are silently dropped.
    /// </summary>
    public LogLevel MinimumLevel { get; set; }

    /// <summary>
    ///     Logs a message at the specified level if it meets the minimum threshold.
    ///     Verbose messages are silently dropped in RELEASE builds.
    /// </summary>
    public void Log(string message, LogLevel level = LogLevel.Info)
    {
#if !DEBUG
            if (level == LogLevel.Verbose)
                return;
#endif
        if (level < MinimumLevel)
        {
            return;
        }

        OutputSink?.Invoke(message, level);
    }

    /// <summary>
    ///     Logs an agent state transition decision.
    /// </summary>
    public void LogDecision(string npcName, AgentState oldState, AgentState newState, string reason)
    {
        if (LogLevel.Debug < MinimumLevel)
        {
            return;
        }

        var message = $"[Decision] {npcName}: {oldState} -> {newState} | Reason: {reason}";
        OutputSink?.Invoke(message, LogLevel.Debug);
    }

    /// <summary>
    ///     Logs an LLM call with metrics and updates session token statistics.
    ///     Does not log prompt content or API keys.
    /// </summary>
    public void LogLlmCall(string provider, int promptLength, TimeSpan responseTime, int tokens)
    {
        if (LogLevel.Debug < MinimumLevel)
        {
            return;
        }

        var metrics = new LlmCallMetrics(provider, promptLength, responseTime, tokens);

        lock (_statsLock)
        {
            _llmCalls.Add(metrics);
        }

        var message =
            $"[LLM] {provider} | Prompt: {promptLength} chars | Response: {responseTime.TotalMilliseconds:F0}ms | Tokens: {tokens}";
        OutputSink?.Invoke(message, LogLevel.Debug);
    }

    /// <summary>
    ///     Logs an exception with context. Does not include stack traces in release builds unless DebugMode is on.
    /// </summary>
    public void LogError(Exception exception, string context)
    {
        if (exception == null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append($"[Error] {context}: {exception.Message}");

#if DEBUG
        // Include stack traces in DEBUG builds
        sb.Append($" | Stack: {exception.StackTrace}");
#endif

        if (MinimumLevel <= LogLevel.Error)
        {
            OutputSink?.Invoke(sb.ToString(), LogLevel.Error);
        }
    }

    /// <summary>
    ///     Gets a snapshot of session token usage statistics.
    ///     Thread-safe: returns a copy of the current stats.
    /// </summary>
    public TokenUsageStats GetTokenUsageStats()
    {
        lock (_statsLock)
        {
            var stats = new TokenUsageStats
            {
                TotalCalls = _llmCalls.Count,
                SessionStarted = DateTime.UtcNow - GetSessionDuration()
            };

            if (_llmCalls.Count == 0)
            {
                return stats;
            }

            var totalResponseMs = 0.0;
            foreach (var call in _llmCalls)
            {
                stats.TotalTokens += call.Tokens;
                totalResponseMs += call.ResponseTime.TotalMilliseconds;

                if (!stats.CallsByProvider.TryGetValue(call.Provider, out var count))
                {
                    count = 0;
                }

                stats.CallsByProvider[call.Provider] = count + 1;
            }

            stats.AverageResponseTime = TimeSpan.FromMilliseconds(totalResponseMs / _llmCalls.Count);
            return stats;
        }
    }

    /// <summary>
    ///     Clears all session token usage history.
    /// </summary>
    public void ClearTokenStats()
    {
        lock (_statsLock)
        {
            _llmCalls.Clear();
        }
    }

    /// <summary>
    ///     Logs performance metrics from the PerformanceMonitor.
    /// </summary>
    public void LogPerformanceMetrics(string metricsReport)
    {
        if (LogLevel.Debug < MinimumLevel)
        {
            return;
        }

        OutputSink?.Invoke($"[Performance] {metricsReport}", LogLevel.Debug);
    }

    /// <summary>
    ///     Logs token budget status.
    /// </summary>
    public void LogTokenBudget(string budgetReport)
    {
        if (LogLevel.Info < MinimumLevel)
        {
            return;
        }

        OutputSink?.Invoke($"[Budget] {budgetReport}", LogLevel.Info);
    }

    /// <summary>
    ///     Logs cache statistics.
    /// </summary>
    public void LogCacheStats(int entryCount, double hitRatio, long evictions)
    {
        if (LogLevel.Debug < MinimumLevel)
        {
            return;
        }

        var message = $"[Cache] Entries: {entryCount} | Hit Ratio: {hitRatio:P1} | Evictions: {evictions}";
        OutputSink?.Invoke(message, LogLevel.Debug);
    }

    /// <summary>
    ///     Logs a performance warning (e.g., high latency, budget exceeded).
    /// </summary>
    public void LogPerformanceWarning(string message)
    {
        if (LogLevel.Warn < MinimumLevel)
        {
            return;
        }

        OutputSink?.Invoke($"[Performance Warning] {message}", LogLevel.Warn);
    }

    /// <summary>
    ///     Returns a formatted summary of session token usage.
    /// </summary>
    public string GetTokenUsageSummary()
    {
        var stats = GetTokenUsageStats();
        var sb = new StringBuilder();

        _ = sb.AppendLine("=== Token Usage Stats ===");
        _ = sb.AppendLine($"Session Duration: {GetSessionDuration().TotalMinutes:F1} minutes");
        _ = sb.AppendLine($"Total LLM Calls: {stats.TotalCalls}");
        _ = sb.AppendLine($"Total Tokens: {stats.TotalTokens}");
        _ = sb.AppendLine($"Avg Response Time: {stats.AverageResponseTime.TotalMilliseconds:F0}ms");

        if (stats.CallsByProvider.Count > 0)
        {
            _ = sb.AppendLine("Calls by Provider:");
            foreach (var kvp in stats.CallsByProvider)
            {
                _ = sb.AppendLine($"  - {kvp.Key}: {kvp.Value}");
            }
        }

        return sb.ToString();
    }

    private TimeSpan GetSessionDuration()
    {
        // Use the oldest call timestamp, or assume session started recently if no calls
        lock (_statsLock)
        {
            if (_llmCalls.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var oldest = DateTime.UtcNow;
            foreach (var call in _llmCalls)
            {
                if (call.Timestamp < oldest)
                {
                    oldest = call.Timestamp;
                }
            }

            return DateTime.UtcNow - oldest;
        }
    }
}