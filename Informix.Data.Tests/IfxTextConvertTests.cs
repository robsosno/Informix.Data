using System;
using Informix.Data;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Values as text, in both directions (<see cref="IfxTextConvert"/>).
/// </summary>
/// <remarks>
/// Every expectation here was taken from the real client library: its conversion functions in
/// <c>isqlt15a.dll</c> were called through P/Invoke with these inputs and their output
/// recorded (see <c>tools/TextConvertProbe</c>, which re-runs that comparison, including a
/// randomized differential pass, on a machine with the Informix Client SDK installed).
/// The dates that depend on <c>DBCENTURY</c> pin
/// <see cref="IfxTextFormat.ReferenceDate"/> so they do not drift with the calendar.
/// </remarks>
public class IfxTextConvertTests
{
    private static readonly IfxTextFormat Plain = new();

    private static IfxTextFormat With(string? dbDate = null, string? century = null, string? glDate = null,
                                        string? glDateTime = null, DateOnly? today = null) => new()
    {
        DbDate = dbDate,
        DbCentury = century,
        GlDate = glDate,
        GlDateTime = glDateTime,
        ReferenceDate = today ?? new DateOnly(2026, 9, 6),
    };

    // ===================================================================== //
    //  DECIMAL in  (deccvasc)                                               //
    // ===================================================================== //

