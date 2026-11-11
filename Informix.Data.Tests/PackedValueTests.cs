using System.Globalization;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

public class PackedValueTests
{
    private static decimal RoundTrip(string value)
    {
        var d = PackedValue.Parse(value);
        byte[] wire = d.ToWireBytes();
        // Strip the 2-byte length prefix; the payload is what FromWire decodes.
        var back = PackedValue.FromWire(wire.AsSpan(2), 0);
        return back.ToDecimal();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("123.45")]
    [InlineData("-123.45")]
    [InlineData("0.005")]
    [InlineData("-0.007")]
    [InlineData("99999999.99")]
    [InlineData("1234567890123456")]
    [InlineData("0.00000001")]
    [InlineData("-9999.9999")]
    [InlineData("150")]
    public void Decimal_round_trips_through_wire_bytes(string value)
    {
        Assert.Equal(decimal.Parse(value, CultureInfo.InvariantCulture), RoundTrip(value));
    }

    [Theory]
    [InlineData(123.45)]
    [InlineData(-0.001)]
    [InlineData(0)]
    [InlineData(79228162514264.3376)]
    public void FromDecimal_round_trips(decimal value)
    {
        var d = PackedValue.FromDecimal(value);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), 0);
        Assert.Equal(value, back.ToDecimal());
    }

    [Fact]
    public void Positive_wire_encoding_matches_the_informix_layout()
    {
        // 1.5 = 0.0150 x 100^1: exp byte (1+64)|0x80 = 0xC1, digits [01, 50].
        Assert.Equal(new byte[] { 0, 3, 0xC1, 1, 50 }, PackedValue.Parse("1.5").ToWireBytes());
    }

    [Fact]
    public void Negative_wire_encoding_uses_hundreds_complement()
    {
        // -1.5: digits 100's-complemented [98, 50], exp byte 0x41 ^ 0x7F = 0x3E.
        Assert.Equal(new byte[] { 0, 3, 0x3E, 98, 50 }, PackedValue.Parse("-1.5").ToWireBytes());
    }

    [Fact]
    public void Null_payload_decodes_as_null()
    {
        Assert.True(PackedValue.FromWire(ReadOnlySpan<byte>.Empty, 0).IsNull);
        Assert.True(PackedValue.FromWire(new byte[] { 0, 0 }, 0).IsNull);
        Assert.True(PackedValue.FromWire(new byte[] { 0 }, 0).IsNull);
    }

    [Fact]
    public void Null_encodes_as_zero_exponent_payload()
    {
        var nullDec = PackedValue.FromWire(new byte[] { 0, 0 }, 0);
        Assert.Equal(new byte[] { 0, 1, 0 }, nullDec.ToWireBytes());
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("0", "0")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("0.005", "0.005")]
    [InlineData("150", "150")]
    public void ToString_returns_normalized_form(string input, string expected)
    {
        Assert.Equal(expected, PackedValue.Parse(input).ToString());
    }

    [Fact]
    public void ToDecimal_on_null_throws()
    {
        var nullDec = PackedValue.FromWire(new byte[] { 0, 0 }, 0);
        Assert.Throws<InvalidOperationException>(() => nullDec.ToDecimal());
    }

    [Fact]
    public void Magnitude_beyond_decimal_range_throws_and_the_rich_error_names_the_subject()
    {
        // 9e29 — a valid Informix DECIMAL(32), above System.Decimal's ~7.92e28 maximum.
        var d = PackedValue.Parse("9" + new string('0', 29));
        var plain = Assert.Throws<OverflowException>(() => d.ToDecimal());

        var rich = d.DecimalRangeError("Column 'big' (DECIMAL(32))", plain);
        Assert.Contains("Column 'big' (DECIMAL(32))", rich.Message);
        Assert.Contains("900000000000000000000000000000", rich.Message); // the offending value
        Assert.Contains("GetDouble", rich.Message);                      // the reader escape hatch
        Assert.Contains("::float", rich.Message);                        // the SQL-cast workaround
        Assert.Same(plain, rich.InnerException);

        // A ROW/collection member has no per-member accessor: only the cast is offered.
        var member = d.DecimalRangeError("ROW member 'big'", plain, hasDoubleAccessor: false);
        Assert.DoesNotContain("GetDouble", member.Message);
        Assert.Contains("::lvarchar", member.Message);
    }

    [Fact]
    public void ToDouble_reads_a_magnitude_that_overflows_decimal()
    {
        var d = PackedValue.Parse("9" + new string('0', 29));
        Assert.Throws<OverflowException>(() => d.ToDecimal());
        Assert.Equal(9e29, d.ToDouble());
    }

    // One-column select of DECIMAL(32) floating (length word 0x20FF, tuple field =
    // exponent byte + 17 digit bytes = 18), carrying the packed digits of
    // <paramref name="value"/> — or a SQL NULL when it is null (a zero exponent byte).
    private static byte[] DecimalColumnScript(string? value)
    {
        byte[] field = new byte[18];
        if (value is not null)
            PackedValue.Parse(value).ToWireBytes().AsSpan(2).CopyTo(field); // [exp][digits], zero-padded

        return SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 18,
                new SqliStatementKit.Col("big_val", (short)SqliTypeCode.Decimal, 0x20FF, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(field),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release
    }

    [Fact]
    public void Out_of_range_decimal_column_names_the_column_in_the_overflow()
    {
        // 9e29 decodes fine as packed digits but cannot be represented as System.Decimal.
        using var cn = SqliStatementKit.OpenConnection(DecimalColumnScript("9" + new string('0', 29)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select big_val from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        var ex = Assert.Throws<OverflowException>(() => r.GetDecimal(0));
        Assert.Contains("big_val", ex.Message);
        Assert.Contains("DECIMAL(32)", ex.Message);      // floating scale renders without ",s"
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    [Fact]
    public void Out_of_range_decimal_column_still_reads_as_a_double()
    {
        using var cn = SqliStatementKit.OpenConnection(DecimalColumnScript("9" + new string('0', 29)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select big_val from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.Throws<OverflowException>(() => r.GetValue(0)); // the decimal path still throws
        Assert.Equal(9e29, r.GetDouble(0));
        Assert.Equal(9e29, r.GetFieldValue<double>(0));
    }

    [Fact]
    public void In_range_decimal_column_reads_the_same_value_through_both_paths()
    {
        using var cn = SqliStatementKit.OpenConnection(DecimalColumnScript("123.45"), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select big_val from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.Equal(123.45m, r.GetDecimal(0));
        Assert.Equal(123.45, r.GetDouble(0));
        Assert.Equal(123.45, r.GetFieldValue<double>(0));
    }

    [Fact]
    public void Null_decimal_column_keeps_the_ordinary_null_semantics_for_double()
    {
        using var cn = SqliStatementKit.OpenConnection(DecimalColumnScript(null), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select big_val from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.True(r.IsDBNull(0));
        Assert.Throws<InvalidCastException>(() => r.GetDouble(0));
        Assert.Throws<InvalidCastException>(() => r.GetFieldValue<double>(0));
    }
}

public class PackedDateTimeTests
{
    private static readonly short Y2F5 = IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Fraction5);
    private static readonly short Y2S = IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Second);
    private static readonly short H2S = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Second);

    [Fact]
    public void DateTime_round_trips_through_year_to_fraction5()
    {
        // 78901 x 10us = 7_890_100 ticks of sub-second fraction.
        var dt = new DateTime(2026, 7, 6, 12, 34, 56).AddTicks(78901 * 100);

        var d = PackedValue.FromDateTime(dt);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), Y2F5, isDateTime: true);

        Assert.Equal(dt, back.ToDateTime());
    }

    [Fact]
    public void DateTime_without_fraction_round_trips()
    {
        var dt = new DateTime(1999, 12, 31, 23, 59, 59);
        var d = PackedValue.FromDateTime(dt);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), Y2F5, isDateTime: true);
        Assert.Equal(dt, back.ToDateTime());
    }

    [Fact]
    public void Year_to_second_payload_decodes()
    {
        // Handcrafted server payload: exp 7 -> byte 0xC7, digits yy yy mo dd hh mi ss.
        byte[] payload = [0xC7, 20, 26, 7, 6, 12, 34, 56];
        var d = PackedValue.FromWire(payload, Y2S, isDateTime: true);
        Assert.Equal(new DateTime(2026, 7, 6, 12, 34, 56), d.ToDateTime());
    }

    [Fact]
    public void Hour_to_second_decodes_with_correct_leading_field()
    {
        // Exercises the TU_FLEN fix: for HOUR TO SECOND the leading field is
        // 2 digits wide (len - end + start = 6 - 10 + 6), not negative.
        var d = PackedValue.FromTime(new TimeOnly(12, 34, 56));
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), H2S, isDateTime: true);

        DateTime? result = back.ToDateTime();
        Assert.NotNull(result);
        Assert.Equal(12, result.Value.Hour);
        Assert.Equal(34, result.Value.Minute);
        Assert.Equal(56, result.Value.Second);
        Assert.Equal(1700, result.Value.Year); // Informix default for a missing year
        Assert.Equal(1, result.Value.Day);     // Informix default for a missing day
    }

    [Fact]
    public void Null_datetime_decodes_as_null()
    {
        var d = PackedValue.FromWire(new byte[] { 0, 0 }, Y2F5, isDateTime: true);
        Assert.Null(d.ToDateTime());
    }

    private static readonly short H2F5 = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Fraction5);

    [Fact]
    public void FromTime_without_fraction_uses_hour_to_second()
    {
        var d = PackedValue.FromTime(new TimeOnly(12, 34, 56));
        Assert.Equal(H2S, d.Qualifier);
    }

    [Fact]
    public void FromTime_with_fraction_uses_hour_to_fraction5_and_round_trips()
    {
        var t = new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(78901 * 100)); // 78901 x 10us

        var d = PackedValue.FromTime(t);
        Assert.Equal(H2F5, d.Qualifier);

        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), H2F5, isDateTime: true);
        Assert.Equal(t, back.ToTimeOnly());
    }

    [Fact]
    public void ToTimeOnly_decodes_whole_seconds()
    {
        var d = PackedValue.FromTime(new TimeOnly(1, 2, 3));
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), H2S, isDateTime: true);
        Assert.Equal(new TimeOnly(1, 2, 3), back.ToTimeOnly());
    }

    [Fact]
    public void ToTimeOnly_returns_null_for_sql_null()
    {
        var d = PackedValue.FromWire(new byte[] { 0, 0 }, H2S, isDateTime: true);
        Assert.Null(d.ToTimeOnly());
    }

    [Fact]
    public void ToTimeOnly_returns_null_when_not_read_as_datetime()
    {
        var d = PackedValue.Parse("123.45"); // a plain DECIMAL, not a DATETIME
        Assert.Null(d.ToTimeOnly());
    }
}

