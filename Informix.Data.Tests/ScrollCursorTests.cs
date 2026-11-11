using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

public class ScrollCursorTests
{
    private static byte[] IntRow(int v)
    {
        var r = new byte[4];
        IfxWireConverter.WriteInt32(r.AsSpan(0, 4), v);
        return r;
    }

    // The prepare reply for a one-INT-column scrollable SELECT.
    private static byte[] Prepare(short stmtId = 1) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("v", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    // A scroll-open acknowledgement (no fetch happens at open).
    private static byte[] OpenReply() => SqliStatementKit.Join(
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    // A scroll fetch reply positioning on absolute row `id` with value `value`.
    private static byte[] ScrollRow(int id, int value) => SqliStatementKit.Join(
        SqliStatementKit.TupleIdFrame(id),
        SqliStatementKit.TupleFrame(IntRow(value)),
        SqliStatementKit.EotFrame());

    // An empty scroll fetch reply (no row at the requested position).
    private static byte[] NoRow() => SqliStatementKit.EotFrame();

    // Replies to the SQ_CLOSE + SQ_RELEASE the reader's Dispose issues (a scroll
    // cursor is not auto-closed by the server, so both round trips happen).
    private static byte[] CloseRelease() => SqliStatementKit.Join(
        SqliStatementKit.EotFrame(),  // close reply
        SqliStatementKit.EotFrame()); // release reply

    [Fact]
    public void Scroll_open_emits_sq_scroll_before_sq_open()
    {
        byte[] script = SqliStatementKit.Join(Prepare(), OpenReply(), CloseRelease());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";

        using var scroll = cmd.ExecuteScrollReader();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // SQ_SCROLL(24) must appear, and immediately precede SQ_OPEN(6); no SQ_HOLD(43).
        string hex = BitConverter.ToString(sent);
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 24, 0, 6 }), hex); // SQ_SCROLL then SQ_OPEN
        Assert.DoesNotContain(BitConverter.ToString(new byte[] { 0, 43 }), hex); // no SQ_HOLD
    }

    [Fact]
    public void Scroll_open_with_hold_emits_sq_scroll_then_sq_hold_then_sq_open()
    {
        byte[] script = SqliStatementKit.Join(Prepare(), OpenReply(), CloseRelease());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        cmd.HoldCursor = true;

        using var scroll = cmd.ExecuteScrollReader();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // SQ_SCROLL(24) + SQ_HOLD(43) + SQ_OPEN(6), in that order.
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 24, 0, 43, 0, 6 }), BitConverter.ToString(sent));
    }

    [Fact]
    public void Scroll_fetch_sends_sq_sfetch_with_direction_index_and_int_buffer_size()
    {
        byte[] script = SqliStatementKit.Join(Prepare(), OpenReply(), ScrollRow(5, 500), CloseRelease());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var scroll = cmd.ExecuteScrollReader();

        long before = duplex.Output.Length;
        Assert.True(scroll.MoveAbsolute(5));

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // SQ_SFETCH(23) + direction ABSOLUTE(6):int16 + index(5):int32 + bufsize(4096):int32.
        // (No fetch-array word, unlike a forward SQ_NFETCH.)
        var expected = new byte[]
        {
            0, 23,             // SQ_SFETCH
            0, 6,              // direction = ABSOLUTE
            0, 0, 0, 5,        // index = 5
            0, 0, 0x10, 0x00,  // tuple buffer size = 4096 (int, 2GB-fetch negotiated)
        };
        Assert.Contains(BitConverter.ToString(expected), BitConverter.ToString(sent));
    }

    [Fact]
    public void Scroll_navigation_positions_on_each_requested_row()
    {
        // Rows are keyed value = id * 100 so the decoded value identifies the row.
        byte[] script = SqliStatementKit.Join(
            Prepare(),
            OpenReply(),
            ScrollRow(1, 100),   // MoveFirst      -> absolute 1
            ScrollRow(10, 1000), // MoveLast       -> last (learns lastRowId = 10)
            ScrollRow(5, 500),   // MoveAbsolute(5)-> absolute 5
            ScrollRow(3, 300),   // MoveRelative(-2) from 5 -> absolute 3
            ScrollRow(2, 200),   // MovePrevious   -> absolute 2
            ScrollRow(3, 300),   // MoveNext       -> next 3
            CloseRelease());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t order by v";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.True(scroll.MoveFirst());
        Assert.Equal(1, scroll.RowNumber);
        Assert.Equal(100, scroll.GetInt32(0));

        Assert.True(scroll.MoveLast());
        Assert.Equal(10, scroll.RowNumber);
        Assert.Equal(1000, scroll.GetInt32(0));

        Assert.True(scroll.MoveAbsolute(5));
        Assert.Equal(5, scroll.RowNumber);
        Assert.Equal(500, scroll.GetInt32(0));

        Assert.True(scroll.MoveRelative(-2));
        Assert.Equal(3, scroll.RowNumber);
        Assert.Equal(300, scroll.GetInt32(0));

        Assert.True(scroll.MovePrevious());
        Assert.Equal(2, scroll.RowNumber);
        Assert.Equal(200, scroll.GetInt32(0));

        Assert.True(scroll.MoveNext());
        Assert.Equal(3, scroll.RowNumber);
        Assert.Equal(300, scroll.GetInt32(0));
    }

    [Fact]
    public void MoveFirst_on_empty_result_returns_false()
    {
        byte[] script = SqliStatementKit.Join(Prepare(), OpenReply(), NoRow(), CloseRelease());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.False(scroll.MoveFirst());
        Assert.Equal(0, scroll.RowNumber);
    }

    // The server answers a NEXT fetch with a whole buffer of rows (SQ_TUPID, then every tuple), not one.
    private static byte[] NextBuffer(int firstId, params int[] values) => SqliStatementKit.Join(
        [SqliStatementKit.TupleIdFrame(firstId),
         .. values.Select(v => SqliStatementKit.TupleFrame(IntRow(v))),
         SqliStatementKit.EotFrame()]);

    [Fact]
    public void MoveNext_serves_the_rest_of_a_next_buffer_without_another_fetch()
    {
        // One NEXT fetch brings rows 1..3. The reader must walk them locally; the only other
        // fetch the script allows is the one that finds the end. A reader that threw the extra
        // rows away would ask the server for row 2 and read nothing (the server stands on row 3).
        byte[] script = SqliStatementKit.Join(
            Prepare(),
            OpenReply(),
            NextBuffer(1, 100, 200, 300),
            NoRow(),            // MoveNext past row 3
            CloseRelease());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t order by v";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.True(scroll.MoveNext());
        Assert.Equal(1, scroll.RowNumber);
        Assert.Equal(100, scroll.GetInt32(0));
        Assert.True(scroll.MoveNext());
        Assert.Equal(2, scroll.RowNumber);
        Assert.Equal(200, scroll.GetInt32(0));
        Assert.True(scroll.MoveNext());
        Assert.Equal(3, scroll.RowNumber);
        Assert.Equal(300, scroll.GetInt32(0));
        Assert.False(scroll.MoveNext());
    }

    [Fact]
    public void Moves_inside_the_buffered_rows_need_no_fetch_in_either_direction()
    {
        byte[] script = SqliStatementKit.Join(
            Prepare(),
            OpenReply(),
            NextBuffer(1, 100, 200, 300, 400),
            CloseRelease());   // nothing else: every move below must be answered from the buffer

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t order by v";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.True(scroll.MoveNext());            // fetches rows 1..4
        Assert.True(scroll.MoveAbsolute(4));
        Assert.Equal(400, scroll.GetInt32(0));
        Assert.True(scroll.MovePrevious());
        Assert.Equal(300, scroll.GetInt32(0));
        Assert.True(scroll.MoveRelative(-2));
        Assert.Equal(100, scroll.GetInt32(0));
        Assert.True(scroll.MoveFirst());
        Assert.Equal(1, scroll.RowNumber);
        Assert.True(scroll.MoveNext());
        Assert.Equal(200, scroll.GetInt32(0));
    }

    [Fact]
    public void Next_from_before_the_first_row_is_fetched_by_number_when_the_server_stands_elsewhere()
    {
        // The server stands on row 5. MoveAbsolute(0) puts only the CLIENT before the first row, so
        // the server's "next" (row 6) is not the wanted row 1: it has to be asked for by number.
        byte[] script = SqliStatementKit.Join(
            Prepare(),
            OpenReply(),
            ScrollRow(5, 500),   // MoveAbsolute(5)
            ScrollRow(1, 100),   // MoveNext -> row 1, absolute
            CloseRelease());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t order by v";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.True(scroll.MoveAbsolute(5));
        Assert.False(scroll.MoveAbsolute(0));
        long before = duplex.Output.Length;
        Assert.True(scroll.MoveNext());
        Assert.Equal(1, scroll.RowNumber);
        Assert.Equal(100, scroll.GetInt32(0));

        // SQ_SFETCH(23), direction 6 (absolute), row 1 - not direction 1 (next).
        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 23, 0, 6, 0, 0, 0, 1 }), BitConverter.ToString(sent));
    }

    [Fact]
    public void MoveNext_iterates_forward_and_stops_after_the_last_row()
    {
        byte[] script = SqliStatementKit.Join(
            Prepare(),
            OpenReply(),
            ScrollRow(1, 100),  // MoveNext -> row 1
            ScrollRow(2, 200),  // MoveNext -> row 2
            NoRow(),            // MoveNext -> past the end
            CloseRelease());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.True(scroll.MoveNext());
        Assert.Equal(100, scroll.GetInt32(0));
        Assert.True(scroll.MoveNext());
        Assert.Equal(200, scroll.GetInt32(0));
        Assert.False(scroll.MoveNext());
    }

    [Fact]
    public void Scroll_reader_reports_the_distinct_type_name_not_the_base_type()
    {
        // Regression test for §4 of the structural refactor: IfxScrollReader.GetDataTypeName
        // used to call SqliTypeCode.ToString() bare, losing the DISTINCT/named-opaque/ROW
        // override IfxColumnMetadata.DataTypeName applies -- the same override
        // IfxDataReader.GetDataTypeName already honoured (see
        // SqliStatementTests.Distinct_type_decodes_as_its_base_and_reports_its_name for the
        // forward-only-reader version of this same assertion). Before the fix this asserted
        // "Int", not "myid".
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.Join(
                SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                    // typeWord 0x0802 = DISTINCT(0x800) over INT(2); owner/name "root"/"myid".
                    new SqliStatementKit.Col("v", unchecked((short)0x0802), 4, 0, ExtOwner: "root", ExtName: "myid")),
                SqliStatementKit.DoneFrame(),
                SqliStatementKit.EotFrame()),
            OpenReply(),
            ScrollRow(1, 42),
            CloseRelease());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var scroll = cmd.ExecuteScrollReader();

        Assert.Equal("myid", scroll.GetDataTypeName(0)); // the distinct name, not "Int"
        Assert.Equal(typeof(int), scroll.GetFieldType(0)); // decodes as its INT base
        Assert.True(scroll.MoveFirst());
        Assert.Equal(42, scroll.GetInt32(0));
    }

    [Fact]
    public void HoldCursor_on_a_forward_reader_emits_sq_hold_before_sq_open()
    {
        // Forward-only reader path with HoldCursor: SQ_HOLD(43) precedes SQ_OPEN(6).
        byte[] script = SqliStatementKit.Join(
            Prepare(),
            SqliStatementKit.DoneFrame(),               // open + fetch reply
            SqliStatementKit.TupleFrame(IntRow(42)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());               // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        cmd.HoldCursor = true;

        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(42, r.GetInt32(0));

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 43, 0, 6 }), BitConverter.ToString(sent));
    }
}
