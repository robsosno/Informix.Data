using System;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Binding the driver's own value types as parameters: <see cref="IfxDecimal"/> and
/// <see cref="IfxDateTime"/> (with the qualifier it carries), and what <see cref="IfxBulkLoader"/>
/// accepts for the same columns, INTERVAL included.
/// </summary>
public class IfxValueBindingTests
{
    private static SqliBindValue Bind(object? value) => SqliBindValue.From(value, IfxWireConverter.DefaultEncoding);

    // ---- IfxDecimal ----

    [Fact]
    public void IfxDecimal_binds_exactly_like_the_decimal_it_holds()
    {
        var viaIfx = Bind(new IfxDecimal(1234.56m));
        var viaClr = Bind(1234.56m);
        Assert.Equal(SqliTypeCode.Decimal, viaIfx.Type);
        Assert.Equal(viaClr.Data, viaIfx.Data);
        Assert.Equal(viaClr.Precision, viaIfx.Precision);
    }

    [Fact]
    public void Null_IfxDecimal_binds_as_a_typed_null()
    {
        var bind = Bind(IfxDecimal.Null);
        Assert.Equal(SqliTypeCode.Decimal, bind.Type);
        Assert.Null(bind.Data);
    }

    // ---- IfxDateTime ----

    [Theory]
    [InlineData(IfxTimeUnit.Year, IfxTimeUnit.Minute, 6)]
    [InlineData(IfxTimeUnit.Year, IfxTimeUnit.Day, 4)]
    [InlineData(IfxTimeUnit.Month, IfxTimeUnit.Day, 2)]
    [InlineData(IfxTimeUnit.Year, IfxTimeUnit.Hour, 5)]
    [InlineData(IfxTimeUnit.Hour, IfxTimeUnit.Second, 3)]
    public void IfxDateTime_binds_with_its_own_qualifier_and_only_its_own_fields(IfxTimeUnit start, IfxTimeUnit end, int digits)
    {
        var value = Make(start, end);
        var bind = Bind(value);

        short qualifier = IfxQualifier.ForDateTime(start, end);
        Assert.Equal(SqliTypeCode.DateTime, bind.Type);
        Assert.Equal(qualifier, bind.Precision);

        // [length:2][exponent byte][digits...]
        int declaredLength = (bind.Data![0] << 8) | bind.Data[1];
        Assert.Equal(bind.Data.Length - 2, declaredLength);
        Assert.Equal(digits, bind.Data.Length - 3);

        // The base-100 point sits after the SECOND field, whether or not the qualifier reaches it.
        int pairsToSecond = start switch
        {
            IfxTimeUnit.Year => 7, IfxTimeUnit.Month => 5, IfxTimeUnit.Day => 4, IfxTimeUnit.Hour => 3, _ => throw new InvalidOperationException(),
        };
        Assert.Equal((byte)((pairsToSecond + 64) | 0x80), bind.Data[2]);

        // And it reads back as the same fields.
        var decoded = PackedValue.FromWire(bind.Data.AsSpan(2), qualifier, isDateTime: true);
        if (start == IfxTimeUnit.Hour)
        {
            Assert.Equal(new TimeOnly(13, 45, 7), decoded.ToTimeOnly());
        }
        else
        {
            var dt = decoded.ToDateTime()!.Value;
            if (start == IfxTimeUnit.Year) Assert.Equal(2024, dt.Year);
            Assert.Equal(5, dt.Month);
            Assert.Equal(17, dt.Day);
            if (end >= IfxTimeUnit.Hour) Assert.Equal(13, dt.Hour);
            if (end >= IfxTimeUnit.Minute) Assert.Equal(45, dt.Minute);
        }
    }

    [Theory]
    [InlineData(IfxTimeUnit.Fraction1, 1, 100)]      // .1     -> one digit, padded "10"
    [InlineData(IfxTimeUnit.Fraction3, 2, 123)]      // .123   -> "12" "30"
    [InlineData(IfxTimeUnit.Fraction5, 3, 123)]      // .12345 -> "12" "34" "50"
    public void IfxDateTime_fraction_digits_are_packed_in_pairs(IfxTimeUnit end, int fractionBytes, int expectedMillis)
    {
        var value = new IfxDateTime(2024, 5, 17, 13, 45, 7, 12345 / (end == IfxTimeUnit.Fraction1 ? 10000 : end == IfxTimeUnit.Fraction3 ? 100 : 1), end);
        var bind = Bind(value);

        short qualifier = IfxQualifier.ForDateTime(IfxTimeUnit.Year, end);
        Assert.Equal(qualifier, bind.Precision);
        Assert.Equal(7 + fractionBytes, bind.Data!.Length - 3);

        var decoded = PackedValue.FromWire(bind.Data.AsSpan(2), qualifier, isDateTime: true).ToDateTime()!.Value;
        Assert.Equal(new DateTime(2024, 5, 17, 13, 45, 7), decoded.AddTicks(-decoded.Ticks % TimeSpan.TicksPerSecond));
        Assert.Equal(expectedMillis, (int)(decoded.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMillisecond));
    }

