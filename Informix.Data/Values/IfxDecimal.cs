using System.Data.SqlTypes;
using System.Globalization;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// A nullable Informix DECIMAL/MONEY value with the arithmetic surface of the
/// official Informix.Net.Core <c>IfxDecimal</c> (modelled on
/// <see cref="System.Data.SqlTypes.SqlDecimal"/>).
/// </summary>
/// <remarks>The value is backed by the CLR <see cref="decimal"/> (28–29 significant
/// digits). Informix DECIMAL carries up to <see cref="MaxPrecision"/> (32) digits;
/// values wider than <see cref="decimal"/> can round-trip on the wire but converting
/// them into an <see cref="IfxDecimal"/> throws <see cref="OverflowException"/> — the
/// precision limit is documented rather than silently faked.</remarks>
public readonly struct IfxDecimal : INullable, IComparable, IEquatable<IfxDecimal>, IComparable<IfxDecimal>
{
    private readonly decimal _value;
    private readonly bool _notNull;

    public IfxDecimal(int value) { _value = value; _notNull = true; }
    public IfxDecimal(long value) { _value = value; _notNull = true; }
    public IfxDecimal(decimal value) { _value = value; _notNull = true; }
    public IfxDecimal(double value) { _value = (decimal)value; _notNull = true; }

    private IfxDecimal(decimal value, bool notNull) { _value = value; _notNull = notNull; }

    /// <summary>The maximum number of significant digits an Informix DECIMAL carries.</summary>
    public static readonly byte MaxPrecision = 32;

    // default(IfxDecimal) is NULL.
    public static readonly IfxDecimal Null = default;
    public static readonly IfxDecimal Zero = new(0m);
    public static readonly IfxDecimal One = new(1m);
    public static readonly IfxDecimal MinusOne = new(-1m);
    public static readonly IfxDecimal Pi = new(3.1415926535897932384626433833m);
    public static readonly IfxDecimal E = new(2.7182818284590452353602874714m);
    public static readonly IfxDecimal MinValue = new(decimal.MinValue);
    public static readonly IfxDecimal MaxValue = new(decimal.MaxValue);

    /// <summary>True for the SQL NULL value.</summary>
    public bool IsNull => !_notNull;

    /// <summary>True when the value is strictly greater than zero.</summary>
    public bool IsPositive => _notNull && _value > 0m;

    /// <summary>False — an Informix DECIMAL is a fixed-point value, not a binary float.</summary>
    public bool IsFloating => false;

    /// <summary>The value as a CLR <see cref="decimal"/>; throws for SQL NULL.</summary>
    public decimal Value => _notNull ? _value : throw new InvalidOperationException("The IfxDecimal value is NULL.");

    internal static IfxDecimal FromNullableDecimal(decimal? value) =>
        value is { } d ? new IfxDecimal(d) : Null;

    public static explicit operator decimal(IfxDecimal value) => value.Value;
    public static explicit operator double(IfxDecimal value) => (double)value.Value;
    public static implicit operator IfxDecimal(decimal value) => new(value);

    // ---- arithmetic (SQL semantics: NULL propagates) ----
    private static IfxDecimal Op(IfxDecimal a, IfxDecimal b, Func<decimal, decimal, decimal> f) =>
        a._notNull && b._notNull ? new IfxDecimal(f(a._value, b._value)) : Null;

    public static IfxDecimal Add(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => x + y);
    public static IfxDecimal Subtract(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => x - y);
    public static IfxDecimal Multiply(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => x * y);
    public static IfxDecimal Divide(IfxDecimal dividend, IfxDecimal divisor) => Op(dividend, divisor, static (x, y) => x / y);
    public static IfxDecimal Modulo(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => x % y);
    public static IfxDecimal Remainder(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => x % y);
    public static IfxDecimal Min(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => Math.Min(x, y));
    public static IfxDecimal Max(IfxDecimal a, IfxDecimal b) => Op(a, b, static (x, y) => Math.Max(x, y));

    public static IfxDecimal Abs(IfxDecimal value) => value._notNull ? new IfxDecimal(Math.Abs(value._value)) : Null;
    public static IfxDecimal Negate(IfxDecimal value) => value._notNull ? new IfxDecimal(-value._value) : Null;
    public static IfxDecimal Floor(IfxDecimal value) => value._notNull ? new IfxDecimal(Math.Floor(value._value)) : Null;
    public static IfxDecimal Ceiling(IfxDecimal value) => value._notNull ? new IfxDecimal(Math.Ceiling(value._value)) : Null;
    public static IfxDecimal Round(IfxDecimal value, int fractionDigits) =>
        value._notNull ? new IfxDecimal(Math.Round(value._value, fractionDigits, MidpointRounding.AwayFromZero)) : Null;

    public static IfxDecimal Truncate(IfxDecimal value, int fractionDigits)
    {
        if (!value._notNull)
            return Null;
        decimal scale = Pow10(fractionDigits);
        return new IfxDecimal(Math.Truncate(value._value * scale) / scale);
    }

    private static decimal Pow10(int n)
    {
        decimal r = 1m;
        for (int i = 0; i < n; i++) r *= 10m;
        for (int i = 0; i > n; i--) r /= 10m;
        return r;
    }

    // ---- comparison ----
    public static int Compare(IfxDecimal a, IfxDecimal b)
    {
        if (!a._notNull) return b._notNull ? -1 : 0;
        if (!b._notNull) return 1;
        return a._value.CompareTo(b._value);
    }

    public static bool Equals(IfxDecimal a, IfxDecimal b) => Compare(a, b) == 0 && a._notNull;
    public static bool NotEquals(IfxDecimal a, IfxDecimal b) => !Equals(a, b);
    public static bool GreaterThan(IfxDecimal a, IfxDecimal b) => a._notNull && b._notNull && a._value > b._value;
    public static bool GreaterThanOrEqual(IfxDecimal a, IfxDecimal b) => a._notNull && b._notNull && a._value >= b._value;
    public static bool LessThan(IfxDecimal a, IfxDecimal b) => a._notNull && b._notNull && a._value < b._value;
    public static bool LessThanOrEqual(IfxDecimal a, IfxDecimal b) => a._notNull && b._notNull && a._value <= b._value;

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        IfxDecimal other => CompareTo(other),
        _ => throw new ArgumentException("Object is not an IfxDecimal.", nameof(obj)),
    };

    /// <summary>Typed <see cref="IComparable{T}"/> comparison; SQL NULL sorts first (matches <see cref="SqlDecimal"/>).</summary>
    public int CompareTo(IfxDecimal other) => Compare(this, other);

    public override bool Equals(object? obj) => obj is IfxDecimal other && Equals(other);

    /// <summary>Typed <see cref="IEquatable{T}"/> equality. SQL NULL is never equal to
    /// anything, including another SQL NULL (mirrors <see cref="Equals(object?)"/>).</summary>
    public bool Equals(IfxDecimal other) => Equals(this, other);

    public override int GetHashCode() => _notNull ? _value.GetHashCode() : 0;

    /// <summary>Parses a plain decimal string using the invariant culture.</summary>
    public static IfxDecimal Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new IfxDecimal(decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture));
    }

    /// <summary>Returns a copy of this value.</summary>
    public IfxDecimal Clone() => this;

    public override string ToString() => _notNull ? _value.ToString(CultureInfo.InvariantCulture) : "NULL";

    public string ToString(string? format) =>
        _notNull ? _value.ToString(format, CultureInfo.InvariantCulture) : "NULL";
}
