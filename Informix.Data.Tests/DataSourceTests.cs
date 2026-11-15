using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxDataSource"/> / <see cref="IfxDataSourceBuilder"/> behaviour, driven
/// entirely through the in-memory ASF transport seam
/// (<see cref="IfxDataSourceBuilder.AsfConnectionFactory"/>). Each data source owns its
/// own pool, so tests are naturally isolated even when they share a connection string.
/// </summary>
public class DataSourceTests
{
    private const string BaseCs =
        "Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd";

    private static byte[] BringUp() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    // A non-query (SQ_COMMAND) reply: describe (no columns) + done(rows) + eot.
    private static byte[] NonQueryReply(int rows) =>
        SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: rows),
            SqliStatementKit.EotFrame());

    // Builds a data source whose every physical connection gets a fresh clone of
    // physicalScript, counting how many the pool creates and (optionally) collecting
    // the duplex streams so a test can inspect what was sent.
    private static IfxDataSource Source(string cs, byte[] physicalScript,
        StrongBox<int>? calls = null, ConcurrentBag<DuplexStream>? streams = null,
        Action<IfxDataSourceBuilder>? configure = null)
    {
        var builder = new IfxDataSourceBuilder(cs);
        configure?.Invoke(builder);
        builder.AsfConnectionFactory = settings =>
        {
            if (calls is not null)
                Interlocked.Increment(ref calls.Value);
            var duplex = new DuplexStream((byte[])physicalScript.Clone());
            streams?.Add(duplex);
            return new AsfConnection(settings, duplex);
        };
        return builder.Build();
    }

    [Fact]
    public void OpenConnection_runs_a_command_then_close_returns_it_to_the_pool()
    {
        byte[] script = BringUp().Concat(NonQueryReply(3)).ToArray();
        using var source = Source(BaseCs, script);

        using (var cn = source.OpenConnection())
        {
            Assert.Equal(ConnectionState.Open, cn.State);
            Assert.Equal("9.59.FC5", cn.ServerVersion);
            Assert.Equal((1, 0, 1), source.Statistics); // one physical, checked out (busy)

            using var cmd = cn.CreateCommand();
            cmd.CommandText = "update t set x = 1";
            Assert.Equal(3, cmd.ExecuteNonQuery());
        }

        Assert.Equal((1, 1, 0), source.Statistics); // returned: now idle
    }

    [Fact]
    public void Two_data_sources_from_the_same_string_keep_independent_pools()
    {
        byte[] script = BringUp();
        var callsA = new StrongBox<int>(0);
        var callsB = new StrongBox<int>(0);
        using var a = Source(BaseCs, script, callsA);
        using var b = Source(BaseCs, script, callsB);

        using (a.OpenConnection()) { }
        using (b.OpenConnection()) { }

        Assert.Equal(1, callsA.Value);
        Assert.Equal(1, callsB.Value);
        Assert.Equal((1, 1, 0), a.Statistics);
        Assert.Equal((1, 1, 0), b.Statistics);
    }

    [Fact]
    public void Dispose_stops_the_idle_pruning_timer()
    {
        // Connection Lifetime > 0 starts the pool's prune timer; a timer left running would
        // keep firing and keep the disposed source's pool reachable for good.
        var source = Source(BaseCs + ";Connection Lifetime=60", BringUp());
        var pool = source.Pool!;
        Assert.True(pool.IsPruning);

        source.Dispose();

        Assert.False(pool.IsPruning);
    }

    [Fact]
    public void Dispose_blocks_new_opens_but_a_checked_out_connection_can_still_close()
    {
        var source = Source(BaseCs, BringUp());
        var cn = source.OpenConnection(); // checked out before disposal

        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.OpenConnection());
        Assert.Throws<ObjectDisposedException>(() => source.CreateConnection());

        cn.Close(); // must not throw — the physical is simply discarded
        Assert.Equal(ConnectionState.Closed, cn.State);
    }

    [Fact]
    public void Clear_retires_idle_and_checked_out_connections()
    {
        var calls = new StrongBox<int>(0);
        using var source = Source(BaseCs, BringUp(), calls);

        using (source.OpenConnection()) { }            // physical #1 -> idle
        Assert.Equal(1, calls.Value);
        Assert.Equal((1, 1, 0), source.Statistics);

        source.Clear();                                // retire the idle one
        Assert.Equal((0, 0, 0), source.Statistics);

        using (source.OpenConnection()) { }            // physical #2 -> idle
        Assert.Equal(2, calls.Value);

        var held = source.OpenConnection();            // reuses idle physical #2, now checked out
        Assert.Equal(2, calls.Value);
        Assert.Equal((1, 0, 1), source.Statistics);
        source.Clear();                                // bump generation while it's out
        held.Close();                                  // stale generation -> discarded, not re-pooled
        Assert.Equal((0, 0, 0), source.Statistics);

        using (source.OpenConnection()) { }            // pool empty -> must build physical #3
        Assert.Equal(3, calls.Value);
    }

    [Fact]
    public void ConnectionString_strips_the_password_unless_persist_security_info_is_set()
    {
        using var stripped = new IfxDataSourceBuilder(BaseCs).Build();
        Assert.DoesNotContain("pwd", stripped.ConnectionString);
        Assert.DoesNotContain("Password", stripped.ConnectionString, StringComparison.OrdinalIgnoreCase);
        // Name defaults to the (stripped) connection string.
        Assert.Equal(stripped.ConnectionString, stripped.Name);

        using var kept = new IfxDataSourceBuilder(BaseCs + ";Persist Security Info=true").Build();
        Assert.Contains("pwd", kept.ConnectionString);
    }

    [Fact]
    public void Builder_name_overrides_the_default_and_surfaces_on_the_source()
    {
        using var source = new IfxDataSourceBuilder(BaseCs) { Name = "orders-db" }.Build();
        Assert.Equal("orders-db", source.Name);
    }

    [Fact]
    public void Initialization_sql_runs_once_per_physical_connection_not_per_checkout()
    {
        // Bring-up + exactly ONE init-statement reply: a second init on the same
        // physical connection would read past the script and fail.
        byte[] script = BringUp().Concat(NonQueryReply(0)).ToArray();
        var calls = new StrongBox<int>(0);
        var streams = new ConcurrentBag<DuplexStream>();
        using var source = Source(BaseCs, script, calls, streams,
            b => b.UseConnectionInitializationSql("set lock mode to wait 5"));

        using (source.OpenConnection()) { } // creates the physical -> init runs once
        using (source.OpenConnection()) { } // reuses it -> no second init

        Assert.Equal(1, calls.Value);       // one physical connection served both opens
        string sent = Encoding.Latin1.GetString(streams.Single().Output.ToArray());
        Assert.Equal(1, CountOccurrences(sent, "set lock mode to wait 5"));
    }

    [Fact]
    public void Connection_pam_handler_overrides_the_data_source_pam_handler()
    {
        var log = new List<string>();

        // (a) A connection with its own handler: it answers the challenge, not the source's.
        using (var source = PamSource(new RecordingPamHandler("ds", log)))
        {
            var cn = source.CreateConnection();
            cn.PamHandler = new RecordingPamHandler("cn", log);
            cn.Open();
            cn.Close();
        }
        Assert.Equal(new[] { "cn" }, log);

        // (b) No connection handler: the data-source handler answers.
        log.Clear();
        using (var source = PamSource(new RecordingPamHandler("ds", log)))
        {
            using var cn = source.OpenConnection();
        }
        Assert.Equal(new[] { "ds" }, log);
    }

    // A PAM-authenticating data source (Pooling=false so each open builds a fresh
    // physical) whose transport replays a challenge/accept bring-up.
    private static IfxDataSource PamSource(IIfxPamHandler dataSourceHandler)
    {
        var builder = new IfxDataSourceBuilder(BaseCs + ";Authentication=Pam;Pooling=false")
            .UsePamHandler(dataSourceHandler);
        builder.AsfConnectionFactory = settings => new AsfConnection(settings, new DuplexStream(PamBringUp()));
        return builder.Build();
    }

    // ASF accept + SQ_PROTOCOLS reply + a password challenge/accept + SQ_VERSION reply.
    private static byte[] PamBringUp()
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Challenge);
        w.WriteInt16((short)PamMessageType.PromptEchoOff);
        byte[] prompt = IfxWireConverter.EncodeChar("Password:");
        w.WriteInt16((short)prompt.Length);
        w.WritePadded(prompt);
        w.WriteInt16((short)SqliMessage.Eot);
        w.WriteInt16((short)SqliMessage.Accept);
        w.WriteInt16((short)SqliMessage.Eot);

        return AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.ProtocolsReply())
            .Concat(ms.ToArray())
            .Concat(SqliTestKit.VersionReply("9.59.FC5"))
            .ToArray();
    }

    private sealed class RecordingPamHandler(string tag, List<string> log) : IIfxPamHandler
    {
        public IfxPamResponse Respond(IfxPamChallenge challenge)
        {
            log.Add(tag);
            return new IfxPamResponse("pw");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
