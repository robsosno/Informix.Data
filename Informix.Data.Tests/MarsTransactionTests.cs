using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The transaction half of MARS: what happens to sibling sessions' cursors when the
/// connection's transaction ends. (The pooling half — an abandoned reader on a sibling
/// session making a connection unsafe to pool, exactly like one on the default session
/// always did — lives in <c>ConnectionPoolTests</c>, alongside its non-MARS sibling test.)
///
/// <para>The commit/rollback semantics here are live-verified against a real IDS 14.10 server
/// (2026-08-03, database <c>testlog</c> — a logged database; the driver's own <c>stores_demo</c>
/// probe database is unlogged and rejects transactions outright) using a ~8836-row result, far
/// beyond any client fetch buffer so cursor survival is provably server-side: a WITH HOLD cursor
/// survives both COMMIT and ROLLBACK identically and keeps yielding every remaining row; a
/// non-hold cursor dies on both, identically — there is no asymmetry to model. These scripted
/// tests reproduce that matrix at the wire level, scaled down to two rows per cursor.</para>
/// </summary>
public class MarsTransactionTests
{
    private static byte[] IntRow(int n)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    // A 1-column select's prepare + open + first-row fetch, deliberately NOT auto-closed by the
    // server (no inline SQ_CLOSE) so a second Read() would need a genuine FetchMore -- the shape
    // that lets a WITH HOLD cursor prove it is still alive server-side after commit/rollback, and
    // a non-hold cursor prove it is NOT (by throwing instead of sending doomed wire bytes).
    private static byte[] PrepareOpenOneRow(short stmtId, int value) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.TupleFrame(IntRow(value)),
        SqliStatementKit.EotFrame());

    // The reply to the FetchMore a WITH HOLD cursor's second Read() sends after the
    // transaction ends -- the server answering proves its cursor is still open.
    private static byte[] FetchMoreOneRow(int value) => SqliStatementKit.Join(
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.TupleFrame(IntRow(value)),
        SqliStatementKit.EotFrame());

    private static byte[] CloseAndRelease() => SqliStatementKit.Join(
        SqliStatementKit.EotFrame(), SqliStatementKit.EotFrame());

    private static byte[] ReleaseOnly() => SqliStatementKit.EotFrame();

    private void RunTransactionEndMatrix(bool commit)
    {
        // Order on the wire matches real execution order exactly:
        //  1. SQ_BEGIN reply
        //  2. non-hold reader: prepare+open+fetch (default session)
        //  3. hold reader: prepare+open+fetch (a sibling session -- the default is busy)
        //  4. commit/rollback reply
        //  5. hold reader's post-transaction FetchMore (proves it survived)
        //  6. hold reader's Dispose: close + release
        //  7. non-hold reader's Dispose: release ONLY (CloseCursor is a no-op -- the
        //     session already knows the server discarded this cursor)
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),          // SQ_BEGIN reply
            PrepareOpenOneRow(stmtId: 1, value: 101),
            PrepareOpenOneRow(stmtId: 2, value: 201),
            SqliStatementKit.EotFrame(),          // commit/rollback reply
            FetchMoreOneRow(202),
            CloseAndRelease(),
            ReleaseOnly());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = cn.BeginTransaction();

        using var nonHoldCmd = cn.CreateCommand();
        nonHoldCmd.Transaction = tx;
        nonHoldCmd.CommandText = "select n from nonhold";
        nonHoldCmd.HoldCursor = false;
        using var nonHoldReader = nonHoldCmd.ExecuteReader();
        Assert.True(nonHoldReader.Read());
        Assert.Equal(101, nonHoldReader.GetInt32(0));

        using var holdCmd = cn.CreateCommand();
        holdCmd.Transaction = tx;
        holdCmd.CommandText = "select n from held";
        holdCmd.HoldCursor = true;
        using var holdReader = holdCmd.ExecuteReader();
        Assert.True(holdReader.Read());
        Assert.Equal(201, holdReader.GetInt32(0));

        if (commit)
            tx.Commit();
        else
            tx.Rollback();

        // The non-hold cursor died with the transaction: reaching past its already-buffered
        // row throws a clear diagnostic instead of silently claiming end-of-data or sending a
        // doomed fetch to a cursor the server has already discarded.
        var ex = Assert.Throws<InvalidOperationException>(() => nonHoldReader.Read());
        Assert.Contains("transaction ended", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The WITH HOLD cursor is completely unaffected -- it genuinely fetches its next row
        // from the server (the scripted FetchMore reply proves this isn't just local buffer).
        Assert.True(holdReader.Read());
        Assert.Equal(202, holdReader.GetInt32(0));
    }

    [Fact]
    public void Commit_closes_non_hold_cursors_but_spares_with_hold_ones() =>
        RunTransactionEndMatrix(commit: true);

    [Fact]
    public void Rollback_closes_non_hold_cursors_but_spares_with_hold_ones_identically_to_commit() =>
        RunTransactionEndMatrix(commit: false);

    [Fact]
    public void Disposing_the_dead_non_hold_reader_sends_no_close_only_a_release()
    {
        // CloseCursor() on a session the transaction already closed must be silent (the
        // server has nothing left to close); only the statement release still goes out.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),
            PrepareOpenOneRow(stmtId: 1, value: 101),
            SqliStatementKit.EotFrame(), // commit reply
            ReleaseOnly());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var tx = cn.BeginTransaction();

        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "select n from t";
        var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        tx.Commit();

        long beforeDispose = duplex.Output.Length;
        reader.Dispose();

        // Exactly one release round trip's worth of bytes, no SQ_CLOSE bytes.
        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)beforeDispose..]);
        Assert.DoesNotContain("00-0A", hex); // SQ_CLOSE(10) never sent
        Assert.Contains("00-04-00-01-00-0B", hex); // SQ_ID(4)=1, SQ_RELEASE(11)
    }

}
