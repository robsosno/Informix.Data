// GeneratedKeys: the SERIAL / SERIAL8 value an INSERT just generated, without a second query.
//
// The server sends the generated value back with the INSERT's completion reply; IfxCommand
// exposes it as LastSerialKind + LastSerial / LastSerial8 after ExecuteNonQuery. With ODBC or the
// CSDK the same value cost an extra SELECT DBINFO('sqlca.sqlerrd1').
//
// Everything happens in temporary tables, so the demo database is not touched.

using Demo;
using Informix.Data;

Ui.Title("GeneratedKeys");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

void Exec(string sql)
{
    using var c = new IfxCommand(sql, cn);
    c.ExecuteNonQuery();
}

Exec("CREATE TEMP TABLE t_serial  (id SERIAL,       name VARCHAR(20)) WITH NO LOG");
Exec("CREATE TEMP TABLE t_serial8 (id SERIAL8(1000), name VARCHAR(20)) WITH NO LOG");
Exec("CREATE TEMP TABLE t_plain   (id INTEGER,      name VARCHAR(20)) WITH NO LOG");

(IfxSerialKind Kind, long Value) Insert(string table, string name, object? id = null)
{
    using var cmd = new IfxCommand($"INSERT INTO {table} VALUES (?, ?)", cn);
    cmd.Parameters.Add(new IfxParameter("id", id ?? 0));      // 0 asks for a generated value
    cmd.Parameters.Add(new IfxParameter("name", name));
    cmd.ExecuteNonQuery();
    long value = cmd.LastSerialKind switch
    {
        IfxSerialKind.Serial => cmd.LastSerial,
        IfxSerialKind.Serial8 => cmd.LastSerial8,
        _ => 0,
    };
    return (cmd.LastSerialKind, value);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. SERIAL, SERIAL8 and a table with neither");
{
    var rows = new List<object?[]>();
    foreach (string table in new[] { "t_serial", "t_serial8", "t_plain" })
        foreach (string name in new[] { "first", "second" })
        {
            var (kind, value) = Insert(table, name);
            rows.Add([table, name, kind, value]);
        }
    Ui.Table(["table", "row", "LastSerialKind", "value"], rows);
    Ui.Note("LastSerialKind says which property holds the value, so nothing has to be guessed");
}

Ui.Section("2. An explicit value moves the counter");
{
    var explicitly = Insert("t_serial", "explicit", 500);
    var next = Insert("t_serial", "after");
    Ui.Kv("explicit 500", $"{explicitly.Kind}, {explicitly.Value}");
    Ui.Kv("next generated", $"{next.Kind}, {next.Value}");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. What it saves: 100 inserts, each followed by reading the key");
{
    const int N = 100;

    cn.ResetFetchStats();
    var t1 = Ui.Time(() =>
    {
        for (int i = 0; i < N; i++)
        {
            using var ins = new IfxCommand("INSERT INTO t_serial VALUES (0, 'x')", cn);
            ins.ExecuteNonQuery();
            _ = ins.LastSerial;
        }
    });
    long trips1 = cn.RequestRoundTrips;

    cn.ResetFetchStats();
    var t2 = Ui.Time(() =>
    {
        for (int i = 0; i < N; i++)
        {
            using var ins = new IfxCommand("INSERT INTO t_serial VALUES (0, 'x')", cn);
            ins.ExecuteNonQuery();
            using var key = new IfxCommand("SELECT DBINFO('sqlca.sqlerrd1') FROM systables WHERE tabid = 1", cn);
            _ = key.ExecuteScalar();
        }
    });
    long trips2 = cn.RequestRoundTrips;

    Ui.Table(["way to get the key", "time", "round trips"],
    [
        ["IfxCommand.LastSerial", Ui.Ms(t1), trips1],
        ["SELECT DBINFO('sqlca.sqlerrd1')", Ui.Ms(t2), trips2],
    ]);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. The usual use: a header row, then its detail rows");
{
    Exec("CREATE TEMP TABLE t_order (order_num SERIAL8(5000), customer INTEGER) WITH NO LOG");
    Exec("CREATE TEMP TABLE t_line (order_num INT8, line SMALLINT, item VARCHAR(20)) WITH NO LOG");

    long Order(int customer)
    {
        using var cmd = new IfxCommand("INSERT INTO t_order VALUES (0, ?)", cn);
        cmd.Parameters.Add(new IfxParameter("c", customer));
        cmd.ExecuteNonQuery();
        return cmd.LastSerial8;
    }

    foreach (int customer in new[] { 101, 104 })
    {
        long orderNum = Order(customer);
        string[] items = ["racket", "balls", "grip"];
        for (int line = 0; line < items.Length; line++)
        {
            using var cmd = new IfxCommand("INSERT INTO t_line VALUES (?, ?, ?)", cn);
            cmd.Parameters.Add(new IfxParameter("o", orderNum));
            cmd.Parameters.Add(new IfxParameter("l", (short)(line + 1)));
            cmd.Parameters.Add(new IfxParameter("i", items[line]));
            cmd.ExecuteNonQuery();
        }
        Ui.Kv($"customer {customer}", $"order {orderNum}, {items.Length} lines");
    }

    using var q = new IfxCommand("SELECT o.order_num, o.customer, COUNT(*) FROM t_order o, t_line l WHERE o.order_num = l.order_num GROUP BY 1, 2 ORDER BY 1", cn);
    using var r = q.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read()) rows.Add([r.GetValue(0), r.GetValue(1), r.GetValue(2)]);
    Ui.Table(["order_num", "customer", "lines"], rows);
}

Ui.Line();
Ui.Line("Done. The temporary tables went away with the connection.");
