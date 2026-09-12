using System;
using System.Collections.Generic;

namespace ValleyAgent.Chat;

/// <summary>
///     E5-3 主动发言额度服务（E5-2 晨间喊话 / E5-3 主动搭话共享）。
///     包装 <see cref="ChatSessionRegistry" /> 的每日主动计数（唯一数据源），
///     在其上叠加三层判定：会话豁免 → 冷却窗口 → 每日上限。
///     被动回应走 <see cref="RecordPassiveResponse" />，永不消耗额度。
///     纯逻辑类，无 Game1 依赖，可单测；游戏侧经 ServiceInitializer 注册为单例。
///     设计文档：docs/ideas/quota-implementation-思路.md
/// </summary>
public sealed class ProactiveSpeechQuota
{
    // 每 NPC 最近一次主动发言时间（UTC），冷却判定的簿记源。
    // 与注册表的每日计数分离：会话内豁免发言不刷新冷却（见 TryConsumeQuota 注释）。
    private readonly Dictionary<string, DateTime> _lastProactiveUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private readonly ChatSessionRegistry _registry;

    public ProactiveSpeechQuota(ChatSessionRegistry? registry = null)
    {
        _registry = registry ?? ChatSessionRegistry.Instance;
    }

    /// <summary>每日主动发言额度上限（来自 ModConfig.ProactiveSpeechDailyLimit，默认 2）。</summary>
    public int DailyLimit { get; set; } = 2;

    /// <summary>主动发言冷却（分钟，来自 ModConfig.ProactiveSpeechCooldownMinutes，默认 30）。</summary>
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>
    ///     主动发言总开关（来自 ModConfig.EnableProactiveSpeech，默认 true）。
    ///     false 时 TryConsumeQuota 直接返回 false，不消耗额度也不触发发言。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     尝试消费一次主动发言额度。
    ///     判定顺序（短路）：空名字 → 会话豁免 → 冷却窗口 → 每日上限。
    ///     返回 true 表示本次主动发言被允许（会话内豁免时同样返回 true 但不消耗额度）。
    /// </summary>
    /// <param name="npcName">NPC 名字（OrdinalIgnoreCase）。</param>
    /// <param name="gameDate">游戏日期字符串（如 "Y1_spring_1"），用于每日计数隔离。</param>
    /// <param name="nowUtc">当前 UTC 时间（测试可注入）。</param>
    public bool TryConsumeQuota(string npcName, string gameDate, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return false;
        }

        // 总开关关闭时直接拒绝，不消耗额度也不刷新冷却
        if (!Enabled)
        {
            return false;
        }

        var now = nowUtc ?? DateTime.UtcNow;

        // 1) 会话豁免：活跃会话内的发言是对话回合，不消耗主动额度。
        //    必须先于冷却判定——豁免发言不得刷新冷却，否则玩家多聊几句
        //    NPC 一整天都不能再主动搭话。
        if (IsSessionExempt(npcName, now))
        {
            return true;
        }

        // 2) 冷却窗口：距上次主动发言不足 CooldownMinutes 则拒绝。
        lock (_lock)
        {
            if (_lastProactiveUtc.TryGetValue(npcName, out var last)
                && (now - last).TotalMinutes < CooldownMinutes)
            {
                return false;
            }
        }

        // 3) 每日上限：当日已用次数达到 DailyLimit 则拒绝。
        if (GetRemainingQuota(npcName, gameDate) <= 0)
        {
            return false;
        }

        // 4) 通过：登记到注册表（会话外主动发言才计数）并刷新冷却时间。
        _registry.RecordNpcSpeech(npcName, true, gameDate, now);
        lock (_lock)
        {
            _lastProactiveUtc[npcName] = now;
        }

        return true;
    }

    /// <summary>当日剩余主动发言额度（DailyLimit - 当日已用，下限 0）。</summary>
    public int GetRemainingQuota(string npcName, string gameDate)
    {
        var used = _registry.GetDailyProactiveCount(npcName, gameDate);
        return Math.Max(0, DailyLimit - used);
    }

    /// <summary>
    ///     记录一次被动回应（玩家搭话/喊话的回应）。不消耗额度、不计数、不刷新冷却。
    ///     仍经注册表走 isProactiveSpeech=false 的早退路径，维持"所有发言都经注册表留痕"。
    /// </summary>
    public void RecordPassiveResponse(string npcName, DateTime? nowUtc = null) =>
        _registry.RecordNpcSpeech(npcName, false, string.Empty, nowUtc);

    /// <summary>
    ///     日切重置：清空全部每日计数与冷却时间。
    ///     注册表是全量每日计数，游戏同一时刻只有一个活跃日期，全清等价于只清当日，
    ///     且能兜住跨天残留 key。
    /// </summary>
    public void ResetDaily(string gameDate)
    {
        _ = gameDate; // 语义参数：调用方显式传入当天日期；实现委托注册表全清
        _registry.ClearDailyCounts();
        lock (_lock)
        {
            _lastProactiveUtc.Clear();
        }
    }

    /// <summary>NPC 是否处于活跃会话中（会话内发言豁免额度）。</summary>
    public bool IsSessionExempt(string npcName, DateTime? nowUtc = null)
        => _registry.IsInActiveSession(npcName, nowUtc);

    /// <summary>上次主动发言时间（UTC），供调试/留痕。</summary>
    public DateTime? GetLastProactiveUtc(string npcName)
    {
        lock (_lock)
        {
            return _lastProactiveUtc.TryGetValue(npcName, out var last) ? last : null;
        }
    }
}