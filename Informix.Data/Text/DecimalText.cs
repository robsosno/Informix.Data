using System.Globalization;
using System.Text;

namespace Informix.Data.Text;

/// <summary>
/// DECIMAL as text, over the client library's own <c>dec_t</c> (<c>struct decimal</c>)
/// representation: <c>deccvasc</c> (<see cref="TryParse"/>) and <c>dectoasc</c>
/// (<see cref="Format"/>).
/// </summary>
/// <remarks>
/// The representation is the C one: a sign (<see cref="Pos"/>: 1 positive, 0 negative,
/// -1 NULL), an exponent counted in base-100 digit positions and up to
/// <see cref="DecSize"/> centesimal digits, so a value is <c>0.d1d2... x 100^exp</c>.
/// It is deliberately independent of <see cref="Informix.Data.Types.PackedValue"/>:
/// the rounding, truncation and default-scale
/// rules replicated here are the C library's, not the Java driver's.
/// </remarks>
internal struct DecimalText
{
    public const int DecSize = 16;          // DECSIZE
    private const short PosNull = -1;       // DECPOSNULL
    private const short PosPositive = 1;
    private const short PosNegative = 0;
    private const short ZeroExponent = -64; // the exponent the C library leaves on a zero

    // value = 0.d1d2d3... x 100^Exp, one digit pair per byte of Dgts; Ndgts counts the pairs in use.
    public short Exp;
    public short Pos;
    public short Ndgts;
    public byte[] Dgts;

    public static DecimalText Null => new() { Exp = 0, Pos = PosNull, Ndgts = 0, Dgts = new byte[DecSize] };

    public static DecimalText Zero => new() { Exp = ZeroExponent, Pos = PosPositive, Ndgts = 0, Dgts = new byte[DecSize] };

    public readonly bool IsNull => Pos == PosNull;

    public readonly bool IsNegative => Pos == PosNegative;

    // ===================================================================== //
    //  deccvasc                                                             //
    // ===================================================================== //

    /// <summary>
    /// Converts printable characters into a decimal, as <c>deccvasc()</c> does: leading
    /// blanks are skipped, an empty or all-blank string yields SQL NULL, an optional sign
    /// may precede either a <c>0x</c> hexadecimal integer or a decimal number with optional
    /// thousands separators, a decimal separator and an <c>e</c>/<c>E</c> exponent, and
    /// blanks (or a newline) may follow.
    /// </summary>
    /// <returns>0, or the C error code (-1200, -1201, -1213).</returns>
    public static int TryParse(ReadOnlySpan<char> s, IfxTextFormat format, out DecimalText value)
    {
        value = Null;
        int rc = TryScan(s, format, out NumberScan scan);
        if (rc != ConversionError.Ok)
            return rc;
        if (scan.IsNull)
            return ConversionError.Ok;
        if (scan.IsHex)
        {
            value = FromInt64(scan.Negative ? -scan.HexValue : scan.HexValue);
            return ConversionError.Ok;
        }
        return Build(scan.Digits, scan.Exp10, scan.Negative, out value);
    }

    /// <summary>
    /// The <c>deccvasc</c> scan, stopping at the digit string and its exponent — shared with
    /// <see cref="IntegerText.TryParseBigInt"/>, which needs the raw digits rather than the
    /// packed value.
    /// </summary>
    public static int TryScan(ReadOnlySpan<char> s, IfxTextFormat format, out NumberScan scan)
    {
        scan = new NumberScan { IsNull = true, Digits = string.Empty };
        int i = 0;
        while (i < s.Length && s[i] == ' ')
            i++;
        if (i == s.Length)
            return ConversionError.Ok;                    // blank string -> NULL, like the C library

        bool negative = false;
        if (s[i] == '-')
        {
            negative = true;
            i++;
        }
        else if (s[i] == '+')
        {
            i++;
        }

        scan.IsNull = false;
        scan.Negative = negative;

        // 0x... / 0X... is taken as a hexadecimal integer.
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            int hexRc = ParseHex(s[(i + 2)..], out long hex);
            scan.IsHex = true;
            scan.HexValue = hex;
            return hexRc;
        }

        char point = format.DecimalSeparator;
        char thousands = format.ThousandsSeparator;

