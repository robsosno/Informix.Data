using System.Data.SqlTypes;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// A nullable, qualifier-aware Informix DATETIME value with the surface of the
/// official Informix.Net.Core <c>IfxDateTime</c>. Backed by a CLR
/// <see cref="System.DateTime"/> plus the DATETIME qualifier that selects which
/// fields are present.
/// </summary>
public readonly struct IfxDateTime : INullable, IComparable, IEquatable<IfxDateTime>, IComparable<IfxDateTime>
{
    private readonly DateTime _dt;
    private readonly bool _notNull;
    private readonly short _qualifier;

    // YEAR TO FRACTION(5), used when a plain DateTime is wrapped.
    private static short FullQualifier => IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Fraction5);

    public static readonly IfxDateTime Null = default;
    public static readonly IfxDateTime MinValue = new(DateTime.MinValue);
    public static readonly IfxDateTime MaxValue = new(DateTime.MaxValue);

    internal IfxDateTime(DateTime value, short qualifier)
    {
        _dt = value;
        _qualifier = qualifier;
        _notNull = true;
    }

    public IfxDateTime(DateTime dt) : this(dt, FullQualifier) { }

    public IfxDateTime(long ticks) : this(new DateTime(ticks), FullQualifier) { }

    public IfxDateTime(int numUnits, IfxTimeUnit unit) : this([numUnits], unit) { }

    public IfxDateTime(int u1, int u2, IfxTimeUnit end) : this([u1, u2], end) { }

    public IfxDateTime(int u1, int u2, int u3, IfxTimeUnit end) : this([u1, u2, u3], end) { }

    public IfxDateTime(int u1, int u2, int u3, int u4, IfxTimeUnit end) : this([u1, u2, u3, u4], end) { }

    public IfxDateTime(int u1, int u2, int u3, int u4, int u5, IfxTimeUnit end) : this([u1, u2, u3, u4, u5], end) { }

    public IfxDateTime(int u1, int u2, int u3, int u4, int u5, int u6, IfxTimeUnit end) : this([u1, u2, u3, u4, u5, u6], end) { }

    public IfxDateTime(int u1, int u2, int u3, int u4, int u5, int u6, int u7, IfxTimeUnit end) : this([u1, u2, u3, u4, u5, u6, u7], end) { }

    // Builds a value from the field numbers of the qualifier, ending at <paramref name="end"/>: the values fill the fields backwards from
    // the end unit, and a FRACTION unit consumes the last number as the fractional part (scaled to 100 ns ticks). Missing leading fields stay at their defaults (year 1, month 1, day 1).
    private IfxDateTime(int[] vals, IfxTimeUnit end)
    {
        byte endCode = (byte)end;
        bool hasFraction = endCode >= 11;
        int wholeCount = hasFraction ? vals.Length - 1 : vals.Length;
        byte lastWhole = hasFraction ? (byte)10 : endCode;

        int year = 1, month = 1, day = 1, hour = 0, minute = 0, second = 0;
        for (int i = 0; i < wholeCount; i++)
        {
            byte code = (byte)(lastWhole - 2 * (wholeCount - 1 - i));
            switch (code)
            {
                case 0: year = vals[i]; break;
                case 2: month = vals[i]; break;
                case 4: day = vals[i]; break;
                case 6: hour = vals[i]; break;
                case 8: minute = vals[i]; break;
                case 10: second = vals[i]; break;
            }
        }

        long fracTicks = 0;
        if (hasFraction)
        {
            int digits = Math.Min(endCode - 10, 7);
            long scale = 1;
            for (int i = 0; i < 7 - digits; i++) scale *= 10;
            fracTicks = vals[^1] * scale;
        }

        byte startCode = hasFraction && wholeCount == 0 ? (byte)11 : (byte)(lastWhole - 2 * (wholeCount - 1));
        _dt = new DateTime(year, month, day, hour, minute, second).AddTicks(fracTicks);
        _qualifier = IfxQualifier.ForDateTime((IfxTimeUnit)startCode, end);
        _notNull = true;
    }

    public bool IsNull => !_notNull;

    /// <summary>The packed qualifier word (start unit, end unit, length) the wire code needs to bind this value.</summary>
    internal short Qualifier => _qualifier;

    private DateTime Dt => _notNull ? _dt : throw new InvalidOperationException("The IfxDateTime value is NULL.");

    public int Year => Dt.Year;
    public int Month => Dt.Month;
    public int Day => Dt.Day;
    public int Hour => Dt.Hour;
    public int Minute => Dt.Minute;
    public int Second => Dt.Second;
    public int Millisecond => Dt.Millisecond;
    public long Ticks => Dt.Ticks;

    // A DATETIME YEAR TO DAY copy of the date part.
    public IfxDateTime Date => new(Dt.Date, IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Day));
    public IfxDateTime Now => new(DateTime.Now);
    public IfxDateTime Today => new(DateTime.Today, IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Day));

    public IfxTimeUnit StartTimeUnit => (IfxTimeUnit)IfxQualifier.GetStartCode(_qualifier);
    public IfxTimeUnit EndTimeUnit => (IfxTimeUnit)IfxQualifier.GetEndCode(_qualifier);

    public IfxDateTime Add(IfxTimeSpan ifxTS) =>
        _notNull && !ifxTS.IsNull ? new IfxDateTime(_dt.AddTicks((long)ifxTS.Ticks), _qualifier) : Null;

    public IfxDateTime Add(IfxMonthSpan ifxMS) =>
        _notNull && !ifxMS.IsNull ? new IfxDateTime(_dt.AddMonths((int)ifxMS.TotalMonths), _qualifier) : Null;

    public IfxDateTime AddYears(int years) => _notNull ? new IfxDateTime(_dt.AddYears(years), _qualifier) : Null;
    public IfxDateTime AddMonths(int months) => _notNull ? new IfxDateTime(_dt.AddMonths(months), _qualifier) : Null;
    public IfxDateTime AddDays(double days) => _notNull ? new IfxDateTime(_dt.AddDays(days), _qualifier) : Null;
    public IfxDateTime AddHours(double hours) => _notNull ? new IfxDateTime(_dt.AddHours(hours), _qualifier) : Null;
    public IfxDateTime AddMinutes(double minutes) => _notNull ? new IfxDateTime(_dt.AddMinutes(minutes), _qualifier) : Null;
    public IfxDateTime AddSeconds(double seconds) => _notNull ? new IfxDateTime(_dt.AddSeconds(seconds), _qualifier) : Null;
    public IfxDateTime AddMilliseconds(double ms) => _notNull ? new IfxDateTime(_dt.AddMilliseconds(ms), _qualifier) : Null;
    public IfxDateTime AddTicks(long ticks) => _notNull ? new IfxDateTime(_dt.AddTicks(ticks), _qualifier) : Null;

    public IfxTimeSpan Subtract(IfxDateTime ifxDT) =>
        _notNull && ifxDT._notNull ? new IfxTimeSpan(_dt - ifxDT._dt) : IfxTimeSpan.Null;

    public IfxDateTime Subtract(IfxTimeSpan ifxTS) =>
        _notNull && !ifxTS.IsNull ? new IfxDateTime(_dt.AddTicks(-(long)ifxTS.Ticks), _qualifier) : Null;

    public IfxDateTime Subtract(IfxMonthSpan ifxMS) =>
        _notNull && !ifxMS.IsNull ? new IfxDateTime(_dt.AddMonths(-(int)ifxMS.TotalMonths), _qualifier) : Null;

    public IfxMonthSpan SubtractYearMonth(IfxDateTime ifxDT)
    {
        if (!_notNull || !ifxDT._notNull)
            return IfxMonthSpan.Null;
        int months = (_dt.Year - ifxDT._dt.Year) * 12 + (_dt.Month - ifxDT._dt.Month);
        return new IfxMonthSpan(0, months);
    }

    public static int DaysInMonth(int year, int month) => DateTime.DaysInMonth(year, month);

    public static int Compare(IfxDateTime a, IfxDateTime b)
    {
        if (!a._notNull) return b._notNull ? -1 : 0;
        if (!b._notNull) return 1;
        return a._dt.CompareTo(b._dt);
    }

    public static bool Equals(IfxDateTime a, IfxDateTime b) => a._notNull && Compare(a, b) == 0;
    public static bool NotEquals(IfxDateTime a, IfxDateTime b) => !Equals(a, b);
    public static bool GreaterThan(IfxDateTime a, IfxDateTime b) => a._notNull && b._notNull && a._dt > b._dt;
    public static bool GreaterThanOrEqual(IfxDateTime a, IfxDateTime b) => a._notNull && b._notNull && a._dt >= b._dt;
    public static bool LessThan(IfxDateTime a, IfxDateTime b) => a._notNull && b._notNull && a._dt < b._dt;
    public static bool LessThanOrEqual(IfxDateTime a, IfxDateTime b) => a._notNull && b._notNull && a._dt <= b._dt;

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        IfxDateTime other => CompareTo(other),
        _ => throw new ArgumentException("Object is not an IfxDateTime.", nameof(obj)),
    };

    /// <summary>Typed <see cref="IComparable{T}"/> comparison; SQL NULL sorts first (matches <see cref="SqlDateTime"/>).</summary>
    public int CompareTo(IfxDateTime other) => Compare(this, other);

    public override bool Equals(object? obj) => obj is IfxDateTime other && Equals(other);

    /// <summary>Typed <see cref="IEquatable{T}"/> equality. SQL NULL is never equal to
    /// anything, including another SQL NULL (mirrors <see cref="Equals(object?)"/>).</summary>
    public bool Equals(IfxDateTime other) => Equals(this, other);

    public override int GetHashCode() => _notNull ? _dt.GetHashCode() : 0;

    public static IfxDateTime Parse(string dateTimeString) =>
        new(DateTime.Parse(dateTimeString, System.Globalization.CultureInfo.InvariantCulture));

    public static IfxDateTime Parse(string dateTimeString, IfxTimeUnit start, IfxTimeUnit end) =>
        new(DateTime.Parse(dateTimeString, System.Globalization.CultureInfo.InvariantCulture), IfxQualifier.ForDateTime(start, end));

    public static IfxDateTime Parse(string dateTimeString, string format, IfxTimeUnit start, IfxTimeUnit end) =>
        new(DateTime.ParseExact(dateTimeString, format, System.Globalization.CultureInfo.InvariantCulture), IfxQualifier.ForDateTime(start, end));

    public override string ToString() =>
        _notNull ? _dt.ToString("yyyy-MM-dd HH:mm:ss.fffff", System.Globalization.CultureInfo.InvariantCulture) : "NULL";

    public string ToString(string? format) =>
        _notNull ? _dt.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "NULL";
}