public class PackedIntervalTests
{
    [Fact]
    public void Day_fraction_interval_round_trips()
    {
        // 1 day 02:03:04.56789 = 93784 s + 567_890_000 ns.
        var interval = new IfxIntervalDayFraction(93784, 567890000);

        var d = PackedValue.FromInterval(interval);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), interval.Qualifier);

        var result = Assert.IsType<IfxIntervalDayFraction>(back.ToInterval());
        Assert.Equal(93784, result.Seconds);
        Assert.Equal(567890000, result.NanoSeconds);
    }

    [Fact]
    public void Negative_day_fraction_interval_round_trips()
    {
        var interval = new IfxIntervalDayFraction(-3661, -10000);

        var d = PackedValue.FromInterval(interval);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), interval.Qualifier);

        var result = Assert.IsType<IfxIntervalDayFraction>(back.ToInterval());
        Assert.Equal(-3661, result.Seconds);
        Assert.Equal(-10000, result.NanoSeconds);
    }

    [Fact]
    public void Zero_day_fraction_interval_round_trips()
    {
        var interval = new IfxIntervalDayFraction(0, 0);
        var d = PackedValue.FromInterval(interval);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), interval.Qualifier);

        var result = Assert.IsType<IfxIntervalDayFraction>(back.ToInterval());
        Assert.Equal(0, result.Seconds);
        Assert.Equal(0, result.NanoSeconds);
    }

    [Fact]
    public void Year_month_interval_round_trips()
    {
        var interval = new IfxIntervalYearMonth(3, 7); // 43 months

        var d = PackedValue.FromInterval(interval);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), interval.Qualifier);

        var result = Assert.IsType<IfxIntervalYearMonth>(back.ToInterval());
        Assert.Equal(43, result.Months);
    }

    [Fact]
    public void Negative_year_month_interval_round_trips()
    {
        var interval = new IfxIntervalYearMonth(-43, new IfxIntervalYearMonth().Qualifier);

        var d = PackedValue.FromInterval(interval);
        var back = PackedValue.FromWire(d.ToWireBytes().AsSpan(2), interval.Qualifier);

        var result = Assert.IsType<IfxIntervalYearMonth>(back.ToInterval());
        Assert.Equal(-43, result.Months);
    }

    [Fact]
    public void Day_fraction_ToString_matches_informix_format()
    {
        Assert.Equal("1 02:03:04.56789", new IfxIntervalDayFraction(93784, 567890000).ToString());
    }

    [Fact]
    public void Year_month_ToString_matches_informix_format()
    {
        Assert.Equal("3-07", new IfxIntervalYearMonth(3, 7).ToString());
        Assert.Equal("-3-07", new IfxIntervalYearMonth(-3, -7).ToString());
    }

    [Fact]
    public void Day_fraction_parses_informix_format()
    {
        var qual = new IfxIntervalDayFraction().Qualifier;
        var interval = new IfxIntervalDayFraction("1 02:03:04.56789", qual);
        Assert.Equal(93784, interval.Seconds);
        Assert.Equal(567890000, interval.NanoSeconds);

        var negative = new IfxIntervalDayFraction("-1 02:03:04.56789", qual);
        Assert.Equal(-93784, negative.Seconds);
        Assert.Equal(-567890000, negative.NanoSeconds);
    }

    [Fact]
    public void Year_month_parses_informix_format()
    {
        var qual = new IfxIntervalYearMonth().Qualifier;
        Assert.Equal(43, new IfxIntervalYearMonth("3-07", qual).Months);
        Assert.Equal(-43, new IfxIntervalYearMonth("-3-07", qual).Months);
    }

    [Fact]
    public void TimeSpan_bridge_round_trips()
    {
        var span = new TimeSpan(1, 2, 3, 4, 567, 890); // 1d 02:03:04.56789
        var interval = new IfxIntervalDayFraction(span);
        Assert.Equal(span, interval.ToTimeSpan());
    }

    [Fact]
    public void Sign_normalization()
    {
        var mixed = new IfxIntervalDayFraction(-10, 500_000_000); // -10 s + 0.5 s => -9.5 s
        Assert.Equal(-9, mixed.Seconds);
        Assert.Equal(-500_000_000, mixed.NanoSeconds);

        var ym = new IfxIntervalYearMonth(-2, 3); // -2 y + 3 m => -1 y -9 m
        Assert.Equal(-21, ym.Months);
    }
}

