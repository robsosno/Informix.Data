using System.Globalization;

namespace Informix.Data.Text;

/// <summary>
/// The integer types as text: <c>rstoi</c>, <c>rstol</c>, <c>bigintcvasc</c> /
/// <c>ifx_int8cvasc</c> and <c>biginttoasc</c> / <c>ifx_int8toasc</c>.
/// </summary>
internal static class IntegerText
{
    /// <summary>
    /// <c>rstoi</c>: leading blanks, an optional sign and digits, ending at the string end,
    /// a blank or a newline. The accepted range is a SMALLINT's, as in the C library, whose
    /// overflow check stops at 32767 in both directions.
    /// </summary>
    public static int TryParseSmallRange(ReadOnlySpan<char> s, out int? value)
    {
        int rc = TryParseBounded(s, 3276, out long parsed, ConversionError.IntTooLarge, out bool isNull);
        value = rc == ConversionError.Ok && !isNull ? (int)parsed : null;
        return rc;
    }

    /// <summary><c>rstol</c>: the same scan with an INT range.</summary>
    public static int TryParseLongRange(ReadOnlySpan<char> s, out long? value)
    {
        int rc = TryParseBounded(s, 214748364, out long parsed, ConversionError.LongTooLarge, out bool isNull);
        value = rc == ConversionError.Ok && !isNull ? parsed : null;
        return rc;
    }

    /// <summary>
    /// The shared <c>rstoi</c>/<c>rstol</c> scan. <paramref name="limit"/> is the accumulator
    /// value past which another digit overflows (the C library's <c>32767 / 10</c> test), so
    /// the bound is symmetric and the most negative value of the target type is rejected too.
    /// </summary>
    private static int TryParseBounded(ReadOnlySpan<char> s, long limit, out long value, int overflowCode, out bool isNull)
    {
        value = 0;
        isNull = false;
        int i = 0;
        while (i < s.Length && s[i] == ' ')
            i++;
        if (i == s.Length)
        {
            isNull = true;                          // the C library stores its "null" sentinel here
            return ConversionError.Ok;
        }

        bool negative = s[i] == '-';
        if (s[i] is '-' or '+')
            i++;
        if (i >= s.Length || !char.IsAsciiDigit(s[i]))
            return ConversionError.NonNumeric;

        long acc = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            if (acc > limit || (acc == limit && s[i] > '7'))
                return overflowCode;
            acc = acc * 10 + (s[i++] - '0');
        }

        if (i < s.Length && s[i] != ' ' && s[i] != '\n')
            return ConversionError.NonNumeric;

        value = negative ? -acc : acc;
        return ConversionError.Ok;
    }

    /// <summary>
    /// <c>bigintcvasc</c> / <c>ifx_int8cvasc</c>: the number is read like a decimal — sign,
    /// thousands separators, a decimal separator, an <c>e</c> exponent or a <c>0x</c>
    /// hexadecimal integer — and then truncated towards zero.
    /// </summary>
    /// <remarks>
    /// The one place this deliberately does not follow the C library: given an explicit
    /// negative exponent whose shift is wider than the digit string, <c>bigintcvasc</c> drops
    /// the shift instead of applying it and answers <c>"1.5e-13"</c> with 15. That is a bug in
    /// the library — the same value written <c>"0.00000000000015"</c> gives 0, as does
    /// <c>"1.5e-1"</c> — so the exponent is applied here whatever its size.
    /// </remarks>
    /// <returns>0, or -1213 (non-numeric) or -1284 (overflow/underflow).</returns>
    public static int TryParseBigInt(ReadOnlySpan<char> s, IfxTextFormat format, out long? value)
    {
        value = null;
        int rc = DecimalText.TryScan(s, format, out var scan);
        if (rc != ConversionError.Ok)
            return rc;
        if (scan.IsNull)
            return ConversionError.Ok;
        if (scan.IsHex)
        {
            value = scan.Negative ? -scan.HexValue : scan.HexValue;
            return ConversionError.Ok;
        }

        // A zero mantissa still goes through the shift, so "0e31" overflows the way the C
        // library's digit buffer does.
        string digits = scan.Digits.Length == 0 ? "0" : scan.Digits;

        // The digit string with the decimal separator removed, shifted by the exponent:
        // `shift` is that combined move, so a right shift past the last digit leaves zero.
        int shift = scan.Exp10 - digits.Length;

        if (shift < 0)
            digits = digits[..Math.Max(digits.Length + shift, 0)];   // truncation towards zero
        else if (shift > 0)
            digits += new string('0', shift);
        if (digits.Length == 0)
            digits = "0";

        if (digits.Length > 19
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long magnitude))
            return ConversionError.Int8Overflow;

        long parsed = scan.Negative ? -magnitude : magnitude;
        if (parsed == long.MinValue)                             // long.MinValue is the BIGINT null
            return ConversionError.Int8Overflow;

        value = parsed;
        return ConversionError.Ok;
    }

    /// <summary>
    /// <c>biginttoasc</c>: base 10 gives the plain digits, base 16 the C library's
    /// <c>0x</c> plus sixteen two's-complement hexadecimal digits. Any other base is base 10.
    /// </summary>
    public static string FormatBigInt(long value, int numericBase) => numericBase == 16
        ? "0x" + value.ToString("x16", CultureInfo.InvariantCulture)
        : value.ToString(CultureInfo.InvariantCulture);
}
