// GenericReader: querying with DbCommand / DbParameter / DbDataReader only.
//
// Shows: parameters made by DbCommand.CreateParameter (named, any order), the typed getters and
// GetFieldValue<T> for DATE / DATETIME / time-of-day / INTERVAL values (DateOnly, DateTime,
// TimeOnly, TimeSpan; DATE read back as a DateTime; the year-month INTERVAL that stays a provider
// struct), CommandBehavior options, and a table dumper that works for ANY query
// because it reads GetColumnSchema(). It also shows where provider-independence ends: a ROW or a
// collection arrives as an object the provider defines.

using System.Data;
using System.Data.Common;
using Demo;

Ui.Title("GenericReader");

using DbConnection cn = Bootstrap.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Named parameters on a DbCommand");
{
    using DbCommand cmd = cn.Command(
        "SELECT stock_num, description, unit_price FROM stock WHERE manu_code = @manu AND unit_price > @min ORDER BY unit_price DESC");
    cmd.Add("@min", 100m, DbType.Decimal);          // the order of adding does not matter
    cmd.Add("@manu", "HRO", DbType.String);
    using DbDataReader r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read())
        rows.Add([r.GetInt16(0), r.GetString(1), r.GetDecimal(2)]);
    Ui.Table(["stock_num", "description", "unit_price"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Date and time values as DateOnly, DateTime, TimeOnly, TimeSpan");
{
    using DbCommand cmd = cn.Command(
        "SELECT o.order_date, c.call_dtime, DATETIME (13:45:07) HOUR TO SECOND, m.lead_time " +
        "FROM orders o, cust_calls c, manufact m WHERE o.order_num = 1001 AND c.call_dtime = DATETIME (2007-11-28 13:34) YEAR TO MINUTE AND m.manu_code = 'SMT'");
    using DbDataReader r = cmd.ExecuteReader();
    r.Read();
    var rows = new List<object?[]>();
    for (int i = 0; i < r.FieldCount; i++)
        rows.Add([r.GetName(i), r.GetFieldType(i).Name, Ui.Show(r.GetValue(i))]);
    Ui.Table(["column", "GetFieldType", "value"], rows);

    DateOnly day = r.GetFieldValue<DateOnly>(0);
    DateTime stamp = r.GetDateTime(1);
    TimeOnly time = r.GetFieldValue<TimeOnly>(2);
    TimeSpan lead = r.GetFieldValue<TimeSpan>(3);
    Ui.Line();
    Ui.Kv("GetFieldValue<DateOnly>", day.AddDays(30));
    Ui.Kv("GetDateTime + 1 hour", stamp.AddHours(1));
    Ui.Kv("GetFieldValue<TimeOnly> + 20 min", time.AddMinutes(20));
    Ui.Kv("GetFieldValue<TimeSpan> x 2", lead * 2);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2b. A DATE the way other providers return it: as a DateTime");
{
    using DbCommand cmd = cn.Command("SELECT order_date FROM orders WHERE order_num = 1001");
    using DbDataReader r = cmd.ExecuteReader();
    r.Read();
    Ui.Kv("GetValue", r.GetValue(0).GetType().Name + "  " + Ui.Show(r.GetValue(0)));
    Ui.Kv("GetDateTime", r.GetDateTime(0));                          // midnight of that day
    Ui.Kv("GetFieldValue<DateTime>", r.GetFieldValue<DateTime>(0));  // the same, for code written against GetValue
    Ui.Kv("GetFieldValue<DateOnly>", r.GetFieldValue<DateOnly>(0));
    Ui.Note("GetValue returns a DateOnly, but GetDateTime and GetFieldValue<DateTime> still work, so existing code keeps running");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2c. What still arrives as before: DATETIME with a date part, INTERVAL YEAR TO MONTH");
{
    using DbCommand cmd = cn.Command(
        "SELECT DATETIME (2007-11-28) YEAR TO DAY, " +
        "DATETIME (2007-11-28 13:34:56.123) YEAR TO FRACTION(3), " +
        "INTERVAL (2-6) YEAR TO MONTH " +
        "FROM systables WHERE tabid = 1");
    using DbDataReader r = cmd.ExecuteReader();
    r.Read();
    var rows = new List<object?[]>();
    for (int i = 0; i < r.FieldCount; i++)
        rows.Add([r.GetDataTypeName(i), r.GetFieldType(i).Name, Ui.Show(r.GetValue(i))]);
    Ui.Table(["column type", "GetFieldType", "value"], rows);

    Ui.Line();
    Ui.Kv("DATETIME YEAR TO DAY", r.GetDateTime(0));
    Ui.Kv("DATETIME YEAR TO FRACTION", r.GetDateTime(1));
    // A year-month INTERVAL has no System type (a TimeSpan cannot say how long a month is), so the
    // provider's own struct arrives; reading it is where provider-independence ends.
    Ui.Kv("INTERVAL YEAR TO MONTH", r.GetValue(2));
    Ui.Kv("  as a month count", r.GetFieldValue<long>(2));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. A dumper that works for any query: the schema comes from GetColumnSchema()");

void Dump(string sql, params (string Name, object Value)[] parameters)
{
    using DbCommand cmd = cn.Command(sql);
    foreach (var (name, value) in parameters) cmd.Add(name, value);
    using DbDataReader r = cmd.ExecuteReader();
    var schema = r.GetColumnSchema();
    Ui.Line($"   {sql}");
    Ui.Table(schema.Select(c => $"{c.ColumnName} ({c.DataTypeName})").ToArray(),
        Enumerable.Range(0, 3).Select(_ => r.Read() ? Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)).ToArray() : null)
            .Where(row => row is not null).Select(row => row!));
    Ui.Line();
}

Dump("SELECT code, sname, sales_tax FROM state ORDER BY code");
Dump("SELECT order_num, order_date, backlog, paid_date FROM orders ORDER BY order_num");
Dump("SELECT manu_code, lead_time, discount FROM manufact ORDER BY manu_code");

// ---------------------------------------------------------------------------------------------
Ui.Section("4. CommandBehavior");
{
    using (DbCommand cmd = cn.Command("SELECT * FROM stock"))
    using (DbDataReader r = cmd.ExecuteReader(CommandBehavior.SchemaOnly))
        Ui.Kv("SchemaOnly", $"{r.FieldCount} columns described, {(r.Read() ? "rows read" : "no rows fetched")}");

    using (DbCommand cmd = cn.Command("SELECT * FROM stock ORDER BY stock_num"))
    using (DbDataReader r = cmd.ExecuteReader(CommandBehavior.SingleRow))
    {
        int n = 0;
        while (r.Read()) n++;
        Ui.Kv("SingleRow", $"{n} row(s) read");
    }

    DbConnection own = Bootstrap.Open();
    using (DbCommand cmd = own.Command("SELECT COUNT(*) FROM state"))
    using (DbDataReader r = cmd.ExecuteReader(CommandBehavior.CloseConnection))
    {
        r.Read();
        Ui.Kv("CloseConnection", $"count {r.GetValue(0)}; connection is {own.State} until the reader is closed");
    }
    Ui.Kv("   after the reader is closed", own.State);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. Where the generic API stops");
{
    using DbCommand cmd = cn.Command("SELECT customer_name, contact_dates FROM retail_customer WHERE customer_num = 101");
    using DbDataReader r = cmd.ExecuteReader();
    r.Read();
    Ui.Kv("ROW column", $"{r.GetFieldType(0).Name}  {r.GetValue(0)}");
    Ui.Kv("LIST column", $"{r.GetFieldType(1).Name}  {Ui.Show(r.GetValue(1))}");
    Ui.Note("a type the standard has no word for is the provider's own class; ask the server for its parts instead (customer_name.first)");
}

Ui.Line();
Ui.Line("Done.");
