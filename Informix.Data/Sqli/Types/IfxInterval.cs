using System.Globalization;

namespace Informix.Data.Types;

/// <summary>
/// Base of the two Informix INTERVAL classes — the .NET port of the abstract
/// <c>com.informix.lang.Interval</c>. Holds the qualifier and the shared
/// string parser; <see cref="IfxIntervalDayFraction"/> and
/// <see cref="IfxIntervalYearMonth"/> mirror <c>IntervalDF</c> and
/// <c>IntervalYM</c>.
/// </summary>
internal abstract class IfxInterval
{
    /// <summary>Field delimiters year..fraction (port of <c>dtdelim</c>).</summary>
    private protected static readonly char[] Delimiters = ['-', '-', ' ', ':', ':', '.', ' '];

    /// <summary>The INTERVAL qualifier word (see <see cref="IfxQualifier"/>).</summary>
    public short Qualifier { get; protected set; }

    public byte StartCode => IfxQualifier.GetStartCode(Qualifier);

    public byte EndCode => IfxQualifier.GetEndCode(Qualifier);

    /// <summary>
    /// Parses a qualifier-shaped field string ("dd hh:mm:ss.fffff" /
    /// "yyyy-mm") into calendar units: [0]=year, [1]=month, [2]=day, [3]=hour,
    /// [4]=minute, [5]=second, [6]=nanoseconds. A fraction-only interval (start code 12) must begin with '.'.
    /// The sign must already be stripped. Parse failures are -79721..-79725.
    /// </summary>
    private protected static int[] StringToUnits(string str, short qual)
    {
        var data = new int[7];
        byte endCode = IfxQualifier.GetEndCode(qual);
        byte startCode = IfxQualifier.GetStartCode(qual);
        byte fwidth = IfxQualifier.GetLength(qual);

        str = str.Trim();
        int strIdx = 0;
        if (str.Length == 0)
            return data;

        try
        {
            if (startCode == 12)
            {
                // Pure-fraction values look like ".fffff" or "0.fffff".
                if (str[strIdx] == '0')
                    strIdx++;
                if (str[strIdx] != '.')
                    throw new IfxException("A fraction interval must start with '.'.", -79721);
                strIdx++;
            }
            else
            {
                int numDigits;
                for (numDigits = 0;
                     numDigits < str.Length && strIdx + numDigits < str.Length && char.IsAsciiDigit(str[strIdx + numDigits]);
                     numDigits++)
                { }

                if (numDigits == 0)
                    throw new IfxException("The leading interval field has no digits.", -79721);
                if (numDigits > fwidth)
                    throw new IfxException("The leading interval field has too many digits.", -79721);

                data[startCode / 2] = int.Parse(str.AsSpan(strIdx, numDigits), CultureInfo.InvariantCulture);
                strIdx += numDigits + 1;
                startCode += 2;
            }

            // Middle fields: exactly one or two digits, checked delimiters.
            while (startCode <= endCode && startCode <= 10)
            {
                int endIdx = strIdx;
                int idx = strIdx + 1;

                if (!char.IsAsciiDigit(str[strIdx]) && str[strIdx] != ' ')
                    throw new IfxException($"Unexpected character at position {strIdx}.", -79722);

                if (idx < str.Length && char.IsAsciiDigit(str[idx]))
                {
                    endIdx = idx;
                    idx++;
                }

                data[startCode / 2] = int.Parse(
                    str.AsSpan(strIdx, endIdx - strIdx + 1).Trim(), CultureInfo.InvariantCulture);

                if (idx < str.Length && str[idx] != Delimiters[startCode / 2])
                {
                    if (startCode == endCode)
                        break;
                    throw new IfxException($"Bad field delimiter at position {idx}.", -79723);
                }

                strIdx = idx + 1;
                startCode += 2;
            }

            // Fractional seconds, scaled to nanoseconds.
            if (strIdx < str.Length)
            {
                int endIdx = strIdx - 1;
                int idx = strIdx;
                int numDigits = 0;
                for (startCode = 11;
                     idx < str.Length && startCode <= endCode && char.IsAsciiDigit(str[idx]);
                     idx++, startCode++, numDigits++)
                {
                    endIdx++;
                }

                if (endIdx >= strIdx)
                {
                    int tmp = int.Parse(str.AsSpan(strIdx, endIdx - strIdx + 1), CultureInfo.InvariantCulture);
                    int numMult = 9 - numDigits;
                    for (int i = 0; i < numMult; i++)
                        tmp *= 10;
                    data[6] = tmp;
                }
                strIdx = idx;
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            throw new IfxException("Invalid interval/datetime string.", -79724, inner: e);
        }

        if (strIdx < str.Length)
            throw new IfxException($"Extra characters in interval string: '{str[strIdx..]}'.", -79725);

        return data;
    }

    /// <summary>Strips a leading '+'/'-' and reports whether the value is negative (shared by both <c>fromString</c> ports).</summary>
    private protected static bool StripSign(ref string str)
    {
        str = str.Trim();
        if (str.Length > 0 && str[0] is '-' or '+')
        {
            if (str.Length == 1)
                throw new IfxException("An interval string cannot be just a sign.", -79706);
            bool negative = str[0] == '-';
            str = str[1..];
            return negative;
        }
        return false;
    }

    public override int GetHashCode() => 31 + Qualifier;

    public override bool Equals(object? obj) =>
        obj is IfxInterval other && GetType() == other.GetType() && Qualifier == other.Qualifier;
}
