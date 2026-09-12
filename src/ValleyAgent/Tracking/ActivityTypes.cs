using System.Collections.Generic;

namespace ValleyAgent.Tracking;

/// <summary>
///     玩家农场流派标签。TS 端 (narrative-types.ts) 使用 lowercase 字符串联合类型，
///     因此 C# 端通过 MessageProtocol 全局 JsonStringEnumConverter + CamelCase 命名策略
///     将枚举值序列化为 lowercase 字符串（如 Brewer → "brewer"）。
/// </summary>
public enum PlayStyleTag
{
    Brewer,
    Farmer,
    Rancher,
    Miner,
    Warrior,
    Forager,
    Socializer
}

/// <summary>
///     玩家行为里程碑类型。spec 6.6 节定义的 8 种里程碑，
///     由 ActivityTracker.CheckMilestones() 在每日 FinalizeDay 时检测。
///     TS 端 milestone.type 为无约束 string，但 C# 端仍统一 camelCase 序列化以保持一致。
/// </summary>
public enum MilestoneType
{
    FishingStreak,
    MiningStreak,
    FarmingStreak,
    SameGiftRepeated,
    DialogueCount,
    MonsterKills,
    NewArea,
    FirstBundle
}

/// <summary>
///     单次送礼记录。镜像 TS GiftRecord 接口 (narrative-types.ts:46-49)。
/// </summary>
public class GiftRecord
{
    /// <summary>受礼 NPC 名称，如 "Willy"。</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>物品内部 ID，如 "Oceanfish_6"。</summary>
    public string ItemId { get; set; } = string.Empty;
}

/// <summary>
///     玩家流派判定结果。镜像 TS PlayStyle 接口 (narrative-types.ts:25-29)。
///     confidence 取值 0.0~1.0，由 FarmProfiler.InferPlayStyles 归一化计算。
/// </summary>
public class PlayStyle
{
    /// <summary>流派标签。</summary>
    public PlayStyleTag Tag { get; set; }

    /// <summary>置信度，0.0~1.0。</summary>
    public double Confidence { get; set; }

    /// <summary>判定依据，自然语言描述，如 "检测到 12 个酒桶"。</summary>
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>
///     里程碑事件。镜像 TS ActivityMilestoneMessage.milestone 接口 (narrative-types.ts:225-229)。
/// </summary>
public class Milestone
{
    /// <summary>里程碑类型。</summary>
    public MilestoneType Type { get; set; }

    /// <summary>人类可读描述，如 "连续 3 天钓鱼 ≥30 分钟"。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>检测时间，ISO 8601 字符串。</summary>
    public string DetectedAt { get; set; } = string.Empty;
}

/// <summary>
///     单日玩家活动汇总。镜像 TS DailyActivity 接口 (narrative-types.ts:50-65)。
///     由 ActivityTracker 在游戏内每帧累加，FinalizeDay() 时输出并通过 WebSocket 上报给 TS 端。
///     字段名严格对齐 TS 端，由 MessageProtocol 的 CamelCase 命名策略自动转换。
/// </summary>
public class DailyActivity
{
    /// <summary>游戏内日期，格式 "YYYY-MM-DD"，由 ActivityTracker.SetDate 设置。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>钓鱼耗时（分钟）。</summary>
    public int FishingMinutes { get; set; }

    /// <summary>种地耗时（分钟）。</summary>
    public int FarmingMinutes { get; set; }

    /// <summary>挖矿耗时（分钟）。</summary>
    public int MiningMinutes { get; set; }

    /// <summary>采集耗时（分钟）。</summary>
    public int ForagingMinutes { get; set; }

    /// <summary>社交耗时（分钟）。</summary>
    public int SocialMinutes { get; set; }

    /// <summary>战斗耗时（分钟）。</summary>
    public int CombatMinutes { get; set; }

    /// <summary>当日访问过的地点名称列表（去重）。</summary>
    public List<string> LocationsVisited { get; set; } = new();

    /// <summary>当日捕获的鱼数量。</summary>
    public int FishCaught { get; set; }

    /// <summary>当日收获的作物数量。</summary>
    public int CropsHarvested { get; set; }

    /// <summary>当日出货的物品数量。</summary>
    public int ItemsShipped { get; set; }

    /// <summary>
    ///     当日采集的物品数量。镜像 TS narrative-types.ts:62 的 itemsForaged 字段，
    ///     由 ActivityTracker.RecordForaging 累加，供 FarmProfiler 推断 forager 流派使用（spec 4.3）。
    /// </summary>
    public int ItemsForaged { get; set; }

    /// <summary>当日击杀的怪物数量。</summary>
    public int MonstersKilled { get; set; }

    /// <summary>当日对话过的 NPC 名称列表（同 NPC 同天不重复加入）。</summary>
    public List<string> NpcsTalkedTo { get; set; } = new();

    /// <summary>当日送礼记录列表。</summary>
    public List<GiftRecord> GiftsGiven { get; set; } = new();
}