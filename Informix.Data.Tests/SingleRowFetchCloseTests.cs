using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="CommandBehavior.SingleRow"/>'s fetch+close piggyback: nothing past the first
/// tuple of the first buffer can ever be surfaced, so SQ_CLOSE(10) rides out in the same PDU
/// as SQ_NFETCH(9) instead of costing a round trip of its own when the reader is disposed.
/// Canned 9.59 frames throughout, like the rest of the statement-pipeline tests.
/// </summary>
public class SingleRowFetchCloseTests
{
    // SQ_ID(4) + statement id + SQ_CLOSE(10): the close appended to the fetch.
    private static string CloseOn(short stmtId) =>
        BitConverter.ToString([0, 4, 0, (byte)stmtId, 0, 10]);

    private static readonly string CloseOnStatementOne = CloseOn(1);

    private const string Eot = "00-0C";

    private static byte[] IntDescribe(short stmtId = 1) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] IntRow(int n)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    // An LVARCHAR tuple field: [flag:1][length:int32][data]. A var-length column forces
    // the SQ_RET_TYPE typed fetch, which is the two-phase (non-OPTOFC) open path.
    private static byte[] Lvc(string value)
    {
        byte[] data = IfxWireConverter.EncodeChar(value);
        var field = new byte[5 + data.Length];
        IfxWireConverter.WriteInt32(field.AsSpan(1, 4), data.Length);
        data.CopyTo(field.AsSpan(5));
        return field;
    }

    private static byte[] LvarcharDescribe(short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 0,
            new SqliStatementKit.Col("v", (short)SqliTypeCode.UdtVar, 500, 0,
                ExtendedId: 1, ExtOwner: "informix", ExtName: "lvarchar")),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    /// <summary>Brings a session up over <paramref name="script"/> and prepares, leaving the
    /// output offset and fetch counters zeroed so a test measures only what the open sends.</summary>
    private static SqliSession Prepared(
        byte[] script, out DuplexStream duplex, out long before, string sql = "select n from t")
    {
        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out duplex);
        session.BringUp();
        session.Prepare(sql);
        before = duplex.Output.Length;
        session.ResetFetchStats();
        return session;
    }

    private static string SentSince(DuplexStream duplex, long offset) =>
        BitConverter.ToString(duplex.Output.ToArray()[(int)offset..]);

    [Fact]
    public void SingleRow_appends_the_close_to_the_fetch_pdu()
    {
        // The reply deliberately carries no SQ_CLOSE frame: the piggybacked close is what
        // ends the cursor here, so the session must not be depending on the server's
        // end-of-rows auto-close acknowledgement to know the cursor is gone.
        byte[] script = SqliStatementKit.Join(
            IntDescribe(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.EotFrame());

        var session = Prepared(script, out var duplex, out long before);
        session.OpenCursor(singleRow: true);

        Assert.Equal(1, session.RequestRoundTrips); // open + fetch + close: one exchange

        string hex = SentSince(duplex, before);
        Assert.Contains("00-09", hex);                             // SQ_NFETCH went out…
        Assert.EndsWith($"{CloseOnStatementOne}-{Eot}", hex);      // …close last, then EOT

        Assert.True(session.HasRows);
        Assert.True(session.NextRow());
        Assert.Equal(7, (int)session.DecodeColumn(0));
        Assert.False(session.NextRow());
    }

    [Fact]
    public void SingleRow_makes_closing_the_cursor_wire_free()
    {
        // No close reply is scripted: if CloseCursor sent anything it would block waiting
        // for a reply that never comes.
        byte[] script = SqliStatementKit.Join(
            IntDescribe(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.EotFrame());

        var session = Prepared(script, out var duplex, out _);
        session.OpenCursor(singleRow: true);
        Assert.True(session.NextRow());

        long afterOpen = duplex.Output.Length;
        session.CloseCursor(); // what disposing the reader does

        Assert.Equal(afterOpen, duplex.Output.Length); // not one byte more
        Assert.Equal(1, session.RequestRoundTrips);
        Assert.False(session.CursorOpen);
    }

    [Fact]
    public void Without_single_row_the_close_is_its_own_round_trip()
    {
        byte[] script = SqliStatementKit.Join(
            IntDescribe(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.EotFrame(),  // fetch reply — the cursor is still open
            SqliStatementKit.EotFrame()); // reply to the standalone SQ_CLOSE

        var session = Prepared(script, out var duplex, out long before);
        session.OpenCursor();

        Assert.DoesNotContain(CloseOnStatementOne, SentSince(duplex, before));
        Assert.Equal(1, session.RequestRoundTrips);

        session.CloseCursor();

        Assert.Equal(2, session.RequestRoundTrips); // the close needed an exchange of its own
        Assert.Contains(CloseOnStatementOne, SentSince(duplex, before));
    }

    [Fact]
    public void SingleRow_keeps_the_session_guarded_until_the_cursor_is_closed()
    {
        byte[] script = SqliStatementKit.Join(
            IntDescribe(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.EotFrame());

        var session = Prepared(script, out _, out _);
        session.OpenCursor(singleRow: true);

        // The server cursor is gone, but the session still owns the tuple buffer the reader
        // decodes from: a second statement must still be refused, and the physical
        // connection must not yet look clean enough to return to the pool.
        Assert.True(session.CursorOpen);
        Assert.False(session.IsIdle);
        Assert.Throws<InvalidOperationException>(() => session.Prepare("select n from t"));

        session.CloseCursor();
        Assert.False(session.CursorOpen);
    }

    [Fact]
    public void SingleRow_over_an_empty_result_does_not_fetch_again()
    {
        // Zero tuples. NextRow's "the first buffer came back empty, fetch once more" path
        // would put a second SQ_NFETCH on a cursor the server has already closed — and would
        // run off the end of this script.
        byte[] script = SqliStatementKit.Join(
            IntDescribe(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        var session = Prepared(script, out var duplex, out _);
        session.OpenCursor(singleRow: true);

        Assert.False(session.HasRows);

        long afterOpen = duplex.Output.Length;
        Assert.False(session.NextRow());

        Assert.Equal(afterOpen, duplex.Output.Length);
        Assert.Equal(1, session.RequestRoundTrips);
    }

    [Fact]
    public void SingleRow_keeps_the_cursor_open_for_byte_and_text_columns()
    {
        // A TEXT value is a 56-byte in-tuple descriptor whose bytes only arrive on a
        // follow-up SQ_FETCHBLOB, so the cursor has to outlive the fetch.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 56,
                new SqliStatementKit.Col("t", (short)SqliTypeCode.Text, 56, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(SqliStatementKit.BlobDescriptor(4)),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // the close still has to go out on its own

        var session = Prepared(script, out var duplex, out long before, "select t from t");
        session.OpenCursor(singleRow: true);

        Assert.DoesNotContain(CloseOnStatementOne, SentSince(duplex, before));
        Assert.True(session.CursorOpen);

        session.CloseCursor();
        Assert.Equal(2, session.RequestRoundTrips);
        Assert.Contains(CloseOnStatementOne, SentSince(duplex, before));
    }

    [Fact]
    public void SingleRow_keeps_the_cursor_open_for_smart_large_objects()
    {
        // CLOB/BLOB describe as UdtFixed(41) with extended ids 11/10; their bytes are read
        // later through the locator, which needs the cursor alive.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("doc", (short)SqliTypeCode.UdtFixed, 72, 4,
                    ExtendedId: 11, ExtOwner: "informix", ExtName: "clob")),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),  // open reply (a walked row opens before it fetches)
            SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(  // fetch reply
                SqliStatementKit.Join(IntRow(1), SqliStatementKit.SmartBlobField(new byte[72]))),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // reply to the standalone SQ_CLOSE

        var session = Prepared(script, out var duplex, out long before, "select id, doc from t");
        session.OpenCursor(singleRow: true);

        Assert.DoesNotContain(CloseOnStatementOne, SentSince(duplex, before));
        Assert.True(session.CursorOpen);

        session.CloseCursor();
        Assert.Contains(CloseOnStatementOne, SentSince(duplex, before));
    }

    [Fact]
    public void SingleRow_with_optofc_puts_open_fetch_and_close_in_one_pdu()
    {
        byte[] script = SqliStatementKit.Join(
            LvarcharDescribe(2),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(Lvc("hi")),
            SqliStatementKit.EotFrame());

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        session.Optofc = true;
        session.Prepare("select v from t");
        long before = duplex.Output.Length;
        session.ResetFetchStats();

        session.OpenCursor(singleRow: true);

        Assert.Equal(1, session.RequestRoundTrips); // open, fetch and close in one flush

        string hex = SentSince(duplex, before);
        Assert.Contains("00-06-00-04-00-02-00-64", hex);       // SQ_OPEN → SQ_ID(2) → SQ_RET_TYPE
        Assert.EndsWith($"{CloseOn(2)}-{Eot}", hex);           // …and SQ_CLOSE last

        Assert.True(session.NextRow());
        Assert.Equal("hi", (string)session.DecodeColumn(0));
        Assert.False(session.NextRow());
    }

    [Fact]
    public void SingleRow_without_optofc_still_closes_with_the_typed_fetch()
    {
        // Var-length row, OPTOFC off: the open is flushed on its own, then the typed fetch
        // — and the close rides in that second PDU, so two exchanges cover all three opcodes.
        byte[] script = SqliStatementKit.Join(
            LvarcharDescribe(2),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),   // open reply
            SqliStatementKit.TupleFrame(Lvc("hi")),
            SqliStatementKit.EotFrame());  // fetch + close reply

        var session = Prepared(script, out var duplex, out long before, "select v from t");
        session.OpenCursor(singleRow: true);

        Assert.Equal(2, session.RequestRoundTrips);

        string hex = SentSince(duplex, before);
        Assert.Contains("00-06-00-0C", hex);          // SQ_OPEN then EOT: flushed on its own
        Assert.EndsWith($"{CloseOn(2)}-{Eot}", hex);  // the close went with the typed fetch

        Assert.True(session.NextRow());
        Assert.Equal("hi", (string)session.DecodeColumn(0));

        long afterOpen = duplex.Output.Length;
        session.CloseCursor();
        Assert.Equal(afterOpen, duplex.Output.Length); // nothing left to send
    }

    [Fact]
    public void ExecuteReader_with_SingleRow_closes_the_cursor_with_the_fetch()
    {
        // End to end through IfxCommand/IfxDataReader. No reply is scripted for a standalone
        // SQ_CLOSE — only the statement release — so a reader Dispose that still closed the
        // cursor on the wire could not complete.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(TwoIntRow(1, 2)),
            SqliStatementKit.TupleFrame(TwoIntRow(3, 4)),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";

        using (var reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
        {
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.False(reader.Read()); // second row withheld by SingleRow
        }

        Assert.Contains(CloseOnStatementOne, BitConverter.ToString(duplex.Output.ToArray()));
    }

    [Fact]
    public void ExecuteReader_without_SingleRow_is_unaffected()
    {
        // The ordinary path still opens a cursor that lives until the reader is disposed,
        // which is when the close goes out.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(TwoIntRow(1, 2)),
            SqliStatementKit.TupleFrame(TwoIntRow(3, 4)),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame(),  // reply to the standalone SQ_CLOSE
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";

        long beforeDispose;
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.Equal(3, reader.GetInt32(0));
            beforeDispose = duplex.Output.Length;
        }

        // Disposing the reader is what sends the close here.
        Assert.True(duplex.Output.Length > beforeDispose);
        Assert.Contains(CloseOnStatementOne, BitConverter.ToString(duplex.Output.ToArray()));
    }

    private static byte[] TwoIntRow(int a, int b)
    {
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), a);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), b);
        return row;
    }
}
