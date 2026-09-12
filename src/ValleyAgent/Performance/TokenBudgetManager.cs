using System;
using System.Threading;

namespace ValleyAgent.Performance;

/// <summary>
///     Event arguments for token budget warnings and exceeded events.
/// </summary>
public class TokenBudgetEventArgs : EventArgs
{
    public TokenBudgetEventArgs(int currentUsage, int budget)
    {
        CurrentUsage = currentUsage;
        Budget = budget;
        UsagePercent = budget > 0 ? (double)currentUsage / budget : 0.0;
    }

    /// <summary>The current total token usage.</summary>
    public int CurrentUsage { get; }

    /// <summary>The configured token budget (0 = unlimited).</summary>
    public int Budget { get; }

    /// <summary>Percentage of budget consumed (0.0 - 1.0).</summary>
    public double UsagePercent { get; }
}

/// <summary>
///     Tracks per-session token usage against a configurable budget.
///     Design:
///     - Thread-safe using Interlocked for counters
///     - Configurable budget: 0 = unlimited
///     - Fires events at 80% warning threshold and when exceeded
///     - Minimal overhead: atomic operations only
/// </summary>
public class TokenBudgetManager
{
    private int _exceededFired;
    private long _totalTokensUsed;
    private int _warningFired;

    /// <summary>
    ///     Creates a new TokenBudgetManager with the specified budget.
    /// </summary>
    /// <param name="budget">Maximum tokens allowed per session. 0 = unlimited.</param>
    public TokenBudgetManager(int budget)
    {
        Budget = budget >= 0 ? budget : 0;
    }

    /// <summary>
    ///     The configured token budget. 0 means unlimited.
    /// </summary>
    public int Budget { get; }

    /// <summary>
    ///     Fired when token usage reaches 80% of the budget.
    ///     Only fires once per session.
    /// </summary>
    public event EventHandler<TokenBudgetEventArgs>? OnBudgetWarning;

    /// <summary>
    ///     Fired when token usage exceeds the budget.
    ///     Only fires once per session.
    /// </summary>
    public event EventHandler<TokenBudgetEventArgs>? OnBudgetExceeded;

    /// <summary>
    ///     Records token usage for a single LLM call.
    ///     Thread-safe.
    /// </summary>
    /// <param name="tokens">Number of tokens used (prompt + completion).</param>
    public void RecordUsage(int tokens)
    {
        if (tokens <= 0)
        {
            return;
        }

        var newTotal = Interlocked.Add(ref _totalTokensUsed, tokens);

        // Check budget thresholds if budget is configured
        if (Budget > 0)
        {
            var usagePercent = (double)newTotal / Budget;

            // Fire warning at 80% (only once)
            if (usagePercent is >= 0.80 and < 1.0)
            {
                if (Interlocked.CompareExchange(ref _warningFired, 1, 0) == 0)
                {
                    OnBudgetWarning?.Invoke(this, new TokenBudgetEventArgs((int)newTotal, Budget));
                }
            }

            // Fire exceeded event (only once)
            if (usagePercent >= 1.0)
            {
                // Ensure warning was also marked as fired so we don't fire it later
                _ = Interlocked.Exchange(ref _warningFired, 1);
                if (Interlocked.CompareExchange(ref _exceededFired, 1, 0) == 0)
                {
                    OnBudgetExceeded?.Invoke(this, new TokenBudgetEventArgs((int)newTotal, Budget));
                }
            }
        }
    }

    /// <summary>
    ///     Gets the total tokens used so far this session.
    ///     Thread-safe.
    /// </summary>
    public int GetTotalUsage() => (int)Interlocked.Read(ref _totalTokensUsed);

    /// <summary>
    ///     Gets the remaining token budget. Returns int.MaxValue if budget is unlimited (0).
    ///     Thread-safe.
    /// </summary>
    public int GetRemainingBudget()
    {
        if (Budget == 0)
        {
            return int.MaxValue;
        }

        var used = Interlocked.Read(ref _totalTokensUsed);
        var remaining = Budget - (int)used;
        return remaining > 0 ? remaining : 0;
    }

    /// <summary>
    ///     Checks if the token budget has been exceeded.
    ///     Returns false if budget is unlimited (0).
    ///     Thread-safe.
    /// </summary>
    public bool IsBudgetExceeded()
    {
        if (Budget == 0)
        {
            return false;
        }

        var used = Interlocked.Read(ref _totalTokensUsed);
        return used >= Budget;
    }

    /// <summary>
    ///     Gets a formatted usage report with current usage, budget, and percentage.
    ///     Thread-safe.
    /// </summary>
    public string GetUsageReport()
    {
        var used = Interlocked.Read(ref _totalTokensUsed);
        var percent = Budget > 0 ? (double)used / Budget * 100.0 : 0.0;
        var status = Budget > 0
            ? used >= Budget ? "EXCEEDED" : $"{percent:F1}%"
            : "UNLIMITED";

        return $"Token Usage: {used:N0} / {Budget:N0} ({status})";
    }

    /// <summary>
    ///     Resets the token usage counter and event flags.
    ///     Thread-safe.
    /// </summary>
    public void Reset()
    {
        _ = Interlocked.Exchange(ref _totalTokensUsed, 0);
        _ = Interlocked.Exchange(ref _warningFired, 0);
        _ = Interlocked.Exchange(ref _exceededFired, 0);
    }
}