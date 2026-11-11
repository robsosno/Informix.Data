using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using Informix.Data;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Client-side connection pooling. Each test uses a unique connection string so it owns a
/// private pool in the process-wide registry (pools are static, keyed by the string). The
/// injected factory hands each new physical connection a fresh one-shot scripted transport
/// and counts how many it built — the pool's whole job is to build as few as possible.
/// Pooled open/close does no wire I/O, so a bring-up-only script serves any number of reuses.
/// </summary>
public class ConnectionPoolTests
{
    private static byte[] BringUpScript() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    // A connection whose transport factory builds a fresh scripted DuplexStream each call and
    // bumps a counter, so a test can assert how many physical connections the pool created.
    private static IfxConnection Conn(string cs, byte[] physicalScript, StrongBox<int> factoryCalls,
        ConcurrentBag<DuplexStream>? streams = null)
    {
        var cn = new IfxConnection(cs);
        cn.AsfConnectionFactory = settings =>
        {
            Interlocked.Increment(ref factoryCalls.Value);
            var duplex = new DuplexStream((byte[])physicalScript.Clone());
            streams?.Add(duplex);
            return new AsfConnection(settings, duplex);
        };
        return cn;
    }

    private static string UniqueCs(string tag) =>
        $"Host=h;Service=9088;Server=ol_test;Database=pool_{tag};User ID=usr;Password=pwd";

    [Fact]
    public void Pooled_open_close_open_reuses_the_same_physical_connection()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("reuse");

        using (var cn = Conn(cs, BringUpScript(), calls))
        {
            cn.Open();
            Assert.Equal("9.59.FC5", cn.ServerVersion);
            cn.Close();
            cn.Open();  // must draw the pooled physical connection back out
            cn.Close();
        }