        Span<char> digits = stackalloc char[DecSize * 2];
        int ndigits = 0;                            // significant digits collected (the C library caps them)
        int exp10 = 0;                              // digits before the decimal separator
        int points = 0;                             // decimal separators seen
        bool groupingChecked = !format.StrictThousandsSeparator;
        int numberStart = i;

        while (i < s.Length)
        {
            char c = s[i];
            if (c == point)
            {
                if (points++ != 0)
                    return ConversionError.NonNumeric;
                i++;
                continue;
            }

            if (c == thousands)
            {
                if (thousands != ' ' && points != 0)
                    return ConversionError.NonNumeric;    // a separator may not follow the decimal point
                if (!groupingChecked)
                {
                    groupingChecked = true;
                    if (!GroupingIsValid(s[numberStart..], thousands))
                        return ConversionError.NonNumeric;
                }
                i++;
                continue;
            }

            if (!char.IsAsciiDigit(c))
                break;

            if (c == '0' && ndigits == 0)
            {
                // Leading zeros carry no significance; after the point they push the exponent down.
                if (points != 0)
                    exp10--;
            }
            else
            {
                // Digits past the mantissa buffer are dropped but still count towards the
                // magnitude, which is how the C library truncates over-long numbers.
                if (ndigits < digits.Length)
                    digits[ndigits++] = c;
                if (points == 0)
                    exp10++;
            }
            i++;
        }

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            exp10 += ParseExponent(s, ref i);
        }

        while (i < s.Length && s[i] == ' ')
            i++;
        if (i < s.Length && s[i] != '\n')
            return ConversionError.NonNumeric;

        scan.Digits = new string(digits[..ndigits]);
        scan.Exp10 = exp10;
        return ConversionError.Ok;
    }

    // "1,234,567" is valid, "1,23,4" and "12,34.5" are not. The C library validates the whole
    // number once, when it meets the first separator, unless IFX_NOT_STRICT_THOUS_SEP=1.
    private static bool GroupingIsValid(ReadOnlySpan<char> s, char thousands)
    {
        int i = 0, group = 0;
        bool afterSeparator = false;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            group++;
            i++;
        }
        if (group is 0 or > 3)
            return false;
        while (i < s.Length && s[i] == thousands)
        {
            afterSeparator = true;
            i++;
            group = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                group++;
                i++;
            }
            if (group != 3)
                return false;
        }
        return afterSeparator;
    }

    private static int ParseExponent(ReadOnlySpan<char> s, ref int i)
    {
        bool negative = i < s.Length && s[i] == '-';
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            i++;
        int value = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            value = value * 10 + (s[i++] - '0');
        return negative ? -value : value;
    }

    private static int ParseHex(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        long acc = 0;
        int i = 0;
        for (; i < s.Length && char.IsAsciiHexDigit(s[i]); i++)
            acc = acc * 16 + HexValue(s[i]);
        int digits = i;
        while (i < s.Length && s[i] == ' ')
            i++;
        if (digits == 0 || (i < s.Length && s[i] != '\n'))
            return ConversionError.NonNumeric;
        value = acc;
        return ConversionError.Ok;
    }

    private static int HexValue(char c) =>
        char.IsAsciiDigit(c) ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

    /// <summary>Packs a decimal digit string and its exponent into the base-100 representation.</summary>
    private static int Build(ReadOnlySpan<char> digits, int exp10, bool negative, out DecimalText value)
    {
        value = Zero;
        if (digits.Length == 0)
            return ConversionError.Ok;                    // "0" and "-0" alike: positive zero

        int exp100 = ((exp10 + 1) & ~1) / 2;
        if (exp100 > 63)
            return ConversionError.DecimalOverflow;
        if (exp100 < -64)
            return ConversionError.DecimalUnderflow;

        var dgts = new byte[DecSize];
        int n = 0, d = 0;
        if ((exp10 & 1) != 0)                       // odd exponent: the first cent holds one digit
            dgts[n++] = (byte)(digits[d++] - '0');
        while (d < digits.Length && n < DecSize)
        {
            int hi = digits[d++] - '0';
            int lo = d < digits.Length ? digits[d++] - '0' : 0;
            dgts[n++] = (byte)(hi * 10 + lo);
        }
        while (n > 0 && dgts[n - 1] == 0)
            n--;

        value = new DecimalText
        {
            Exp = (short)exp100,
            Pos = negative ? PosNegative : PosPositive,
            Ndgts = (short)n,
            Dgts = dgts,
        };
        if (n == 0)
            value = Zero;
        return ConversionError.Ok;
    }

    public static DecimalText FromInt64(long v) =>
        FromPlainString(v.ToString(CultureInfo.InvariantCulture));

    public static DecimalText FromDecimal(decimal v) =>
        FromPlainString(v.ToString(CultureInfo.InvariantCulture));

    private static DecimalText FromPlainString(string plain)
    {
        // Always a well-formed "[-]digits[.digits]" string, so the parse cannot fail.
        TryParse(plain, IfxTextFormat.Default, out var value);
        return value;
    }

    /// <summary>The value as a CLR <see cref="decimal"/>; throws <see cref="OverflowException"/> beyond its range.</summary>
    public readonly decimal ToDecimal() =>
        decimal.Parse(ToPlainString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The exact value as a plain (never exponential) invariant string.</summary>
    public readonly string ToPlainString()
    {
        if (IsNull || Ndgts == 0)
            return "0";
        char[] digits = SignificantDigits(out int lead);
        var sb = new StringBuilder();
        if (IsNegative)
            sb.Append('-');
        if (lead <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -lead);
            sb.Append(digits);
        }
        else if (lead >= digits.Length)
        {
            sb.Append(digits);
            sb.Append('0', lead - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, lead);
            sb.Append('.');
            sb.Append(digits, lead, digits.Length - lead);
        }
        return sb.ToString();
    }

    // ===================================================================== //
    //  dectoasc                                                             //
    // ===================================================================== //

    /// <summary>
    /// Writes the value into <paramref name="destination"/> as <c>dectoasc()</c> fills its
    /// buffer: left-justified and blank-padded (never NUL-terminated), falling back to
    /// exponential notation when the fixed-point form does not fit, and filling the buffer
    /// with asterisks when even that fails.
    /// </summary>
    /// <param name="right">Digits after the decimal separator; -1 derives it from the value.</param>
    /// <returns>0, or -1 when the value does not fit.</returns>
    public readonly int Format(Span<char> destination, int right, IfxTextFormat format)
    {
        if (IsNull)
        {
            destination.Fill(' ');
            return ConversionError.Ok;
        }

        if (right < 0)
            right = DefaultScale();

        while (true)
        {
            destination.Fill(' ');
            char[] digits = RoundedDigits(right, out int lead);
            int sign = IsNegative ? 1 : 0;
            int total = sign + (right > 0 ? 1 : 0) + 1 + right + (lead > 0 ? lead - 1 : 0);

            if (total > destination.Length)
            {
                if (lead > -4 && lead > -right)
                {
                    int excess = total - destination.Length;
                    if (right >= excess)
                    {
                        right -= excess;
                        continue;
                    }
                    if (right == total - 1 - destination.Length && right != 0)
                    {
                        right = 0;
                        continue;
                    }
                }
                if (FormatExponential(destination, format))
                    return ConversionError.Ok;
                destination.Fill('*');
                return -1;
            }

            int p = 0, d = 0;
            if (sign != 0)
                destination[p++] = '-';
            if (lead > 0)
            {
                for (int k = 0; k < lead; k++)
                    destination[p++] = DigitAt(digits, d++);
            }
            else
            {
                if (sign != 0 && IsAllZero(digits, lead + right))
                    p--;                            // a value that rounds to zero loses its sign
                destination[p++] = '0';
            }

            if (right > 0)
            {
                destination[p++] = format.DecimalSeparator;
                for (int k = 0; k < right; k++)
                    destination[p++] = lead < 0 && k < -lead ? '0' : DigitAt(digits, d++);
            }
            return ConversionError.Ok;
        }
    }

    private static char DigitAt(char[] digits, int index) =>
        index >= 0 && index < digits.Length ? digits[index] : '0';

    private static bool IsAllZero(char[] digits, int count)
    {
        for (int i = 0; i < count && i < digits.Length; i++)
        {
            if (digits[i] != '0')
                return false;
        }
        return true;
    }

    /// <summary>The scale <c>dectoasc()</c> derives when <c>right</c> is -1.</summary>
    private readonly int DefaultScale()
    {
        if (Ndgts == 0)
            return 1;
        int right = (Ndgts - Exp) * 2;
        if (Dgts[Ndgts - 1] % 10 == 0)
            right--;
        return right <= 0 ? 1 : right;
    }

    private readonly bool FormatExponential(Span<char> destination, IfxTextFormat format)
    {
        SignificantDigits(out int lead);
        int need = 4 + (IsNegative ? 1 : 0);
        int e = lead - 1;
        if (e < 0)
            need++;
        if (e is < (-99) or > 99)
            need++;
        if (need >= destination.Length)
            return false;

        char[] digits = SignificantDigits(destination.Length - need, out int roundedLead);
        int last = digits.Length;
        while (last > 1 && digits[last - 1] == '0')
            last--;

        int p = 0;
        if (IsNegative)
            destination[p++] = '-';
        destination[p++] = digits[0];
        if (last > 1)
        {
            destination[p++] = format.DecimalSeparator;
            for (int i = 1; i < last; i++)
                destination[p++] = digits[i];
        }
        destination[p++] = 'e';

        int exp = roundedLead - 1;
        if (exp < 0)
        {
            destination[p++] = '-';
            exp = -exp;
        }
        if (exp >= 100)
        {
            destination[p++] = (char)('0' + exp / 100);
            exp %= 100;
        }
        destination[p++] = (char)('0' + exp / 10);
        destination[p] = (char)('0' + exp % 10);
        return true;
    }

    // ===================================================================== //
    //  digit generation                                                     //
    // ===================================================================== //

    /// <summary>All significant digits of |value|, with <paramref name="lead"/> integer digits.</summary>
    private readonly char[] SignificantDigits(out int lead)
    {
        var all = new char[Ndgts * 2];
        for (int k = 0; k < Ndgts; k++)
        {
            all[2 * k] = (char)('0' + Dgts[k] / 10);
            all[2 * k + 1] = (char)('0' + Dgts[k] % 10);
        }
        int e = Exp * 2;
        int start = 0;
        while (start < all.Length && all[start] == '0')
        {
            start++;
            e--;
        }
        int end = all.Length;
        while (end > start && all[end - 1] == '0')
            end--;
        lead = e;
        if (start == end)
        {
            lead = 0;
            return ['0'];
        }
        return all[start..end];
    }

    /// <summary>|value| rounded to <paramref name="count"/> significant digits (half-up, as in the C library).</summary>
    private readonly char[] SignificantDigits(int count, out int lead)
    {
        char[] all = SignificantDigits(out lead);
        return Round(all, count, ref lead);
    }

    /// <summary>|value| rounded to <paramref name="right"/> fractional digits; the array holds <c>lead + right</c> digits.</summary>
    private readonly char[] RoundedDigits(int right, out int lead)
    {
        if (Ndgts == 0)
        {
            lead = 0;
            return new string('0', Math.Max(right, 1)).ToCharArray();
        }
        char[] all = SignificantDigits(out lead);
        return Round(all, lead + right, ref lead);
    }

    private static char[] Round(char[] all, int count, ref int lead)
    {
        if (count <= 0)
        {
            // Everything is dropped; a leading digit of 5 or more still rounds up to "1".
            // `lead` keeps the value's own exponent — the caller needs it to decide whether
            // the fixed-point form fits at all.
            if (count < 0 || all.Length == 0 || all[0] < '5')
                return ['0'];
            lead++;
            return ['1'];
        }

        var kept = new char[count];
        for (int i = 0; i < count; i++)
            kept[i] = i < all.Length ? all[i] : '0';

        if (count < all.Length && all[count] >= '5')
        {
            int i = count - 1;
            for (; i >= 0; i--)
            {
                if (kept[i] != '9')
                {
                    kept[i]++;
                    break;
                }
                kept[i] = '0';
            }
            if (i < 0)
            {
                // The carry ran off the front: 9.99 -> 10.0, one more integer digit.
                var grown = new char[count + 1];
                grown[0] = '1';
                Array.Copy(kept, 0, grown, 1, count);
                lead++;
                return grown;
            }
        }
        return kept;
    }
}

/// <summary>
/// What <see cref="DecimalText.TryScan"/> read out of a number written as text: the mantissa
/// digits with leading zeros stripped, the count of digits before the decimal separator
/// (so the value is <c>0.Digits x 10^Exp10</c>), and the <c>0x</c> form kept apart.
/// </summary>
internal struct NumberScan
{
    public bool IsNull;
    public bool Negative;
    public string Digits;
    public int Exp10;
    public bool IsHex;
    public long HexValue;
}