public class QualifierTests
{
    [Fact]
    public void Datetime_qualifiers_encode_the_documented_values()
    {
        Assert.Equal(0x130F, IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Fraction5));
        Assert.Equal(0x066A, IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Second));
        Assert.Equal(0x0D4F, IfxQualifier.ForInterval(2, IfxTimeUnit.Day, IfxTimeUnit.Fraction5));
        Assert.Equal(0x0602, IfxQualifier.ForInterval(4, IfxTimeUnit.Year, IfxTimeUnit.Month));
    }

    [Theory]
    [InlineData(0x130F, 4)]  // YEAR TO FRACTION(5): year is 4 digits
    [InlineData(0x066A, 2)]  // HOUR TO SECOND: hour is 2 digits
    [InlineData(0x0D4F, 2)]  // DAY(2) TO FRACTION(5)
    [InlineData(0x0602, 4)]  // YEAR(4) TO MONTH
    public void Leading_field_width_uses_the_corrected_tu_flen(int qualifier, int expected)
    {
        Assert.Equal(expected, IfxQualifier.GetLeadingFieldWidth((short)qualifier));
    }

    [Fact]
    public void Scale_is_the_fraction_digit_count()
    {
        Assert.Equal(5, IfxQualifier.GetScale(0x130F));
        Assert.Equal(0, IfxQualifier.GetScale(0x066A));
    }

    [Fact]
    public void Type_names_render_like_the_jdbc_driver()
    {
        Assert.Equal("datetime year to fraction(5)", IfxQualifier.GetIfxTypeName(SqliTypeCode.DateTime, 0x130F));
        Assert.Equal("interval day to fraction(5)", IfxQualifier.GetIfxTypeName(SqliTypeCode.Interval, 0x0D4F));
    }
}

