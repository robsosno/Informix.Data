using System.Data.Common;
using System.Globalization;
using System.Text;
using Informix.Data;

namespace Demo;

/// <summary>
/// Writes a reader's rows as an Informix UNLOAD file. Every value is turned into text by the
/// driver's own <see cref="IfxTextConvert"/>, with the formats of an <see cref="IfxTextFormat"/>,
/// so the file loads with dbload / LOAD (and reads back with IfxTextConvert.Parse*).
/// </summary>
/// <remarks>
/// A NULL is an empty field and every field is followed by '|'. Nothing here escapes '|' or '\':
/// the sample data has neither, and quoting is the caller's business, not the driver's.
/// </remarks>
public static class Unl
{
    public static string FormatField(IfxDataReader r, int i, IfxTextFormat format, DbColumn column)
    {
        if (r.IsDBNull(i)) return "";
        Type t = r.GetFieldType(i);

        if (t == typeof(DateOnly)) return IfxTextConvert.FormatDate(r.GetFieldValue<DateOnly>(i), format);
        if (t == typeof(TimeOnly)) return IfxTextConvert.FormatDateTime(r.GetIfxDateTime(i), format);
        if (t == typeof(DateTime))
        {
            var (start, end) = Qualifier(column);
            return IfxTextConvert.FormatDateTime(WithQualifier(r.GetDateTime(i), start, end), format);
        }
        if (t == typeof(TimeSpan))
        {
            var (start, end) = Qualifier(column);
            return IfxTextConvert.FormatInterval(WithQualifier(r.GetTimeSpan(i), start, end)).Trim();   // intoasc pads the leading field; dbaccess does not
        }
        if (t == typeof(IfxMonthSpan)) return IfxTextConvert.FormatInterval(r.GetIfxMonthSpan(i)).Trim();
        if (t == typeof(decimal)) return IfxTextConvert.FormatDecimal(r.GetIfxDecimal(i), -1, format);
        if (t == typeof(bool)) return r.GetBoolean(i) ? "t" : "f";
        if (t == typeof(string)) return r.GetString(i);
        if (t == typeof(short) || t == typeof(int) || t == typeof(long))
            return Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(float) || t == typeof(double))
            return Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);

        throw new NotSupportedException($"Column '{r.GetName(i)}' ({r.GetDataTypeName(i)}) cannot be unloaded as a plain field.");
    }


    // ---- DATETIME / INTERVAL qualifiers ------------------------------------------------------
    // A DateTime or TimeSpan carries no qualifier, but the UNLOAD text of a DATETIME YEAR TO MINUTE
    // or an INTERVAL DAY TO DAY depends on it. The column knows it (IfxDbColumn.StartTimeUnit /
    // EndTimeUnit), so the value is rebuilt as an IfxDateTime / IfxTimeSpan with exactly those fields.

    /// <summary>The start and end units of a DATETIME or INTERVAL column.</summary>
    public static (IfxTimeUnit Start, IfxTimeUnit End) Qualifier(DbColumn column) =>
        column is IfxDbColumn { StartTimeUnit: { } start, EndTimeUnit: { } end }
            ? (start, end)
            : throw new ArgumentException($"Column '{column.ColumnName}' is not a DATETIME or INTERVAL.", nameof(column));

    private static List<IfxTimeUnit> Units(IfxTimeUnit start, IfxTimeUnit end)
    {
        var units = new List<IfxTimeUnit>();
        foreach (var u in new[] { IfxTimeUnit.Year, IfxTimeUnit.Month, IfxTimeUnit.Day, IfxTimeUnit.Hour, IfxTimeUnit.Minute, IfxTimeUnit.Second })
            if (u >= start && u <= end) units.Add(u);
        if (end >= IfxTimeUnit.Fraction1) units.Add(end);   // FRACTION(n) is one unit of n digits
        return units;
    }

    private static int FractionDigits(IfxTimeUnit end) => end >= IfxTimeUnit.Fraction1 ? (int)end - 10 : 0;

    private static int FractionValue(long ticksWithinSecond, IfxTimeUnit end) =>
        (int)(ticksWithinSecond / (long)Math.Pow(10, 7 - FractionDigits(end)));

    public static IfxDateTime WithQualifier(DateTime dt, IfxTimeUnit start, IfxTimeUnit end)
    {
        var values = new List<int>();
        foreach (var u in Units(start, end))
            values.Add(u switch
            {
                IfxTimeUnit.Year => dt.Year,
                IfxTimeUnit.Month => dt.Month,
                IfxTimeUnit.Day => dt.Day,
                IfxTimeUnit.Hour => dt.Hour,
                IfxTimeUnit.Minute => dt.Minute,
                IfxTimeUnit.Second => dt.Second,
                _ => FractionValue(dt.Ticks % TimeSpan.TicksPerSecond, end),
            });
        return values.Count switch
        {
            1 => new IfxDateTime(values[0], end),
            2 => new IfxDateTime(values[0], values[1], end),
            3 => new IfxDateTime(values[0], values[1], values[2], end),
            4 => new IfxDateTime(values[0], values[1], values[2], values[3], end),
            5 => new IfxDateTime(values[0], values[1], values[2], values[3], values[4], end),
            6 => new IfxDateTime(values[0], values[1], values[2], values[3], values[4], values[5], end),
            _ => new IfxDateTime(values[0], values[1], values[2], values[3], values[4], values[5], values[6], end),
        };
    }

    public static IfxTimeSpan WithQualifier(TimeSpan ts, IfxTimeUnit start, IfxTimeUnit end)
    {
        long ticks = ts.Ticks;
        var values = new List<int>();
        bool first = true;
        foreach (var u in Units(start, end))
        {
            long unitTicks = u switch
            {
                IfxTimeUnit.Day => TimeSpan.TicksPerDay,
                IfxTimeUnit.Hour => TimeSpan.TicksPerHour,
                IfxTimeUnit.Minute => TimeSpan.TicksPerMinute,
                IfxTimeUnit.Second => TimeSpan.TicksPerSecond,
                _ => 0,
            };
            if (unitTicks == 0)
            {
                values.Add(FractionValue(ticks % TimeSpan.TicksPerSecond, end));   // the leading unit cannot be a fraction here
                continue;
            }
            values.Add((int)(ticks / unitTicks));
            ticks %= unitTicks;
            first = false;
        }
        _ = first;
        return values.Count switch
        {
            1 => new IfxTimeSpan(values[0], end),
            2 => new IfxTimeSpan(values[0], values[1], end),
            3 => new IfxTimeSpan(values[0], values[1], values[2], end),
            4 => new IfxTimeSpan(values[0], values[1], values[2], values[3], end),
            _ => new IfxTimeSpan(values[0], values[1], values[2], values[3], values[4], end),
        };
    }

    /// <summary>Streams every remaining row of <paramref name="reader"/> to <paramref name="writer"/>; returns the row count.</summary>
    public static long Write(IfxDataReader reader, TextWriter writer, IfxTextFormat format)
    {
        long rows = 0;
        var line = new StringBuilder();
        var schema = reader.GetColumnSchema();
        while (reader.Read())
        {
            line.Clear();
            for (int i = 0; i < reader.FieldCount; i++)
                line.Append(FormatField(reader, i, format, schema[i])).Append('|');
            writer.Write(line.Append('\n'));
            rows++;
        }
        return rows;
    }

    /// <summary>The fields of each line of an UNLOAD file (the empty field after the last '|' is dropped).</summary>
    public static IEnumerable<string[]> ReadLines(string path)
    {
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            string[] parts = line.Split('|');
            yield return parts[^1].Length == 0 ? parts[..^1] : parts;
        }
    }

    public static StreamWriter Create(string path) => new(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
}
