using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.Tracking;

/// <summary>
///     玩家行为分类（阶段 3，3.6）。10-tick 采样玩家当前活动，按日累计百分比 + 趋势，
///     day_started 聚合后供 DirectorContextBuilder 注入导演上下文（导演据此编排"该把 NPC 往哪推"）。
///     与 ActivityTracker（旧版逐日事件累计）完全独立——本组件是新的轻量采样分类器。
/// </summary>
public enum PlayerActivity
{
    Mine,
    Farm,
    Fish,
    Forage,
    Social,
    Other
}

/// <summary>
///     玩家行为采样跟踪器（阶段 3，3.6）。
///     每 10 tick 采样一次玩家当前位置 + 手持物，纯分类（<see cref="Classify" /> 无游戏依赖、可单测），
///     按日累计百分比；StartDay 时把昨日汇总压入趋势并重置当日桶。
///     设计文档：docs/ideas/phase3-director-l2-default-思路.md §2.7。
/// </summary>
public class PlayerActionTracker
{
    private readonly IMonitor? _monitor;
    private readonly Dictionary<PlayerActivity, int> _todayCounts;
    private readonly Dictionary<PlayerActivity, int> _yesterdayCounts;
    private int _todayTotalSamples;
    private string? _currentDateIso;
    private string? _lastTrendDescription;

    /// <summary>采样间隔（tick）。</summary>
    public const int SampleIntervalTicks = 10;

    public PlayerActionTracker(IMonitor? monitor = null)
    {
        _monitor = monitor;
        _todayCounts = AllActivities.ToDictionary(a => a, _ => 0);
        _yesterdayCounts = AllActivities.ToDictionary(a => a, _ => 0);
    }

    private static IReadOnlyList<PlayerActivity> AllActivities { get; } = Enum.GetValues<PlayerActivity>();

    /// <summary>
    ///     纯分类（无游戏依赖，可单测）：按"矿洞地图 > 手持钓鱼竿 > 农场地图 > 户外采集地图 > 城镇社交 > 其他"判定。
    /// </summary>
    /// <param name="locationName">当前地图名（Game1.currentLocation?.Name）。</param>
    /// <param name="heldItemName">手持物名称（Game1.player?.CurrentItem?.Name，可为 null）。</param>
    public static PlayerActivity Classify(string? locationName, string? heldItemName)
    {
        // 矿洞：任何矿洞类地图都是 Mine（含深层矿井、火山、骷髅洞穴）
        if (locationName != null && MineLocations.Contains(locationName, StringComparer.OrdinalIgnoreCase))
        {
            return PlayerActivity.Mine;
        }

        // 钓鱼：手持任何钓鱼竿（Fishing Rod / Training Rod / Fiberglass Rod / Iridium Rod）。
        // 所有名为 "*Rod" 的物品都是钓鱼竿（SDV 无其他含 "Rod" 的物品），直接按 "Rod" 判定。
        if (heldItemName != null
            && heldItemName.Contains("Rod", StringComparison.OrdinalIgnoreCase))
        {
            return PlayerActivity.Fish;
        }

        // 农场：农场类地图（含农场建筑内）→ Farm
        if (locationName != null && FarmLocations.Contains(locationName, StringComparer.OrdinalIgnoreCase))
        {
            return PlayerActivity.Farm;
        }

        // 采集：野外户外地图 → Forage
        if (locationName != null && ForageLocations.Contains(locationName, StringComparer.OrdinalIgnoreCase))
        {
            return PlayerActivity.Forage;
        }

        // 社交：城镇 + 室内建筑（已知社交地点）→ Social
        if (locationName != null && SocialLocations.Contains(locationName, StringComparer.OrdinalIgnoreCase))
        {
            return PlayerActivity.Social;
        }

        return PlayerActivity.Other;
    }

    /// <summary>
    ///     采样一次（由 OnUpdateTicked 每 10 tick 调用，主线程）。读取 Game1 当前状态分类入当日桶。
    /// </summary>
    public void Sample()
    {
        var locationName = Game1.currentLocation?.Name;
        var heldItemName = Game1.player?.CurrentItem?.Name;
        RecordSample(Classify(locationName, heldItemName));
    }

    /// <summary>
    ///     记录一次采样结果（内部：单测接缝，绕过 Game1 直接喂分类结果；
    ///     生产路径由 <see cref="Sample" /> 调用）。
    /// </summary>
    internal void RecordSample(PlayerActivity activity)
    {
        _todayCounts[activity]++;
        _todayTotalSamples++;
    }

