using System.Text;
using Informix.Data.Types;

namespace Informix.Data.Text;

/// <summary>
/// DATETIME and INTERVAL as text, in both directions (<c>dtcvasc</c>, <c>dttoasc</c>,
/// <c>incvasc</c> and <c>intoasc</c>), over the field values a qualifier selects.
/// </summary>
/// <remarks>
/// Both types are written in the ANSI SQL form: the fields the qualifier names, in order,
/// separated by <c>- - : : .</c> and a blank between DAY and HOUR (the C library's
/// <c>dtdelim</c> table). The leading field is as wide as the qualifier's precision, every
/// other whole field is two digits and the fraction is as wide as its precision.
/// </remarks>
internal static class DateTimeText
{
    /// <summary>The delimiter that precedes the field ending at unit <c>u</c>, indexed by <c>u / 2 - 1</c>.</summary>
    private static readonly char[] Delimiters = ['-', '-', ' ', ':', ':', '.'];

    /// <summary>The delimiter written in front of the field ending at <paramref name="unit"/>.</summary>
    private static char DelimiterBefore(int unit) => Delimiters[unit >= 11 ? 5 : unit / 2 - 1];

    /// <summary>Field slots: 0 year, 1 month, 2 day, 3 hour, 4 minute, 5 second, 6 fraction.</summary>
    public const int FractionSlot = 6;

    public static int SlotOf(int unit) => unit >= 11 ? FractionSlot : unit / 2;

    /// <summary>Digits in the leading field (<c>TU_FLEN</c>).</summary>
    public static int LeadingWidth(short qualifier) =>
        IfxQualifier.GetLength(qualifier) - (IfxQualifier.GetEndCode(qualifier) - IfxQualifier.GetStartCode(qualifier));

    /// <summary>Digits in the fractional-seconds field, 0 when the value stops at SECOND or coarser.</summary>
    public static int FractionDigits(short qualifier)
    {
        byte end = IfxQualifier.GetEndCode(qualifier);
        return end > 10 ? end - 10 : 0;
    }

    /// <summary>The units the qualifier covers, from the leading field to the last.</summary>
    private static List<int> Units(short qualifier)
    {
        byte start = IfxQualifier.GetStartCode(qualifier);
        byte end = IfxQualifier.GetEndCode(qualifier);
        var units = new List<int>();
        for (int u = start; u <= Math.Min((int)end, 10); u += 2)
            units.Add(u);
        if (end > 10)
            units.Add(end);
        if (units.Count == 0)
            units.Add(end);
        return units;
    }

    // ===================================================================== //
    //  dtcvasc / incvasc                                                    //
    // ===================================================================== //

