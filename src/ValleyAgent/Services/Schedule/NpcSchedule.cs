using System;

namespace ValleyAgent.Services.Schedule;

/// <summary>
///     单个 NPC 的作息（起床/就寝）。
///     起床时间优先来自原版 schedule 提取，就寝时间来自人设默认表（见 NpcScheduleService）。
/// </summary>
public readonly struct NpcSchedule : IEquatable<NpcSchedule>
{
    /// <summary>
    ///     起床时间。
    /// </summary>
    public StardewTime WakeUpTime { get; }

    /// <summary>
    ///     就寝时间。
    /// </summary>
    public StardewTime Bedtime { get; }

    /// <summary>
    ///     构造作息。
    /// </summary>
    /// <param name="wakeUpTime">起床时间。</param>
    /// <param name="bedtime">就寝时间。</param>
    public NpcSchedule(StardewTime wakeUpTime, StardewTime bedtime)
    {
        WakeUpTime = wakeUpTime;
        Bedtime = bedtime;
    }

    /// <summary>
    ///     判断给定时刻 NPC 是否醒着。
    ///     语义为半开区间 [WakeUpTime, Bedtime)：恰在起床时刻视为已醒，恰在就寝时刻视为已睡。
    ///     起床不早于就寝的坏数据（wake &gt;= bed）恒返回 false，防御性兜底。
    /// </summary>
    public bool IsAwakeAt(StardewTime currentTime)
        => WakeUpTime <= currentTime && currentTime < Bedtime;

    public bool Equals(NpcSchedule other)
        => WakeUpTime == other.WakeUpTime && Bedtime == other.Bedtime;

    public override bool Equals(object? obj) => obj is NpcSchedule other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(WakeUpTime, Bedtime);

    /// <summary>
    ///     人类可读格式 "6:00 - 24:00"。
    /// </summary>
    public override string ToString() => $"{WakeUpTime} - {Bedtime}";
}