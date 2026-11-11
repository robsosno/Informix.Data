using System;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Binding the new default read types back to the wire: <see cref="TimeOnly"/> (DATETIME
/// HOUR-start), <see cref="TimeSpan"/>/<see cref="IfxTimeSpan"/> (day-time INTERVAL) and
/// <see cref="IfxMonthSpan"/> (year-month INTERVAL) — the round-trip symmetry half of the
/// DateOnly/TimeOnly/TimeSpan default-mapping change.
/// </summary>
public class TimeBindingTests
{
    private static readonly short H2S = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Second);
    private static readonly short H2F5 = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Fraction5);
    private static readonly short DayToSecond = IfxQualifier.ForInterval(2, IfxTimeUnit.Day, IfxTimeUnit.Second);
    private static readonly short DayToFraction5 = IfxQualifier.ForInterval(2, IfxTimeUnit.Day, IfxTimeUnit.Fraction5);

    [Fact]
    public void TimeOnly_without_fraction_binds_as_hour_to_second()
    {
        var bind = SqliBindValue.From(new TimeOnly(12, 34, 56), IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.DateTime, bind.Type);
        Assert.Equal(H2S, bind.Precision);
        int declaredLength = (bind.Data![0] << 8) | bind.Data[1];
        Assert.Equal(bind.Data.Length - 2, declaredLength);

        var decoded = PackedValue.FromWire(bind.Data.AsSpan(2), H2S, isDateTime: true);
        Assert.Equal(new TimeOnly(12, 34, 56), decoded.ToTimeOnly());
    }

    [Fact]
    public void TimeOnly_with_fraction_binds_as_hour_to_fraction5()
    {
        var t = new TimeOnly(1, 2, 3).Add(TimeSpan.FromTicks(78901 * 100));
        var bind = SqliBindValue.From(t, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.DateTime, bind.Type);
        Assert.Equal(H2F5, bind.Precision);

        var decoded = PackedValue.FromWire(bind.Data!.AsSpan(2), H2F5, isDateTime: true);
        Assert.Equal(t, decoded.ToTimeOnly());
    }

    [Fact]
    public void TimeOnly_pinned_to_IfxType_Date_throws_before_touching_the_wire()
    {
        Assert.Throws<NotSupportedException>(() =>
            SqliBindValue.From(new TimeOnly(1, 2, 3), IfxWireConverter.DefaultEncoding, IfxTypeMap.ToWire(IfxType.Date)));
    }

    [Fact]
    public void TimeOnly_pinned_to_IfxType_Time_binds_the_hour_start_path()
    {
        var bind = SqliBindValue.From(new TimeOnly(1, 2, 3), IfxWireConverter.DefaultEncoding, IfxTypeMap.ToWire(IfxType.Time));
        Assert.Equal(SqliTypeCode.DateTime, bind.Type);
        Assert.Equal(H2S, bind.Precision);
    }

    [Fact]
    public void DateOnly_bind_is_unaffected()
    {
        var bind = SqliBindValue.From(new DateOnly(2026, 7, 14), IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Date, bind.Type);
        Assert.Equal(IfxWireConverter.ToDate(new DateTime(2026, 7, 14)), bind.Data);
    }

    [Fact]
    public void TimeSpan_without_fraction_binds_as_day_to_second()
    {
        var span = new TimeSpan(1, 2, 3, 4); // 1d 02:03:04
        var bind = SqliBindValue.From(span, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Interval, bind.Type);
        Assert.Equal(DayToSecond, bind.Precision);

        var decoded = PackedValue.FromWire(bind.Data!.AsSpan(2), DayToSecond);
        var df = Assert.IsType<IfxIntervalDayFraction>(decoded.ToInterval());
        Assert.Equal(span, df.ToTimeSpan());
    }

    [Fact]
    public void TimeSpan_with_fraction_binds_as_day_to_fraction5()
    {
        var span = new TimeSpan(1, 2, 3, 4, 567, 890); // 1d 02:03:04.56789
        var bind = SqliBindValue.From(span, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Interval, bind.Type);
        Assert.Equal(DayToFraction5, bind.Precision);

        var decoded = PackedValue.FromWire(bind.Data!.AsSpan(2), DayToFraction5);
        var df = Assert.IsType<IfxIntervalDayFraction>(decoded.ToInterval());
        Assert.Equal(span, df.ToTimeSpan());
    }

    [Fact]
    public void IfxTimeSpan_round_trips_through_bind()
    {
        // The provider type the reader hands out (via the public TimeSpan bridge) must
        // bind back — same day-time shape end to end.
        var span = new TimeSpan(1, 2, 3, 4, 567, 890); // 1d 02:03:04.56789
        var ts = new IfxTimeSpan(span);
        var bind = SqliBindValue.From(ts, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Interval, bind.Type);

        var decoded = PackedValue.FromWire(bind.Data!.AsSpan(2), bind.Precision);
        var df = Assert.IsType<IfxIntervalDayFraction>(decoded.ToInterval());
        Assert.Equal(span, df.ToTimeSpan());
    }

    [Fact]
    public void IfxMonthSpan_round_trips_through_bind()
    {
        var ms = new IfxMonthSpan(3, 7); // 43 months
        var bind = SqliBindValue.From(ms, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Interval, bind.Type);

        var decoded = PackedValue.FromWire(bind.Data!.AsSpan(2), bind.Precision);
        var ym = Assert.IsType<IfxIntervalYearMonth>(decoded.ToInterval());
        Assert.Equal(43, ym.Months);
    }

    [Fact]
    public void Null_IfxTimeSpan_binds_sql_null()
    {
        var bind = SqliBindValue.From(IfxTimeSpan.Null, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Interval, bind.Type);
        Assert.Null(bind.Data);
    }

    [Fact]
    public void Null_IfxMonthSpan_binds_sql_null()
    {
        var bind = SqliBindValue.From(IfxMonthSpan.Null, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Interval, bind.Type);
        Assert.Null(bind.Data);
    }
}
