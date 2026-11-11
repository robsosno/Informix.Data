namespace Informix.Data;

/// <summary>
/// Informix DATETIME/INTERVAL time-unit codes (the <c>TU_*</c> family), used to
/// name the start/end field of a qualifier on <see cref="IfxDateTime"/>,
/// <see cref="IfxTimeSpan"/> and <see cref="IfxMonthSpan"/>.
/// </summary>
/// <remarks>The numeric values are the wire-level qualifier field codes; the
/// fractional-second precisions <see cref="Fraction1"/>..<see cref="Fraction5"/>
/// select 1–5 digits, and <see cref="Fraction"/> is the generic (3-digit
/// default) fraction. Matches the official Informix.Net.Core <c>IfxTimeUnit</c>.</remarks>
public enum IfxTimeUnit : byte
{
    Year = 0,
    Month = 2,
    Day = 4,
    Hour = 6,
    Minute = 8,
    Second = 10,
    Fraction1 = 11,
    Fraction2 = 12,
    Fraction3 = 13,
    Fraction4 = 14,
    Fraction5 = 15,
    /// <summary>The generic FRACTION unit (defaults to 3 digits).</summary>
    Fraction = 16,
}