        Assert.Equal(1, calls.Value); // one physical connection served both opens
    }

    [Fact]
    public void Pooling_false_builds_a_fresh_physical_connection_each_open()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("nopool") + ";Pooling=false";

        using (var cn = Conn(cs, BringUpScript(), calls))
        {
            cn.Open();
            cn.Close();
            cn.Open();
            cn.Close();
        }

        Assert.Equal(2, calls.Value); // no reuse — two physical connections
    }

    [Fact]
    public void Max_pool_size_blocks_then_times_out_when_saturated()
    {
        var calls = new StrongBox<int>(0);
        // Max Pool Size 1, Login Timeout 1s: a second concurrent open finds no free slot.
        string cs = UniqueCs("maxsize") + ";Max Pool Size=1;Login Timeout=1";

        using var first = Conn(cs, BringUpScript(), calls);
        first.Open(); // takes the only slot

        using var second = Conn(cs, BringUpScript(), calls);
        var ex = Assert.Throws<IfxException>(second.Open);
        Assert.Equal(-908, ex.IfxSqlCode);
        Assert.Equal(ConnectionState.Closed, second.State);

        // Freeing the first slot lets a later open succeed against the pooled connection.
        first.Close();
        second.Open();
        Assert.Equal(ConnectionState.Open, second.State);
        second.Close();

        Assert.Equal(1, calls.Value); // never exceeded one physical connection
    }

    [Fact]
    public void ClearPool_retires_idle_connections()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("clear");

        using var cn = Conn(cs, BringUpScript(), calls);
        cn.Open();
        cn.Close();               // returns one connection to the pool
        Assert.Equal(1, calls.Value);

        IfxConnection.ClearPool(cn); // disposes the idle connection

        cn.Open();                // must build a new one
        cn.Close();
        Assert.Equal(2, calls.Value);
    }

    [Fact]
    public void Connection_lifetime_prunes_an_aged_out_connection_on_checkout()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("lifetime") + ";Connection Lifetime=1";

        using var cn = Conn(cs, BringUpScript(), calls);
        cn.Open();
        cn.Close();               // pooled, created just now
        Assert.Equal(1, calls.Value);

        Thread.Sleep(1200);       // exceed the 1-second lifetime

        cn.Open();                // the idle connection is too old -> discard + build fresh
        cn.Close();
        Assert.Equal(2, calls.Value);
    }

    [Fact]
    public void Returning_a_connection_with_an_open_transaction_rolls_it_back()
    {
        var calls = new StrongBox<int>(0);
        var streams = new ConcurrentBag<DuplexStream>();
        // Script: bring-up, the SQ_BEGIN reply, then the SQ_RBWORK reply issued on checkin.
        byte[] script = BringUpScript()
            .Concat(SqliStatementKit.EotFrame())  // begin reply
            .Concat(SqliStatementKit.EotFrame())  // rollback reply (on Close)
            .ToArray();
        string cs = UniqueCs("txn");

        using (var cn = Conn(cs, script, calls, streams))
        {
            cn.Open();
            cn.BeginTransaction();  // SQ_BEGIN — left uncommitted on purpose
            cn.Close();             // checkin must roll the transaction back
        }

        Assert.Equal(1, calls.Value); // connection was reusable, not discarded
        var sent = streams.Single().Output.ToArray();
        // SQ_BEGIN(35) then, on checkin, SQ_RBWORK(20) — both flushed with EOT(12).
        string hex = BitConverter.ToString(sent);
        Assert.Contains("00-23-00-0C", hex); // SQ_BEGIN + EOT
        Assert.Contains("00-14-00-00-00-0C", hex); // SQ_RBWORK + savepoint word 0 + EOT
    }

    [Fact]
    public void Concurrent_open_close_stays_within_max_pool_size()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("parallel") + ";Max Pool Size=5;Login Timeout=5";
        byte[] script = BringUpScript();

        Parallel.For(0, 200, _ =>
        {
            using var cn = Conn(cs, script, calls);
            cn.Open();
            Assert.Equal(ConnectionState.Open, cn.State);
            cn.Close();
        });

        Assert.True(IfxConnectionPools.TryGet(
            IfxConnection.PoolKey(new IfxConnectionStringBuilder(cs)), out var pool));
        Assert.True(pool.TotalCount <= 5, $"pool grew to {pool.TotalCount}, over Max Pool Size 5");
        Assert.True(calls.Value <= 5, $"created {calls.Value} physical connections, over Max Pool Size 5");
    }

    [Fact]
    public void Min_pool_size_pre_warms_connections()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("minsize") + ";Min Pool Size=3";

        using var cn = Conn(cs, BringUpScript(), calls);
        cn.Open(); // creating the pool warms Min Pool Size connections first
        cn.Close();

        Assert.True(IfxConnectionPools.TryGet(
            IfxConnection.PoolKey(new IfxConnectionStringBuilder(cs)), out var pool));
        Assert.Equal(3, calls.Value);   // three warmed up (the open reused one of them)
        Assert.Equal(3, pool.TotalCount);
    }

    [Fact]
    public void Closing_with_a_dangling_prepared_statement_releases_it_instead_of_discarding_the_connection()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("preparedidle");
        // Bring-up, then the Prepare() round trip, then the release Close()'s IsIdle
        // safety net sends for the statement the command left prepared (never disposed).
        byte[] script = BringUpScript()
            .Concat(SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0))
            .Concat(SqliStatementKit.DoneFrame())
            .Concat(SqliStatementKit.EotFrame())
            .Concat(SqliStatementKit.EotFrame()) // release reply
            .ToArray();

        using var cn = Conn(cs, script, calls);
        cn.Open();
        var cmd = cn.CreateCommand(); // deliberately never Dispose()d -- leaves the handle dangling
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();

        cn.Close(); // must release the dangling statement itself, not discard the connection
        cn.Open();  // must draw the same physical connection back out
        cn.Close();

        Assert.Equal(1, calls.Value); // one physical connection served both opens
    }

    [Fact]
    public void Closing_with_an_abandoned_reader_on_a_MARS_sibling_session_discards_the_connection()
    {
        // IsIdle/pooling must aggregate over every session on the link, not just the default
        // one: an abandoned reader on a sibling session AcquireSession minted is exactly as
        // unsafe to pool as one on the default session always was (see the plain
        // single-session version of this test elsewhere for that baseline).
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("marsabandoned");
        byte[] script = BringUpScript()
            // default session: prepare + open + one-row fetch, deliberately never closed
            .Concat(SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)))
            .Concat(SqliStatementKit.DoneFrame())
            .Concat(SqliStatementKit.EotFrame())
            .Concat(SqliStatementKit.DoneFrame())
            .Concat(SqliStatementKit.TupleFrame(OneIntRow(1)))
            .Concat(SqliStatementKit.EotFrame())
            // sibling session: its own prepare + open + one-row fetch, also never closed
            .Concat(SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)))
            .Concat(SqliStatementKit.DoneFrame())
            .Concat(SqliStatementKit.EotFrame())
            .Concat(SqliStatementKit.DoneFrame())
            .Concat(SqliStatementKit.TupleFrame(OneIntRow(2)))
            .Concat(SqliStatementKit.EotFrame())
            .ToArray();

        using var cn = Conn(cs, script, calls);
        cn.Open();

        using var cmd1 = cn.CreateCommand();
        cmd1.CommandText = "select n from a";
        var r1 = cmd1.ExecuteReader(); // default session -- deliberately never disposed
        Assert.True(r1.Read());

        using var cmd2 = cn.CreateCommand();
        cmd2.CommandText = "select n from b";
        var r2 = cmd2.ExecuteReader(); // AcquireSession steers this to a fresh sibling session
        Assert.True(r2.Read());

        // Neither reader is disposed before the connection itself closes: Close() must see the
        // sibling's open cursor, not just the default session's, and discard rather than pool.
        cn.Close();
        Assert.Equal(0, cn.GetIdleConnectionsCount);

        // Confirms the discard, not a fluke: a fresh Open() needs a brand new physical
        // connection (a second factory call) since nothing usable was returned to the pool.
        using var cn2 = Conn(cs, BringUpScript(), calls);
        cn2.Open();
        cn2.Close();
        Assert.Equal(2, calls.Value);
    }

    private static byte[] OneIntRow(int n)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    [Fact]
    public void GetIdleAndActiveConnectionsCount_reflect_the_pool_as_connections_are_rented_and_returned()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("counts") + ";Min Pool Size=2";

        using var warm = Conn(cs, BringUpScript(), calls);
        warm.Open(); // creating the pool warms Min Pool Size=2 first, then reuses one of them
        warm.Close();
        Assert.Equal(2, warm.GetIdleConnectionsCount);
        Assert.Equal(0, warm.GetActiveConnectionsCount);

        using var rented = Conn(cs, BringUpScript(), calls);
        rented.Open(); // draws one of the two idle connections back out
        Assert.Equal(1, rented.GetIdleConnectionsCount);
        Assert.Equal(1, rented.GetActiveConnectionsCount);

        rented.Close(); // returned — back to fully idle
        Assert.Equal(2, rented.GetIdleConnectionsCount);
        Assert.Equal(0, rented.GetActiveConnectionsCount);
    }

    [Fact]
    public void GetIdleAndActiveConnectionsCount_are_zero_when_Pooling_is_false()
    {
        var calls = new StrongBox<int>(0);
        string cs = UniqueCs("countsnopool") + ";Pooling=false";

        using var cn = Conn(cs, BringUpScript(), calls);
        cn.Open();
        Assert.Equal(0, cn.GetIdleConnectionsCount);
        Assert.Equal(0, cn.GetActiveConnectionsCount);
        cn.Close();
        Assert.Equal(0, cn.GetIdleConnectionsCount);
        Assert.Equal(0, cn.GetActiveConnectionsCount);
    }

    [Fact]
    public void GetIdleAndActiveConnectionsCount_are_zero_for_an_unopened_connection_with_an_unknown_string()
    {
        using var cn = new IfxConnection(UniqueCs("neveropened"));
        Assert.Equal(0, cn.GetIdleConnectionsCount);
        Assert.Equal(0, cn.GetActiveConnectionsCount);
    }
}