public class WirePackedStreamTests
{
    [Fact]
    public void Decimal_datetime_and_intervals_round_trip_through_the_stream()
    {
        var ms = new MemoryStream();
        var w = new Informix.Data.Wire.IfxWireWriter(ms);

        var dt = new DateTime(2026, 7, 6, 12, 34, 56).AddTicks(78901 * 100);
        var df = new IfxIntervalDayFraction(93784, 567890000);
        var ym = new IfxIntervalYearMonth(3, 7);

        w.WriteDecimal(-123.45m);
        w.WriteDateTime(dt);
        w.WriteInterval(df);
        w.WriteInterval(ym);

        ms.Position = 0;
        var r = new Informix.Data.Wire.IfxWireReader(ms);

        Assert.Equal(-123.45m, r.ReadDecimal(0));
        Assert.Equal(dt, r.ReadDateTime(IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Fraction5)));

        var dfBack = Assert.IsType<IfxIntervalDayFraction>(r.ReadInterval(df.Qualifier));
        Assert.Equal(df.Seconds, dfBack.Seconds);
        Assert.Equal(df.NanoSeconds, dfBack.NanoSeconds);

        var ymBack = Assert.IsType<IfxIntervalYearMonth>(r.ReadInterval(ym.Qualifier));
        Assert.Equal(ym.Months, ymBack.Months);

        Assert.Equal(ms.Length, ms.Position); // every pad byte consumed
    }

    [Fact]
    public void Null_decimal_reads_as_null()
    {
        var ms = new MemoryStream();
        // A NULL decimal on the wire: length 1, payload [0], plus the odd-length pad.
        ms.Write([0, 1, 0, 0]);
        ms.Position = 0;

        var r = new Informix.Data.Wire.IfxWireReader(ms);
        Assert.Null(r.ReadDecimal(0));
    }
}
