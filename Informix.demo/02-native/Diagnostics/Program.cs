// Diagnostics: errors, retries, logging, tracing and metrics.
//
// Shows: what an IfxException carries (SQLCODE, ISAM code, SqlState, IsTransient), retrying a
// transient lock conflict, the driver's ILogger output (through IfxDataSourceBuilder), its
// "Informix.Data" ActivitySource (one span per command) and "Informix.Data" Meter
// (OpenTelemetry-style db.client.* instruments). Only System.Diagnostics is used to listen, so
// no exporter packages are needed; in an application the same names go to AddSource / AddMeter.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Demo;
using Informix.Data;
using Microsoft.Extensions.Logging;

Ui.Title("Diagnostics");

string cs = DemoEnv.ConnectionString();
using var cn = new IfxConnection(cs);
cn.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. What an error carries");
{
    cn.Run("CREATE TEMP TABLE t_unique (id INT NOT NULL, UNIQUE (id))");
    cn.Run("INSERT INTO t_unique VALUES (1)");

    var cases = new (string What, string Sql)[]
    {
        ("unknown table", "SELECT * FROM no_such_table"),
        ("syntax error", "SELEC 1 FROM systables"),
        ("duplicate in a unique column", "INSERT INTO t_unique VALUES (1)"),
        ("NULL into NOT NULL", "INSERT INTO t_unique VALUES (NULL)"),
        ("conversion", "SELECT 'abc'::INT FROM systables WHERE tabid = 1"),
    };
    var rows = new List<object?[]>();
    foreach (var (what, sql) in cases)
    {
        try { cn.Run(sql); }
        catch (IfxException e)
        {
            rows.Add([what, e.IfxSqlCode, e.IsamCode, e.SqlState, e.IsTransient, e.Errors.Count]);
        }
    }
    Ui.Table(["case", "SQLCODE", "ISAM", "SqlState", "IsTransient", "Errors"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. A transient error and a retry");
{
    const string Touch = "UPDATE items SET quantity = quantity WHERE order_num = 1001 AND item_num = 1";

    using var holder = new IfxConnection(cs);
    holder.Open();
    var tx = holder.BeginTransaction();
    holder.Run(Touch, tx);                                  // holds the row lock
    var release = Task.Run(async () => { await Task.Delay(800); tx.Rollback(); });

    int attempt = 0;
    var sw = Stopwatch.StartNew();
    while (true)
    {
        attempt++;
        try
        {
            using var other = new IfxConnection(cs);
            other.Open();
            using var otx = other.BeginTransaction();
            other.Run(Touch, otx);
            otx.Rollback();                                    // the demo changes nothing for good
            Ui.Kv("succeeded", $"on attempt {attempt}, after {Ui.Ms(sw.Elapsed)}");
            break;
        }
        catch (IfxException e) when (e.IsTransient && attempt < 10)
        {
            Ui.Kv($"attempt {attempt}", $"SQLCODE {e.IfxSqlCode}, IsTransient = true - waiting 250 ms");
            Thread.Sleep(250);
        }
    }
    release.Wait();
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. Logging, tracing and metrics for a few commands");
{
    var logger = new CollectingLoggerFactory();
    var spans = new List<string>();
    var measurements = new SortedDictionary<string, double>();

    using var listener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == "Informix.Data",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = a => spans.Add($"{a.OperationName,-22} {a.Duration.TotalMilliseconds,6:N1} ms  {a.DisplayName}"),
    };
    ActivitySource.AddActivityListener(listener);

    using var meters = new MeterListener();
    meters.InstrumentPublished = (instrument, l) =>
    {
        if (instrument.Meter.Name == "Informix.Data") l.EnableMeasurementEvents(instrument);
    };
    meters.SetMeasurementEventCallback<double>((i, v, _, _) => { measurements[i.Name] = measurements.GetValueOrDefault(i.Name) + v; });
    meters.SetMeasurementEventCallback<int>((i, v, _, _) => { measurements[i.Name] = measurements.GetValueOrDefault(i.Name) + v; });
    meters.SetMeasurementEventCallback<long>((i, v, _, _) => { measurements[i.Name] = measurements.GetValueOrDefault(i.Name) + v; });
    meters.Start();

    using var dataSource = new IfxDataSourceBuilder(cs)
        .UseLoggerFactory(logger)
        .EnableParameterLogging()
        .Build();

    using (var c = dataSource.OpenConnection())
    {
        using (var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = @m", c))
        {
            cmd.Parameters.Add(new IfxParameter("@m", "HRO"));
            cmd.ExecuteScalar();
        }
        try { c.Run("SELECT * FROM no_such_table"); } catch (IfxException) { }
    }
    meters.RecordObservableInstruments();

    Ui.Line("   spans (ActivitySource \"Informix.Data\"):");
    foreach (string s in spans) Ui.Line("      " + s);

    Ui.Line();
    Ui.Line($"   log lines from the driver ({logger.Lines.Count}), the first few:");
    foreach (string line in logger.Lines.Take(8)) Ui.Line("      " + (line.Length > 130 ? line[..130] + "..." : line));

    Ui.Line();
    Ui.Line("   metrics (Meter \"Informix.Data\"), summed:");
    foreach (var (name, value) in measurements) Ui.Line($"      {name,-42} {value:0.###}");
}

Ui.Line();
Ui.Line("Done.");

static class Extensions
{
    /// <summary>Runs one non-query statement.</summary>
    public static void Run(this IfxConnection cn, string sql, IfxTransaction? tx = null)
    {
        using var cmd = new IfxCommand(sql, cn) { Transaction = tx };
        cmd.ExecuteNonQuery();
    }
}

/// <summary>A tiny ILoggerFactory that keeps the formatted lines, so the demo needs no logging package.</summary>
sealed class CollectingLoggerFactory : ILoggerFactory
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class Collector(CollectingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines)
                owner.Lines.Add($"{logLevel,-11} {category.Split('.').Last(),-12} {formatter(state, exception)}");
        }
    }
}