    /// <summary>
    ///     新的一天开始（day_started）：把昨日汇总压入趋势并重置当日桶。
    ///     幂等：同一日期重复调用不重复聚合。
    /// </summary>
    public void StartDay(string dateIso)
    {
        if (string.Equals(dateIso, _currentDateIso, StringComparison.Ordinal))
        {
            return; // 同日重复调用 → 不重复聚合
        }

        if (_currentDateIso != null && _todayTotalSamples > 0)
        {
            // 先算趋势（对比"今日"与"上一日"），再滚动昨日桶——顺序不能反，否则两个桶相同、delta 全为 0
            _lastTrendDescription = BuildTrendDescription();
            foreach (var activity in AllActivities)
            {
                _yesterdayCounts[activity] = _todayCounts[activity];
            }

            _monitor?.Log(
                $"[PlayerActionTracker] day {_currentDateIso} aggregated: {GetTodaySummary()}",
                LogLevel.Debug);
        }

        _currentDateIso = dateIso;
        foreach (var activity in AllActivities)
        {
            _todayCounts[activity] = 0;
        }

        _todayTotalSamples = 0;
    }

    /// <summary>当日各活动百分比摘要（如 "farm 45%, mine 30%, other 25%"）。</summary>
    public string GetTodaySummary()
    {
        return BuildSummary(_todayCounts, _todayTotalSamples);
    }

    /// <summary>上一日（最近一次 StartDay 聚合的）百分比摘要；尚无聚合时返回空串。</summary>
    public string GetYesterdaySummary()
    {
        return BuildSummary(_yesterdayCounts, _yesterdayCounts.Values.Sum());
    }

    /// <summary>趋势描述（"今日偏向 X 较昨日 +N%"）；尚无两日对比时返回空串。</summary>
    public string GetTrendDescription()
    {
        return _lastTrendDescription ?? "";
    }

    /// <summary>已采样的日期（首日尚未 StartDay 前为 null）。</summary>
    public string? CurrentDateIso => _currentDateIso;

    /// <summary>
    ///     趋势描述：找出占比变化最大的活动（含稳定活动占比），压缩为一行文本。
    ///     例子："trend: mine 12% (prev 5%, up), farm 40% (stable)"。
    /// </summary>
    private string BuildTrendDescription()
    {
        var today = _todayCounts;
        var yesterday = _yesterdayCounts;
        var todayTotal = Math.Max(1, _todayTotalSamples);
        var yesterdayTotal = Math.Max(1, yesterday.Values.Sum());

        var parts = new List<string>();
        foreach (var activity in AllActivities)
        {
            var todayPct = (int)Math.Round(today[activity] * 100.0 / todayTotal);
            var yesterdayPct = (int)Math.Round(yesterday[activity] * 100.0 / yesterdayTotal);
            var delta = todayPct - yesterdayPct;
            var label = activity.ToString().ToLowerInvariant();
            parts.Add(delta switch
            {
                > 5 => $"{label} {todayPct}% (prev {yesterdayPct}%, up)",
                < -5 => $"{label} {todayPct}% (prev {yesterdayPct}%, down)",
                _ => $"{label} {todayPct}%"
            });
        }

        return "player trend: " + string.Join(", ", parts);
    }

    private static string BuildSummary(Dictionary<PlayerActivity, int> counts, int total)
    {
        if (total <= 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var activity in AllActivities)
        {
            var pct = (int)Math.Round(counts[activity] * 100.0 / total);
            if (pct <= 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(activity.ToString().ToLowerInvariant()).Append(' ').Append(pct).Append('%');
        }

        return sb.Length == 0 ? "no samples" : sb.ToString();
    }

    // ── 地点集合（覆盖原版全部地图；mod 地图未知 → Other）──

    private static readonly HashSet<string> MineLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mine", "MineShaft", "UndergroundMine", "VolcanoDungeon", "SkullCave", "BugLand"
    };

    private static readonly HashSet<string> FarmLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Farm", "FarmHouse", "Greenhouse", "GreenHouse", "Coop", "Barn", "Shed", "Cellar",
        "SlimeHutch", "FarmCave", "Stable", "FishPond"
    };

    private static readonly HashSet<string> ForageLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Forest", "Woods", "Beach", "Mountain", "Desert", "BusStop", "Railroad", "Backwoods",
        "SecretWoods", "CindersapForest", "WizardHouseBasement"
    };

    private static readonly HashSet<string> SocialLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Town", "Saloon", "SeedShop", "Blacksmith", "Clinic", "FishShop", "Museum", "CommunityCenter",
        "JojaMart", "AdventurerGuild", "WizardHouse", "ElliottHouse", "HaleyHouse", "SamHouse",
        "SebastianHouse", "AlexHouse", "EvelynHouse", "LeahHouse", "Trailer", "AnimalShop", "SandyHouse",
        "ManorHouse", "Club", "MovieTheater", "QiNutRoom"
    };
}
