using System.Buffers.Binary;
using System.Text;

namespace Informix.Data.Wire;

/// <summary>
/// Converts between the Informix on-the-wire byte representation and CLR
/// primitives.
///
/// <para>Byte shifts for big-endian integers and IEEE floats are delegated to
/// <see cref="System.Buffers.Binary.BinaryPrimitives"/>, which is the idiomatic
/// .NET way. Only the genuinely Informix-specific layouts — the INT8 sign/two-halves
/// layout and the DATE day count — are reproduced by hand.</para>
///
/// <para>The complex packed-decimal types (DECIMAL, MONEY, DATETIME, INTERVAL)
/// are handled by <c>PackedValue</c> (ported separately) and are not part of this
/// primitive converter.</para>
/// </summary>
internal static class IfxWireConverter
{
    /// <summary>Days from the proleptic year 0 to 1900-01-01, per Informix's rday/rjulmdy math (<c>DAYS1900</c>).</summary>
    public const int Days1900 = 693594;

    /// <summary>Days in four Gregorian centuries (<c>DAYS4CENT</c>).</summary>
    public const int Days4Cent = 146097;

    /// <summary>Days in a four-year leap cycle (<c>DAYS4YEAR</c>).</summary>
    public const int Days4Year = 1461;

    /// <summary>Sentinel the server sends for a NULL DATE.</summary>
    public const int DateNull = int.MinValue;

    /// <summary>Sentinel the server sends for a NULL INT8.</summary>
    public const long Int8Null = long.MinValue;

    /// <summary>ISO-8859-1 — the Informix "NOENCODING"/default single-byte code set.</summary>
    public static readonly Encoding DefaultEncoding = Encoding.Latin1;