    /// <summary>
    /// Parses an ANSI SQL DATETIME or INTERVAL string into the fields the qualifier names.
    /// A blank string yields <paramref name="isNull"/>.
    /// </summary>
    /// <param name="isDateTime">True for <c>dtcvasc</c> (calendar ranges and <c>DBCENTURY</c>),
    /// false for <c>incvasc</c> (a duration, whose leading field is unbounded).</param>
    /// <returns>0, or the C error code (-1261, -1262, -1263, -1264).</returns>
    public static int TryParse(
        ReadOnlySpan<char> s,
        short qualifier,
        bool isDateTime,
        IfxTextFormat format,
        out bool isNull,
        out int[] fields)
    {
        fields = new int[7];
        isNull = false;

        var text = s.Trim(' ');
        if (text.Length == 0)
        {
            isNull = true;
            return ConversionError.Ok;
        }

        var units = Units(qualifier);
        int leadingWidth = LeadingWidth(qualifier);
        int fractionDigits = FractionDigits(qualifier);
        int i = 0;

        for (int f = 0; f < units.Count; f++)
        {
            int unit = units[f];
            if (f > 0)
            {
                char delimiter = DelimiterBefore(unit);
                int before = i;
                while (i < text.Length && text[i] == ' ')
                    i++;
                if (delimiter == ' ')
                {
                    if (i == before)
                        return ConversionError.NonNumericInDateTime;
                }
                else
                {
                    if (i >= text.Length || text[i] != delimiter)
                        return ConversionError.NonNumericInDateTime;
                    i++;
                }
            }

            int width = f == 0 ? leadingWidth : unit >= 11 ? fractionDigits : 2;
            int start = i;
            int value = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]) && i - start < width)
                value = value * 10 + (text[i++] - '0');

            int digits = i - start;
            if (digits == 0)
                return ConversionError.NonNumericInDateTime;
            if (f == 0 && i < text.Length && char.IsAsciiDigit(text[i]))
                return ConversionError.TooManyDigitsInFirstField;

            if (unit >= 11)
            {
                // A short fraction is padded on the right: ".5" with FRACTION(5) is 50000.
                for (int k = digits; k < fractionDigits; k++)
                    value *= 10;
                fields[FractionSlot] = value;
            }
            else
            {
                fields[SlotOf(unit)] = value;
                if (unit == 0 && isDateTime && digits <= 2)
                {
                    int rc = DateText.ExpandYear(value, digits, fields[1], fields[2], format, out int expanded);
                    if (rc != ConversionError.Ok)
                        return rc;
                    fields[0] = expanded;
                }
            }
        }

        while (i < text.Length && text[i] == ' ')
            i++;
        if (i != text.Length)
            return ConversionError.ExtraCharacters;

        return isDateTime ? ValidateDateTime(qualifier, fields) : ValidateInterval(qualifier, fields);
    }

    private static int ValidateDateTime(short qualifier, int[] fields)
    {
        byte start = IfxQualifier.GetStartCode(qualifier);
        byte end = IfxQualifier.GetEndCode(qualifier);
        bool Has(int unit) => unit >= start && unit <= end;

        if (Has(0) && fields[0] is < 1 or > 9999)
            return ConversionError.FieldOutOfRange;
        if (Has(2) && fields[1] is < 1 or > 12)
            return ConversionError.FieldOutOfRange;
        if (Has(4))
        {
            int year = Has(0) ? fields[0] : 2000;           // a leap-safe stand-in when the year is absent
            int month = Has(2) ? fields[1] : 1;
            if (month is < 1 or > 12)
                return ConversionError.FieldOutOfRange;
            if (fields[2] < 1 || fields[2] > DateTime.DaysInMonth(year, month))
                return ConversionError.FieldOutOfRange;
        }
        return ValidateTimeFields(qualifier, fields);
    }

    private static int ValidateInterval(short qualifier, int[] fields)
    {
        byte start = IfxQualifier.GetStartCode(qualifier);
        byte end = IfxQualifier.GetEndCode(qualifier);

        // Only the leading field of an interval is unbounded; the rest carry over normally.
        if (start < 2 && end >= 2 && fields[1] > 11)
            return ConversionError.FieldOutOfRange;
        return ValidateTimeFields(qualifier, fields);
    }

    private static int ValidateTimeFields(short qualifier, int[] fields)
    {
        byte start = IfxQualifier.GetStartCode(qualifier);
        byte end = IfxQualifier.GetEndCode(qualifier);
        bool Inner(int unit) => unit > start && unit <= end;

        if (Inner(6) && fields[3] > 23)
            return ConversionError.FieldOutOfRange;
        if (Inner(8) && fields[4] > 59)
            return ConversionError.FieldOutOfRange;
        if (Inner(10) && fields[5] > 59)
            return ConversionError.FieldOutOfRange;
        return ConversionError.Ok;
    }

    // ===================================================================== //
    //  dttoasc / intoasc                                                    //
    // ===================================================================== //

    /// <summary>Writes the fields in the ANSI SQL form <c>dttoasc()</c> produces.</summary>
    public static string Format(short qualifier, int[] fields)
    {
        var units = Units(qualifier);
        int leadingWidth = LeadingWidth(qualifier);
        int fractionDigits = FractionDigits(qualifier);
        var sb = new StringBuilder();

        for (int f = 0; f < units.Count; f++)
        {
            int unit = units[f];
            if (f > 0)
                sb.Append(DelimiterBefore(unit));
            int width = f == 0 ? leadingWidth : unit >= 11 ? fractionDigits : 2;
            int value = unit >= 11 ? fields[FractionSlot] : fields[SlotOf(unit)];
            sb.Append(value.ToString($"D{Math.Max(width, 1)}"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes an interval the way <c>intoasc()</c> does: one leading blank, the leading
    /// field's zeros blanked out and the sign, when negative, taking the blank in front of
    /// the first significant digit.
    /// </summary>
    public static string FormatInterval(short qualifier, int[] fields, bool negative)
    {
        var chars = new StringBuilder(" ").Append(Format(qualifier, fields)).ToString().ToCharArray();

        int p = 1;
        while (p + 1 < chars.Length && chars[p] == '0' && char.IsAsciiDigit(chars[p + 1]))
            chars[p++] = ' ';
        if (negative)
            chars[p - 1] = '-';
        return new string(chars);
    }
}
