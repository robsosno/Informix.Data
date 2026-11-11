// Load: read an UNLOAD file and insert it fast.
//
// Shows: IfxTextConvert.Parse* turning UNL text into typed values (DATE, DATETIME, DECIMAL,
// INTERVAL, BOOLEAN, NULL as an empty field) that go in as they are - IfxDecimal, IfxDateTime and
// IfxTimeSpan are parameter types - and three ways to insert them: one statement per row,
// IfxRowBatch (many rows per round trip) and IfxBulkLoader (the SQ_PUT insert cursor).
// Finishes by unloading the table again and comparing the two files byte for byte.
//
// usage: Load [rows]      (default 20000)

using System.Diagnostics;
using Demo;
using Informix.Data;

int total = args.Length > 0 ? int.Parse(args[0]) : 20000;
int slowRows = Math.Min(total, 3000);

Ui.Title($"Load - {total:N0} rows from an UNLOAD file");
string dir = DemoEnv.WorkDir("load");
string source = Path.Combine(dir, "demo_load.unl");

using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", "MDY4/")));
cn.Open();
IfxTextFormat fmt = cn.TextFormat;

const string Insert = "INSERT INTO demo_load VALUES (?, ?, ?, ?, ?, ?, ?)";

void Exec(string sql)
{
    using var c = new IfxCommand(sql, cn);
    c.ExecuteNonQuery();
}

