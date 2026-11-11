namespace Informix.Data.Types;

/// <summary>
/// Helpers for the packed DATETIME/INTERVAL qualifier word:
/// <c>[length:8][startCode:4][endCode:4]</c>, where <c>length</c> is the total
/// number of decimal digits and start/end are <see cref="IfxTimeUnit"/> codes.
/// </summary>
internal static class IfxQualifier
{
    /// <summary>Total number of decimal digits in the value (<c>TU_LEN</c>).</summary>
    public static byte GetLength(short qualifier) => (byte)(qualifier >> 8);

    /// <summary>The first (most significant) field (<c>TU_START</c>).</summary>
    public static byte GetStartCode(short qualifier) => (byte)((qualifier >> 4) & 0xF);

    /// <summary>The last (least significant) field (<c>TU_END</c>).</summary>
    public static byte GetEndCode(short qualifier) => (byte)(qualifier & 0xF);

    /// <summary>Fractional-second digit count, 0 when the value ends at SECOND or coarser.</summary>
    public static byte GetScale(short qualifier)
    {
        byte end = GetEndCode(qualifier);
        return end > 10 ? (byte)(end - 10) : (byte)0;
    }

    /// <summary>
    /// Digit width of the leading field (<c>TU_FLEN</c>).
    /// The Java driver computes <c>len - end - start</c>, which is only
    /// correct for YEAR-start qualifiers; the Informix C macro is
    /// <c>TU_LEN - TU_END + TU_START</c>, which this port uses so that
    /// HOUR TO SECOND datetimes and DAY TO FRACTION intervals decode correctly.
    /// </summary>
    public static int GetLeadingFieldWidth(short qualifier) =>
        GetLength(qualifier) - GetEndCode(qualifier) + GetStartCode(qualifier);

    /// <summary>
    /// Builds a DATETIME qualifier; the leading field is 4 digits for YEAR,
    /// otherwise 2 (port of <c>encodeDateTimePrecision</c>).
    /// </summary>
    public static short ForDateTime(IfxTimeUnit start, IfxTimeUnit end)
    {
        byte s = (byte)start, e = (byte)end;
        Validate(0, s, e);
        int length = e - s + (s == 0 ? 4 : 2);
        return Encode(length, s, e);
    }

    /// <summary>
    /// Builds an INTERVAL qualifier with an explicit leading-field digit width
    /// (port of <c>Interval.getQualifier(len, start, end)</c> /
    /// <c>encodeIntervalPrecision</c>).
    /// </summary>
    public static short ForInterval(int leadingWidth, IfxTimeUnit start, IfxTimeUnit end)
    {
        byte s = (byte)start, e = (byte)end;
        Validate(leadingWidth, s, e);
        int length = e - s + leadingWidth;
        return Encode(length, s, e);
    }

    private static short Encode(int length, byte s, byte e) =>
        (short)((byte)length << 8 | s << 4 | e);

    // Error codes -79717..-79719 are: invalid precision / start / end code.
    private static void Validate(int leadingWidth, byte s, byte e)
    {
        if (leadingWidth is < 0 or > 9)
            throw new IfxException($"Invalid leading-field precision {leadingWidth}.", -79717);
        if (s > 15)
            throw new IfxException($"Invalid start code {s}.", -79718);
        if (e > 15 || e < s)
            throw new IfxException($"Invalid end code {e}.", -79719);
    }

    /// <summary>SQL field name for a unit code.</summary>
    public static string GetFieldName(byte code) => code switch
    {
        0 => "year",
        2 => "month",
        4 => "day",
        6 => "hour",
        8 => "minute",
        10 => "second",
        11 => "fraction(1)",
        12 => "fraction(2)",
        13 => "fraction(3)",
        14 => "fraction(4)",
        15 => "fraction(5)",
        _ => "",
    };

    /// <summary>
    /// Human-readable type name, e.g. <c>"datetime year to second"</c>
    /// (port of <c>Interval.getIfxTypeName</c>).
    /// </summary>
    public static string? GetIfxTypeName(SqliTypeCode type, short qualifier) => type switch
    {
        SqliTypeCode.Interval => $"interval {GetFieldName(GetStartCode(qualifier))} to {GetFieldName(GetEndCode(qualifier))}",
        SqliTypeCode.DateTime => $"datetime {GetFieldName(GetStartCode(qualifier))} to {GetFieldName(GetEndCode(qualifier))}",
        _ => null,
    };
}
