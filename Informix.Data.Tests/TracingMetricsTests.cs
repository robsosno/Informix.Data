using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>Runs <see cref="TracingMetricsTests"/> without any other test collection
/// executing concurrently: its <see cref="ActivityListener"/> is process-global and would
/// otherwise capture spans emitted by connections opened in other test classes.</summary>
[CollectionDefinition("ActivitySource-global", DisableParallelization = true)]
public sealed class ActivitySourceGlobalCollection { }

/// <summary>
/// OpenTelemetry tracing (the <c>Informix.Data</c> <see cref="ActivitySource"/>) and
/// metrics (the <c>Informix.Data</c> <see cref="Meter"/>), driven over the in-memory
/// ASF transport. Spans are captured with an <see cref="ActivityListener"/> and metric
/// measurements with a <see cref="MeterListener"/> — both in-box, no OTel SDK needed.
/// </summary>
[Collection("ActivitySource-global")]
public class TracingMetricsTests
{
    private const string BaseCs =
        "Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd";

    // ---- transport scripts ----

    private static byte[] BringUp() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    private static byte[] NonQueryReply(int rows) =>
        SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: rows),
            SqliStatementKit.EotFrame());

    private static byte[] TwoIntRow(int a, int b)
    {
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), a);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), b);
        return row;
    }

    private static byte[] SelectReply(params (int A, int B)[] rows)
    {
        byte[] tuples = SqliStatementKit.Join(
            rows.Select(r => SqliStatementKit.TupleFrame(TwoIntRow(r.A, r.B))).ToArray());
        return SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),      // open + fetch reply
            tuples,
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());       // release reply
    }

    private static IfxDataSource Source(byte[] physicalScript,
        Action<IfxDataSourceBuilder>? configure = null, string extraCs = "")
    {
        var builder = new IfxDataSourceBuilder(BaseCs + extraCs);
        configure?.Invoke(builder);
        builder.AsfConnectionFactory = settings =>
            new AsfConnection(settings, new DuplexStream((byte[])physicalScript.Clone()));
        return builder.Build();
    }

    // ---- tracing capture ----

    private static ActivityListener Capture(List<Activity> started, List<Activity> stopped) =>
        Register(new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Informix.Data",
            Sample = static (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Add,
            ActivityStopped = stopped.Add,
        });

    private static ActivityListener Register(ActivityListener listener)
    {
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // ---- tracing tests ----

    [Fact]
    public void Select_produces_one_client_span_ending_at_reader_close()
    {
        using var source = Source(BringUp().Concat(SelectReply((1, 2), (3, 4))).ToArray());
        using var cn = source.OpenConnection();     // physical-open span not captured (listener added below)
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";

        using (var reader = cmd.ExecuteReader())
        {
            var activity = Assert.Single(started);
            Assert.Empty(stopped);                  // the span is still open across the fetch
            Assert.Equal("informix", activity.OperationName);
            Assert.Equal(ActivityKind.Client, activity.Kind);
            Assert.Equal("informix", activity.GetTagItem("db.system.name"));
            Assert.Equal("db", activity.GetTagItem("db.namespace"));
            Assert.Equal("select a, b from t", activity.GetTagItem("db.query.text"));
            Assert.Equal("h", activity.GetTagItem("server.address"));
            Assert.Equal(9088, activity.GetTagItem("server.port"));
            Assert.Contains(activity.Events, e => e.Name == "received-first-response");

            while (reader.Read()) { }
            Assert.Empty(stopped);                  // still open while rows are being read
        }

        var ended = Assert.Single(stopped);         // ended exactly at reader close
        Assert.Equal(ActivityStatusCode.Ok, ended.Status);
    }

    [Fact]
    public void NonQuery_span_ends_when_execute_returns()
    {
        using var source = Source(BringUp().Concat(NonQueryReply(2)).ToArray());
        using var cn = source.OpenConnection();
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";
        Assert.Equal(2, cmd.ExecuteNonQuery());

        Assert.Single(started);
        var ended = Assert.Single(stopped);          // ended synchronously with the call
        Assert.Equal(ActivityStatusCode.Ok, ended.Status);
    }

    [Fact]
    public void Server_error_sets_error_status_and_records_the_exception()
    {
        using var source = Source(BringUp()
            .Concat(SqliStatementKit.ErrorReply(-206, "The specified table (t) is not in the database."))
            .ToArray());
        using var cn = source.OpenConnection();
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select * from t";
        Assert.Throws<IfxException>(() => cmd.ExecuteReader());

        var ended = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, ended.Status);
        Assert.Equal("-206", ended.GetTagItem("error.type"));   // no SQLSTATE on the wire -> sqlcode
        Assert.Contains(ended.Events, e => e.Name == "exception");
    }

    [Fact]
    public void ExecuteScalar_produces_exactly_one_span()
    {
        using var source = Source(BringUp().Concat(SelectReply((42, 7))).ToArray());
        using var cn = source.OpenConnection();
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";
        Assert.Equal(42, cmd.ExecuteScalar());

        Assert.Single(started);   // ExecuteScalar goes through ExecuteReader -> one span, not two
        Assert.Single(stopped);
    }

    [Fact]
    public void Command_filter_returning_false_suppresses_the_span()
    {
        using var source = Source(BringUp().Concat(NonQueryReply(1)).ToArray(),
            b => b.ConfigureTracing(t => t.ConfigureCommandFilter(_ => false)));
        using var cn = source.OpenConnection();
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";
        cmd.ExecuteNonQuery();

        Assert.Empty(started);    // the filter excluded this command
    }

    [Fact]
    public void Enrichment_callback_and_span_name_provider_are_applied()
    {
        using var source = Source(BringUp().Concat(NonQueryReply(1)).ToArray(),
            b => b.ConfigureTracing(t => t
                .ConfigureCommandSpanNameProvider(_ => "custom-span")
                .ConfigureCommandEnrichmentCallback((a, _) => a.SetTag("app.enriched", "yes"))));
        using var cn = source.OpenConnection();
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";
        cmd.ExecuteNonQuery();

        var activity = Assert.Single(started);
        Assert.Equal("custom-span", activity.OperationName);
        Assert.Equal("yes", activity.GetTagItem("app.enriched"));
    }

    [Fact]
    public void Physical_open_emits_a_connect_span_when_enabled()
    {
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        using var listener = Capture(started, stopped); // added BEFORE open so the connect span is captured
        using var source = Source(BringUp());
        using var cn = source.OpenConnection();

        Assert.Contains(stopped, a =>
            a.OperationName == "connect" && a.Kind == ActivityKind.Client
            && (string?)a.GetTagItem("db.system.name") == "informix"
            && (string?)a.GetTagItem("db.namespace") == "db");
    }

    [Fact]
    public void No_listener_means_no_activity_is_created()
    {
        // No ActivityListener registered anywhere in this test.
        Assert.False(IfxActivitySource.IsEnabled);

        using var source = Source(BringUp().Concat(NonQueryReply(1)).ToArray());
        using var cn = source.OpenConnection();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";
        cmd.ExecuteNonQuery();

        Assert.Null(Activity.Current);   // nothing was started, so nothing leaked as Current
    }

    // ---- metrics capture ----

    private readonly record struct Meas(string Name, double Value, KeyValuePair<string, object?>[] Tags);

    private static MeterListener CaptureMeter(ConcurrentBag<Meas> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "Informix.Data")
                    l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<double>((inst, val, tags, _) => sink.Add(new Meas(inst.Name, val, tags.ToArray())));
        listener.SetMeasurementEventCallback<int>((inst, val, tags, _) => sink.Add(new Meas(inst.Name, val, tags.ToArray())));
        listener.Start();
        return listener;
    }

    private static string? Tag(Meas m, string key) =>
        m.Tags.FirstOrDefault(t => t.Key == key).Value?.ToString();

    // ---- metrics tests ----

    [Fact]
    public void Meter_records_duration_create_time_and_connection_count()
    {
        string name = "metrics-count-" + Guid.NewGuid();
        var sink = new ConcurrentBag<Meas>();
        using var meter = CaptureMeter(sink);

        using var source = Source(BringUp().Concat(NonQueryReply(1)).ToArray(), b => b.Name = name);
        using (var cn = source.OpenConnection())    // building the physical records create_time
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "update t set x = 1";
            cmd.ExecuteNonQuery();                  // records operation.duration once
            meter.RecordObservableInstruments();    // pull the gauges while the connection is busy
        }

        // Duration recorded exactly once for this data source.
        Assert.Equal(1, sink.Count(m =>
            m.Name == "db.client.operation.duration" && Tag(m, "db.client.connection.pool.name") == name));
        // A physical connection was built -> create_time recorded.
        Assert.Contains(sink, m =>
            m.Name == "db.client.connection.create_time" && Tag(m, "db.client.connection.pool.name") == name);
        // The busy connection shows up in the observable count, and Max Pool Size is reported.
        Assert.Contains(sink, m =>
            m.Name == "db.client.connection.count" && Tag(m, "db.client.connection.pool.name") == name
            && Tag(m, "db.client.connection.state") == "used" && m.Value == 1);
        Assert.Contains(sink, m =>
            m.Name == "db.client.connection.max" && Tag(m, "db.client.connection.pool.name") == name);
        // db.system.name flows onto the duration histogram tags.
        Assert.Contains(sink, m =>
            m.Name == "db.client.operation.duration" && Tag(m, "db.system.name") == "informix");
    }

    [Fact]
    public void Meter_counts_pool_rent_timeouts()
    {
        string name = "metrics-timeout-" + Guid.NewGuid();
        var sink = new ConcurrentBag<Meas>();
        using var meter = CaptureMeter(sink);

        using var source = Source(BringUp(), b => b.Name = name, extraCs: ";Max Pool Size=1;Login Timeout=1");
        using var first = source.OpenConnection();  // takes the only slot
        var second = source.CreateConnection();
        Assert.Throws<IfxException>(second.Open);    // no slot free -> rent timeout

        Assert.Contains(sink, m =>
            m.Name == "db.client.connection.timeouts" && Tag(m, "db.client.connection.pool.name") == name && m.Value == 1);
    }

    [Fact]
    public void Standalone_connections_emit_no_metrics()
    {
        // A standalone IfxConnection (no data source) has no metrics reporter, so its
        // command records nothing — the reporter is the sole entry point for metrics.
        string cs = BaseCs + ";Pooling=false";
        var cn = new IfxConnection(cs)
        {
            AsfConnectionFactory = s => new AsfConnection(s,
                new DuplexStream(BringUp().Concat(NonQueryReply(1)).ToArray())),
        };
        cn.Open();
        Assert.Null(cn.MetricsReporter);   // no data source -> no reporter -> no metrics
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "update t set x = 1";
            Assert.Equal(1, cmd.ExecuteNonQuery());  // still runs fine with metrics off
        }
        cn.Close();
    }
}
