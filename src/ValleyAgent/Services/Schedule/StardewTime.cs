using System;

namespace ValleyAgent.Services.Schedule;

/// <summary>
///     星露谷游戏时刻的值模型。
///     包装 Game1.timeOfDay 的原始整数格式 HHMM（600 = 6:00，2600 = 次日 2:00），
///     提供小时/分钟/总分钟数与比较运算。不可变 struct，可在查询路径上零分配复用。
/// </summary>
public readonly struct StardewTime : IEquatable<StardewTime>
{
    /// <summary>合法时间域下限：6:00（游戏日最早时刻）。</summary>
    public const int MinTimeOfDay = 600;

    /// <summary>合法时间域上限：26:00（次日 2:00，游戏强制入睡时刻）。</summary>
    public const int MaxTimeOfDay = 2600;

    /// <summary>6:00 — 多数 NPC 的默认起床时刻。</summary>
    public static readonly StardewTime Dawn = new(MinTimeOfDay);

    /// <summary>24:00 — 默认就寝时刻（午夜过后入睡）。</summary>
    public static readonly StardewTime Midnight = new(2400);

    /// <summary>
    ///     原始时刻，HHMM 格式（600..2600）。
    /// </summary>
    public int TimeOfDay { get; }

    /// <summary>
    ///     小时部分（TimeOfDay / 100）。
    /// </summary>
    public int Hours { get; }

    /// <summary>
    ///     分钟部分（TimeOfDay % 100）。
    /// </summary>
    public int Minutes { get; }

    /// <summary>
    ///     自当日 0:00 起的总分钟数（600 → 360）。
    /// </summary>
    public int TotalMinutes { get; }

    /// <summary>
    ///     从 HHMM 整数构造时刻。
    /// </summary>
    /// <param name="timeOfDay">HHMM 格式（600..2600，分钟必须 &lt; 60）。</param>
    /// <exception cref="ArgumentOutOfRangeException">超出合法时间域。</exception>
    public StardewTime(int timeOfDay)
    {
        if (!IsValid(timeOfDay))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeOfDay),
                timeOfDay,
                $"Stardew time must be in [{MinTimeOfDay}, {MaxTimeOfDay}] with minutes < 60, got {timeOfDay}.");
        }

        TimeOfDay = timeOfDay;
        Hours = timeOfDay / 100;
        Minutes = timeOfDay % 100;
        TotalMinutes = Hours * 60 + Minutes;
    }

    /// <summary>
    ///     尝试构造时刻。查询路径用此方法：非法时刻返回 false 而非抛异常，
    ///     调用方据此把非法输入当作"未醒"处理，避免游戏内查询崩溃。
    /// </summary>
    public static bool TryCreate(int timeOfDay, out StardewTime time)
    {
        if (!IsValid(timeOfDay))
        {
            time = default;
            return false;
        }

        time = new StardewTime(timeOfDay);
        return true;
    }

    /// <summary>
    ///     按时分构造时刻（如 6, 30 → 6:30）。
    /// </summary>
    public static StardewTime FromHoursMinutes(int hours, int minutes)
        => new(hours * 100 + minutes);

    /// <summary>
    ///     合法域校验：600..2600 且分钟位 &lt; 60。
    /// </summary>
    private static bool IsValid(int timeOfDay)
        => timeOfDay >= MinTimeOfDay
           && timeOfDay <= MaxTimeOfDay
           && timeOfDay % 100 < 60;

    public static bool operator ==(StardewTime left, StardewTime right) => left.TimeOfDay == right.TimeOfDay;

    public static bool operator !=(StardewTime left, StardewTime right) => !(left == right);

    public static bool operator <(StardewTime left, StardewTime right) => left.TimeOfDay < right.TimeOfDay;

    public static bool operator <=(StardewTime left, StardewTime right) => left.TimeOfDay <= right.TimeOfDay;

    public static bool operator >(StardewTime left, StardewTime right) => left.TimeOfDay > right.TimeOfDay;

    public static bool operator >=(StardewTime left, StardewTime right) => left.TimeOfDay >= right.TimeOfDay;

    public bool Equals(StardewTime other) => TimeOfDay == other.TimeOfDay;

    public override bool Equals(object? obj) => obj is StardewTime other && Equals(other);

    public override int GetHashCode() => TimeOfDay;

    /// <summary>
    ///     人类可读格式 "6:00"（与 GameWorldContextProvider.FormatDecisionTime 一致）。
    /// </summary>
    public override string ToString() => $"{Hours}:{Minutes:D2}";
}