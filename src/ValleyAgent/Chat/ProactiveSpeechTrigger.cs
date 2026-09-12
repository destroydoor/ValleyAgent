using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ValleyAgent.Chat;

/// <summary>
///     E5-3 时间触发主动发言决策（纯逻辑，无 Game1 依赖，可单测）。
///     在游戏时间到达窗口触发点（早晨 600 / 中午 1200 / 傍晚 1800）时，
///     从已醒且通过主动额度判定的候选 NPC 中生成主动发言，
///     由调用方（EventHandlerInitializer.OnTimeChanged）入队 _pendingPreSpeakActions
///     并在主线程经 ActiveSpeechRouter.Route 渲染。
///     设计文档：docs/ideas/e53-implementation-思路.md
/// </summary>
public sealed class ProactiveSpeechTrigger
{
    /// <summary>窗口触发时间（Stardew 时间制，600 = 6:00）。</summary>
    public const int MorningTrigger = 600;

    public const int NoonTrigger = 1200;
    public const int EveningTrigger = 1800;

    // 各窗口文案池：3 条/窗口，FNV 哈希确定性选一条，避免随机与重复。
    private static readonly IReadOnlyList<string> MorningLines = new[]
    {
        "早上好！新的一天，精神满满。",
        "早安！今天天气不错，适合出门走走。",
        "早呀，昨晚睡得好吗？"
    };

    private static readonly IReadOnlyList<string> NoonLines = new[]
    {
        "中午好！吃过午饭了吗？",
        "正午的太阳可真晒，记得多喝水。",
        "中午好，要不要一起歇一会儿？"
    };

    private static readonly IReadOnlyList<string> EveningLines = new[]
    {
        "傍晚好！今天过得怎么样？",
        "天快黑了，早点回家休息吧。",
        "晚上好，星露谷的夜景也很美呢。"
    };

    /// <summary>
    ///     主动搭话触发概率 [0,1]（来自 ModConfig.ProactiveSpeechProbability，默认 1.0 表示全量通过）。
    ///     在额度判定前做概率门控：未通过则跳过该候选，不消耗额度。
    ///     默认 1.0 保证既有测试全量通过；0.0 = 禁用主动发言；0.3 = 30% 概率触发。
    /// </summary>
    public float SpeechProbability { get; set; } = 1.0f;

    /// <summary>
    ///     生成当前时间窗口的主动发言。
    ///     逐候选：未醒 → 跳过；额度不足 → 跳过；通过 → 确定性选文案。
    /// </summary>
    /// <param name="time">Stardew 时间（600~2600）。</param>
    /// <param name="gameDate">游戏日期（"Y1_spring_1"），额度每日计数隔离。</param>
    /// <param name="candidates">候选 NPC。</param>
    /// <param name="quota">主动发言额度服务（每日上限 + 冷却 + 会话豁免）。</param>
    /// <param name="nowUtc">当前 UTC（测试注入）。</param>
    public IReadOnlyList<ProactiveSpeech> Evaluate(
        int time,
        string gameDate,
        IReadOnlyList<ProactiveCandidate> candidates,
        ProactiveSpeechQuota quota,
        DateTime? nowUtc = null)
    {
        var result = new List<ProactiveSpeech>();
        if (candidates == null || quota == null)
        {
            return result;
        }

        var window = GetWindow(time);
        if (window == Window.None)
        {
            return result;
        }

        var pool = GetPool(window);
        if (pool == null)
        {
            return result;
        }

        foreach (var c in candidates)
        {
            if (c == null || !c.IsAwake)
            {
                continue; // 未醒不开口
            }

            // 概率门控：在额度判定前做随机检查，未通过则跳过不消耗额度
            if (RandomNumberGenerator.GetInt32(100) >= SpeechProbability * 100)
            {
                continue;
            }

            if (!quota.TryConsumeQuota(c.NpcName, gameDate, nowUtc))
            {
                continue; // 额度不足/冷却中/会话外超限 → 跳过
            }

            var text = PickLine(pool, c.NpcName, gameDate, (int)window);
            result.Add(new ProactiveSpeech(c.NpcName, text));
        }

        return result;
    }

    /// <summary>按时间判定窗口：600~1159 早晨、1200~1759 中午、1800+ 傍晚。</summary>
    private static Window GetWindow(int time)
    {
        if (time >= MorningTrigger && time < NoonTrigger)
        {
            return Window.Morning;
        }

        if (time >= NoonTrigger && time < EveningTrigger)
        {
            return Window.Noon;
        }

        if (time >= EveningTrigger)
        {
            return Window.Evening;
        }

        return Window.None;
    }

    private static IReadOnlyList<string>? GetPool(Window window) => window switch
    {
        Window.Morning => MorningLines,
        Window.Noon => NoonLines,
        Window.Evening => EveningLines,
        _ => null
    };

    /// <summary>FNV-1a 哈希确定性选文案（同 NPC+日期+窗口 → 同一条，跨天变化）。</summary>
    private static string PickLine(IReadOnlyList<string> pool, string npcName, string gameDate, int windowOrdinal)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in npcName + "\u0001" + gameDate + "\u0001" + windowOrdinal)
        {
            hash ^= c;
            hash *= prime;
        }

        var index = (int)(hash % (uint)pool.Count);
        return pool[index];
    }

    /// <summary>一条主动发言产出。</summary>
    public sealed record ProactiveSpeech(string NpcName, string Text);

    /// <summary>候选 NPC 信息（由调用方从 AgentService + 日程 + 经济档案装配）。</summary>
    public sealed record ProactiveCandidate(
        string NpcName,
        string DisplayName,
        bool IsAwake,
        double Talkativeness);

    /// <summary>窗口枚举（None = 触发点之外的时间段，不发言）。</summary>
    private enum Window
    {
        None,
        Morning,
        Noon,
        Evening
    }
}