using System.Data.SqlTypes;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// A nullable Informix INTERVAL YEAR TO MONTH value (and its narrower forms) with
/// the surface of the official Informix.Net.Core <c>IfxMonthSpan</c>.
/// </summary>
public readonly struct IfxMonthSpan : INullable, IComparable, IEquatable<IfxMonthSpan>, IComparable<IfxMonthSpan>
{
    private readonly IfxIntervalYearMonth? _iv;

    internal IfxMonthSpan(IfxIntervalYearMonth iv) => _iv = iv;

    // default(IfxMonthSpan) is NULL: the struct holds no interval object.
    public static readonly IfxMonthSpan Null = default;
    public static readonly IfxMonthSpan Zero = new(new IfxIntervalYearMonth(0, YearMonthQualifier));
    public static readonly IfxMonthSpan MinValue = new(new IfxIntervalYearMonth(int.MinValue, YearMonthQualifier));
    public static readonly IfxMonthSpan MaxValue = new(new IfxIntervalYearMonth(int.MaxValue, YearMonthQualifier));

    // YEAR(9) TO MONTH: the widest qualifier, so any month count fits.
    private static short YearMonthQualifier => IfxQualifier.ForInterval(9, IfxTimeUnit.Year, IfxTimeUnit.Month);

    public IfxMonthSpan(int value, IfxTimeUnit timeUnit)
    {
        int months = timeUnit == IfxTimeUnit.Year ? value * 12 : value;
        var end = timeUnit == IfxTimeUnit.Year ? IfxTimeUnit.Year : IfxTimeUnit.Month;
        _iv = new IfxIntervalYearMonth(months, IfxQualifier.ForInterval(9, IfxTimeUnit.Year, end));
    }

    public IfxMonthSpan(int years, long months) =>
        _iv = new IfxIntervalYearMonth((int)(years * 12 + months), YearMonthQualifier);

    public bool IsNull => _iv is null;

    private IfxIntervalYearMonth Iv => _iv ?? throw new InvalidOperationException("The IfxMonthSpan value is NULL.");

    /// <summary>The underlying year-month interval, for binding a non-NULL value back to the wire.</summary>
    internal IfxIntervalYearMonth ToInterval() => Iv;

    /// <summary>The signed total number of months.</summary>
    public long TotalMonths => _iv?.Months ?? 0;

    /// <summary>The whole-years component (<see cref="TotalMonths"/> / 12).</summary>
    public int Years => (int)(Iv.Months / 12);

    /// <summary>The month component beyond whole years (<see cref="TotalMonths"/> % 12).</summary>
    public int Months => (int)(Iv.Months % 12);

    public IfxTimeUnit StartTimeUnit => (IfxTimeUnit)IfxQualifier.GetStartCode(Iv.Qualifier);
    public IfxTimeUnit EndTimeUnit => (IfxTimeUnit)IfxQualifier.GetEndCode(Iv.Qualifier);

    public IfxMonthSpan Add(IfxMonthSpan ms) =>
        _iv is null || ms._iv is null ? Null : FromTotal(TotalMonths + ms.TotalMonths);

    public IfxMonthSpan Subtract(IfxMonthSpan ms) =>
        _iv is null || ms._iv is null ? Null : FromTotal(TotalMonths - ms.TotalMonths);

    public IfxMonthSpan Negate() => _iv is null ? Null : FromTotal(-TotalMonths);

    public IfxMonthSpan Duration() => _iv is null ? Null : FromTotal(Math.Abs(TotalMonths));

    public IfxMonthSpan Multiply(decimal value) => _iv is null ? Null : FromTotal((long)(TotalMonths * value));

    public IfxMonthSpan Divide(decimal value) => _iv is null ? Null : FromTotal((long)(TotalMonths / value));

    public decimal Divide(IfxMonthSpan ms) => TotalMonths / (decimal)ms.TotalMonths;

    private static IfxMonthSpan FromTotal(long totalMonths) =>
        new(new IfxIntervalYearMonth((int)totalMonths, YearMonthQualifier));

    public static int Compare(IfxMonthSpan a, IfxMonthSpan b)
    {
        if (a._iv is null) return b._iv is null ? 0 : -1;
        if (b._iv is null) return 1;
        return a.TotalMonths.CompareTo(b.TotalMonths);
    }

    // Like SQL, two NULLs are not equal (this differs from CompareTo, which treats two NULLs as equal).
    public static bool Equals(IfxMonthSpan a, IfxMonthSpan b) => a._iv is not null && Compare(a, b) == 0;
    public static bool NotEquals(IfxMonthSpan a, IfxMonthSpan b) => !Equals(a, b);
    public static bool GreaterThan(IfxMonthSpan a, IfxMonthSpan b) => a._iv is not null && b._iv is not null && a.TotalMonths > b.TotalMonths;
    public static bool GreaterThanOrEqual(IfxMonthSpan a, IfxMonthSpan b) => a._iv is not null && b._iv is not null && a.TotalMonths >= b.TotalMonths;
    public static bool LessThan(IfxMonthSpan a, IfxMonthSpan b) => a._iv is not null && b._iv is not null && a.TotalMonths < b.TotalMonths;
    public static bool LessThanOrEqual(IfxMonthSpan a, IfxMonthSpan b) => a._iv is not null && b._iv is not null && a.TotalMonths <= b.TotalMonths;

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        IfxMonthSpan other => CompareTo(other),
        _ => throw new ArgumentException("Object is not an IfxMonthSpan.", nameof(obj)),
    };

    /// <summary>Typed <see cref="IComparable{T}"/> comparison; SQL NULL sorts first.</summary>
    public int CompareTo(IfxMonthSpan other) => Compare(this, other);

    public override bool Equals(object? obj) => obj is IfxMonthSpan other && Equals(other);

    /// <summary>Typed <see cref="IEquatable{T}"/> equality. SQL NULL is never equal to
    /// anything, including another SQL NULL (mirrors <see cref="Equals(object?)"/>).</summary>
    public bool Equals(IfxMonthSpan other) => Equals(this, other);

    public override int GetHashCode() => _iv is null ? 0 : TotalMonths.GetHashCode();

    public static IfxMonthSpan Parse(string s) => Parse(s, IfxTimeUnit.Year, IfxTimeUnit.Month);

    public static IfxMonthSpan Parse(string s, IfxTimeUnit start, IfxTimeUnit end)
    {
        ArgumentNullException.ThrowIfNull(s);
        short qual = IfxQualifier.ForInterval(9, start, end);
        return new IfxMonthSpan(new IfxIntervalYearMonth(s, qual));
    }

    public static IfxMonthSpan Parse(string s, string format, IfxTimeUnit start, IfxTimeUnit end) => Parse(s, start, end);

    public override string ToString() => _iv?.ToString() ?? "NULL";

    public string ToString(string? format) => ToString();
}
