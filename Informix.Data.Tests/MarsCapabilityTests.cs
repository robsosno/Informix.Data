using System.Data;
using System.Threading;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Is Informix.Data MARS-capable? Can a second statement run on the same
/// <see cref="IfxConnection"/> while a <see cref="IfxDataReader"/> from an earlier one is
/// still open, the way SQL Server's MARS, the Informix ODBC driver
/// (<c>tools/InformixMarsTest</c>), or — live-verified 2026-08-03 against a real IDS
/// 14.10 server — the real IBM JDBC driver all allow?
///
/// <see cref="IfxConnection.AcquireSession"/> makes the answer yes: it hands a new
/// statement the connection's default <see cref="SqliSession"/> when that is free, an
/// already-idle sibling when it isn't, or mints a fresh sibling (sharing the same
/// physical socket and connection-wide state via <see cref="SqliLink"/>) when every
/// existing session is busy — port of the reference driver's <c>IfxSqliConnect.createProto()</c>
/// being called again for a new <c>IfxStatement</c> rather than reusing whichever session
/// the connection already had. <see cref="SqliSession.CursorOpen"/> and its
/// <c>ThrowIfCursorOpen()</c> guard still exist and still matter — they protect a single
/// session's own internal consistency (e.g. against <see cref="IfxBulkLoader"/> or
/// <c>IfxConnection.ApplyIsolationLevel</c> reaching for a session that already has a
/// cursor open) — but they no longer block an unrelated nested reader, because
/// <see cref="IfxConnection.AcquireSession"/> simply never hands that reader the busy
/// session in the first place.
/// </summary>
public class MarsCapabilityTests
{
    private static byte[] TwoIntRow(int a, int b)
    {
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), a);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), b);
        return row;
    }

    // The open half of a 2-col select's wire cycle: prepare + open + 2-row fetch, WITHOUT
    // the close/release -- for a reader a test deliberately keeps open across other activity.
    private static byte[] OpenTwoRowSelect() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
            new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
            new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.TupleFrame(TwoIntRow(1, 2)),
        SqliStatementKit.TupleFrame(TwoIntRow(3, 4)),
        SqliStatementKit.EotFrame());

    // The close/release reply a reader's Dispose sends once it actually runs.
    private static byte[] CloseAndRelease() => SqliStatementKit.Join(
        SqliStatementKit.EotFrame(),  // close reply
        SqliStatementKit.EotFrame()); // release reply

    // A full prepare(2-col select) + open + 2-row fetch + close + release script, matching
    // what disposing an ordinary (non-SingleRow) reader sends -- used for a reader that runs
    // to completion within one scope (the nested/inner reader in the tests below).
    private static byte[] TwoRowSelectScript() => SqliStatementKit.Join(OpenTwoRowSelect(), CloseAndRelease());

    [Fact]
    public void Nested_ExecuteReader_on_the_same_connection_now_runs_on_a_sibling_session()
    {
        // Outer opens and reads its first row; while it's still open, a second command on
        // the SAME IfxConnection runs its own full prepare+open+fetch+close+release cycle on
        // a sibling session AcquireSession mints for it -- then the outer resumes correctly.
        byte[] script = SqliStatementKit.Join(
            OpenTwoRowSelect(),   // outer: prepare + open + fetch (stays open)
            TwoRowSelectScript(), // inner: its own complete, independent cycle
            CloseAndRelease());  // outer: close + release once ITS reader disposes
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var outerCmd = cn.CreateCommand();
        outerCmd.CommandText = "select a, b from t";

        using var outerReader = outerCmd.ExecuteReader();
        Assert.True(outerReader.Read());
        Assert.Equal(1, outerReader.GetInt32(0));

        long beforeInnerAttempt = duplex.Output.Length;

        using (var innerCmd = cn.CreateCommand())
        {
            innerCmd.CommandText = "select a, b from t";
            using var innerReader = innerCmd.ExecuteReader();
            Assert.True(innerReader.Read());
            Assert.Equal(1, innerReader.GetInt32(0));
            Assert.True(innerReader.Read());
            Assert.Equal(3, innerReader.GetInt32(0));
        } // inner reader's own Dispose closes + releases ITS statement, on ITS session

        // Real wire traffic went out for the inner statement this time -- a whole
        // independent prepare/open/fetch/close/release cycle, not a client-side refusal.
        Assert.True(duplex.Output.Length > beforeInnerAttempt);

        // The outer reader is completely unaffected by the inner's independent activity.
        Assert.True(outerReader.Read());
        Assert.Equal(3, outerReader.GetInt32(0));
    }

    [Fact]
    public void Nested_ExecuteNonQuery_on_the_same_connection_also_runs_on_a_sibling_session()
    {
        // Same sibling-session steering, different entry point (ExecuteImmediate's
        // one-round-trip SQ_COMMAND path instead of Prepare/OpenCursor).
        byte[] script = SqliStatementKit.Join(
            OpenTwoRowSelect(),
            // the inner ExecuteNonQuery's own one-round-trip SQ_COMMAND cycle
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            CloseAndRelease());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var outerCmd = cn.CreateCommand();
        outerCmd.CommandText = "select a, b from t";
        using var outerReader = outerCmd.ExecuteReader();
        Assert.True(outerReader.Read());

        long beforeInnerAttempt = duplex.Output.Length;

        using var innerCmd = cn.CreateCommand();
        innerCmd.CommandText = "update t set a = 0 where b = 2";
        int affected = innerCmd.ExecuteNonQuery();

        Assert.Equal(1, affected);
        Assert.True(duplex.Output.Length > beforeInnerAttempt);

        Assert.True(outerReader.Read());
        Assert.Equal(3, outerReader.GetInt32(0));
    }

    [Fact]
    public void Sequential_reuse_of_the_same_connection_works_once_the_reader_is_disposed()
    {
        // Contrast case: the guard is about *concurrency*, not about reusing the
        // connection at all. Once the first reader is gone, a second statement on the
        // very same connection runs normally -- this is the ordinary ADO.NET pattern
        // (one connection, one statement at a time), just not MARS.
        byte[] script = SqliStatementKit.Join(TwoRowSelectScript(), TwoRowSelectScript());
        using var cn = SqliStatementKit.OpenConnection(script, out _);

        using (var cmd1 = cn.CreateCommand())
        {
            cmd1.CommandText = "select a, b from t";
            using var r1 = cmd1.ExecuteReader();
            Assert.True(r1.Read());
            Assert.True(r1.Read());
        } // Dispose closes the cursor + releases the statement

        using var cmd2 = cn.CreateCommand();
        cmd2.CommandText = "select a, b from t";
        using var r2 = cmd2.ExecuteReader();
        Assert.True(r2.Read());
        Assert.Equal(1, r2.GetInt32(0));
    }

    [Fact]
    public void Two_independent_connections_are_not_serialized_against_each_other()
    {
        // The workaround the ODBC probe (tools/InformixMarsTest) lands on for "no MARS" is
        // a second connection for the nested query. Confirms Informix.Data's guard is a
        // per-SqliSession property, not e.g. an accidental process-wide lock: a second
        // IfxConnection's reader can be opened and read while the first's is still open.
        using var cn1 = SqliStatementKit.OpenConnection(TwoRowSelectScript(), out _);
        using var cn2 = SqliStatementKit.OpenConnection(TwoRowSelectScript(), out _);

        using var cmd1 = cn1.CreateCommand();
        cmd1.CommandText = "select a, b from t";
        using var r1 = cmd1.ExecuteReader();
        Assert.True(r1.Read()); // r1 stays open across the whole block

        using var cmd2 = cn2.CreateCommand();
        cmd2.CommandText = "select a, b from t";
        using var r2 = cmd2.ExecuteReader(); // would throw if this were a shared guard
        Assert.True(r2.Read());
        Assert.Equal(1, r2.GetInt32(0));

        // r1 is still perfectly usable afterward.
        Assert.True(r1.Read());
        Assert.Equal(3, r1.GetInt32(0));
    }

    private static byte[] OneIntRow(int n)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    // A 1-column select's prepare + open + first-row fetch, NOT auto-closed (no inline
    // SQ_CLOSE) -- reading past this one buffered row genuinely needs a FetchMore.
    private static byte[] PrepareOpenOneRow(short stmtId, int value) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.TupleFrame(OneIntRow(value)),
        SqliStatementKit.EotFrame());

    // The FetchMore reply for a second (and final) row -- auto-closed inline, so the
    // reader's own Dispose needs only a release, not a separate SQ_CLOSE round trip.
    private static byte[] FetchMoreLastRow(int value) => SqliStatementKit.Join(
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.TupleFrame(OneIntRow(value)),
        SqliStatementKit.CloseFrame(),
        SqliStatementKit.EotFrame());

    [Fact]
    public void Two_readers_on_the_same_connection_can_be_read_in_true_interleaved_order()
    {
        // The strict MARS test: never fully drain either reader before touching the other --
        // mirrors the live JDBC probe (scratchpad/marsjdbc/MarsLiveTestInterleaved.java) that
        // alternated next() calls between two open, non-drained ResultSets on one Connection
        // against a real IDS 14.10 server. Wire order here matches real execution order:
        // A opens, B opens (a sibling session -- A's default session is now busy), A's second
        // row needs its own FetchMore, then B's does, interleaved exactly as the reads are.
        byte[] script = SqliStatementKit.Join(
            PrepareOpenOneRow(stmtId: 1, value: 101),
            PrepareOpenOneRow(stmtId: 2, value: 201),
            FetchMoreLastRow(102),
            FetchMoreLastRow(202),
            SqliStatementKit.EotFrame(),  // A's release (CloseCursor is a no-op: already auto-closed)
            SqliStatementKit.EotFrame()); // B's release
        using var cn = SqliStatementKit.OpenConnection(script, out _);

        using var cmdA = cn.CreateCommand();
        cmdA.CommandText = "select n from a";
        using var readerA = cmdA.ExecuteReader();

        using var cmdB = cn.CreateCommand();
        cmdB.CommandText = "select n from b";
        using var readerB = cmdB.ExecuteReader();

        // Both already-buffered first rows, read in alternation -- no wire needed for either.
        Assert.True(readerA.Read());
        Assert.Equal(101, readerA.GetInt32(0));
        Assert.True(readerB.Read());
        Assert.Equal(201, readerB.GetInt32(0));

        // Each side's second row is a genuine FetchMore on its OWN session, interleaved.
        Assert.True(readerA.Read());
        Assert.Equal(102, readerA.GetInt32(0));
        Assert.True(readerB.Read());
        Assert.Equal(202, readerB.GetInt32(0));

        // Both exhausted, independently.
        Assert.False(readerA.Read());
        Assert.False(readerB.Read());
    }

    [Fact]
    public void AcquireSession_never_hands_the_same_session_to_two_concurrent_callers()
    {
        // Regression test for a real bug live testing caught on 2026-08-03: two real threads
        // streaming readers on one shared IfxConnection occasionally got each other's session,
        // because the original AcquireSession scanned for a free session without holding any
        // lock across the scan -- two threads could both observe the connection's default
        // session as free before either had reserved it, and both get handed it (the second
        // one's Prepare() then clobbering the first's mid-flight, live-verified stack trace:
        // "A data reader is already open on this connection" thrown from inside Prepare(),
        // called via a session AcquireSession had *just* handed to the wrong thread).
        //
        // A Barrier forces both threads to call AcquireSession at, as close as the runtime
        // allows, the same instant, on every one of many iterations -- turning a timing-
        // dependent race into something this test can catch deterministically rather than by
        // luck. The fix (IfxConnection.AcquireSession holding SqliLink.Gate across its whole
        // scan-and-reserve) makes the two calls serialize instead of racing; this test does not
        // exercise the wire at all (AcquireSession is pure session bookkeeping), so a bring-up-
        // only script is enough.
        using var cn = SqliStatementKit.OpenConnection([], out _);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            using var barrier = new Barrier(2);
            SqliSession? a = null, b = null;

            var tA = new Thread(() => { barrier.SignalAndWait(); a = cn.AcquireSession(); });
            var tB = new Thread(() => { barrier.SignalAndWait(); b = cn.AcquireSession(); });
            tA.Start();
            tB.Start();
            tA.Join();
            tB.Join();

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.False(ReferenceEquals(a, b),
                $"iteration {iteration}: both threads were handed the same session");

            // Simulate IfxCommand's finally-block cleanup so the next iteration starts clean
            // (both sessions free again) rather than piling up an ever-growing sibling set.
            a!.ClearReservedForAcquire();
            b!.ClearReservedForAcquire();
        }
    }
}
