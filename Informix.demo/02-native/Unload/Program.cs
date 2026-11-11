// Unload: write tables to Informix UNLOAD files (.unl), streaming, with the driver's own text
// conversion (IfxTextConvert / IfxTextFormat), so the files load with dbload / LOAD.
//
// Shows: IfxConnection.TextFormat (follows the connection's DB Date), an explicit IfxTextFormat,
// IfxTextConvert.FormatDate / FormatDateTime / FormatDecimal / FormatInterval, a streaming read
// (rows go to the file as they arrive) and the effect of the Fetch Buffer Size.

using System.Diagnostics;
using Demo;
using Informix.Data;

Ui.Title("Unload - tables to UNLOAD files");
string dir = DemoEnv.WorkDir("unload");
Ui.Kv("files go to", dir);

// ---------------------------------------------------------------------------------------------
Ui.Section("1. The text format follows the session: the connection's DB Date");
Ui.Note("the same DBDATE is sent to the server and used by connection.TextFormat, so client and server agree");

string? first = null;
foreach (string dbDate in new[] { "MDY4/", "DMY4.", "Y4MD-" })
{
    using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", dbDate)));
    cn.Open();

    string path = Path.Combine(dir, $"orders_{dbDate.TrimEnd('/', '.', '-')}.unl");
    using (var cmd = new IfxCommand(
        "SELECT order_num, order_date, customer_num, backlog, po_num, paid_date FROM orders ORDER BY order_num", cn))
    using (var reader = cmd.ExecuteReader())
    using (var writer = Unl.Create(path))
    {
        long rows = Unl.Write(reader, writer, cn.TextFormat);
        Ui.Kv($"DB Date={dbDate}", $"{rows} rows");
    }
    Ui.Head(path, 2);

    // What the server itself writes for the same DATE, to show the two agree.
    using var check = new IfxCommand("SELECT FIRST 1 order_date::LVARCHAR FROM orders ORDER BY order_num", cn);
    string serverText = (string)check.ExecuteScalar()!;
    string clientText = Unl.ReadLines(path).First()[1];
    Ui.Kv("server says / file says", $"{serverText} / {clientText}  {(serverText == clientText ? "same" : "DIFFERENT")}");
    first ??= path;
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. A format that does not depend on any connection");
{
    var polish = new IfxTextFormat { DbDate = "DMY4.", DecimalSeparator = ',' };
    using var cn = new IfxConnection(DemoEnv.ConnectionString());
    cn.Open();
    string path = Path.Combine(dir, "stock_pl.unl");
    using (var cmd = new IfxCommand(
        "SELECT stock_num, manu_code, unit, description, unit_price, unit_cost FROM stock ORDER BY stock_num, manu_code, unit", cn))
    using (var reader = cmd.ExecuteReader())
    using (var writer = Unl.Create(path))
        Ui.Kv("stock (decimal comma)", $"{Unl.Write(reader, writer, polish)} rows");
    Ui.Head(path, 3);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. DATETIME qualifiers, INTERVAL and a distinct type come out in their own forms");
{
    using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", "MDY4/")));
    cn.Open();

    string path = Path.Combine(dir, "cust_calls.unl");
    using (var cmd = new IfxCommand(
        "SELECT customer_num, call_dtime, call_code, res_dtime FROM cust_calls ORDER BY call_dtime", cn))
    using (var reader = cmd.ExecuteReader())
    using (var writer = Unl.Create(path))
        Ui.Kv("cust_calls (YEAR TO MINUTE)", $"{Unl.Write(reader, writer, cn.TextFormat)} rows");
    Ui.Head(path, 2);

    path = Path.Combine(dir, "manufact.unl");
    using (var cmd = new IfxCommand(
        "SELECT manu_code, manu_name, lead_time, terms_net, discount FROM manufact ORDER BY manu_code", cn))
    using (var reader = cmd.ExecuteReader())
    using (var writer = Unl.Create(path))
        Ui.Kv("manufact (INTERVAL, percent)", $"{Unl.Write(reader, writer, cn.TextFormat)} rows");
    Ui.Head(path, 3);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Streaming: 300,000 rows leave in the order they arrive, nothing is held back");
Ui.Note("a cross join of three tables; the fetch buffer decides how many rows each round trip brings");

const string bigQuery =
    "SELECT FIRST 300000 i.order_num, i.item_num, s.stock_num, s.manu_code, s.description, s.unit_price, st.code " +
    "FROM items i, stock s, state st";

foreach (int bufferSize in new[] { 4096, 32767 })
{
    using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", "MDY4/"), ("Fetch Buffer Size", bufferSize.ToString())));
    cn.Open();
    string path = Path.Combine(dir, "big.unl");

    cn.ResetFetchStats();
    long before = GC.GetTotalMemory(true);
    long peak = before;
    var sw = Stopwatch.StartNew();
    long rows = 0;
    using (var cmd = new IfxCommand(bigQuery, cn))
    using (var reader = cmd.ExecuteReader())
    using (var writer = Unl.Create(path))
    {
        var schema = reader.GetColumnSchema();
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
                writer.Write(Unl.FormatField(reader, i, cn.TextFormat, schema[i]) + "|");
            writer.Write('\n');
            if (++rows % 50000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(false));
        }
    }
    sw.Stop();
    Ui.Kv($"Fetch Buffer Size={bufferSize}", $"{rows:N0} rows, {Ui.Ms(sw.Elapsed)}, {rows / sw.Elapsed.TotalSeconds:N0} rows/s");
    Ui.Kv("   fetch messages", cn.FetchMessagesSent);
    Ui.Kv("   managed memory growth", $"{(peak - before) / 1024.0 / 1024:N1} MB, file {new FileInfo(path).Length / 1024.0 / 1024:N1} MB");
}

Ui.Line();
Ui.Line("Done. Load the files back with the Load demo, or with dbload / LOAD.");
