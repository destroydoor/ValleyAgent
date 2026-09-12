using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Beats;
using ValleyAgent.Config;
using ValleyAgent.Services;
using ValleyAgent.Tracking;

namespace ValleyAgent.AI;

/// <summary>
///     Director 上下文拼装器（阶段 3，3.7）。day_started 时把全局信息 + 玩家活动摘要 + 全部 NPC 的
///     L2 状态摘要压成压缩结构化文本（预算 800-1500 token，超出裁掉低信息 NPC 行），
///     随 day_started 消息发给 TS 端 Director agent 作为上下文。
///     只读数据：不进 NPC 的 LLM 上下文，也不调用任何 NPC LLM（职责隔离）。
/// </summary>
public class DirectorContextBuilder
{
    private readonly IMonitor? _monitor;
    private readonly AgentService _agentService;
    private readonly PlayerActionTracker? _playerActionTracker;
    private readonly NpcConfigLoader? _npcConfigs;
    private readonly BeatStore? _beatStore;
    private readonly int _minTokens;
    private readonly int _maxTokens;

    public DirectorContextBuilder(
        IMonitor monitor,
        AgentService agentService,
        PlayerActionTracker? playerActionTracker = null,
        NpcConfigLoader? npcConfigs = null,
        BeatStore? beatStore = null,
        int minTokens = 800,
        int maxTokens = 1500)
    {
        _monitor = monitor;
        _agentService = agentService;
        _playerActionTracker = playerActionTracker;
        _npcConfigs = npcConfigs;
        _beatStore = beatStore;
        _minTokens = Math.Max(200, minTokens);
        _maxTokens = Math.Max(_minTokens, maxTokens);
    }

    /// <summary>
    ///     拼装 Director 上下文。游戏主线程调用（内部读取 Game1 全局状态），
    ///     文本预算 [<see cref="_minTokens" />, <see cref="_maxTokens" />]。
    /// </summary>
    public string Build(string dateIso)
    {
        var globalInfo = BuildGlobalInfo(dateIso);
        var npcLines = BuildNpcLines(null);
        return Compress(globalInfo, npcLines);
    }