    // ---- month lengths, non-leap; index 0 unused ----
    private static ReadOnlySpan<byte> DaysInMonth => [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    // ===================================================================== //
    //  Integers                                                             //
    // ===================================================================== //

    public static short ReadInt16(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt16BigEndian(b);

    public static int ReadInt32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32BigEndian(b);

    /// <summary>Reads a BIGINT (native 8-byte big-endian two's complement).</summary>
    public static long ReadBigInt(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt64BigEndian(b);

    public static void WriteInt16(Span<byte> dest, short value) => BinaryPrimitives.WriteInt16BigEndian(dest, value);

    public static void WriteInt32(Span<byte> dest, int value) => BinaryPrimitives.WriteInt32BigEndian(dest, value);

    public static void WriteBigInt(Span<byte> dest, long value) => BinaryPrimitives.WriteInt64BigEndian(dest, value);

    public static byte[] ToInt16(short value)
    {
        var b = new byte[2];
        WriteInt16(b, value);
        return b;
    }

    public static byte[] ToInt32(int value)
    {
        var b = new byte[4];
        WriteInt32(b, value);
        return b;
    }

    public static byte[] ToBigInt(long value)
    {
        var b = new byte[8];
        WriteBigInt(b, value);
        return b;
    }

    // ---- INT8: Informix's own 10-byte layout [sign:int16][low32:int32][high32:int32] ----
    // A leading big-endian smallint sign (0 => NULL, 1 => positive, -1 => negative)
    // followed by the magnitude as two big-endian 32-bit halves, low half first.

    public const int Int8Size = 10;

    public static long ReadInt8(ReadOnlySpan<byte> b)
    {
        short sign = ReadInt16(b);
        if (sign == 0)
            return Int8Null; // NULL sentinel

        // Magnitude bytes, high half from bytes [6..9],
        // low half from bytes [2..5].
        long l = b[6];
        l = (l << 8) | b[7];
        l = (l << 8) | b[8];
        l = (l << 8) | b[9];
        l = (l << 8) | b[2];
        l = (l << 8) | b[3];
        l = (l << 8) | b[4];
        l = (l << 8) | b[5];

        return sign == -1 ? -l : l;
    }

    /// <summary>Inverse of <see cref="ReadInt8"/>: sign word, then the low and high 32 bits of the magnitude.</summary>
    public static byte[] ToInt8(long value)
    {
        var b = new byte[Int8Size];
        short sign = 1;
        if (value < 0)
        {
            sign = -1;
            value = -value;
        }

        WriteInt16(b.AsSpan(0, 2), sign);
        WriteInt32(b.AsSpan(2, 4), unchecked((int)(value & 0xFFFFFFFFL))); // low 32
        WriteInt32(b.AsSpan(6, 4), (int)(value >> 32));                    // high 32
        return b;
    }

    // ===================================================================== //
    //  IEEE floating point (SMALLFLOAT / FLOAT)                             //
    // ===================================================================== //

    public static float ReadReal(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadSingleBigEndian(b);

    public static double ReadDouble(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadDoubleBigEndian(b);

    public static byte[] ToReal(float value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, value);
        return b;
    }

    public static byte[] ToDouble(double value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, value);
        return b;
    }

    // ===================================================================== //
    //  DATE  (a signed day count relative to the Informix epoch)            //
    // ===================================================================== //

    /// <summary>
    /// Decodes a 4-byte Informix DATE. Returns <c>null</c> for the NULL sentinel.
    /// </summary>
    public static DateTime? ReadDate(ReadOnlySpan<byte> b)
    {
        int dt = ReadInt32(b);
        if (dt == DateNull)
            return null;
        return ConvertDaysToDate(dt);
    }

    public static byte[] ToDate(DateTime date) => ToInt32(ConvertDateToDays(date));

    /// <summary>Gregorian leap-year test, faithful to Informix's <c>rleapyear</c>.</summary>
    public static bool IsLeapYear(int year) =>
        !((year & 0x3) != 0 || (year % 400 != 0 && year % 100 == 0));

    /// <summary>Port of <c>convertDaysToDate</c>: an Informix day count to a calendar date.</summary>
    public static DateTime ConvertDaysToDate(int dt)
    {
        int jdate = dt + Days1900;

        int lyear = (jdate * 4 + 3) / Days4Cent;
        jdate -= lyear * Days4Cent / 4;
        int year = lyear * 100;

        lyear = (jdate * 4 + 3) / Days4Year;
        jdate -= lyear * Days4Year / 4;
        year += lyear + 1;

        int day = jdate + 1;
        int febDays = IsLeapYear(year) ? 29 : 28;

        int mon;
        for (mon = 1; mon <= 12; mon++)
        {
            int monthLen = mon == 2 ? febDays : DaysInMonth[mon];
            if (day <= monthLen)
                break;
            day -= monthLen;
        }

        return new DateTime(year, mon, day, 0, 0, 0, DateTimeKind.Unspecified);
    }

    /// <summary>Port of <c>convertDateToDays</c>: a calendar date to an Informix day count.</summary>
    public static int ConvertDateToDays(DateTime date)
    {
        int mon = date.Month;
        int day = date.Day;
        int year = date.Year;
        int febDays = IsLeapYear(year) ? 29 : 28;
        int lyear = year - 1;

        int jdate = lyear / 100 * Days4Cent / 4 + lyear % 100 * Days4Year / 4 + day - 1 - Days1900;

        for (int i = 1; i < mon; i++)
            jdate += i == 2 ? febDays : DaysInMonth[i];

        return jdate;
    }

    // ===================================================================== //
    //  CHAR / VARCHAR text                                                  //
    // ===================================================================== //

    /// <summary>
    /// Decodes a CHAR/VARCHAR payload, stripping the Informix trailing NUL
    /// padding first.
    /// </summary>
    /// <param name="trimTrailingSpaces">
    /// When true, also strips trailing ASCII space (0x20) bytes — the fixed-width
    /// CHAR/NCHAR trim <c>TrimTrailingSpaces</c> enables (<c>IFX_TRIMTRAILINGSPACES</c>
    /// / <c>IfxChar.trimIfxChar</c>). Done on the byte span before decoding (not on the
    /// built string) so the default (false) path allocates and behaves exactly as before.
    /// </param>
    public static string DecodeChar(ReadOnlySpan<byte> b, Encoding? encoding = null, bool trimTrailingSpaces = false)
    {
        int len = b.Length;
        while (len > 0 && b[len - 1] == 0)
            len--;
        if (trimTrailingSpaces)
            while (len > 0 && b[len - 1] == (byte)' ')
                len--;
        return (encoding ?? DefaultEncoding).GetString(b[..len]);
    }

    /// <summary>Encodes a string to its raw code-set bytes (no length prefix).</summary>
    public static byte[] EncodeChar(string value, Encoding? encoding = null) =>
        (encoding ?? DefaultEncoding).GetBytes(value);
}