    [Fact]
    public void Null_IfxDateTime_binds_as_a_typed_null()
    {
        var bind = Bind(IfxDateTime.Null);
        Assert.Equal(SqliTypeCode.DateTime, bind.Type);
        Assert.Null(bind.Data);
    }

    [Fact]
    public void IfxDateTime_at_midnight_stays_a_DATETIME_unlike_a_plain_DateTime()
    {
        var bind = Bind(new IfxDateTime(2024, 5, 17, IfxTimeUnit.Day));
        Assert.Equal(SqliTypeCode.DateTime, bind.Type);
        Assert.Equal(SqliTypeCode.Date, Bind(new DateTime(2024, 5, 17)).Type);
    }

    private static IfxDateTime Make(IfxTimeUnit start, IfxTimeUnit end) => (start, end) switch
    {
        (IfxTimeUnit.Year, IfxTimeUnit.Minute) => new IfxDateTime(2024, 5, 17, 13, 45, end),
        (IfxTimeUnit.Year, IfxTimeUnit.Day) => new IfxDateTime(2024, 5, 17, end),
        (IfxTimeUnit.Month, IfxTimeUnit.Day) => new IfxDateTime(5, 17, end),
        (IfxTimeUnit.Year, IfxTimeUnit.Hour) => new IfxDateTime(2024, 5, 17, 13, end),
        (IfxTimeUnit.Hour, IfxTimeUnit.Second) => new IfxDateTime(13, 45, 7, end),
        _ => throw new InvalidOperationException(),
    };

    // ---- IfxBulkLoader converters ----

    [Fact]
    public void Bulk_decimal_column_takes_an_IfxDecimal_and_its_null()
    {
        var convert = SqliBindValue.ConverterFor(IfxType.Decimal, 0, IfxWireConverter.DefaultEncoding);
        Assert.Equal(Bind(5.25m).Data, convert(new IfxDecimal(5.25m)).Data);
        Assert.Null(convert(IfxDecimal.Null).Data);
    }

    [Fact]
    public void Bulk_datetime_column_keeps_the_qualifier_of_an_IfxDateTime()
    {
        var convert = SqliBindValue.ConverterFor(IfxType.DateTime, 0, IfxWireConverter.DefaultEncoding);
        var bind = convert(new IfxDateTime(2024, 5, 17, 13, 45, IfxTimeUnit.Minute));
        Assert.Equal(IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Minute), bind.Precision);
        Assert.Null(convert(IfxDateTime.Null).Data);
    }

    [Fact]
    public void Bulk_day_time_interval_column_takes_TimeSpan_and_IfxTimeSpan()
    {
        var convert = SqliBindValue.ConverterFor(IfxType.IntervalDayFraction, 0, IfxWireConverter.DefaultEncoding);

        var fromClr = convert(TimeSpan.FromDays(3));
        var fromIfx = convert(new IfxTimeSpan(3, IfxTimeUnit.Day));
        Assert.Equal(SqliTypeCode.Interval, fromClr.Type);
        Assert.Equal(SqliTypeCode.Interval, fromIfx.Type);
        Assert.Equal(Bind(TimeSpan.FromDays(3)).Data, fromClr.Data);   // exactly what a command parameter sends

        Assert.Null(convert(null).Data);
        Assert.Null(convert(IfxTimeSpan.Null).Data);
        Assert.Throws<InvalidCastException>(() => convert("3 days"));
    }

    [Fact]
    public void Bulk_year_month_interval_column_takes_an_IfxMonthSpan()
    {
        var convert = SqliBindValue.ConverterFor(IfxType.IntervalYearMonth, 0, IfxWireConverter.DefaultEncoding);

        var bind = convert(new IfxMonthSpan(2, 6));
        Assert.Equal(SqliTypeCode.Interval, bind.Type);
        Assert.Equal(Bind(new IfxMonthSpan(2, 6)).Data, bind.Data);

        Assert.Null(convert(IfxMonthSpan.Null).Data);
        Assert.Throws<InvalidCastException>(() => convert(TimeSpan.FromDays(1)));
    }
}
