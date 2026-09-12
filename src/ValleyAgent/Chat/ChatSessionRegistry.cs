using System;
using System.Collections.Generic;

namespace ValleyAgent.Chat;

/// <summary>
///     会话模式的触发源。
///     PlayerChat：玩家在聊天栏路由命中该 NPC（E2-2）；
///     ProactiveSpeech：NPC 主动发言后玩家接话（E5-3 使用同一注册表）。
/// </summary>
public enum ChatSessionInitiator
{
    PlayerChat,
    ProactiveSpeech
}

/// <summary>
///     单个 NPC 的会话状态（E2-2 会话模式）。
///     会话期间 NPC 的来回发言不计入每日主动额度；玩家 60 秒（可配置）未回应则会话过期。
/// </summary>
public sealed class ChatSession
{
    public ChatSession(string npcName, ChatSessionInitiator initiator, DateTime nowUtc)
    {
        NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        Initiator = initiator;
        LastExchangeUtc = nowUtc;
        ExchangeCount = 0;
    }

    public string NpcName { get; }

    public ChatSessionInitiator Initiator { get; }

    /// <summary>最近一次"回合"时间（UTC）。用于 60s 超时判定。</summary>
    public DateTime LastExchangeUtc { get; set; }

    /// <summary>会话内来回轮数（会话豁免的簿记依据）。</summary>
    public int ExchangeCount { get; set; }
}

/// <summary>
///     会话模式注册表（E2-2）：每 NPC 一条会话，支持 60s 超时、会话切换（取最近触碰）、
///     以及 E5-3 需要的"会话内不计主动额度"簿记钩子。
///     纯逻辑类，无 Game1 依赖，可单测；游戏侧使用 <see cref="Instance" /> 单例。
/// </summary>
public sealed class ChatSessionRegistry
{
    /// <summary>会话默认超时（秒），与需求 §3.2 的 60 秒一致（可配置覆盖）。</summary>
    public const double DefaultSessionTimeoutSeconds = 60.0;

    // E5-3 钩子：每日主动发言计数。key = $"{gameDate}|{npcName}"（OrdinalIgnoreCase）。
    private readonly Dictionary<string, int> _dailyProactiveCounts;
    private readonly object _lock = new();

    private readonly Dictionary<string, ChatSession> _sessions;

    public ChatSessionRegistry()
    {
        _sessions = new Dictionary<string, ChatSession>(StringComparer.OrdinalIgnoreCase);
        _dailyProactiveCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>会话超时秒数。由 ModConfig.ChatSessionTimeoutSeconds 在初始化时覆盖。</summary>
    public double SessionTimeoutSeconds { get; set; } = DefaultSessionTimeoutSeconds;

    /// <summary>游戏侧共享实例。</summary>
    public static ChatSessionRegistry Instance { get; } = new();

    /// <summary>
    ///     创建或刷新与指定 NPC 的会话。路由命中即调用：
    ///     新会话 → ExchangeCount 从 1 起；已有会话 → 刷新时间戳并 ExchangeCount++。
    /// </summary>
    public ChatSession StartOrTouch(string npcName, ChatSessionInitiator initiator, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be empty.", nameof(npcName));
        }

        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            if (_sessions.TryGetValue(npcName, out var existing))
            {
                existing.LastExchangeUtc = now;
                existing.ExchangeCount++;
                return existing;
            }

            var session = new ChatSession(npcName, initiator, now) { ExchangeCount = 1 };
            _sessions[npcName] = session;
            return session;
        }
    }

    /// <summary>
    ///     指定 NPC 是否处于未过期的会话中（LastExchangeUtc 距今 ≤ SessionTimeoutSeconds）。
    /// </summary>
    public bool IsInActiveSession(string npcName, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return false;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            return _sessions.TryGetValue(npcName, out var session)
                   && (now - session.LastExchangeUtc).TotalSeconds <= SessionTimeoutSeconds;
        }
    }

    /// <summary>
    ///     返回当前"进行中的会话"对象：所有未过期会话中最近被触碰的那个。
    ///     会话切换 = 换对话对象（旧会话自然过期，不强制互斥）。
    /// </summary>
    public string? GetActiveSessionNpc(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            ChatSession? latest = null;
            foreach (var session in _sessions.Values)
            {
                if ((now - session.LastExchangeUtc).TotalSeconds > SessionTimeoutSeconds)
                {
                    continue;
                }

                if (latest == null || session.LastExchangeUtc > latest.LastExchangeUtc)
                {
                    latest = session;
                }
            }

            return latest?.NpcName;
        }
    }

    /// <summary>指定 NPC 当前会话的来回轮数（无会话返回 0）。</summary>
    public int GetSessionExchangeCount(string npcName)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(npcName, out var session) ? session.ExchangeCount : 0;
        }
    }

    /// <summary>强制结束指定 NPC 的会话（如玩家明确离开）。</summary>
    public void EndSession(string npcName)
    {
        lock (_lock)
        {
            _ = _sessions.Remove(npcName);
        }
    }

    /// <summary>
    ///     E5-3 钩子：记录一次 NPC 发言。
    ///     会话内 → 只作为会话轮次（不计主动额度，豁免）；会话外且 isProactiveSpeech → 计入每日主动计数。
    ///     E5-3 做主动发言额度强制时调用本方法并读取 <see cref="GetDailyProactiveCount" />。
    /// </summary>
    public void RecordNpcSpeech(string npcName, bool isProactiveSpeech, string gameDate, DateTime? nowUtc = null)
    {
        if (IsInActiveSession(npcName, nowUtc))
        {
            return; // 会话内：豁免，只计入会话轮次
        }

        if (!isProactiveSpeech)
        {
            return; // 被动回应不占额度（§3.4 喊话回应同理，E5-2 复用）
        }

        var key = BuildDailyKey(gameDate, npcName);
        lock (_lock)
        {
            _dailyProactiveCounts[key] = _dailyProactiveCounts.TryGetValue(key, out var count) ? count + 1 : 1;
        }
    }

    /// <summary>E5-3 钩子：查询某 NPC 某日期的主动发言次数。</summary>
    public int GetDailyProactiveCount(string npcName, string gameDate)
    {
        var key = BuildDailyKey(gameDate, npcName);
        lock (_lock)
        {
            return _dailyProactiveCounts.TryGetValue(key, out var count) ? count : 0;
        }
    }

    /// <summary>清空每日主动计数（E5-3 在日切/额度重置时调用）。</summary>
    public void ClearDailyCounts()
    {
        lock (_lock)
        {
            _dailyProactiveCounts.Clear();
        }
    }

    /// <summary>清空全部会话与计数（返回标题/卸载时调用）。</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _sessions.Clear();
            _dailyProactiveCounts.Clear();
        }
    }

    private static string BuildDailyKey(string gameDate, string npcName)
        => string.IsNullOrEmpty(gameDate) ? npcName : $"{gameDate}|{npcName}";
}