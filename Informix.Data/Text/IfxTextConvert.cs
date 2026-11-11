using Informix.Data.Text;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Reads and writes Informix values in their text form — the representation an UNLOAD file
/// (<c>.unl</c>) is made of, and the one <c>DBDATE</c>, <c>DBCENTURY</c> and their siblings
/// govern.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> Unloading a table is writing each value the way the server
/// would, and loading it back is reading that text with the same rules; this class is that
/// per-value half, so a caller who knows the table's columns can write the row loop:</para>
/// <code>
/// var line = string.Join('|', [
///     reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
///     reader.GetString(1),
///     IfxTextConvert.FormatDate(DateOnly.FromDateTime(reader.GetDateTime(2))),
///     IfxTextConvert.FormatDecimal(reader.GetDecimal(3), scale: 2),
/// ]) + "|";
/// </code>
/// <para>and, going the other way, <see cref="ParseDate"/> / <see cref="ParseDecimal(ReadOnlySpan{char}, IfxTextFormat)"/>
/// turn the fields of such a line back into values to bind. The formats, the environment
/// variables and the error codes are those of the client library's conversion functions
/// (<c>deccvasc</c>, <c>rstrdate</c>, <c>dtcvasc</c>, ...), which is what code being ported
/// from ESQL/C already depends on; the signatures read like .NET.</para>
/// <para><b>What maps to what.</b>
/// <list type="table">
/// <item><term><c>deccvasc</c> / <c>dectoasc</c></term><description><see cref="ParseDecimal(ReadOnlySpan{char}, IfxTextFormat)"/> / <see cref="FormatDecimal(IfxDecimal, int, IfxTextFormat)"/>, <see cref="TryFormatDecimal"/></description></item>
/// <item><term><c>rstoi</c> / <c>rstol</c></term><description><see cref="ParseInt32"/> / <see cref="ParseInt64"/></description></item>
/// <item><term><c>ifx_int8cvasc</c> / <c>bigintcvasc</c></term><description><see cref="ParseBigInt"/></description></item>
/// <item><term><c>ifx_int8toasc</c> / <c>biginttoasc</c></term><description><see cref="TryFormatInt8"/> / <see cref="FormatBigInt"/></description></item>
/// <item><term><c>rstrdate</c> / <c>rdatestr</c></term><description><see cref="ParseDate"/> / <see cref="FormatDate"/></description></item>
/// <item><term><c>dtcvasc</c> / <c>dttoasc</c></term><description><see cref="ParseDateTime"/> / <see cref="FormatDateTime"/></description></item>
/// <item><term><c>incvasc</c> / <c>intoasc</c></term><description><see cref="ParseDayTimeInterval"/>, <see cref="ParseYearMonthInterval"/> / <see cref="FormatInterval(IfxTimeSpan)"/></description></item>
/// </list></para>
/// <para><b>Errors.</b> The library's conversion functions return a negative code; the
/// <c>Parse</c>/<c>Format</c> methods throw <see cref="IfxException"/> carrying that code in
/// <see cref="IfxException.IfxSqlCode"/> (-1213 for a non-numeric string, -1205 for an
/// invalid month, -1262 for a bad DATETIME, ...), and each has a <c>Try...</c> overload that
/// returns <see langword="false"/> instead.</para>
/// <para><b>SQL NULL.</b> Where a C function stores its null representation — a blank string
/// into <c>deccvasc</c>, an empty <c>dttoasc</c> buffer — this API uses the nullable value
/// (<see cref="IfxDecimal.Null"/>, <see langword="null"/>, an empty string) rather than the
/// sentinel the C structures carry.</para>
/// <para><b>Formats.</b> <c>DBDATE</c>, <c>GL_DATE</c>, <c>DBCENTURY</c>, <c>GL_DATETIME</c> and
/// <c>IFX_NOT_STRICT_THOUS_SEP</c> are carried by <see cref="IfxTextFormat"/>. A call that is not
/// given one uses <see cref="IfxTextFormat.Default"/> — <b>the process environment is not read</b>,
/// nor are the machine's regional settings: with nothing set the format is <c>MDY4/</c> with
/// <c>.</c> and <c>,</c> on every machine. To convert the way the server does for a connection,
/// pass <see cref="IfxConnection.TextFormat"/>, which comes from that connection string's
/// <c>DB Date</c>, <c>GL Date</c>, <c>DB Century</c>, <c>GL DateTime</c> and
/// <c>Strict Thousands Separator</c> keys. See <see cref="IfxTextFormat"/> for why the
/// regional settings are left out.</para>
/// <para><b>How faithful.</b> The text formats, environment variables and error codes were
/// read out of the shipping client library (<c>isqlt15a.dll</c>, Client SDK 4.50) and then
/// checked against it: <c>tools/TextConvertProbe</c> calls both implementations with the same
/// inputs — a fixed suite plus a randomized pass of roughly 34,000 comparisons across
/// numbers, dates, DATETIMEs and INTERVALs, under several <c>DBDATE</c>, <c>DBCENTURY</c> and
/// <c>GL_DATE</c> settings — and they agree everywhere except the cases below. Behaviour that
/// merely looks odd is replicated rather than corrected, because a file has to load into the
/// same values the server would have written, and ported code depends on what the library does
/// rather than on what the manual says; the field widths <see cref="ParseDate"/> accepts are
/// the example.</para>
/// <para><b>The one deliberate divergence.</b> <c>bigintcvasc</c> drops a negative exponent
/// whose shift is wider than the digit string instead of applying it, reading <c>"1.5e-13"</c>
/// as 15 while the same value written <c>"0.00000000000015"</c> is 0. That is a plain bug, not
/// a convention anything can depend on, so <see cref="ParseBigInt"/> applies the exponent
/// whatever its size.</para>
/// <para><b>Where it cannot follow.</b> Two further differences are structural, not choices.
/// A DECIMAL carries 32 significant digits and exponents to +/-126, more than a CLR
/// <see cref="decimal"/> holds, so <see cref="ParseDecimal(ReadOnlySpan{char}, IfxTextFormat)"/>
/// throws <see cref="OverflowException"/> beyond that range — the text-to-text
/// <see cref="FormatDecimal(ReadOnlySpan{char}, int, IfxTextFormat)"/> keeps the full range.
/// And the C <c>dec_t</c> has a signed zero, so <c>intoasc</c> writes "-0" for a negative zero
/// interval, while <see cref="IfxTimeSpan"/> and <see cref="IfxMonthSpan"/> have only one zero
/// and <see cref="FormatInterval(IfxTimeSpan)"/> writes it unsigned.</para>
/// </remarks>
public static class IfxTextConvert
{
    // ===================================================================== //
    //  deccvasc / dectoasc                                                  //
    // ===================================================================== //

