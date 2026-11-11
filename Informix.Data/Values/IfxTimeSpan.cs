using System.Data.SqlTypes;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// A nullable Informix INTERVAL DAY TO FRACTION value (and its narrower forms) with
/// the surface of the official Informix.Net.Core <c>IfxTimeSpan</c>.
/// </summary>
public readonly struct IfxTimeSpan : INullable, IComparable, IEquatable<IfxTimeSpan>, IComparable<IfxTimeSpan>
{
    // null => SQL NULL. The interval is effectively immutable once constructed.
    private readonly IfxIntervalDayFraction? _iv;

    internal IfxTimeSpan(IfxIntervalDayFraction iv) => _iv = iv;

    /// <summary>The largest FRACTION precision Informix supports (FRACTION(5)).</summary>
    public static readonly int MaxScale = 5;

    public static readonly IfxTimeSpan Null = default;
    public static readonly IfxTimeSpan Zero = new(new IfxIntervalDayFraction(0, 0));
    public static readonly IfxTimeSpan MinValue = new(new IfxIntervalDayFraction(long.MinValue / 2, 0));
    public static readonly IfxTimeSpan MaxValue = new(new IfxIntervalDayFraction(long.MaxValue / 2, 0));

    public IfxTimeSpan(long ticks) => _iv = FromTicks(ticks);

    public IfxTimeSpan(TimeSpan ts) => _iv = new IfxIntervalDayFraction(ts);

    public IfxTimeSpan(decimal ticks) => _iv = FromTicks((long)ticks);

    public IfxTimeSpan(int value, IfxTimeUnit timeUnit) => _iv = FromFields([value], timeUnit);

    public IfxTimeSpan(int value1, int value2, IfxTimeUnit end) => _iv = FromFields([value1, value2], end);

    public IfxTimeSpan(int value1, int value2, int value3, IfxTimeUnit end) =>
        _iv = FromFields([value1, value2, value3], end);

    public IfxTimeSpan(int value1, int value2, int value3, int value4, IfxTimeUnit end) =>
        _iv = FromFields([value1, value2, value3, value4], end);

    public IfxTimeSpan(int value1, int value2, int value3, int value4, int value5, IfxTimeUnit end) =>
        _iv = FromFields([value1, value2, value3, value4, value5], end);

    private static IfxIntervalDayFraction FromTicks(long ticks) =>
        new(ticks / TimeSpan.TicksPerSecond, ticks % TimeSpan.TicksPerSecond * 100);

    // Interpret the values as consecutive DAY..SECOND (and optional FRACTION) fields
    // ending at `end`; the last value is the fraction when `end` is a fraction unit.
    private static IfxIntervalDayFraction FromFields(int[] vals, IfxTimeUnit end)
    {
        byte endCode = (byte)end;
        bool hasFraction = endCode >= 11;
        int wholeCount = hasFraction ? vals.Length - 1 : vals.Length;
        byte lastWhole = hasFraction ? (byte)10 : endCode;

        long seconds = 0;
        for (int i = 0; i < wholeCount; i++)
        {
            byte code = (byte)(lastWhole - 2 * (wholeCount - 1 - i));
            seconds += code switch
            {
                4 => vals[i] * 86400L,
                6 => vals[i] * 3600L,
                8 => vals[i] * 60L,
                10 => vals[i],
                _ => 0L,
            };
        }

        long nanos = 0;
        if (hasFraction)
        {
            int digits = endCode - 10;           // Fraction1..5 -> 1..5 (Fraction=16 -> 6, clamp)
            digits = Math.Min(digits, 9);
            long scale = 1;
            for (int i = 0; i < 9 - digits; i++) scale *= 10;
            nanos = vals[^1] * scale;
        }

        byte startCode = hasFraction && wholeCount == 0 ? (byte)12 : (byte)(lastWhole - 2 * (wholeCount - 1));
        int leadingWidth = Math.Max(2, DigitCount(vals.Length > 0 ? Math.Abs(vals[0]) : 0));
        short qual = IfxQualifier.ForInterval(leadingWidth, (IfxTimeUnit)startCode, end);
        return new IfxIntervalDayFraction(seconds, nanos, qual);
    }

    private static int DigitCount(long v) => v < 10 ? 1 : 1 + DigitCount(v / 10);

    public bool IsNull => _iv is null;

    private IfxIntervalDayFraction Iv => _iv ?? throw new InvalidOperationException("The IfxTimeSpan value is NULL.");

    /// <summary>The underlying day-time interval, for binding a non-NULL value back to the wire.</summary>
    internal IfxIntervalDayFraction ToInterval() => Iv;

    private TimeSpan AsTimeSpan => Iv.ToTimeSpan();

    public int Days => AsTimeSpan.Days;
    public int Hours => AsTimeSpan.Hours;
    public int Minutes => AsTimeSpan.Minutes;
    public int Seconds => AsTimeSpan.Seconds;
    public int Milliseconds => AsTimeSpan.Milliseconds;

    /// <summary>The total length in 100-nanosecond ticks.</summary>
    public decimal Ticks => _iv is null ? 0m : Iv.Seconds * (decimal)TimeSpan.TicksPerSecond + Iv.NanoSeconds / 100m;

    public IfxTimeUnit StartTimeUnit => (IfxTimeUnit)IfxQualifier.GetStartCode(Iv.Qualifier);
    public IfxTimeUnit EndTimeUnit => (IfxTimeUnit)IfxQualifier.GetEndCode(Iv.Qualifier);

    public IfxTimeSpan Add(IfxTimeSpan ts) =>
        _iv is null || ts._iv is null ? Null : new IfxTimeSpan(AsTimeSpan + ts.AsTimeSpan);

    public IfxTimeSpan Subtract(IfxTimeSpan ts) =>
        _iv is null || ts._iv is null ? Null : new IfxTimeSpan(AsTimeSpan - ts.AsTimeSpan);

    public IfxTimeSpan Negate() => _iv is null ? Null : new IfxTimeSpan(-AsTimeSpan);

    public IfxTimeSpan Duration() => _iv is null ? Null : new IfxTimeSpan(AsTimeSpan.Duration());

    public IfxTimeSpan Multiply(decimal value) => _iv is null ? Null : new IfxTimeSpan(new TimeSpan((long)(AsTimeSpan.Ticks * value)));

    public IfxTimeSpan Divide(decimal value) => _iv is null ? Null : new IfxTimeSpan(new TimeSpan((long)(AsTimeSpan.Ticks / value)));

    public decimal Divide(IfxTimeSpan ts) => AsTimeSpan.Ticks / (decimal)ts.AsTimeSpan.Ticks;

    public static int Compare(IfxTimeSpan a, IfxTimeSpan b)
    {
        if (a._iv is null) return b._iv is null ? 0 : -1;
        if (b._iv is null) return 1;
        return a.Ticks.CompareTo(b.Ticks);
    }

    public static bool Equals(IfxTimeSpan a, IfxTimeSpan b) => a._iv is not null && Compare(a, b) == 0;
    public static bool NotEquals(IfxTimeSpan a, IfxTimeSpan b) => !Equals(a, b);
    public static bool GreaterThan(IfxTimeSpan a, IfxTimeSpan b) => a._iv is not null && b._iv is not null && a.Ticks > b.Ticks;
    public static bool GreaterThanOrEqual(IfxTimeSpan a, IfxTimeSpan b) => a._iv is not null && b._iv is not null && a.Ticks >= b.Ticks;
    public static bool LessThan(IfxTimeSpan a, IfxTimeSpan b) => a._iv is not null && b._iv is not null && a.Ticks < b.Ticks;
    public static bool LessThanOrEqual(IfxTimeSpan a, IfxTimeSpan b) => a._iv is not null && b._iv is not null && a.Ticks <= b.Ticks;

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        IfxTimeSpan other => CompareTo(other),
        _ => throw new ArgumentException("Object is not an IfxTimeSpan.", nameof(obj)),
    };

    /// <summary>Typed <see cref="IComparable{T}"/> comparison; SQL NULL sorts first.</summary>
    public int CompareTo(IfxTimeSpan other) => Compare(this, other);

    public override bool Equals(object? obj) => obj is IfxTimeSpan other && Equals(other);

    /// <summary>Typed <see cref="IEquatable{T}"/> equality. SQL NULL is never equal to
    /// anything, including another SQL NULL (mirrors <see cref="Equals(object?)"/>).</summary>
    public bool Equals(IfxTimeSpan other) => Equals(this, other);

    public override int GetHashCode() => _iv is null ? 0 : Ticks.GetHashCode();

    public static IfxTimeSpan Parse(string s) =>
        Parse(s, IfxTimeUnit.Day, IfxTimeUnit.Fraction5);

    public static IfxTimeSpan Parse(string s, IfxTimeUnit start, IfxTimeUnit end)
    {
        ArgumentNullException.ThrowIfNull(s);
        short qual = IfxQualifier.ForInterval(9, start, end);
        return new IfxTimeSpan(new IfxIntervalDayFraction(s, qual));
    }

    public static IfxTimeSpan Parse(string s, string format, IfxTimeUnit start, IfxTimeUnit end) => Parse(s, start, end);

    public override string ToString() => _iv?.ToString() ?? "NULL";

    public string ToString(string? format) => ToString();
}
