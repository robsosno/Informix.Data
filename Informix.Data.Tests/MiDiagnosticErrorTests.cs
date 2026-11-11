using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// SQ_ERR's MI (DataBlade/UDR) variant: sqlcode -937 for the error form and 0 for the
/// warning form. Both carry two fields the ordinary shape does not — a raw 5-byte
/// SQLSTATE and the real diagnostic text — with the routine's name occupying the slot
/// where an ordinary error puts its message.
///
/// The bytes here are the shape captured live from IDS 9.59.FC5 when a zero-length
/// binaryvar made the server's own binaryvarout() fail:
///     [13][-937][0][offset]["binaryvarout"]["UBD01"]["memory allocation failed"]
/// Reading only as far as the routine name left the SQLSTATE and the text on the wire,
/// and the next ReceiveMessage took "D0" (0x4430 = 17456) for a message id.
/// </summary>
public class MiDiagnosticErrorTests
{
    /// <summary>A SQ_ERR reply in the MI shape, then SQ_EOT.</summary>
    private static byte[] MiErrorReply(short sqlcode, short isam, string routine, string sqlState, string message)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Err);
        w.WriteInt16(sqlcode);
        w.WriteInt16(isam);
        w.WriteInt16(0);              // 16-bit offset (64K-limit path not enabled)
        w.WriteChar(routine);         // NOT the message: the failing routine's name
        w.WritePadded(IfxWireConverter.EncodeChar(sqlState, IfxWireConverter.DefaultEncoding));
        w.WriteChar(message);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    [Fact]
    public void Mi_error_surfaces_the_diagnostic_text_and_sqlstate()
    {
        var session = SqliTestKit.SessionOver(
            MiErrorReply(-937, 0, "binaryvarout", "UBD01", "memory allocation failed"), out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);

        Assert.Equal(-937, ex.IfxSqlCode);
        Assert.Equal("UBD01", ex.SqlState);
        Assert.Contains("memory allocation failed", ex.Message);
        // The routine name is the most useful thing in the reply — "memory allocation
        // failed" on its own does not say which UDR failed.
        Assert.Contains("binaryvarout", ex.Message);
    }

    [Fact]
    public void Mi_error_consumes_its_whole_body_so_the_stream_stays_in_sync()
    {
        // The regression itself: a DONE frame queued after the MI error must still be
        // readable. Before the fix the leftover SQLSTATE/message bytes were taken for
        // the next message id and the session was unusable.
        byte[] script = MiErrorReply(-937, 0, "binaryvarout", "UBD01", "memory allocation failed")
            .Concat(SqliTestKit.DoneReply(warnings: 0, rows: 7, rowid: 0, serial: 0))
            .ToArray();
        var session = SqliTestKit.SessionOver(script, out _);

        Assert.Throws<IfxException>(session.ReceiveMessage);

        // Second round trip reads the DONE cleanly: no stray bytes were left behind.
        session.ReceiveMessage();
        Assert.Equal(7, session.RowsProcessed);
    }

    [Fact]
    public void Mi_warning_form_is_collected_as_a_diagnostic_not_an_error()
    {
        // sqlcode 0 is the same body in its warning form. It must not throw, must not
        // desync, and must not be silently dropped either.
        var session = SqliTestKit.SessionOver(
            MiErrorReply(0, 0, "mi_udr", "01U01", "value was rounded"), out _);

        session.ReceiveMessage(); // does not throw

        var diagnostic = Assert.Single(session.Diagnostics);
        Assert.Equal("01U01", diagnostic.SQLState);
        Assert.Equal(0, diagnostic.NativeError);
        Assert.Contains("value was rounded", diagnostic.Message);
        Assert.Contains("mi_udr", diagnostic.Message);
    }

    [Fact]
    public void Diagnostics_are_cleared_at_the_start_of_each_round_trip()
    {
        byte[] script = MiErrorReply(0, 0, "mi_udr", "01U01", "value was rounded")
            .Concat(SqliTestKit.DoneReply(warnings: 0, rows: 1, rowid: 0, serial: 0))
            .ToArray();
        var session = SqliTestKit.SessionOver(script, out _);

        session.ReceiveMessage();
        Assert.Single(session.Diagnostics);

        session.ReceiveMessage();
        Assert.Empty(session.Diagnostics);
    }

    [Fact]
    public void An_ordinary_error_still_reads_only_the_common_body()
    {
        // Guard the split: a normal sqlcode must NOT try to read a SQLSTATE and a second
        // string, or it would desync exactly the way the MI shape used to.
        byte[] script = SqliTestKit.ErrorReply(-201, 0, 12, "A syntax error has occurred.")
            .Concat(SqliTestKit.DoneReply(warnings: 0, rows: 3, rowid: 0, serial: 0))
            .ToArray();
        var session = SqliTestKit.SessionOver(script, out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);
        Assert.Equal(-201, ex.IfxSqlCode);

        session.ReceiveMessage();
        Assert.Equal(3, session.RowsProcessed);
    }

    [Fact]
    public void An_error_with_an_empty_message_gets_IBMs_wording_for_the_sqlcode()
    {
        // -9636 arrives from a real server with an empty message field: the text lives in
        // the client-side message catalog this driver does not carry. Live-observed by
        // pushing a binaryvar past its 253-byte ceiling on IDS 9.59.FC5.
        var session = SqliTestKit.SessionOver(SqliTestKit.ErrorReply(-9636, 0, 0, ""), out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);

        Assert.Equal(-9636, ex.IfxSqlCode);
        Assert.Equal("Opaque type exceeded its maximum length.", ex.Message);
    }

    [Fact]
    public void An_unknown_sqlcode_with_an_empty_message_still_names_the_code()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.ErrorReply(-12345, 0, 0, ""), out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);
        Assert.Equal("Informix error -12345.", ex.Message);
    }

    [Fact]
    public void A_server_supplied_message_is_never_overridden()
    {
        // The catalog fallback must only fill a genuine gap: whenever the reply carries
        // text, that text wins even for a code the fallback knows.
        var session = SqliTestKit.SessionOver(
            SqliTestKit.ErrorReply(-9636, 0, 0, "value too long for bin_probe.bv"), out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);
        Assert.Equal("value too long for bin_probe.bv", ex.Message);
    }

    [Fact]
    public void InfoMessage_carries_the_server_diagnostic_text()
    {
        byte[] script = SqliStatementKit.Join(
            // prepare reply
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // execute reply: the UDR warns, then the statement completes
            MiWarningThenDone(),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        IfxInfoMessageEventArgs? received = null;
        cn.InfoMessage += (_, e) => received = e;

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add("p1", 1);
        cmd.ExecuteNonQuery();

        Assert.NotNull(received);
        // IfxErrorCollection is the non-generic ICollection ADO.NET's error collections
        // are, so Assert.Single hands back an object.
        var diagnostic = (IfxError)Assert.Single(received!.Errors)!;
        Assert.Equal("01U01", diagnostic.SQLState);
        Assert.Contains("value was rounded", diagnostic.Message);
    }

    // A SQ_ERR(0) MI warning followed by the statement's DONE — what a UDR that warns
    // but succeeds actually sends.
    private static byte[] MiWarningThenDone()
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Err);
        w.WriteInt16(0);  // sqlcode 0: the MI warning form
        w.WriteInt16(0);  // isam code
        w.WriteInt32(0);  // statement offset (int: this script goes through the full
                          // bring-up, so remove-64K-limit is negotiated)
        w.WriteChar("mi_udr");
        w.WritePadded(IfxWireConverter.EncodeChar("01U01", IfxWireConverter.DefaultEncoding));
        w.WriteChar("value was rounded");
        return ms.ToArray().Concat(SqliStatementKit.DoneFrame(rows: 1, warnings: 0)).ToArray();
    }
}