    /// <summary>
    /// Reads a DECIMAL written as text (<c>deccvasc</c>). Leading blanks are
    /// skipped, an optional sign may precede a <c>0x</c> hexadecimal integer or a number with
    /// thousands separators, a decimal separator and an <c>e</c>/<c>E</c> exponent, and blanks
    /// may follow. A blank string is SQL NULL.
    /// </summary>
    /// <exception cref="IfxException">-1213 (non-numeric), -1200 (overflow), -1201 (underflow).</exception>
    /// <exception cref="OverflowException">The value is outside the CLR <see cref="decimal"/> range.</exception>
    public static IfxDecimal ParseDecimal(ReadOnlySpan<char> value, IfxTextFormat? format = null)
    {
        int rc = DecimalText.TryParse(value, IfxTextFormat.Resolve(format), out var dec);
        if (rc != ConversionError.Ok)
            throw ConversionError.Exception(rc);
        return dec.IsNull ? IfxDecimal.Null : new IfxDecimal(dec.ToDecimal());
    }

    /// <summary><see cref="ParseDecimal"/> without exceptions; <see langword="false"/> for the error codes.</summary>
    public static bool TryParseDecimal(ReadOnlySpan<char> value, out IfxDecimal result, IfxTextFormat? format = null)
    {
        result = IfxDecimal.Null;
        if (DecimalText.TryParse(value, IfxTextFormat.Resolve(format), out var dec) != ConversionError.Ok)
            return false;
        if (dec.IsNull)
            return true;
        try
        {
            result = new IfxDecimal(dec.ToDecimal());
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a DECIMAL as text (<c>dectoasc</c>), with <paramref name="scale"/> digits after the
    /// decimal separator (-1 derives the scale from the value, as the C function does).
    /// The string is not padded — use <see cref="TryFormatDecimal"/> for the fixed-width,
    /// blank-padded buffer the C function fills. SQL NULL becomes an empty string.
    /// </summary>
    public static string FormatDecimal(IfxDecimal value, int scale = -1, IfxTextFormat? format = null)
    {
        if (value.IsNull)
            return string.Empty;
        var resolved = IfxTextFormat.Resolve(format);
        var dec = DecimalText.FromDecimal(value.Value);
        // A buffer wide enough that the fixed-point form always fits (DECSIZE digits, sign,
        // separator and any leading zeros), so the exponential fallback never kicks in here.
        Span<char> buffer = stackalloc char[160];
        dec.Format(buffer, scale, resolved);
        return buffer.TrimEnd(' ').ToString();
    }

    /// <summary>
    /// Writes a DECIMAL into a fixed-width field the way <c>dectoasc</c> fills its buffer:
    /// <paramref name="destination"/> is filled
    /// left-justified and blank-padded, exponential notation is used when the fixed-point form
    /// does not fit, and the buffer is filled with asterisks (and <see langword="false"/>
    /// returned) when even that fails. A SQL NULL value fills the buffer with blanks.
    /// </summary>
    public static bool TryFormatDecimal(IfxDecimal value, Span<char> destination, int scale = -1, IfxTextFormat? format = null)
    {
        var resolved = IfxTextFormat.Resolve(format);
        var dec = value.IsNull ? DecimalText.Null : DecimalText.FromDecimal(value.Value);
        return dec.Format(destination, scale, resolved) == ConversionError.Ok;
    }

    /// <summary>
    /// Reformats a number that is already text (<c>deccvasc</c> straight into <c>dectoasc</c>) without
    /// passing through a CLR <see cref="decimal"/>, so the full Informix DECIMAL range (32
    /// significant digits, exponents to +/-126) survives. A blank string stays blank.
    /// </summary>
    /// <exception cref="IfxException">-1213 (non-numeric), -1200 (overflow), -1201 (underflow).</exception>
    public static string FormatDecimal(ReadOnlySpan<char> value, int scale = -1, IfxTextFormat? format = null)
    {
        var resolved = IfxTextFormat.Resolve(format);
        int rc = DecimalText.TryParse(value, resolved, out var dec);
        if (rc != ConversionError.Ok)
            throw ConversionError.Exception(rc);
        if (dec.IsNull)
            return string.Empty;
        Span<char> buffer = stackalloc char[160];
        dec.Format(buffer, scale, resolved);
        return buffer.TrimEnd(' ').ToString();
    }

    /// <summary>
    /// Reformats a number that is already text into a fixed-width field (blank padding,
    /// exponential fallback, asterisks on failure) and the full Informix DECIMAL range.
    /// </summary>
    public static bool TryFormatDecimal(ReadOnlySpan<char> value, Span<char> destination, int scale = -1, IfxTextFormat? format = null)
    {
        var resolved = IfxTextFormat.Resolve(format);
        if (DecimalText.TryParse(value, resolved, out var dec) != ConversionError.Ok)
        {
            destination.Fill(' ');
            return false;
        }
        return dec.Format(destination, scale, resolved) == ConversionError.Ok;
    }

    // ===================================================================== //
    //  rstoi / rstol                                                        //
    // ===================================================================== //

    /// <summary>
    /// Reads a SMALLINT written as text (<c>rstoi</c>): leading blanks, an optional sign and
    /// digits, ending at the end of the
    /// string, a blank or a newline. A blank string is SQL NULL (<see langword="null"/>).
    /// </summary>
    /// <remarks>The C function's overflow check is a SMALLINT's and is symmetric, so -32768
    /// overflows exactly as 32768 does.</remarks>
    /// <exception cref="IfxException">-1213 (non-numeric) or -1214 (too large).</exception>
    public static int? ParseInt32(ReadOnlySpan<char> value)
    {
        int rc = IntegerText.TryParseSmallRange(value, out int? parsed);
        return rc == ConversionError.Ok ? parsed : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseInt32"/> without exceptions.</summary>
    public static bool TryParseInt32(ReadOnlySpan<char> value, out int? result) =>
        IntegerText.TryParseSmallRange(value, out result) == ConversionError.Ok;

    /// <summary>
    /// Reads an INT written as text (<c>rstol</c>): the same scan as <see cref="ParseInt32"/>
    /// with a wider range. A blank
    /// string is SQL NULL (<see langword="null"/>).
    /// </summary>
    /// <exception cref="IfxException">-1213 (non-numeric) or -1215 (too large).</exception>
    public static long? ParseInt64(ReadOnlySpan<char> value)
    {
        int rc = IntegerText.TryParseLongRange(value, out long? parsed);
        return rc == ConversionError.Ok ? parsed : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseInt64"/> without exceptions.</summary>
    public static bool TryParseInt64(ReadOnlySpan<char> value, out long? result) =>
        IntegerText.TryParseLongRange(value, out result) == ConversionError.Ok;

    // ===================================================================== //
    //  ifx_int8cvasc / bigintcvasc / ifx_int8toasc / biginttoasc            //
    // ===================================================================== //

    /// <summary>
    /// Reads a BIGINT or INT8 written as text (<c>bigintcvasc</c>, and <c>ifx_int8cvasc</c>,
    /// which shares its parser): the number is read like a decimal — sign, thousands
    /// separators, decimal separator, <c>e</c> exponent or a <c>0x</c> hexadecimal integer —
    /// and then truncated towards zero. A blank string is SQL NULL (<see langword="null"/>).
    /// </summary>
    /// <remarks>
    /// The exponent is always applied, which is the one place this differs from the C library:
    /// <c>bigintcvasc</c> drops a negative exponent whose shift is wider than the digit string,
    /// so it reads <c>"1.5e-13"</c> as 15 where this reads 0 — the same value the same library
    /// gives for <c>"0.00000000000015"</c>.
    /// </remarks>
    /// <exception cref="IfxException">-1213 (non-numeric) or -1284 (overflow or underflow).</exception>
    public static long? ParseBigInt(ReadOnlySpan<char> value, IfxTextFormat? format = null)
    {
        int rc = IntegerText.TryParseBigInt(value, IfxTextFormat.Resolve(format), out long? parsed);
        return rc == ConversionError.Ok ? parsed : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseBigInt"/> without exceptions.</summary>
    public static bool TryParseBigInt(ReadOnlySpan<char> value, out long? result, IfxTextFormat? format = null) =>
        IntegerText.TryParseBigInt(value, IfxTextFormat.Resolve(format), out result) == ConversionError.Ok;

    /// <summary>
    /// Writes a BIGINT as text (<c>biginttoasc</c>). <paramref name="numericBase"/> 16 gives the C
    /// library's <c>0x</c> plus sixteen two's-complement hexadecimal digits; any other value
    /// means base 10. SQL NULL becomes an empty string.
    /// </summary>
    public static string FormatBigInt(long? value, int numericBase = 10) =>
        value is { } v ? IntegerText.FormatBigInt(v, numericBase) : string.Empty;

    /// <summary>
    /// Writes a BIGINT into a caller buffer (<c>biginttoasc</c>): left-justified, reporting how
    /// much it wrote, or returns <see langword="false"/> (leaving the buffer untouched) when it
    /// does not fit — the C function's negative return.
    /// </summary>
    public static bool TryFormatBigInt(long? value, Span<char> destination, out int charsWritten, int numericBase = 10)
    {
        string text = FormatBigInt(value, numericBase);
        charsWritten = 0;
        if (text.Length > destination.Length)
            return false;
        text.AsSpan().CopyTo(destination);
        charsWritten = text.Length;
        return true;
    }

    /// <summary>
    /// Writes an INT8 into a fixed-width field (<c>ifx_int8toasc</c>): left-justified, blank-padding the rest of
    /// <paramref name="destination"/> (the C function does not NUL-terminate); a SQL NULL value
    /// leaves the buffer all blanks. Returns <see langword="false"/> when the value does not fit.
    /// </summary>
    public static bool TryFormatInt8(long? value, Span<char> destination)
    {
        destination.Fill(' ');
        string text = FormatBigInt(value);
        if (text.Length > destination.Length)
            return false;
        text.AsSpan().CopyTo(destination);
        return true;
    }

    // ===================================================================== //
    //  rstrdate / rdatestr                                                  //
    // ===================================================================== //

    /// <summary>
    /// Reads a DATE written in the end-user format (<c>rstrdate</c>): <c>DBDATE</c>, else
    /// <c>GL_DATE</c>, else <c>mm/dd/yyyy</c>; with <c>DBDATE</c> any non-digit run separates
    /// the fields and a spelled-out month is accepted, and a one- or two-digit year is expanded
    /// according to <c>DBCENTURY</c>. A blank string is the null DATE (<see langword="null"/>).
    /// </summary>
    /// <exception cref="IfxException">-1204 (year), -1205 (month), -1206 (day), -1212 (incomplete <c>DBDATE</c>), -1218 (does not match the format).</exception>
    public static DateOnly? ParseDate(ReadOnlySpan<char> value, IfxTextFormat? format = null)
    {
        int rc = DateText.TryParse(value, IfxTextFormat.Resolve(format), out DateOnly? date);
        return rc == ConversionError.Ok ? date : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseDate"/> without exceptions.</summary>
    public static bool TryParseDate(ReadOnlySpan<char> value, out DateOnly? result, IfxTextFormat? format = null) =>
        DateText.TryParse(value, IfxTextFormat.Resolve(format), out result) == ConversionError.Ok;

    /// <summary>
    /// Writes a DATE in the end-user format (<c>rdatestr</c>) — <c>DBDATE</c>, else
    /// <c>GL_DATE</c>, else <c>mm/dd/yyyy</c>.
    /// </summary>
    /// <exception cref="IfxException">-1212 when <c>DBDATE</c> names no month, day or year.</exception>
    public static string FormatDate(DateOnly value, IfxTextFormat? format = null)
    {
        int rc = DateText.TryFormat(value, IfxTextFormat.Resolve(format), out string text);
        return rc == ConversionError.Ok ? text : throw ConversionError.Exception(rc);
    }

    /// <summary>
    /// The internal DATE of a calendar date — the <c>int4</c> day count (<c>rmdyjul</c>)
    /// that DATE columns and the <c>rstrdate</c>/<c>rdatestr</c> C signatures carry, counted
    /// from 31 December 1899.
    /// </summary>
    public static int ToInternalDate(DateOnly value) => DateText.ToInternalDate(value);

    /// <summary>The calendar date an internal DATE stands for (<c>rjulmdy</c>).</summary>
    public static DateOnly FromInternalDate(int internalDate) => DateText.FromInternalDate(internalDate);

    // ===================================================================== //
    //  dtcvasc / dttoasc                                                    //
    // ===================================================================== //

    /// <summary>
    /// Reads a DATETIME written in the ANSI SQL form <c>yyyy-mm-dd hh:mm:ss.fffff</c>
    /// (<c>dtcvasc</c>) into the
    /// fields between <paramref name="start"/> and <paramref name="end"/>, which stand for the
    /// qualifier the C function takes from its target variable. A one- or two-digit year is
    /// expanded according to <c>DBCENTURY</c>; a blank string is SQL NULL. The C function reads
    /// only this format — <c>DBDATE</c> and <c>GL_DATETIME</c> do not affect it.
    /// </summary>
    /// <exception cref="IfxException">-1261, -1262, -1263, -1264 or -1268 (bad qualifier).</exception>
    public static IfxDateTime ParseDateTime(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, IfxTextFormat? format = null)
    {
        int rc = ParseDateTimeCore(value, start, end, IfxTextFormat.Resolve(format), out IfxDateTime result);
        return rc == ConversionError.Ok ? result : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseDateTime"/> without exceptions.</summary>
    public static bool TryParseDateTime(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, out IfxDateTime result, IfxTextFormat? format = null) =>
        ParseDateTimeCore(value, start, end, IfxTextFormat.Resolve(format), out result) == ConversionError.Ok;

    // Fields the qualifier leaves out default to year 1, month 1, day 1; the fraction is scaled to ticks (100 ns).
    private static int ParseDateTimeCore(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, IfxTextFormat format, out IfxDateTime result)
    {
        result = IfxDateTime.Null;
        short qualifier;
        try
        {
            qualifier = IfxQualifier.ForDateTime(start, end);
        }
        catch (IfxException)
        {
            return ConversionError.BadQualifier;
        }

        int rc = DateTimeText.TryParse(value, qualifier, isDateTime: true, format, out bool isNull, out int[] fields);
        if (rc != ConversionError.Ok || isNull)
            return rc;

        int year = fields[0] == 0 ? 1 : fields[0];
        int month = fields[1] == 0 ? 1 : fields[1];
        int day = fields[2] == 0 ? 1 : fields[2];
        long ticks;
        try
        {
            ticks = new DateTime(year, month, day, fields[3], fields[4], fields[5]).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return ConversionError.FieldOutOfRange;
        }

        int fractionDigits = DateTimeText.FractionDigits(qualifier);
        if (fractionDigits > 0)
        {
            long scale = 1;
            for (int i = 0; i < 7 - Math.Min(fractionDigits, 7); i++)
                scale *= 10;
            ticks += fields[DateTimeText.FractionSlot] * scale;
        }

        result = new IfxDateTime(new DateTime(ticks), qualifier);
        return ConversionError.Ok;
    }

    /// <summary>
    /// Writes a DATETIME in the ANSI SQL form its qualifier selects (<c>dttoasc</c>), or in the
    /// <c>GL_DATETIME</c> format when that variable is set (the C function consults it on output
    /// only). SQL NULL becomes an empty string.
    /// </summary>
    public static string FormatDateTime(IfxDateTime value, IfxTextFormat? format = null)
    {
        if (value.IsNull)
            return string.Empty;

        var resolved = IfxTextFormat.Resolve(format);
        short qualifier = IfxQualifier.ForDateTime(value.StartTimeUnit, value.EndTimeUnit);
        int fractionDigits = DateTimeText.FractionDigits(qualifier);
        var dt = new DateTime(value.Ticks);

        if (!string.IsNullOrEmpty(resolved.GlDateTime))
            return DateText.FormatStrftime(resolved.GlDateTime, dt, fractionDigits);

        var fields = new int[7];
        fields[0] = dt.Year;
        fields[1] = dt.Month;
        fields[2] = dt.Day;
        fields[3] = dt.Hour;
        fields[4] = dt.Minute;
        fields[5] = dt.Second;
        if (fractionDigits > 0)
        {
            long scale = 1;
            for (int i = 0; i < 7 - Math.Min(fractionDigits, 7); i++)
                scale *= 10;
            fields[DateTimeText.FractionSlot] = (int)(dt.Ticks % TimeSpan.TicksPerSecond / scale);
        }
        return DateTimeText.Format(qualifier, fields);
    }

    // ===================================================================== //
    //  incvasc / intoasc                                                    //
    // ===================================================================== //

    /// <summary>
    /// Reads a DAY TO FRACTION INTERVAL (<c>incvasc</c>): <c>dd hh:mm:ss.fffff</c> limited to
    /// the fields between <paramref name="start"/> and <paramref name="end"/>, with
    /// <paramref name="leadingPrecision"/> digits in the leading field. A leading minus sign
    /// negates the value; a blank string is SQL NULL.
    /// </summary>
    /// <exception cref="IfxException">-1261, -1262, -1263, -1264 or -1268 (bad qualifier).</exception>
    public static IfxTimeSpan ParseDayTimeInterval(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, int leadingPrecision = 2)
    {
        int rc = ParseDayTimeCore(value, start, end, leadingPrecision, out IfxTimeSpan result);
        return rc == ConversionError.Ok ? result : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseDayTimeInterval"/> without exceptions.</summary>
    public static bool TryParseDayTimeInterval(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, out IfxTimeSpan result, int leadingPrecision = 2) =>
        ParseDayTimeCore(value, start, end, leadingPrecision, out result) == ConversionError.Ok;

    private static int ParseDayTimeCore(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, int leadingPrecision, out IfxTimeSpan result)
    {
        result = IfxTimeSpan.Null;
        int rc = ParseIntervalFields(value, start, end, leadingPrecision, out short qualifier, out bool negative, out bool isNull, out int[] fields);
        if (rc != ConversionError.Ok || isNull)
            return rc;

        long seconds = fields[2] * 86400L + fields[3] * 3600L + fields[4] * 60L + fields[5];
        long nanos = 0;
        int fractionDigits = DateTimeText.FractionDigits(qualifier);
        if (fractionDigits > 0)
        {
            long scale = 1;
            for (int i = 0; i < 9 - Math.Min(fractionDigits, 9); i++)
                scale *= 10;
            nanos = fields[DateTimeText.FractionSlot] * scale;
        }
        if (negative)
        {
            seconds = -seconds;
            nanos = -nanos;
        }
        result = new IfxTimeSpan(new IfxIntervalDayFraction(seconds, nanos, qualifier));
        return ConversionError.Ok;
    }

    /// <summary>
    /// Reads a YEAR TO MONTH INTERVAL (<c>incvasc</c>): <c>yyyy-mm</c> limited to the fields
    /// between <paramref name="start"/> and <paramref name="end"/>. A blank string is SQL NULL.
    /// </summary>
    /// <exception cref="IfxException">-1261, -1262, -1263, -1264 or -1268 (bad qualifier).</exception>
    public static IfxMonthSpan ParseYearMonthInterval(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, int leadingPrecision = 4)
    {
        int rc = ParseYearMonthCore(value, start, end, leadingPrecision, out IfxMonthSpan result);
        return rc == ConversionError.Ok ? result : throw ConversionError.Exception(rc);
    }

    /// <summary><see cref="ParseYearMonthInterval"/> without exceptions.</summary>
    public static bool TryParseYearMonthInterval(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, out IfxMonthSpan result, int leadingPrecision = 4) =>
        ParseYearMonthCore(value, start, end, leadingPrecision, out result) == ConversionError.Ok;

    private static int ParseYearMonthCore(ReadOnlySpan<char> value, IfxTimeUnit start, IfxTimeUnit end, int leadingPrecision, out IfxMonthSpan result)
    {
        result = IfxMonthSpan.Null;
        int rc = ParseIntervalFields(value, start, end, leadingPrecision, out short qualifier, out bool negative, out bool isNull, out int[] fields);
        if (rc != ConversionError.Ok || isNull)
            return rc;

        int months = fields[0] * 12 + fields[1];
        result = new IfxMonthSpan(new IfxIntervalYearMonth(negative ? -months : months, qualifier));
        return ConversionError.Ok;
    }

    // A leading '-' marks a negative interval; the rest is parsed like a DATETIME of the same qualifier but without year expansion.
    private static int ParseIntervalFields(
        ReadOnlySpan<char> value,
        IfxTimeUnit start,
        IfxTimeUnit end,
        int leadingPrecision,
        out short qualifier,
        out bool negative,
        out bool isNull,
        out int[] fields)
    {
        qualifier = 0;
        negative = false;
        isNull = false;
        fields = new int[7];
        try
        {
            qualifier = IfxQualifier.ForInterval(leadingPrecision, start, end);
        }
        catch (IfxException)
        {
            return ConversionError.BadQualifier;
        }

        var text = value.TrimStart(' ');
        if (text.Length > 0 && text[0] == '-')      // incvasc strips the sign before parsing
        {
            negative = true;
            text = text[1..];
        }

        return DateTimeText.TryParse(text, qualifier, isDateTime: false, IfxTextFormat.Default, out isNull, out fields);
    }

    /// <summary>
    /// Writes a DAY TO FRACTION INTERVAL (<c>intoasc</c>): the ANSI SQL form, right-aligned in the
    /// leading field's width behind one blank, with the sign taking the blank in front of the
    /// first significant digit. SQL NULL becomes an empty string.
    /// </summary>
    public static string FormatInterval(IfxTimeSpan value)
    {
        if (value.IsNull)
            return string.Empty;

        var interval = value.ToInterval();
        short qualifier = interval.Qualifier;
        bool negative = interval.Seconds < 0 || interval.NanoSeconds < 0;
        long seconds = Math.Abs(interval.Seconds);
        long nanos = Math.Abs(interval.NanoSeconds);

        var fields = new int[7];
        fields[2] = (int)(seconds / 86400);
        fields[3] = (int)(seconds / 3600 % 24);
        fields[4] = (int)(seconds / 60 % 60);
        fields[5] = (int)(seconds % 60);

        int fractionDigits = DateTimeText.FractionDigits(qualifier);
        if (fractionDigits > 0)
        {
            long scale = 1;
            for (int i = 0; i < 9 - Math.Min(fractionDigits, 9); i++)
                scale *= 10;
            fields[DateTimeText.FractionSlot] = (int)(nanos / scale);
        }

        // Fields coarser than the qualifier's leading unit fold into it, as in the C library.
        FoldIntoLeadingField(IfxQualifier.GetStartCode(qualifier), fields);
        return DateTimeText.FormatInterval(qualifier, fields, negative);
    }

    /// <summary>
    /// Writes a YEAR TO MONTH INTERVAL (<c>intoasc</c>). SQL NULL becomes an empty string.
    /// </summary>
    public static string FormatInterval(IfxMonthSpan value)
    {
        if (value.IsNull)
            return string.Empty;

        var interval = value.ToInterval();
        short qualifier = interval.Qualifier;
        bool negative = interval.Months < 0;
        long months = Math.Abs(interval.Months);

        var fields = new int[7];
        fields[0] = (int)(months / 12);
        fields[1] = (int)(months % 12);
        FoldIntoLeadingField(IfxQualifier.GetStartCode(qualifier), fields);
        return DateTimeText.FormatInterval(qualifier, fields, negative);
    }

    private static void FoldIntoLeadingField(byte startCode, int[] fields)
    {
        // MONTH-leading intervals carry the years as months, HOUR-leading ones the days as
        // hours, and so on down the chain.
        int[] multipliers = [12, 0, 24, 60, 60];    // year->month, (month->day n/a), day->hour, hour->minute, minute->second
        for (int slot = 0; slot < DateTimeText.SlotOf(startCode); slot++)
        {
            if (fields[slot] == 0)
                continue;
            int factor = multipliers[slot];
            if (factor == 0)
                continue;                            // months never fold into days
            fields[slot + 1] += fields[slot] * factor;
            fields[slot] = 0;
        }
    }
}