    [Theory]
    [InlineData("123.45", "123.45")]
    [InlineData("-0.5", "-0.5")]
    [InlineData("+7", "7.0")]
    [InlineData("1e10", "10000000000.0")]
    [InlineData("1E+10", "10000000000.0")]
    [InlineData("1e-5", "0.00001")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5.0")]
    [InlineData("0", "0.0")]
    [InlineData("-0", "0.0")]           // the C library reports "-0" as a positive zero
    [InlineData("000123", "123.0")]
    [InlineData("1,234", "1234.0")]
    [InlineData("1,234,567", "1234567.0")]
    [InlineData("  42  ", "42.0")]
    [InlineData("0x1f", "31.0")]        // a 0x prefix is read as hexadecimal
    [InlineData("0X10", "16.0")]
    [InlineData("1e", "1.0")]           // an exponent with no digits is simply zero
    public void ParseDecimal_reads_the_documented_forms(string text, string expected) =>
        Assert.Equal(expected, IfxTextConvert.FormatDecimal(text.AsSpan(), format: Plain));

    [Fact]
    public void ParseDecimal_reads_a_blank_string_as_SQL_NULL()
    {
        Assert.True(IfxTextConvert.ParseDecimal("").IsNull);
        Assert.True(IfxTextConvert.ParseDecimal("   ").IsNull);
        Assert.Equal(string.Empty, IfxTextConvert.FormatDecimal(IfxDecimal.Null));
    }

    [Theory]
    [InlineData("abc", -1213)]
    [InlineData("1eX", -1213)]
    [InlineData("1,23,4", -1213)]       // thousands separators must group by three
    [InlineData("12,34.5", -1213)]
    [InlineData("1 234", -1213)]
    [InlineData("1.2.3", -1213)]
    [InlineData("--5", -1213)]
    [InlineData("1-", -1213)]
    [InlineData("0xzz", -1213)]
    [InlineData("1e300", -1200)]        // beyond DECIMAL's exponent range
    [InlineData("1e-300", -1201)]
    public void ParseDecimal_reports_the_library_error_codes(string text, int code)
    {
        var error = Assert.Throws<IfxException>(() => IfxTextConvert.ParseDecimal(text));
        Assert.Equal(code, error.IfxSqlCode);
        Assert.False(IfxTextConvert.TryParseDecimal(text, out _));
    }

    [Fact]
    public void ParseDecimal_accepts_loose_thousands_separators_when_the_environment_says_so()
    {
        var lenient = new IfxTextFormat { StrictThousandsSeparator = false };
        Assert.Equal("1234.0", IfxTextConvert.FormatDecimal("1,23,4".AsSpan(), format: lenient));
    }

    [Fact]
    public void ParseDecimal_keeps_32_digits_and_truncates_the_rest()
    {
        // The C mantissa is 16 centesimal digits; further digits are dropped, not rounded.
        Assert.Equal("12345678901234567890123456789012000000.0",
            IfxTextConvert.FormatDecimal("12345678901234567890123456789012345678".AsSpan(), format: Plain));
    }

    // ===================================================================== //
    //  DECIMAL out (dectoasc)                                               //
    // ===================================================================== //

    [Theory]
    [InlineData("123.456", -1, 20, "123.456             ")]
    [InlineData("123.456", 2, 20, "123.46              ")]
    [InlineData("123.456", 0, 20, "123                 ")]
    [InlineData("123.456", 5, 20, "123.45600           ")]
    [InlineData("0", -1, 20, "0.0                 ")]
    [InlineData("-0.001", -1, 20, "-0.001              ")]
    [InlineData("9.99", 1, 10, "10.0      ")]           // half-up rounding
    [InlineData("-9.99", 1, 10, "-10.0     ")]
    [InlineData("-0.0", -1, 10, "0.0       ")]
    [InlineData("0.5", -1, 2, "1 ")]                    // the scale shrinks to make the value fit
    [InlineData("123.456", -1, 6, "123.46")]
    [InlineData("123.456", -1, 5, "123.5")]
    public void FormatDecimal_fills_the_buffer_left_justified_and_blank_padded(string value, int scale, int length, string expected)
    {
        var buffer = new char[length];
        Assert.True(IfxTextConvert.TryFormatDecimal(value.AsSpan(), buffer, scale, Plain));
        Assert.Equal(expected, new string(buffer));
    }

    [Theory]
    [InlineData("1234567890", 8, "1.235e09")]           // falls back to exponential notation
    [InlineData("1e30", 6, "1e30  ")]
    [InlineData("1e-20", 10, "1e-20     ")]
    [InlineData("-1e30", 8, "-1e30   ")]
    [InlineData("1.5e-100", 20, "1.5e-100            ")]
    public void FormatDecimal_uses_exponential_notation_when_the_fixed_form_does_not_fit(string value, int length, string expected)
    {
        var buffer = new char[length];
        Assert.True(IfxTextConvert.TryFormatDecimal(value.AsSpan(), buffer, -1, Plain));
        Assert.Equal(expected, new string(buffer));
    }

    [Fact]
    public void FormatDecimal_fills_the_buffer_with_asterisks_when_nothing_fits()
    {
        var buffer = new char[3];
        Assert.False(IfxTextConvert.TryFormatDecimal("-3.2e-11".AsSpan(), buffer, 3, Plain));
        Assert.Equal("***", new string(buffer));
    }

    [Fact]
    public void FormatDecimal_blanks_the_buffer_for_SQL_NULL()
    {
        var buffer = new char[5];
        Assert.True(IfxTextConvert.TryFormatDecimal(IfxDecimal.Null, buffer));
        Assert.Equal("     ", new string(buffer));
    }

    // ===================================================================== //
    //  SMALLINT / INT in (rstoi, rstol)                                     //
    // ===================================================================== //

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-5", -5)]
    [InlineData("+5", 5)]
    [InlineData(" 42 ", 42)]
    [InlineData("4 2", 4)]              // a blank ends the number
    [InlineData("32767", 32767)]
    public void ParseInt32_reads_a_number(string text, int expected) =>
        Assert.Equal(expected, IfxTextConvert.ParseInt32(text));

    [Fact]
    public void ParseInt32_reads_a_blank_string_as_SQL_NULL() => Assert.Null(IfxTextConvert.ParseInt32(""));

    [Theory]
    [InlineData("42x", -1213)]
    [InlineData("abc", -1213)]
    [InlineData("32768", -1214)]
    [InlineData("-32768", -1214)]       // the C overflow test is symmetric: -32768 overflows too
    [InlineData("2147483647", -1214)]
    public void ParseInt32_reports_the_library_error_codes(string text, int code) =>
        Assert.Equal(code, Assert.Throws<IfxException>(() => IfxTextConvert.ParseInt32(text)).IfxSqlCode);

    [Theory]
    [InlineData("2147483647", 2147483647L)]
    [InlineData("-2147483647", -2147483647L)]
    [InlineData("32768", 32768L)]
    public void ParseInt64_reads_an_int_sized_number(string text, long expected) =>
        Assert.Equal(expected, IfxTextConvert.ParseInt64(text));

    [Theory]
    [InlineData("2147483648", -1215)]
    [InlineData("-2147483648", -1215)]
    [InlineData("9223372036854775807", -1215)]
    [InlineData("abc", -1213)]
    public void ParseInt64_reports_the_library_error_codes(string text, int code) =>
        Assert.Equal(code, Assert.Throws<IfxException>(() => IfxTextConvert.ParseInt64(text)).IfxSqlCode);

    // ===================================================================== //
    //  BIGINT / INT8 (bigintcvasc, ifx_int8cvasc, biginttoasc, int8toasc)   //
    // ===================================================================== //

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("-123", -123L)]
    [InlineData("5.9", 5L)]             // truncation towards zero
    [InlineData("-5.9", -5L)]
    [InlineData("1e3", 1000L)]
    [InlineData("2.5e2", 250L)]
    [InlineData("1500e-3", 1L)]
    [InlineData("1,234", 1234L)]
    [InlineData("0x1f", 31L)]
    [InlineData("  42  ", 42L)]
    [InlineData("9223372036854775807", 9223372036854775807L)]
    [InlineData(".0284722", 0L)]
    public void ParseBigInt_reads_a_number(string text, long expected) =>
        Assert.Equal(expected, IfxTextConvert.ParseBigInt(text));

