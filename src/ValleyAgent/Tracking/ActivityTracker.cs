using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValleyAgent.Tracking;

/// <summary>
///     玩家活动采集器 + 里程碑检测器（spec §2.1, §6.6）。
///     <para>
///         在游戏内每帧累积玩家活动数据，<see cref="FinalizeDay" /> 时输出当日汇总，
///         <see cref="CheckMilestones" /> 检测 5 种行为里程碑并按 7 天冷却去重。
///         所有公开方法线程安全（<c>lock(_lock)</c> 保护 <c>_current</c> 与 <c>_history</c>）。
///     </para>
///     <para>
///         生命周期约束：
///         <list type="bullet">
///             <item>每个游戏日开始时调用 <see cref="SetDate" /> 设置日期。</item>
///             <item>玩家活动事件触发时调用对应 Record 方法累加。</item>
///             <item>当日结束时调用 <see cref="FinalizeDay" /> 返回当日数据并清空 _current。</item>
///             <item><see cref="CheckMilestones" /> 可在任意时刻调用，建议在 <see cref="FinalizeDay" /> 之后调用。</item>
///         </list>
///     </para>
/// </summary>
public class ActivityTracker
{
    // ───────────────────────── 里程碑阈值常量（spec §6.6） ─────────────────────────

    /// <summary>连续活动里程碑的最小天数。</summary>
    private const int StreakMinDays = 3;

    /// <summary>FishingStreak 单日最小钓鱼分钟数。</summary>
    private const int FishingStreakMinMinutes = 30;

    /// <summary>MiningStreak 单日最小挖矿分钟数。</summary>
    private const int MiningStreakMinMinutes = 60;

    /// <summary>FarmingStreak 单日最小种地分钟数。</summary>
    private const int FarmingStreakMinMinutes = 30;

    /// <summary>SameGiftRepeated 同 NPC+物品累计最小次数。</summary>
    private const int SameGiftRepeatMinCount = 3;

    /// <summary>DialogueCount 同 NPC 累计最小对话次数。</summary>
    private const int DialogueCountMin = 10;

    /// <summary>同类里程碑冷却天数（spec §6.6 "同类里程碑 7 天内只触发一次"）。</summary>
    private const int MilestoneCooldownDays = 7;

    /// <summary>
    ///     里程碑触发冷却记录：type → 上次触发的游戏内日期（"YYYY-MM-DD"）。
    ///     用于实现 spec §6.6 "同类里程碑 7 天内只触发一次"。
    /// </summary>
    private readonly Dictionary<MilestoneType, string> _firedMilestones = new();

    private readonly List<DailyActivity> _history = new();

    // ───────────────────────── 实例字段 ─────────────────────────

    private readonly object _lock = new();

    /// <summary>当前日累加数据。Date 由 <see cref="SetDate" /> 设置；<see cref="FinalizeDay" /> 后重置。</summary>
    private DailyActivity _current = new();

    // ───────────────────────── 公开方法 ─────────────────────────

    /// <summary>
    ///     设置当前游戏内日期，格式 "YYYY-MM-DD"。
    ///     重置 _current 为空对象并填入 Date。
    ///     每个游戏日开始时必须调用一次。
    /// </summary>
    /// <param name="date">"YYYY-MM-DD" 格式的日期字符串。</param>
    /// <exception cref="ArgumentException"><paramref name="date" /> 为 null 或空字符串。</exception>
    public void SetDate(string date)
    {
        if (string.IsNullOrEmpty(date))
        {
            throw new ArgumentException("日期不能为 null 或空字符串", nameof(date));
        }

        lock (_lock)
        {
            _current = new DailyActivity { Date = date };
        }
    }

