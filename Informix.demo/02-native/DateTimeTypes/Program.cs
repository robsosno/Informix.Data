// DateTimeTypes: DATE, DATETIME and INTERVAL as .NET types.
//
// The driver maps a DATE to DateOnly, a time-only DATETIME (HOUR and below) to TimeOnly, a
// DATETIME with a date part to DateTime, a day-time INTERVAL to TimeSpan, and keeps a
// year-month INTERVAL as IfxMonthSpan. The qualifier (YEAR TO MINUTE, HOUR TO FRACTION(3) ...)
// is not part of a DateTime, so IfxDateTime / IfxTimeSpan are there when it matters.
//
// Shows: the default mapping, GetFieldValue<T> alternatives, IfxDateTime and its units,
// binding DateOnly / DateTime / TimeOnly / TimeSpan / IfxMonthSpan as parameters, and the text
// forms from IfxTextConvert.

using System.Data;
using Demo;
using Informix.Data;

Ui.Title("DateTimeTypes");

using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", "Y4MD-")));
cn.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. What each type becomes");
{
    var samples = new (string What, string Sql)[]
    {
        ("DATE (orders.order_date)", "SELECT order_date FROM orders WHERE order_num = 1001"),
        ("DATETIME YEAR TO MINUTE (cust_calls)", "SELECT FIRST 1 call_dtime FROM cust_calls ORDER BY call_dtime"),
        ("DATETIME YEAR TO DAY", "SELECT DATETIME (2024-03-15) YEAR TO DAY FROM systables WHERE tabid = 1"),
        ("DATETIME YEAR TO FRACTION(5)", "SELECT DATETIME (2024-03-15 13:45:07.12345) YEAR TO FRACTION(5) FROM systables WHERE tabid = 1"),
        ("DATETIME MONTH TO DAY", "SELECT DATETIME (03-15) MONTH TO DAY FROM systables WHERE tabid = 1"),
        ("DATETIME HOUR TO SECOND", "SELECT DATETIME (13:45:07) HOUR TO SECOND FROM systables WHERE tabid = 1"),
        ("DATETIME HOUR TO FRACTION(3)", "SELECT DATETIME (13:45:07.123) HOUR TO FRACTION(3) FROM systables WHERE tabid = 1"),
        ("CURRENT HOUR TO SECOND", "SELECT CURRENT HOUR TO SECOND FROM systables WHERE tabid = 1"),
        ("INTERVAL DAY(3) TO DAY (manufact)", "SELECT lead_time FROM manufact WHERE manu_code = 'SMT'"),
        ("INTERVAL HOUR TO SECOND", "SELECT INTERVAL (02:30:15) HOUR TO SECOND FROM systables WHERE tabid = 1"),
        ("INTERVAL YEAR TO MONTH", "SELECT INTERVAL (2-6) YEAR TO MONTH FROM systables WHERE tabid = 1"),
    };

    var rows = new List<object?[]>();
    foreach (var (what, sql) in samples)
    {
        using var cmd = new IfxCommand(sql, cn);
        using var r = cmd.ExecuteReader();
        r.Read();
        rows.Add([what, r.GetFieldType(0).Name, r.GetValue(0)]);
    }
    Ui.Table(["Informix", ".NET type", "value"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Other ways to read the same value (GetFieldValue<T>)");
{
    string Read<T>(IfxDataReader r)
    {
        try { return Ui.Show(r.GetFieldValue<T>(0)); }
        catch (Exception e) { return $"({e.GetType().Name})"; }
    }

    var probes = new (string What, string Sql)[]
    {
        ("DATE", "SELECT order_date FROM orders WHERE order_num = 1001"),
        ("DATETIME YEAR TO MINUTE", "SELECT FIRST 1 call_dtime FROM cust_calls ORDER BY call_dtime"),
        ("DATETIME HOUR TO SECOND", "SELECT DATETIME (13:45:07) HOUR TO SECOND FROM systables WHERE tabid = 1"),
        ("INTERVAL DAY TO DAY", "SELECT lead_time FROM manufact WHERE manu_code = 'SMT'"),
    };
    var rows = new List<object?[]>();
    foreach (var (what, sql) in probes)
    {
        using var cmd = new IfxCommand(sql, cn);
        using var r = cmd.ExecuteReader();
        r.Read();
        rows.Add([what, Read<DateOnly>(r), Read<DateTime>(r), Read<TimeOnly>(r), Read<TimeSpan>(r), Read<IfxDateTime>(r)]);
    }
    Ui.Table(["column", "DateOnly", "DateTime", "TimeOnly", "TimeSpan", "IfxDateTime"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. IfxDateTime keeps the qualifier");
{
    using var cmd = new IfxCommand("SELECT FIRST 1 call_dtime, res_dtime FROM cust_calls ORDER BY call_dtime", cn);
    using var r = cmd.ExecuteReader();
    r.Read();
    IfxDateTime call = r.GetIfxDateTime(0);
    var column = r.GetColumnSchema()[0];
    var (start, end) = Unl.Qualifier(column);
    Ui.Kv("call_dtime as DateTime", Ui.Show(r.GetDateTime(0)));
    Ui.Kv("column qualifier", $"{start} TO {end}");
    Ui.Kv("as IfxDateTime", $"{call.Year}-{call.Month:D2}-{call.Day:D2} {call.Hour:D2}:{call.Minute:D2}");
    Ui.Kv("text with that qualifier", IfxTextConvert.FormatDateTime(Unl.WithQualifier(r.GetDateTime(0), start, end), cn.TextFormat));
    Ui.Kv("same value, full precision", IfxTextConvert.FormatDateTime(call, cn.TextFormat));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Writing them: parameters of each .NET type into a temporary table");
{
    using (var ddl = new IfxCommand(
        "CREATE TEMP TABLE t_dt (d DATE, dt_sec DATETIME YEAR TO SECOND, dt_frac DATETIME YEAR TO FRACTION(5), " +
        "tm DATETIME HOUR TO SECOND, tm_frac DATETIME HOUR TO FRACTION(3), iv INTERVAL DAY(3) TO SECOND, " +
        "ym INTERVAL YEAR TO MONTH) WITH NO LOG", cn))
        ddl.ExecuteNonQuery();

    var sent = new object[]
    {
        new DateOnly(2024, 2, 29),
        new DateTime(2024, 2, 29, 23, 59, 58),
        new DateTime(2024, 2, 29, 23, 59, 58).AddTicks(1234500),     // 123.45 ms: five fraction digits
        new TimeOnly(7, 5, 3),
        new TimeOnly(7, 5, 3, 456),
        new TimeSpan(45, 6, 30, 15),
        new IfxMonthSpan(2, 6),
    };

    using (var ins = new IfxCommand("INSERT INTO t_dt VALUES (?, ?, ?, ?, ?, ?, ?)", cn))
    {
        for (int i = 0; i < sent.Length; i++) ins.Parameters.Add(new IfxParameter($"p{i}", sent[i]));
        ins.ExecuteNonQuery();
    }

    using var sel = new IfxCommand("SELECT * FROM t_dt", cn);
    using var r = sel.ExecuteReader();
    r.Read();
    var rows = new List<object?[]>();
    for (int i = 0; i < r.FieldCount; i++)
    {
        object back = r.GetValue(i);
        rows.Add([r.GetName(i), sent[i].GetType().Name, Ui.Show(sent[i]), back.GetType().Name, Ui.Show(back),
                  Equals(sent[i], back) ? "same" : "differs"]);
    }
    Ui.Table(["column", "sent as", "sent", "read as", "read", ""], rows);
    Ui.Note("TimeSpan 45 days 06:30:15 fits INTERVAL DAY(3) TO SECOND; IfxMonthSpan is the year-month interval");

    Ui.Note("a DateTime at exactly midnight binds as a DATE; set IfxParameter.IfxType = IfxType.DateTime to force a DATETIME");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. The same values as UNLOAD text (IfxTextConvert)");
{
    var fmt = new IfxTextFormat { DbDate = "DMY4." };
    var day = new DateOnly(2024, 2, 29);
    var stamp = new IfxDateTime(2024, 2, 29, 23, 59, 58, IfxTimeUnit.Second);
    var time = new IfxDateTime(7, 5, 3, IfxTimeUnit.Second);
    var interval = new IfxTimeSpan(45, 6, 30, 15, IfxTimeUnit.Second);

    string d = IfxTextConvert.FormatDate(day, fmt);
    string s = IfxTextConvert.FormatDateTime(stamp, fmt);
    string t = IfxTextConvert.FormatDateTime(time, fmt);
    string i = IfxTextConvert.FormatInterval(interval).Trim();

    Ui.Table(["value", "text", "parsed back"],
    [
        ["DATE", d, IfxTextConvert.ParseDate(d, fmt)],
        ["DATETIME YEAR TO SECOND", s, IfxTextConvert.ParseDateTime(s, IfxTimeUnit.Year, IfxTimeUnit.Second, fmt)],
        ["DATETIME HOUR TO SECOND", t, IfxTextConvert.ParseDateTime(t, IfxTimeUnit.Hour, IfxTimeUnit.Second, fmt)],
        ["INTERVAL DAY TO SECOND", i, IfxTextConvert.ParseDayTimeInterval(i, IfxTimeUnit.Day, IfxTimeUnit.Second, 3)],
    ]);
}

Ui.Line();
Ui.Line("Done.");
