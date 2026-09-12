using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleyAgent.Services.Schedule;

/// <summary>
///     NPC 作息表查询服务（E5-1 数据层）。
///     回答任意游戏时刻"某 NPC 是否已醒"：优先从原版 schedule 提取真实起床时间，
///     找不到时回退到 C# 内置的人设默认表。不触发任何喊话/主动发言逻辑（E5-2 再消费）。
/// </summary>
public sealed class NpcScheduleService
{
    /// <summary>
    ///     全局默认作息：6:00 起床，24:00 就寝。所有未列入人设表的 NPC 使用此默认。
    /// </summary>
    public static readonly NpcSchedule GlobalDefault = new(StardewTime.Dawn, StardewTime.Midnight);

    /// <summary>
    ///     人设默认表。key = 原版英文内部名（Game1.getCharacterFromName 的 key），
    ///     OrdinalIgnoreCase 大小写不敏感；中文备注为便于阅读。
    ///     只作兜底：运行时有原版 schedule 时原版优先（见 GetSchedule 的调用方）。
    /// </summary>
    private static readonly Dictionary<string, NpcSchedule> DefaultScheduleTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Willy"] = new NpcSchedule(new StardewTime(600), new StardewTime(2400)), // 威利：早起的渔夫，天亮出海
        ["Marnie"] = new NpcSchedule(new StardewTime(600), new StardewTime(2200)), // 玛妮：早起的牧场主，天黑收工
        ["Robin"] = new NpcSchedule(new StardewTime(600), new StardewTime(2200)), // 罗宾：木匠，天亮开工
        ["Haley"] = new NpcSchedule(new StardewTime(900), new StardewTime(2400)), // 海莉：需求 §3.4 明示"9 点前别指望心情好"
        ["Sebastian"] = new NpcSchedule(new StardewTime(1000), new StardewTime(2600)), // 塞巴斯蒂安：夜猫子，晚睡晚起
        ["Sam"] = new NpcSchedule(new StardewTime(700), new StardewTime(2400)), // 山姆：年轻人，多睡一会儿
        ["Vincent"] = new NpcSchedule(new StardewTime(700), new StardewTime(2100)), // 文森特：小孩，早睡
        ["Jas"] = new NpcSchedule(new StardewTime(700), new StardewTime(2100)) // 贾斯：小孩，早睡
    };

    private readonly IReadOnlyDictionary<string, NpcSchedule> _defaults;

    /// <summary>
    ///     使用内置人设默认表构造服务。
    /// </summary>
    public NpcScheduleService()
        : this(DefaultScheduleTable)
    {
    }

    /// <summary>
    ///     注入自定义默认表构造服务（测试隔离用；生产代码走无参构造）。
    /// </summary>
    /// <param name="defaults">NPC 名 → 作息 的映射，null 时退化为全局默认。</param>
    public NpcScheduleService(IReadOnlyDictionary<string, NpcSchedule> defaults)
    {
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
    }

    /// <summary>
    ///     查询 NPC 的作息（默认表语义）。
    ///     未知 NPC / 空名回退全局默认；已知 NPC 返回人设表条目。
    /// </summary>
    public NpcSchedule GetSchedule(string npcName)
        => !string.IsNullOrWhiteSpace(npcName) && _defaults.TryGetValue(npcName, out var schedule)
            ? schedule
            : GlobalDefault;

    /// <summary>
    ///     查询 NPC 的起床时间（默认表语义，HHMM 格式）。
    /// </summary>
    public StardewTime GetWakeUpTime(string npcName)
        => GetSchedule(npcName).WakeUpTime;

    /// <summary>
    ///     查询 NPC 的就寝时间（默认表语义，HHMM 格式）。
    /// </summary>
    public StardewTime GetBedtime(string npcName)
        => GetSchedule(npcName).Bedtime;

    /// <summary>
    ///     判断 NPC 当前时刻是否已醒（默认表语义，不含原版 schedule）。
    ///     非法时刻（&lt;600 / &gt;2600 / 分钟 ≥60）视为未醒，不抛异常。
    /// </summary>
    /// <param name="npcName">NPC 英文内部名。</param>
    /// <param name="currentTime">当前游戏时刻，HHMM 格式（如 900 = 9:00）。</param>
    public bool IsAwake(string npcName, int currentTime)
        => StardewTime.TryCreate(currentTime, out var time) && IsAwake(npcName, time);

    /// <summary>
    ///     判断 NPC 当前时刻是否已醒（默认表语义，不含原版 schedule）。
    /// </summary>
    public bool IsAwake(string npcName, StardewTime currentTime)
        => GetSchedule(npcName).IsAwakeAt(currentTime);

    /// <summary>
    ///     判断 NPC 当前时刻是否已醒（游戏内语义：原版 schedule 优先，默认表兜底）。
    ///     起床时间取原版 schedule 最小 key（真实作息）；就寝时间始终取默认表（原版末条不可靠，见思路 §3.1）。
    ///     NPC 为 null 或时刻非法 → false，不抛。
    /// </summary>
    public bool IsAwake(NPC npc, int currentTime)
        => StardewTime.TryCreate(currentTime, out var time) && IsAwake(npc, time);

    /// <summary>
    ///     判断 NPC 当前时刻是否已醒（游戏内语义，见 int 重载说明）。
    /// </summary>
    public bool IsAwake(NPC npc, StardewTime currentTime)
    {
        if (npc is null)
        {
            return false;
        }

        var schedule = GetSchedule(npc.Name);
        var wakeUpTime = TryGetVanillaWakeUpTime(npc, out var vanillaWake) ? vanillaWake : schedule.WakeUpTime;
        return wakeUpTime <= currentTime && currentTime < schedule.Bedtime;
    }

    /// <summary>
    ///     从 NPC 的原版 schedule 提取真实起床时间。
    ///     只读访问 npc.Schedule.Keys，不触发 checkSchedule / 不改 NPC 状态（纯查询无副作用）。
    ///     成功返回 true；schedule 未加载 / 为空 / NPC 为 null 返回 false。
    /// </summary>
    public bool TryGetVanillaWakeUpTime(NPC npc, out StardewTime wakeUpTime)
    {
        if (npc is null || npc.Schedule is null)
        {
            wakeUpTime = default;
            return false;
        }

        return TryExtractVanillaWakeUpTime(npc.Schedule.Keys, out wakeUpTime);
    }

    /// <summary>
    ///     从 schedule 的 key 集合提取起床时间（= 最小 key，原版日程从起床开始编排）。
    ///     纯函数，不依赖游戏类型，便于单测。
    /// </summary>
    /// <param name="scheduleKeys">schedule 字典的全部 key（游戏时刻）。</param>
    /// <param name="wakeUpTime">提取到的起床时间；失败时为 default。</param>
    internal static bool TryExtractVanillaWakeUpTime(IEnumerable<int> scheduleKeys, out StardewTime wakeUpTime)
    {
        var earliest = -1;
        if (scheduleKeys != null)
        {
            foreach (var key in scheduleKeys)
            {
                if (earliest < 0 || key < earliest)
                {
                    earliest = key;
                }
            }
        }

        if (earliest < 0 || !StardewTime.TryCreate(earliest, out wakeUpTime))
        {
            wakeUpTime = default;
            return false;
        }

        return true;
    }
}