void Recreate()
{
    try { Exec("DROP TABLE demo_load"); } catch (IfxException) { }
    Exec("CREATE TABLE demo_load (id INTEGER, name VARCHAR(30), amount DECIMAL(12,2), created DATE, " +
         "stamp DATETIME YEAR TO SECOND, active BOOLEAN, wait INTERVAL DAY(3) TO DAY)");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Writing the source file with IfxTextConvert");
{
    var epoch = new DateOnly(2000, 1, 1);
    using var w = Unl.Create(source);
    for (int i = 1; i <= total; i++)
    {
        var day = epoch.AddDays(i % 3000);
        var stamp = Unl.WithQualifier(new DateTime(2020, 1, 1).AddSeconds(i * 37L), IfxTimeUnit.Year, IfxTimeUnit.Second);
        w.Write(i + "|");
        w.Write("item " + i + "|");
        w.Write(IfxTextConvert.FormatDecimal((IfxDecimal)(i * 1.25m), -1, fmt) + "|");
        w.Write(IfxTextConvert.FormatDate(day, fmt) + "|");
        w.Write(IfxTextConvert.FormatDateTime(stamp, fmt) + "|");
        w.Write((i % 2 == 0 ? "t" : "f") + "|");
        w.Write((i % 7 == 0 ? "" : IfxTextConvert.FormatInterval(new IfxTimeSpan(i % 30, IfxTimeUnit.Day)).Trim()) + "|");   // every 7th is NULL
        w.Write('\n');
    }
}
Ui.Head(source, 4);
Ui.Kv("file size", $"{new FileInfo(source).Length / 1024.0 / 1024:N1} MB");

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Reading it back: IfxTextConvert.Parse* turns text into typed values");
// The conversion results go in as they are: IfxDecimal, IfxDateTime and IfxTimeSpan are parameter types.
object?[] ParseLine(string[] f) =>
[
    IfxTextConvert.ParseInt32(f[0]),
    f[1],
    f[2].Length == 0 ? null : IfxTextConvert.ParseDecimal(f[2], fmt),
    IfxTextConvert.ParseDate(f[3], fmt),
    f[4].Length == 0 ? null : IfxTextConvert.ParseDateTime(f[4], IfxTimeUnit.Year, IfxTimeUnit.Second, fmt),
    f[5] == "t",
    f[6].Length == 0 ? null : IfxTextConvert.ParseDayTimeInterval(f[6], IfxTimeUnit.Day, IfxTimeUnit.Day, 3),
];

foreach (var f in Unl.ReadLines(source).Take(2))
    Ui.Line("   " + string.Join(" | ", ParseLine(f).Select(v => v is null ? "NULL" : $"{Ui.Show(v)} ({v.GetType().Name})")));

// ---------------------------------------------------------------------------------------------
var results = new List<object?[]>();

void Check(string method, int expected, TimeSpan time)
{
    using var c = new IfxCommand("SELECT COUNT(*), SUM(amount) FROM demo_load", cn);
    using var r = c.ExecuteReader();
    r.Read();
    long count = r.GetInt64(0);
    results.Add([method, $"{count:N0}", Ui.Ms(time), $"{count / time.TotalSeconds:N0}", count == expected ? "ok" : $"EXPECTED {expected}"]);
}

Ui.Section($"3. One INSERT per row (first {slowRows:N0} rows, one transaction)");
Recreate();
{
    using var tx = cn.BeginTransaction();
    using var cmd = new IfxCommand(Insert, cn) { Transaction = tx };
    foreach (var name in new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" })
        cmd.Parameters.Add(new IfxParameter(name, DBNull.Value));
    cmd.Prepare();
    var sw = Stopwatch.StartNew();
    foreach (var f in Unl.ReadLines(source).Take(slowRows))
    {
        var v = ParseLine(f);
        for (int i = 0; i < v.Length; i++) cmd.Parameters[i].Value = v[i] ?? DBNull.Value;
        cmd.ExecuteNonQuery();
    }
    sw.Stop();
    tx.Commit();
    Check("row by row", slowRows, sw.Elapsed);
}

Ui.Section("4. IfxRowBatch: 1,000 rows per round trip");
Recreate();
{
    using var tx = cn.BeginTransaction();
    var batch = new IfxRowBatch(Insert, cn) { Transaction = tx };
    var sw = Stopwatch.StartNew();
    foreach (var f in Unl.ReadLines(source))
    {
        batch.AddRow(ParseLine(f).Select(v => v ?? DBNull.Value).ToArray());
        if (batch.RowCount == 1000) { batch.Execute(); batch.Clear(); }
    }
    if (batch.RowCount > 0) batch.Execute();
    sw.Stop();
    tx.Commit();
    Check("IfxRowBatch", total, sw.Elapsed);
}

Ui.Section("5. IfxBulkLoader: the insert cursor");
Recreate();
{
    using var tx = cn.BeginTransaction();
    using var loader = cn.CreateBulkLoader(Insert);
    loader.Transaction = tx;
    loader.DefineParameters(IfxType.Integer, IfxType.VarChar, IfxType.Decimal, IfxType.Date,
        IfxType.DateTime, IfxType.Boolean, IfxType.IntervalDayFraction);
    var sw = Stopwatch.StartNew();
    long inserted = loader.Execute(l =>
    {
        foreach (var f in Unl.ReadLines(source))
            l.Put(ParseLine(f));
    });
    sw.Stop();
    tx.Commit();
    Check("IfxBulkLoader", total, sw.Elapsed);
    Ui.Kv("rows the loader reports", inserted);
}

Ui.Section("Comparison");
Ui.Table(["method", "rows", "time", "rows/s", "check"], results);

// ---------------------------------------------------------------------------------------------
Ui.Section("6. Round trip: unload the table and compare it with the file we started from");
string again = Path.Combine(dir, "demo_load_again.unl");
using (var cmd = new IfxCommand("SELECT * FROM demo_load ORDER BY id", cn))
using (var reader = cmd.ExecuteReader())
using (var writer = Unl.Create(again))
    Ui.Kv("rows unloaded", Unl.Write(reader, writer, fmt));

bool same = File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(again));
Ui.Kv("files identical", same ? "yes - the load is lossless" : "NO");
if (!same)
{
    var a = File.ReadLines(source).GetEnumerator();
    var b = File.ReadLines(again).GetEnumerator();
    int line = 0;
    while (a.MoveNext() && b.MoveNext())
    {
        line++;
        if (a.Current != b.Current) { Ui.Line($"   line {line}:\n   {a.Current}\n   {b.Current}"); break; }
    }
}

Exec("DROP TABLE demo_load");
Ui.Line();
Ui.Line("Done. The demo_load table was dropped.");