    [Theory]
    [InlineData("1.5e-13", 0L)]         // bigintcvasc answers 15 here: it drops the shift
    [InlineData("1e-13", 0L)]           // ... and 1 here
    [InlineData("0.5e-1", 0L)]          // ... and 5 here
    [InlineData("1234e-5", 0L)]         // ... and 1234 here
    [InlineData("12345e-5", 0L)]        // the shift fits the digits, so the C library agrees
    [InlineData("123456789e-5", 1234L)]
    [InlineData("1500e-3", 1L)]
    public void ParseBigInt_applies_a_negative_exponent_the_C_library_would_drop(string text, long expected)
    {
        // The one deliberate divergence from the C library: bigintcvasc drops a negative
        // exponent whose shift is wider than the digit string instead of applying it, so it
        // reads "1.5e-13" as 15 while the same value written "0.00000000000015" is 0. The
        // exponent is applied here whatever its size.
        Assert.Equal(expected, IfxTextConvert.ParseBigInt(text));
    }

    [Fact]
    public void ParseBigInt_reads_a_blank_string_as_SQL_NULL() => Assert.Null(IfxTextConvert.ParseBigInt(""));

    [Theory]
    [InlineData("abc", -1213)]
    [InlineData("9223372036854775808", -1284)]
    [InlineData("-9223372036854775808", -1284)]     // the most negative long is the BIGINT null
    [InlineData("1e19", -1284)]
    public void ParseBigInt_reports_the_library_error_codes(string text, int code) =>
        Assert.Equal(code, Assert.Throws<IfxException>(() => IfxTextConvert.ParseBigInt(text)).IfxSqlCode);

    [Theory]
    [InlineData(123L, 10, "123")]
    [InlineData(-123L, 10, "-123")]
    [InlineData(123L, 16, "0x000000000000007b")]
    [InlineData(-123L, 16, "0xffffffffffffff85")]
    public void FormatBigInt_writes_base_10_and_base_16(long value, int numericBase, string expected) =>
        Assert.Equal(expected, IfxTextConvert.FormatBigInt(value, numericBase));

    [Fact]
    public void TryFormatInt8_blank_pads_the_buffer()
    {
        var buffer = new char[10];
        Assert.True(IfxTextConvert.TryFormatInt8(-42, buffer));
        Assert.Equal("-42       ", new string(buffer));

        Assert.True(IfxTextConvert.TryFormatInt8(null, buffer));
        Assert.Equal("          ", new string(buffer));

        Assert.False(IfxTextConvert.TryFormatInt8(1234567890123L, new char[4]));
    }

    // ===================================================================== //
    //  DATE (rstrdate, rdatestr)                                            //
    // ===================================================================== //

