using System;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Canned-tuple coverage for the new default read mappings: DATE -&gt; DateOnly,
/// a time-only DATETIME (start HOUR or finer) -&gt; TimeOnly, a date-bearing DATETIME
/// stays DateTime, a day-time INTERVAL -&gt; TimeSpan, a year-month INTERVAL stays
/// IfxMonthSpan. Uses the same <see cref="SqliStatementKit"/>/<see cref="PackedValue"/>
/// harness as <c>SqliStatementTests.cs</c>.
/// </summary>
public class DateTimeIntervalMappingTests
{
    // Strips the 2-byte bind-only length prefix: SqliSession.DecodeValue slices packed
    // tuple fields directly (bare bytes), unlike a SQ_BIND payload.
    private static byte[] Packed(PackedValue d) => d.ToWireBytes()[2..];

    private static readonly short H2S = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Second);
    private static readonly short H2F5 = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Fraction5);
    private static readonly short M2S = IfxQualifier.ForDateTime(IfxTimeUnit.Minute, IfxTimeUnit.Second);
    private static readonly short Y2S = IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Second);
    private static readonly short D2S = IfxQualifier.ForDateTime(IfxTimeUnit.Day, IfxTimeUnit.Second);
    private static readonly short DayToSecond = IfxQualifier.ForInterval(2, IfxTimeUnit.Day, IfxTimeUnit.Second);
    private static readonly short DayToFraction5 = IfxQualifier.ForInterval(2, IfxTimeUnit.Day, IfxTimeUnit.Fraction5);
    private static readonly short YearToMonth = IfxQualifier.ForInterval(4, IfxTimeUnit.Year, IfxTimeUnit.Month);

    // A one-column SELECT script: prepare (describe+done+eot), open+fetch (done+tuple+close+eot,
    // fixed-layout single-PDU), release (eot). Mirrors SqliQueryExecutionTests.SelectScript.
    private static byte[] OneColumnScript(SqliTypeCode type, int length, byte[] row) =>
        SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: (short)row.Length,
                new SqliStatementKit.Col("v", (short)type, length, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

    private static IfxDataReader OpenOneColumnReader(SqliTypeCode type, int length, byte[] row, out IfxConnection cn)
    {
        cn = SqliStatementKit.OpenConnection(OneColumnScript(type, length, row), out _);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return reader;
    }

    [Fact]
    public void Date_column_decodes_as_DateOnly()
    {
        var date = new DateTime(2026, 7, 14);
        byte[] row = IfxWireConverter.ToDate(date);
        using var reader = OpenOneColumnReader(SqliTypeCode.Date, 4, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(DateOnly), reader.GetFieldType(0));
            Assert.Equal(new DateOnly(2026, 7, 14), reader.GetValue(0));
            // Escape hatch: the old DateTime default still reachable via GetFieldValue<T>.
            Assert.Equal(date, reader.GetFieldValue<DateTime>(0));
            Assert.Equal(date, reader.GetDateTime(0));
            Assert.Equal(date, reader.GetDate(0));

            var schema = reader.GetSchemaTable()!;
            Assert.Equal(typeof(DateOnly), schema.Rows[0]["DataType"]);
        }
    }

    [Fact]
    public void Hour_to_second_datetime_decodes_as_TimeOnly()
    {
        byte[] row = Packed(PackedValue.FromTime(new TimeOnly(12, 34, 56)));
        using var reader = OpenOneColumnReader(SqliTypeCode.DateTime, H2S, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(TimeOnly), reader.GetFieldType(0));
            Assert.Equal(new TimeOnly(12, 34, 56), reader.GetValue(0));
        }
    }

    [Fact]
    public void Hour_to_fraction5_datetime_preserves_fraction_ticks()
    {
        var time = new TimeOnly(1, 2, 3).Add(TimeSpan.FromTicks(78901 * 100));
        byte[] row = Packed(PackedValue.FromTime(time));
        using var reader = OpenOneColumnReader(SqliTypeCode.DateTime, H2F5, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(time, reader.GetValue(0));
        }
    }

    [Fact]
    public void Minute_to_second_datetime_also_decodes_as_TimeOnly()
    {
        // start(Minute)=8 >= 6 (HOUR): still "time-only" by the normalized rule even
        // though the leading field isn't HOUR. bexpon = (len+10-end+1)/2 = (4+10-10+1)/2 = 2.
        byte[] payload = [(byte)((2 + 64) | 0x80), 34, 56];
        var d = PackedValue.FromWire(payload, M2S, isDateTime: true);
        byte[] row = Packed(d);
        using var reader = OpenOneColumnReader(SqliTypeCode.DateTime, M2S, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(TimeOnly), reader.GetFieldType(0));
            var value = Assert.IsType<TimeOnly>(reader.GetValue(0));
            Assert.Equal(34, value.Minute);
            Assert.Equal(56, value.Second);
        }
    }

    [Fact]
    public void Year_to_second_datetime_stays_DateTime()
    {
        byte[] payload = [0xC7, 20, 26, 7, 14, 12, 34, 56];
        byte[] row = Packed(PackedValue.FromWire(payload, Y2S, isDateTime: true));
        using var reader = OpenOneColumnReader(SqliTypeCode.DateTime, Y2S, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
            Assert.Equal(new DateTime(2026, 7, 14, 12, 34, 56), reader.GetValue(0));
        }
    }

    [Fact]
    public void Day_to_second_datetime_stays_DateTime()
    {
        // start(Day)=4 < 6: has a date part, keeps the DateTime default (1700-quirk year).
        // bexpon = (len+10-end+1)/2 = (8+10-10+1)/2 = 4.
        byte[] payload = [(byte)((4 + 64) | 0x80), 14, 12, 34, 56];
        byte[] row = Packed(PackedValue.FromWire(payload, D2S, isDateTime: true));
        using var reader = OpenOneColumnReader(SqliTypeCode.DateTime, D2S, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
            var v = Assert.IsType<DateTime>(reader.GetValue(0));
            Assert.Equal(1700, v.Year); // missing-year quirk, unchanged
            Assert.Equal(14, v.Day);
            Assert.Equal(12, v.Hour);
        }
    }

    [Fact]
    public void GetFieldValue_DateTime_on_a_time_only_column_reproduces_the_1700_quirk()
    {
        byte[] row = Packed(PackedValue.FromTime(new TimeOnly(12, 34, 56)));
        using var reader = OpenOneColumnReader(SqliTypeCode.DateTime, H2S, row, out var cn);
        using (cn) using (reader)
        {
            DateTime v = reader.GetFieldValue<DateTime>(0);
            Assert.Equal(1700, v.Year);
            Assert.Equal(1, v.Day);
            Assert.Equal(12, v.Hour);
            Assert.Equal(34, v.Minute);
            Assert.Equal(56, v.Second);
            // GetDateTime must agree.
            Assert.Equal(v, reader.GetDateTime(0));
        }
    }

    [Fact]
    public void Day_to_second_interval_decodes_as_TimeSpan()
    {
        var interval = new IfxIntervalDayFraction(93784, 0, DayToSecond); // 1d 02:03:04
        byte[] row = Packed(PackedValue.FromInterval(interval));
        using var reader = OpenOneColumnReader(SqliTypeCode.Interval, DayToSecond, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(TimeSpan), reader.GetFieldType(0));
            Assert.Equal(new TimeSpan(1, 2, 3, 4), reader.GetValue(0));
            // Escape hatch: the provider type is still reachable.
            var ifxTs = reader.GetFieldValue<IfxTimeSpan>(0);
            Assert.False(ifxTs.IsNull);
            Assert.Equal(new TimeSpan(1, 2, 3, 4), reader.GetTimeSpan(0));

            var schema = reader.GetSchemaTable()!;
            Assert.Equal(typeof(TimeSpan), schema.Rows[0]["DataType"]);
        }
    }

    [Fact]
    public void Day_to_fraction5_interval_preserves_fraction()
    {
        var interval = new IfxIntervalDayFraction(93784, 567890000, DayToFraction5);
        byte[] row = Packed(PackedValue.FromInterval(interval));
        using var reader = OpenOneColumnReader(SqliTypeCode.Interval, DayToFraction5, row, out var cn);
        using (cn) using (reader)
        {
            var ts = Assert.IsType<TimeSpan>(reader.GetValue(0));
            Assert.Equal(interval.ToTimeSpan(), ts);
        }
    }

    [Fact]
    public void Year_to_month_interval_stays_IfxMonthSpan()
    {
        var interval = new IfxIntervalYearMonth(3, 7); // 43 months
        byte[] row = Packed(PackedValue.FromInterval(interval));
        using var reader = OpenOneColumnReader(SqliTypeCode.Interval, YearToMonth, row, out var cn);
        using (cn) using (reader)
        {
            Assert.Equal(typeof(IfxMonthSpan), reader.GetFieldType(0));
            var ms = Assert.IsType<IfxMonthSpan>(reader.GetValue(0));
            Assert.Equal(43, ms.TotalMonths);
            // New escape hatch: the signed total-month count as a plain long.
            Assert.Equal(43L, reader.GetFieldValue<long>(0));

            var schema = reader.GetSchemaTable()!;
            Assert.Equal(typeof(IfxMonthSpan), schema.Rows[0]["DataType"]);
        }
    }
}