    /// <summary>
    ///     估算 token 数（CJK 友好启发式：约 1 token / 2 字符）。供预算判断与单测。
    /// </summary>
    public static int EstimateTokens(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 2.0));
    }

    /// <summary>
    ///     压缩到 token 预算内：先整行丢弃低信息 NPC 行（无 L2 数据、非主角、无 beat），
    ///     仍超预算则硬截断到上限字符数。不足下限不强凑（小村庄信息量小是正常的）。
    /// </summary>
    internal string Compress(string globalInfo, IReadOnlyList<string> npcLines)
    {
        var full = new StringBuilder();
        full.Append(globalInfo).Append('\n');
        full.Append("== NPC L2 state ==\n");
        foreach (var line in npcLines)
        {
            full.Append(line).Append('\n');
        }

        var text = full.ToString();
        if (EstimateTokens(text) <= _maxTokens || npcLines.Count == 0)
        {
            return text;
        }

        // 丢弃信息量最低的 NPC 行（最后一行信息最少——BuildNpcLines 已按信息量降序）
        var remaining = npcLines.ToList();
        while (remaining.Count > 0)
        {
            remaining.RemoveAt(remaining.Count - 1);
            var candidate = new StringBuilder();
            candidate.Append(globalInfo).Append('\n');
            candidate.Append("== NPC L2 state ==\n");
            foreach (var line in remaining)
            {
                candidate.Append(line).Append('\n');
            }

            var candidateText = candidate.ToString();
            if (EstimateTokens(candidateText) <= _maxTokens)
            {
                _monitor?.Log(
                    $"[DirectorContext] compressed: dropped {npcLines.Count - remaining.Count} low-info NPC lines",
                    LogLevel.Debug);
                return candidateText;
            }
        }

        // 只剩全局信息仍超预算（理论不会：globalInfo 自身很小）→ 硬截断
        return globalInfo.Length <= _maxTokens * 2 ? globalInfo : globalInfo[..(_maxTokens * 2)];
    }

    /// <summary>全局信息段：日期/天气/时间 + 玩家活动摘要 + 趋势 + 活跃 beat 计数。</summary>
    private string BuildGlobalInfo(string dateIso)
    {
        var sb = new StringBuilder();
        sb.Append("[global] date:").Append(dateIso);

        try
        {
            sb.Append(" season:").Append(Game1.currentSeason);
            sb.Append(" weather:").Append(Game1.weatherForTomorrow);
            sb.Append(" time:").Append(Game1.timeOfDay);
            sb.Append(" player_money:").Append(Game1.player?.Money ?? 0);
        }
        catch (NullReferenceException)
        {
            // 标题屏/测试环境无游戏状态：只保留日期
        }

        var playerSummary = _playerActionTracker?.GetTodaySummary();
        if (!string.IsNullOrWhiteSpace(playerSummary))
        {
            sb.Append(" | player_yesterday:").Append(playerSummary);
        }

        var trend = _playerActionTracker?.GetTrendDescription();
        if (!string.IsNullOrWhiteSpace(trend))
        {
            sb.Append(" | ").Append(trend);
        }

        if (_beatStore != null && _beatStore.Count > 0)
        {
            sb.Append(" | active_beats:").Append(_beatStore.Count);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     每个 NPC 一行 L2 摘要，按信息量降序（有事件/工作/beat/人设配置的排前面）。
    ///     locationResolver 供单测注入假位置；null = 走 Game1 查 NPC 当前位置。
    /// </summary>
    internal IReadOnlyList<string> BuildNpcLines(Func<string, string?>? locationResolver)
    {
        locationResolver ??= name =>
        {
            try
            {
                return Game1.getCharacterFromName<NPC>(name)?.currentLocation?.Name;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        };

        var lines = new List<(int InfoScore, string Line)>();
        foreach (var agent in _agentService.AllBrains)
        {
            var brain = agent.Brain;
            var npcName = agent.NpcName;
            var sb = new StringBuilder();
            sb.Append(npcName).Append(" | ");

            var score = 0;
            if (!string.IsNullOrEmpty(brain.MoodTag))
            {
                sb.Append("mood:").Append(brain.MoodTag).Append(' ');
                score += 2;
            }

            if (!string.IsNullOrEmpty(brain.WorkingOn))
            {
                sb.Append("work:").Append(brain.WorkingOn).Append(' ');
                score += 2;
            }

            if (brain.TodayEvents.Count > 0)
            {
                sb.Append("events:[")
                    .Append(string.Join("; ", brain.RecentEventTexts.Take(3)))
                    .Append("] ");
                score += 3;
            }

            if (brain.OwedMoney > 0)
            {
                sb.Append("owed:").Append(brain.OwedMoney).Append("g ");
                score += 1;
            }

            sb.Append("money:").Append(agent.Inventory.Money).Append('g');

            var location = locationResolver(npcName);
            if (!string.IsNullOrEmpty(location))
            {
                sb.Append(" at:").Append(location);
                score += 1;
            }

            var beat = _beatStore?.GetActiveBeat(npcName);
            if (beat != null)
            {
                sb.Append(" beat:\"").Append(beat.SceneDesc).Append('"');
                score += 3;
            }

            var config = _npcConfigs?.GetProfile(npcName);
            if (config != null)
            {
                sb.Append(" cfg:\"").Append(config.Personality).Append('"');
                score += 1;
            }

            lines.Add((score, sb.ToString()));
        }

        return lines
            .OrderByDescending(t => t.InfoScore)
            .ThenBy(t => t.Line, StringComparer.Ordinal)
            .Select(t => t.Line)
            .ToList();
    }
}