    /// <summary>
    ///     记录钓鱼活动：累加捕获鱼数和耗时。
    /// </summary>
    /// <param name="caught">本次钓到的鱼数（非负）。</param>
    /// <param name="minutes">本次钓鱼耗时分钟（非负）。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数为负数。</exception>
    public void RecordFishing(int caught, int minutes)
    {
        if (caught < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(caught), "钓到的鱼数不能为负数");
        }

        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "钓鱼耗时不能为负数");
        }

        lock (_lock)
        {
            _current.FishCaught += caught;
            _current.FishingMinutes += minutes;
        }
    }

    /// <summary>
    ///     记录种地活动：累加收获作物数和耗时。
    /// </summary>
    /// <param name="harvested">本次收获的作物数（非负）。</param>
    /// <param name="minutes">本次种地耗时分钟（非负）。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数为负数。</exception>
    public void RecordFarming(int harvested, int minutes)
    {
        if (harvested < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(harvested), "收获作物数不能为负数");
        }

        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "种地耗时不能为负数");
        }

        lock (_lock)
        {
            _current.CropsHarvested += harvested;
            _current.FarmingMinutes += minutes;
        }
    }

    /// <summary>
    ///     记录挖矿活动：累加耗时。levelsDescended 仅作运行时统计用，
    ///     不写入 <see cref="DailyActivity" />（TS 端 totalStats.miningLevelsDescended 由 ActivityLogStore 单独维护）。
    /// </summary>
    /// <param name="minutes">本次挖矿耗时分钟（非负）。</param>
    /// <param name="levelsDescended">本次下降的矿洞层数（非负，默认 0）。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数为负数。</exception>
    public void RecordMining(int minutes, int levelsDescended = 0)
    {
        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "挖矿耗时不能为负数");
        }

        if (levelsDescended < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(levelsDescended), "下降层数不能为负数");
        }

        lock (_lock)
        {
            _current.MiningMinutes += minutes;
        }
    }

    /// <summary>
    ///     记录采集活动：累加采集物品数和耗时（spec §4.3 forager 流派判定依据）。
    /// </summary>
    /// <param name="items">本次采集的物品数（非负）。</param>
    /// <param name="minutes">本次采集耗时分钟（非负）。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数为负数。</exception>
    public void RecordForaging(int items, int minutes)
    {
        if (items < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(items), "采集物品数不能为负数");
        }

        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "采集耗时不能为负数");
        }

        lock (_lock)
        {
            _current.ItemsForaged += items;
            _current.ForagingMinutes += minutes;
        }
    }

    /// <summary>
    ///     记录战斗活动：累加击杀怪物数和耗时。
    /// </summary>
    /// <param name="killed">本次击杀怪物数（非负）。</param>
    /// <param name="minutes">本次战斗耗时分钟（非负）。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数为负数。</exception>
    public void RecordCombat(int killed, int minutes)
    {
        if (killed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(killed), "击杀怪物数不能为负数");
        }

        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "战斗耗时不能为负数");
        }

        lock (_lock)
        {
            _current.MonstersKilled += killed;
            _current.CombatMinutes += minutes;
        }
    }

    /// <summary>
    ///     记录一次送礼。同一 NPC 同一物品多次送礼不去重（每次都追加到 <see cref="DailyActivity.GiftsGiven" />），
    ///     供 SameGiftRepeated 里程碑按累计次数检测。
    /// </summary>
    /// <param name="toNpc">受礼 NPC 名称（不能为 null 或空）。</param>
    /// <param name="itemId">物品内部 ID（不能为 null 或空）。</param>
    /// <exception cref="ArgumentException">参数为 null 或空字符串。</exception>
    public void RecordGift(string toNpc, string itemId)
    {
        if (string.IsNullOrEmpty(toNpc))
        {
            throw new ArgumentException("NPC 名称不能为空", nameof(toNpc));
        }

        if (string.IsNullOrEmpty(itemId))
        {
            throw new ArgumentException("物品 ID 不能为空", nameof(itemId));
        }

        lock (_lock)
        {
            _current.GiftsGiven.Add(new GiftRecord { To = toNpc, ItemId = itemId });
        }
    }

    /// <summary>
    ///     记录一次对话。同 NPC 同天不重复加入 <see cref="DailyActivity.NpcsTalkedTo" />（去重保留首次）。
    /// </summary>
    /// <param name="npcName">对话 NPC 名称（不能为 null 或空）。</param>
    /// <exception cref="ArgumentException">参数为 null 或空字符串。</exception>
    public void RecordDialogue(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            throw new ArgumentException("NPC 名称不能为空", nameof(npcName));
        }

        lock (_lock)
        {
            if (!_current.NpcsTalkedTo.Contains(npcName))
            {
                _current.NpcsTalkedTo.Add(npcName);
            }
        }
    }

    /// <summary>
    ///     记录一次地点访问。同地点不重复加入 <see cref="DailyActivity.LocationsVisited" />（去重保留首次）。
    /// </summary>
    /// <param name="location">地点名称（不能为 null 或空）。</param>
    /// <exception cref="ArgumentException">参数为 null 或空字符串。</exception>
    public void RecordLocationVisit(string location)
    {
        if (string.IsNullOrEmpty(location))
        {
            throw new ArgumentException("地点名称不能为空", nameof(location));
        }

        lock (_lock)
        {
            if (!_current.LocationsVisited.Contains(location))
            {
                _current.LocationsVisited.Add(location);
            }
        }
    }

    /// <summary>
    ///     返回当前日 _current 的深拷贝。修改返回值不影响内部状态。
    /// </summary>
    /// <returns>当前日 <see cref="DailyActivity" /> 的深拷贝。</returns>
    public DailyActivity GetCurrentDaily()
    {
        lock (_lock)
        {
            return CloneDaily(_current);
        }
    }

    /// <summary>
    ///     结束当日：返回 _current 的深拷贝，将其追加到 _history，重置 _current 为空对象（Date 也清空）。
    ///     调用方应在收到返回值后通过 WebSocket 推送给 TS 端 ActivityLogStore。
    /// </summary>
    /// <returns>当日 <see cref="DailyActivity" /> 数据（深拷贝）。</returns>
    public DailyActivity FinalizeDay()
    {
        lock (_lock)
        {
            var snapshot = CloneDaily(_current);
            _history.Add(snapshot);
            _current = new DailyActivity();
            return snapshot;
        }
    }

    /// <summary>
    ///     检测所有 5 种里程碑并返回新触发的列表。
    ///     <para>
    ///         检测顺序：FishingStreak → MiningStreak → FarmingStreak → SameGiftRepeated → DialogueCount。
    ///         已在冷却期（7 天内）触发的同类里程碑不会再次返回。
    ///         触发的里程碑会更新 <see cref="_firedMilestones" /> 的冷却时间。
    ///     </para>
    ///     <para>
    ///         调用时机：建议在 <see cref="FinalizeDay" /> 之后调用，使当日数据已写入 _history。
    ///     </para>
    /// </summary>
    /// <returns>新触发的里程碑列表（按检测顺序排列）。可能为空列表。</returns>
    public List<Milestone> CheckMilestones()
    {
        var result = new List<Milestone>();
        var detectedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            var today = GetCurrentDateLocked();

            // 1. FishingStreak：连续 ≥3 天 FishingMinutes >= 30
            if (TryCheckStreakMilestoneLocked(MilestoneType.FishingStreak,
                    a => a.FishingMinutes >= FishingStreakMinMinutes,
                    out var fishingStreakDays))
            {
                result.Add(new Milestone
                {
                    Type = MilestoneType.FishingStreak,
                    Description = $"连续 {fishingStreakDays} 天钓鱼 ≥{FishingStreakMinMinutes} 分钟",
                    DetectedAt = detectedAt
                });
                MarkFiredLocked(MilestoneType.FishingStreak, today);
            }

            // 2. MiningStreak：连续 ≥3 天 MiningMinutes >= 60
            if (TryCheckStreakMilestoneLocked(MilestoneType.MiningStreak,
                    a => a.MiningMinutes >= MiningStreakMinMinutes,
                    out var miningStreakDays))
            {
                result.Add(new Milestone
                {
                    Type = MilestoneType.MiningStreak,
                    Description = $"连续 {miningStreakDays} 天挖矿 ≥{MiningStreakMinMinutes} 分钟",
                    DetectedAt = detectedAt
                });
                MarkFiredLocked(MilestoneType.MiningStreak, today);
            }

            // 3. FarmingStreak：连续 ≥3 天 FarmingMinutes >= 30
            if (TryCheckStreakMilestoneLocked(MilestoneType.FarmingStreak,
                    a => a.FarmingMinutes >= FarmingStreakMinMinutes,
                    out var farmingStreakDays))
            {
                result.Add(new Milestone
                {
                    Type = MilestoneType.FarmingStreak,
                    Description = $"连续 {farmingStreakDays} 天种地 ≥{FarmingStreakMinMinutes} 分钟",
                    DetectedAt = detectedAt
                });
                MarkFiredLocked(MilestoneType.FarmingStreak, today);
            }

            // 4. SameGiftRepeated：同 NPC 同 item 累计 >= 3 次
            if (TryCheckSameGiftRepeatedLocked(out var giftNpc, out var giftItem, out var giftCount))
            {
                result.Add(new Milestone
                {
                    Type = MilestoneType.SameGiftRepeated,
                    Description = $"向 {giftNpc} 累计赠送 {giftItem} {giftCount} 次",
                    DetectedAt = detectedAt
                });
                MarkFiredLocked(MilestoneType.SameGiftRepeated, today);
            }

            // 5. DialogueCount：同 NPC 累计 >= 10 次
            if (TryCheckDialogueCountLocked(out var dialogueNpc, out var dialogueCount))
            {
                result.Add(new Milestone
                {
                    Type = MilestoneType.DialogueCount,
                    Description = $"与 {dialogueNpc} 累计对话 {dialogueCount} 次",
                    DetectedAt = detectedAt
                });
                MarkFiredLocked(MilestoneType.DialogueCount, today);
            }
        }

        return result;
    }

    // ───────────────────────── 私有辅助方法 ─────────────────────────

    /// <summary>
    ///     检查连续活动里程碑。从 _history 末尾往前数，连续满足阈值的天数 >= StreakMinDays 即触发。
    ///     已在冷却期内的里程碑直接返回 false。
    /// </summary>
    private bool TryCheckStreakMilestoneLocked(
        MilestoneType type,
        Func<DailyActivity, bool> threshold,
        out int streakDays)
    {
        streakDays = 0;
        if (IsInCooldownLocked(type))
        {
            return false;
        }

        for (var i = _history.Count - 1; i >= 0; i--)
        {
            var activity = _history[i];
            if (threshold(activity))
            {
                streakDays++;
            }
            else
            {
                break;
            }
        }

        return streakDays >= StreakMinDays;
    }

    /// <summary>
    ///     检查 SameGiftRepeated 里程碑。遍历 _history 所有 GiftsGiven，按 (To, ItemId) 分组计数，
    ///     任意一组 >= SameGiftRepeatMinCount 即触发。返回首个满足条件的组信息。
    /// </summary>
    private bool TryCheckSameGiftRepeatedLocked(
        out string npc, out string itemId, out int count)
    {
        npc = string.Empty;
        itemId = string.Empty;
        count = 0;

        if (IsInCooldownLocked(MilestoneType.SameGiftRepeated))
        {
            return false;
        }

        var groups = new Dictionary<string, int>();
        foreach (var activity in _history)
        {
            foreach (var gift in activity.GiftsGiven)
            {
                var key = $"{gift.To}|{gift.ItemId}";
                groups.TryGetValue(key, out var current);
                groups[key] = current + 1;
            }
        }

        foreach (var pair in groups)
        {
            if (pair.Value >= SameGiftRepeatMinCount)
            {
                var parts = pair.Key.Split('|');
                npc = parts.Length > 0 ? parts[0] : string.Empty;
                itemId = parts.Length > 1 ? parts[1] : string.Empty;
                count = pair.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     检查 DialogueCount 里程碑。遍历 _history 所有 NpcsTalkedTo，按 NPC 计数，
    ///     任意 NPC 累计 >= DialogueCountMin 即触发。返回首个满足条件的 NPC。
    /// </summary>
    private bool TryCheckDialogueCountLocked(out string npc, out int count)
    {
        npc = string.Empty;
        count = 0;

        if (IsInCooldownLocked(MilestoneType.DialogueCount))
        {
            return false;
        }

        var counts = new Dictionary<string, int>();
        foreach (var activity in _history)
        {
            foreach (var talked in activity.NpcsTalkedTo)
            {
                counts.TryGetValue(talked, out var current);
                counts[talked] = current + 1;
            }
        }

        foreach (var pair in counts)
        {
            if (pair.Value >= DialogueCountMin)
            {
                npc = pair.Key;
                count = pair.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     判断指定类型的里程碑是否在 7 天冷却期内。
    ///     冷却按游戏内日期计算：上次触发日期距今 < MilestoneCooldownDays 即在冷却期内。
    /// </summary>
    private bool IsInCooldownLocked(MilestoneType type)
    {
        if (!_firedMilestones.TryGetValue(type, out var lastFired))
        {
            return false;
        }

        var today = GetCurrentDateLocked();
        if (string.IsNullOrEmpty(today) || string.IsNullOrEmpty(lastFired))
        {
            return false;
        }

        if (!DateTime.TryParseExact(lastFired, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var lastDate))
        {
            return false;
        }

        if (!DateTime.TryParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var todayDate))
        {
            return false;
        }

        var diff = todayDate - lastDate;
        return diff.TotalDays >= 0 && diff.TotalDays < MilestoneCooldownDays;
    }

    /// <summary>记录里程碑已触发（更新冷却时间）。</summary>
    private void MarkFiredLocked(MilestoneType type, string today) => _firedMilestones[type] = today;

    /// <summary>
    ///     获取当前游戏内日期。优先取 _current.Date；若 _current 已被 FinalizeDay 清空，
    ///     则回退到 _history 最后一条记录的 Date。
    /// </summary>
    private string GetCurrentDateLocked()
    {
        if (!string.IsNullOrEmpty(_current.Date))
        {
            return _current.Date;
        }

        if (_history.Count > 0)
        {
            return _history[_history.Count - 1].Date;
        }

        return string.Empty;
    }

    /// <summary>深拷贝 <see cref="DailyActivity" />（含 List 字段）。</summary>
    private static DailyActivity CloneDaily(DailyActivity source)
    {
        var clone = new DailyActivity
        {
            Date = source.Date,
            FishingMinutes = source.FishingMinutes,
            FarmingMinutes = source.FarmingMinutes,
            MiningMinutes = source.MiningMinutes,
            ForagingMinutes = source.ForagingMinutes,
            SocialMinutes = source.SocialMinutes,
            CombatMinutes = source.CombatMinutes,
            FishCaught = source.FishCaught,
            CropsHarvested = source.CropsHarvested,
            ItemsShipped = source.ItemsShipped,
            ItemsForaged = source.ItemsForaged,
            MonstersKilled = source.MonstersKilled,
            LocationsVisited = new List<string>(source.LocationsVisited),
            NpcsTalkedTo = new List<string>(source.NpcsTalkedTo),
            GiftsGiven = source.GiftsGiven.ConvertAll(g => new GiftRecord { To = g.To, ItemId = g.ItemId })
        };
        return clone;
    }
}