    [Theory]
    [InlineData("12/31/2008", 2008, 12, 31)]
    [InlineData("1/2/08", 2008, 1, 2)]
    [InlineData("1-2-08", 2008, 1, 2)]          // any non-digit separates the fields
    [InlineData("1.2.2008", 2008, 1, 2)]
    [InlineData("12312008", 2008, 12, 31)]      // no separators at all
    [InlineData("  12/31/2008  ", 2008, 12, 31)]
    [InlineData("2/29/2008", 2008, 2, 29)]
    public void ParseDate_reads_the_default_mdy_format(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), IfxTextConvert.ParseDate(text, With()));

    [Theory]
    [InlineData("2008-12-31", -1204)]           // "2008" cannot be a two-digit month plus a year
    [InlineData("31/12/2008", -1205)]
    [InlineData("13/1/2008", -1205)]
    [InlineData("2/30/2008", -1206)]
    [InlineData("1231908", -1204)]              // a three-digit year is never valid
    [InlineData("12/31/2008 extra", -1218)]
    public void ParseDate_reports_the_library_error_codes(string text, int code) =>
        Assert.Equal(code, Assert.Throws<IfxException>(() => IfxTextConvert.ParseDate(text, With())).IfxSqlCode);

    [Fact]
    public void ParseDate_reads_a_blank_string_as_the_null_date() => Assert.Null(IfxTextConvert.ParseDate("", With()));

    [Theory]
    [InlineData("MDY4/", "12/31/2008")]
    [InlineData("DMY2.", "31.12.08")]
    [InlineData("Y4MD-", "2008-12-31")]
    [InlineData("MDY40", "12312008")]           // a '0' separator means none at all
    [InlineData("DMY4-", "31-12-2008")]
    public void FormatDate_writes_the_DBDATE_format(string dbDate, string expected) =>
        Assert.Equal(expected, IfxTextConvert.FormatDate(new DateOnly(2008, 12, 31), With(dbDate)));

    [Theory]
    [InlineData("DMY2.", "31/12/2008", 2008, 12, 31)]
    [InlineData("Y4MD-", "2008-12-31", 2008, 12, 31)]
    [InlineData("DMY2.", "31 Dec 2008", 2008, 12, 31)]   // a spelled-out month is accepted
    public void ParseDate_follows_DBDATE(string dbDate, string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), IfxTextConvert.ParseDate(text, With(dbDate)));

    [Fact]
    public void An_incomplete_DBDATE_is_rejected_with_1212()
    {
        var format = With("MD/");
        Assert.Equal(-1212, Assert.Throws<IfxException>(() => IfxTextConvert.ParseDate("1/2/2008", format)).IfxSqlCode);
        Assert.Equal(-1212, Assert.Throws<IfxException>(() => IfxTextConvert.FormatDate(new DateOnly(2008, 1, 2), format)).IfxSqlCode);
    }

    [Theory]
    // Reference date 2026-09-06; R (the default) takes the present century.
    [InlineData(null, "1/2/08", 2008)]
    [InlineData(null, "1/2/68", 2068)]
    [InlineData("R", "1/2/68", 2068)]
    [InlineData("P", "1/2/08", 2008)]           // past: 2008 is already behind us
    [InlineData("P", "1/2/68", 1968)]
    [InlineData("F", "1/2/08", 2108)]           // future: 2008 is behind us, so add a century
    [InlineData("F", "1/2/68", 2068)]
    [InlineData("C", "1/2/08", 2008)]           // closest to the reference date
    [InlineData("C", "1/2/68", 2068)]
    public void DBCENTURY_expands_a_two_digit_year(string? century, string text, int expectedYear) =>
        Assert.Equal(expectedYear, IfxTextConvert.ParseDate(text, With(century: century))!.Value.Year);

    [Theory]
    [InlineData("%d.%m.%Y", "31.12.2008")]
    [InlineData("%Y-%m-%d", "2008-12-31")]
    [InlineData("%d %b %Y", "31 Dec 2008")]
    [InlineData("%m/%d/%y", "12/31/08")]
    public void GL_DATE_drives_both_directions(string pattern, string text)
    {
        var format = With(glDate: pattern);
        var date = new DateOnly(2008, 12, 31);
        Assert.Equal(text, IfxTextConvert.FormatDate(date, format));
        Assert.Equal(date, IfxTextConvert.ParseDate(text, format));
    }

    [Fact]
    public void DBDATE_wins_over_GL_DATE()
    {
        var format = With("MDY4/", glDate: "%d.%m.%Y");
        Assert.Equal("12/31/2008", IfxTextConvert.FormatDate(new DateOnly(2008, 12, 31), format));
    }

    [Fact]
    public void GL_DATE_takes_a_four_digit_year_literally()
    {
        // %Y is a full year, so DBCENTURY never applies to it -- unlike %y.
        var format = With(century: "R", glDate: "%d.%m.%Y");
        Assert.Equal(76, IfxTextConvert.ParseDate("20.8.76", format)!.Value.Year);
        Assert.Equal(2076, IfxTextConvert.ParseDate("20.8.76", With(century: "R", glDate: "%d.%m.%y"))!.Value.Year);
    }

    [Theory]
    [InlineData(0, 1899, 12, 31)]
    [InlineData(1, 1900, 1, 1)]
    [InlineData(-1, 1899, 12, 30)]
    [InlineData(39812, 2008, 12, 31)]
    public void The_internal_date_counts_days_from_the_last_day_of_1899(int internalDate, int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);
        Assert.Equal(date, IfxTextConvert.FromInternalDate(internalDate));
        Assert.Equal(internalDate, IfxTextConvert.ToInternalDate(date));
    }

    // ===================================================================== //
    //  DATETIME (dtcvasc, dttoasc)                                          //
    // ===================================================================== //

    [Theory]
    [InlineData("2008-12-31 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Second, "2008-12-31 10:20:30")]
    [InlineData("2008-12-31", IfxTimeUnit.Year, IfxTimeUnit.Day, "2008-12-31")]
    [InlineData("2008-12-31 10:20:30.12345", IfxTimeUnit.Year, IfxTimeUnit.Fraction5, "2008-12-31 10:20:30.12345")]
    [InlineData("2008-12-31 10:20:30.5", IfxTimeUnit.Year, IfxTimeUnit.Fraction5, "2008-12-31 10:20:30.50000")]
    [InlineData("10:20:30", IfxTimeUnit.Hour, IfxTimeUnit.Second, "10:20:30")]
    [InlineData("2008-2-3 4:5:6", IfxTimeUnit.Year, IfxTimeUnit.Second, "2008-02-03 04:05:06")]
    [InlineData("  2008-12-31 10:20:30  ", IfxTimeUnit.Year, IfxTimeUnit.Second, "2008-12-31 10:20:30")]
    [InlineData("08-12-31 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Second, "2008-12-31 10:20:30")]
    public void DateTime_round_trips_the_ANSI_form(string text, IfxTimeUnit start, IfxTimeUnit end, string expected)
    {
        var value = IfxTextConvert.ParseDateTime(text, start, end, With());
        Assert.Equal(expected, IfxTextConvert.FormatDateTime(value, With()));
    }

    [Theory]
    [InlineData("2008-12-31 10:20", IfxTimeUnit.Year, IfxTimeUnit.Second, -1262)]
    [InlineData("garbage", IfxTimeUnit.Year, IfxTimeUnit.Second, -1262)]
    [InlineData("2008-13-31", IfxTimeUnit.Year, IfxTimeUnit.Day, -1263)]
    [InlineData("2008-12-31 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Day, -1264)]
    [InlineData("2008-12-31 10:20:30 extra", IfxTimeUnit.Year, IfxTimeUnit.Second, -1264)]
    public void ParseDateTime_reports_the_library_error_codes(string text, IfxTimeUnit start, IfxTimeUnit end, int code)
    {
        var error = Assert.Throws<IfxException>(() => IfxTextConvert.ParseDateTime(text, start, end, With()));
        Assert.Equal(code, error.IfxSqlCode);
        Assert.False(IfxTextConvert.TryParseDateTime(text, start, end, out _, With()));
    }

    [Fact]
    public void ParseDateTime_reads_a_blank_string_as_SQL_NULL()
    {
        var value = IfxTextConvert.ParseDateTime("", IfxTimeUnit.Year, IfxTimeUnit.Second, With());
        Assert.True(value.IsNull);
        Assert.Equal(string.Empty, IfxTextConvert.FormatDateTime(value));
    }

    [Fact]
    public void ParseDateTime_expands_a_two_digit_year_with_DBCENTURY()
    {
        var past = IfxTextConvert.ParseDateTime("68-12-31 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Second, With(century: "P"));
        Assert.Equal("1968-12-31 10:20:30", IfxTextConvert.FormatDateTime(past, With(century: "P")));
    }

    [Fact]
    public void FormatDateTime_uses_GL_DATETIME_when_it_is_set()
    {
        var format = With(glDateTime: "%d/%m/%Y %H:%M:%S");
        var value = IfxTextConvert.ParseDateTime("2008-12-31 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Second, format);
        Assert.Equal("31/12/2008 10:20:30", IfxTextConvert.FormatDateTime(value, format));
    }

    [Fact]
    public void ParseDateTime_ignores_GL_DATETIME_and_DBDATE()
    {
        // The C function reads the ANSI form only, whatever the formatting variables say.
        var format = With("DMY2.", glDateTime: "%d/%m/%Y %H:%M:%S");
        Assert.False(IfxTextConvert.TryParseDateTime("31/12/2008 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Second, out _, format));
        Assert.True(IfxTextConvert.TryParseDateTime("2008-12-31 10:20:30", IfxTimeUnit.Year, IfxTimeUnit.Second, out _, format));
    }

    // ===================================================================== //
    //  INTERVAL (incvasc, intoasc)                                          //
    // ===================================================================== //

    [Theory]
    [InlineData("5 03:24:00", IfxTimeUnit.Day, IfxTimeUnit.Second, 2, "  5 03:24:00")]
    [InlineData("-5 03:24:00", IfxTimeUnit.Day, IfxTimeUnit.Second, 2, " -5 03:24:00")]
    [InlineData("100 10:00:00", IfxTimeUnit.Day, IfxTimeUnit.Second, 3, " 100 10:00:00")]
    [InlineData("10:20", IfxTimeUnit.Hour, IfxTimeUnit.Minute, 2, " 10:20")]
    [InlineData("1 02:03:04.12345", IfxTimeUnit.Day, IfxTimeUnit.Fraction5, 2, "  1 02:03:04.12345")]
    [InlineData("  5 03:24:00  ", IfxTimeUnit.Day, IfxTimeUnit.Second, 2, "  5 03:24:00")]
    public void Interval_round_trips_a_day_to_fraction_value(string text, IfxTimeUnit start, IfxTimeUnit end, int precision, string expected)
    {
        var value = IfxTextConvert.ParseDayTimeInterval(text, start, end, precision);
        Assert.Equal(expected, IfxTextConvert.FormatInterval(value));
    }

    [Theory]
    [InlineData("3-04", 4, "    3-04")]
    [InlineData("-3-04", 4, "   -3-04")]
    [InlineData("3-04", 2, "  3-04")]
    public void Interval_round_trips_a_year_to_month_value(string text, int precision, string expected)
    {
        var value = IfxTextConvert.ParseYearMonthInterval(text, IfxTimeUnit.Year, IfxTimeUnit.Month, precision);
        Assert.Equal(expected, IfxTextConvert.FormatInterval(value));
    }

    [Fact]
    public void FormatInterval_writes_the_leading_field_right_aligned_behind_one_blank()
    {
        // DAY(4) TO SECOND: the leading zeros become blanks and the sign takes the blank in
        // front of the first significant digit.
        var value = IfxTextConvert.ParseDayTimeInterval("-5 03:24:00", IfxTimeUnit.Day, IfxTimeUnit.Second, 4);
        Assert.Equal("   -5 03:24:00", IfxTextConvert.FormatInterval(value));
    }

    [Fact]
    public void ParseInterval_reads_a_blank_string_as_SQL_NULL()
    {
        var dayTime = IfxTextConvert.ParseDayTimeInterval("", IfxTimeUnit.Day, IfxTimeUnit.Second);
        Assert.True(dayTime.IsNull);
        Assert.Equal(string.Empty, IfxTextConvert.FormatInterval(dayTime));

        var yearMonth = IfxTextConvert.ParseYearMonthInterval("", IfxTimeUnit.Year, IfxTimeUnit.Month);
        Assert.True(yearMonth.IsNull);
        Assert.Equal(string.Empty, IfxTextConvert.FormatInterval(yearMonth));
    }

    [Fact]
    public void ParseInterval_reports_the_library_error_codes()
    {
        var error = Assert.Throws<IfxException>(() =>
            IfxTextConvert.ParseDayTimeInterval("garbage", IfxTimeUnit.Day, IfxTimeUnit.Second));
        Assert.Equal(-1262, error.IfxSqlCode);
        Assert.False(IfxTextConvert.TryParseDayTimeInterval("5 03:24", IfxTimeUnit.Day, IfxTimeUnit.Second, out _));
    }

    // ===================================================================== //
    //  formats                                                            //
    // ===================================================================== //

    [Fact]
    public void A_call_without_a_format_ignores_the_process_environment()
    {
        string? previous = Environment.GetEnvironmentVariable("DBDATE");
        try
        {
            Environment.SetEnvironmentVariable("DBDATE", "DMY4-");
            Assert.Equal("12/31/2008", IfxTextConvert.FormatDate(new DateOnly(2008, 12, 31)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBDATE", previous);
        }
    }

    [Fact]
    public void The_default_format_uses_the_C_locale_separators()
    {
        Assert.Equal('.', IfxTextFormat.Default.DecimalSeparator);
        Assert.Equal(',', IfxTextFormat.Default.ThousandsSeparator);
        Assert.True(IfxTextFormat.Default.StrictThousandsSeparator);
    }

    [Fact]
    public void Separators_can_be_switched_to_a_European_convention()
    {
        var format = new IfxTextFormat { DecimalSeparator = ',', ThousandsSeparator = '.' };
        Assert.Equal("1234,5", IfxTextConvert.FormatDecimal("1.234,5".AsSpan(), format: format));
    }
}
