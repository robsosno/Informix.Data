using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>Builds canned SQLI reply frames for the in-memory session tests.</summary>
internal static class SqliTestKit
{
    /// <summary>A SQ_VERSION reply carrying <paramref name="version"/>, followed by SQ_EOT.</summary>
    public static byte[] VersionReply(string version)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Version);
        w.WriteChar(version);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    /// <summary>A SQ_PROTOCOLS reply carrying the server capability vector, then SQ_EOT.</summary>
    public static byte[] ProtocolsReply(byte[]? serverProto = null)
    {
        serverProto ??= [0xbd, 0xbe, 0x9f, 0xfe, 0x7f, 0xbf, 0xff, 0xef, 0xf0]; // captured 9.59 reply
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Protocols);
        w.WriteInt16((short)serverProto.Length);
        w.WritePadded(serverProto);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    /// <summary>The full remote bring-up server script: protocols reply then version reply.</summary>
    public static byte[] BringUpReply(string version) =>
        ProtocolsReply().Concat(VersionReply(version)).ToArray();

    /// <summary>A SQ_ERR reply with the given sqlcode/isam/message, then SQ_EOT.</summary>
    public static byte[] ErrorReply(short sqlcode, short isam, int offset, string message)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Err);
        w.WriteInt16(sqlcode);
        w.WriteInt16(isam);
        w.WriteInt16((short)offset);   // 16-bit offset (64K-limit path not enabled)
        w.WriteChar(message);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    /// <summary>A SQ_DONE reply (warnings + rowcount + rowid + serial), then SQ_EOT.</summary>
    public static byte[] DoneReply(short warnings, int rows, int rowid, int serial)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Done);
        w.WriteInt16(warnings);
        w.WriteInt32(rows);
        w.WriteInt32(rowid);
        w.WriteInt32(serial);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    /// <summary>
    /// A SQ_DONE reply in the Informix 15 <c>isLongRowId</c> shape: the row count and
    /// row id widen to 8 bytes while the serial stays a 4-byte field (IfxSqli.receiveDone
    /// reads sqlerrd[1] with readInt() regardless of the capability).
    /// </summary>
    public static byte[] DoneReplyLongRowId(short warnings, long rows, long rowid, int serial)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Done);
        w.WriteInt16(warnings);
        w.WriteBigInt(rows);
        w.WriteBigInt(rowid);
        w.WriteInt32(serial);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    /// <summary>
    /// A server capability vector whose ninth byte grants both Informix 15 large-row
    /// bits (0x04 isLargeTupleSize | 0x02 isLongRowId); the first eight bytes are the
    /// captured 9.59 reply so only the new bits differ.
    /// </summary>
    public static byte[] LargeRowGrantVector =>
        [0xbd, 0xbe, 0x9f, 0xfe, 0x7f, 0xbf, 0xff, 0xef, 0x06];

    /// <summary>Wraps a canned reply in an AsfConnection whose transport is a duplex stream.</summary>
    public static SqliSession SessionOver(
        byte[] serverReply, out DuplexStream duplex, bool requestLargeRowFormats = false)
    {
        duplex = new DuplexStream(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept).Concat(serverReply).ToArray());
        var asf = new AsfConnection(AsfTestKit.Settings(), duplex);
        asf.Connect(); // consumes the accept frame; leaves the SQLI reply queued
        return new SqliSession(asf) { RequestLargeRowFormats = requestLargeRowFormats };
    }
}

public class SqliVersionTests
{
    [Fact]
    public void ExecuteVersion_reads_the_server_version_and_sends_version_then_eot()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.VersionReply("9.59.FC5"), out var duplex);
        long afterConnect = duplex.Output.Length;

        session.ExecuteVersion();

        Assert.Equal("9.59.FC5", session.ServerVersion);

        // The request is exactly SQ_VERSION (53) + SQ_EOT (12).
        byte[] sent = duplex.Output.ToArray()[(int)afterConnect..];
        Assert.Equal(new byte[] { 0, 53, 0, 12 }, sent);
    }

    [Fact]
    public void ExecuteProtocols_sends_the_client_vector_and_reads_the_server_reply()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.ProtocolsReply(), out var duplex);
        long afterConnect = duplex.Output.Length;

        session.ExecuteProtocols();

        // Request: SQ_PROTOCOLS(126) + len(8) + the 8 client bytes + SQ_EOT(12).
        byte[] sent = duplex.Output.ToArray()[(int)afterConnect..];
        Assert.Equal(
            new byte[] { 0, 126, 0, 8, 0xff, 0xfc, 0x7f, 0xfc, 0x3c, 0x8c, 0xaa, 0x97, 0, 12 },
            sent);
        Assert.Equal(9, session.ServerProtocol.Length);
    }

    [Fact]
    public void ExecuteProtocols_appends_the_ninth_byte_when_large_row_formats_are_requested()
    {
        var session = SqliTestKit.SessionOver(
            SqliTestKit.ProtocolsReply(), out var duplex, requestLargeRowFormats: true);
        long afterConnect = duplex.Output.Length;

        session.ExecuteProtocols();

        // The JDBC 15.0.1.3 vector: the same eight bytes plus 0x06 (0x04 | 0x02),
        // asking for isLargeTupleSize and isLongRowId. Length word becomes 9.
        byte[] sent = duplex.Output.ToArray()[(int)afterConnect..];
        Assert.Equal(
            new byte[] { 0, 126, 0, 9, 0xff, 0xfc, 0x7f, 0xfc, 0x3c, 0x8c, 0xaa, 0x97, 0x06, 0, 0, 12 },
            sent);
    }

    [Fact]
    public void A_server_that_does_not_grant_the_ninth_bits_leaves_both_capabilities_off()
    {
        // The captured 9.59 reply is already nine bytes long, but its ninth byte (0xf0)
        // has neither 0x04 nor 0x02 — so parsing it must not enable the wide formats.
        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5"), out _);

        session.BringUp();

        Assert.False(session.Capabilities.IsLargeTupleSize);
        Assert.False(session.Capabilities.IsLongRowId);
    }

    [Fact]
    public void A_server_granting_the_ninth_bits_enables_both_capabilities()
    {
        byte[] script = SqliTestKit.ProtocolsReply(SqliTestKit.LargeRowGrantVector)
            .Concat(SqliTestKit.VersionReply("15.0.FC1"))
            .ToArray();
        var session = SqliTestKit.SessionOver(script, out _, requestLargeRowFormats: true);

        session.BringUp();

        Assert.True(session.Capabilities.IsLargeTupleSize);
        Assert.True(session.Capabilities.IsLongRowId);
    }

    [Fact]
    public void Ninth_bits_the_client_did_not_ask_for_are_ignored()
    {
        // Informix 15.0.1.0 sets 0x04 | 0x02 in its reply even to the eight-byte request
        // and then keeps the classic layouts. Honouring the bits would read a 4-byte
        // tuple size out of a 2-byte field and hang in the describe.
        byte[] script = SqliTestKit.ProtocolsReply(SqliTestKit.LargeRowGrantVector)
            .Concat(SqliTestKit.VersionReply("15.0.1.0"))
            .ToArray();
        var session = SqliTestKit.SessionOver(script, out _);

        session.BringUp();

        Assert.False(session.Capabilities.IsLargeTupleSize);
        Assert.False(session.Capabilities.IsLongRowId);
        // Only the two large-row bits are dropped; the rest of the reply still applies.
        Assert.True(session.Capabilities.Is4ByteOffsetSupported);
        Assert.True(session.Capabilities.IsExtendedDescriptor);
    }

    [Fact]
    public void Done_reply_widens_counts_but_not_the_serial_under_long_rowid()
    {
        // Values above int range prove the 8-byte reads, while the serial must still be
        // read as 4 bytes — mis-widening it would desynchronise the frame and the
        // trailing SQ_EOT would not parse.
        const long rows = 5_000_000_000L;
        const long rowid = 9_000_000_000L;
        byte[] script = SqliTestKit.ProtocolsReply(SqliTestKit.LargeRowGrantVector)
            .Concat(SqliTestKit.VersionReply("15.0.FC1"))
            .Concat(SqliTestKit.DoneReplyLongRowId(3, rows, rowid, 1001))
            .ToArray();
        var session = SqliTestKit.SessionOver(script, out _, requestLargeRowFormats: true);
        session.BringUp();

        session.ReceiveMessage();

        Assert.Equal(3, session.Warnings);
        Assert.Equal(unchecked((int)rows), session.RowsProcessed); // narrowed like JDBC
        Assert.Equal(1001, session.SerialInserted);
    }

    [Fact]
    public void Done_reply_keeps_the_four_byte_shape_when_the_bits_are_not_granted()
    {
        // Guards the default path: an ungranted session must read the original frame.
        var session = SqliTestKit.SessionOver(SqliTestKit.DoneReply(3, 42, 0, 1001), out _);

        session.ReceiveMessage();

        Assert.Equal(42, session.RowsProcessed);
        Assert.Equal(1001, session.SerialInserted);
    }

    [Fact]
    public void BringUp_sends_protocols_before_version()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5"), out var duplex);
        long afterConnect = duplex.Output.Length;

        session.BringUp();

        byte[] sent = duplex.Output.ToArray()[(int)afterConnect..];
        // First message code must be SQ_PROTOCOLS (126), not SQ_VERSION (53).
        Assert.Equal(126, sent[0] << 8 | sent[1]);
        Assert.Equal("9.59.FC5", session.ServerVersion);
    }

    [Fact]
    public void BringUp_resolves_capabilities_from_version_and_protocol_vector()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5"), out _);

        session.BringUp();

        Assert.Equal("9.59.FC5", session.Capabilities.ServerVersion);
        Assert.True(session.Capabilities.IsExtendedDescriptor);       // bit 2 (version ladder + proto byte 0)
        // The higher feature bits come from the SQ_PROTOCOLS reply vector:
        Assert.True(session.Capabilities.Is4ByteOffsetSupported);     // bit 50 <- byte 6 = 0xff
        Assert.True(session.Capabilities.IsRemove64KLimitSupported);  // bit 62 <- byte 7 = 0xef
        Assert.True(session.Capabilities.Is2GBFetchBufferSupported);  // bit 63 <- byte 7 = 0xef
        Assert.True(session.Capabilities.IsFetchArray);               // bit 8  <- byte 1 = 0xbe
        Assert.True(session.Capabilities.IsVarcharVarLenSupported);   // bit 13 <- byte 1 = 0xbe
        Assert.False(session.Capabilities.IsXps840);                  // bit 18 <- byte 2 = 0x9f
    }
}

public class SqliCapabilityMappingTests
{
    [Theory]
    [InlineData("9.59.FC5", true, false)]   // 9.x -> extended descriptor, no 4-byte offset
    [InlineData("9.03", true, false)]
    [InlineData("8.32", false, false)]      // 8.2x -> not extended
    [InlineData("7.31.UC1", false, true)]   // 7.31 -> 4-byte offset bit (50) set
    public void Version_maps_to_the_documented_bits(string version, bool extended, bool fourByteOffset)
    {
        var caps = new SqliCapabilities();
        caps.ApplyVersionString(version);
        Assert.Equal(extended, caps.IsExtendedDescriptor);
        Assert.Equal(fourByteOffset, caps.Is4ByteOffsetSupported);
    }
}

public class SqliReplyDispatchTests
{
    [Fact]
    public void Server_error_reply_is_raised_as_IfxException()
    {
        var session = SqliTestKit.SessionOver(
            SqliTestKit.ErrorReply(-201, 0, 12, "A syntax error has occurred."), out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);
        Assert.Equal(-201, ex.IfxSqlCode);
        Assert.Contains("syntax error", ex.Message);
    }

    [Fact]
    public void Sqlcode_100_no_rows_is_a_warning_not_an_error()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.ErrorReply(100, 0, 0, ""), out _);
        session.ReceiveMessage(); // does not throw
    }

    [Fact]
    public void Done_reply_captures_warnings_and_row_count()
    {
        var session = SqliTestKit.SessionOver(SqliTestKit.DoneReply(3, 42, 0, 1001), out _);

        session.ReceiveMessage();

        Assert.Equal(3, session.Warnings);
        Assert.Equal(42, session.RowsProcessed);
        Assert.Equal(1001, session.SerialInserted);
    }

    [Fact]
    public void Unhandled_reply_code_fails_loudly_with_minus_408()
    {
        // SQ_CDR (96, replication) is not ported -> should surface as a clear
        // -408, not a mis-parse. (SQ_BLOB=39 and SQ_LODATA=97 are handled now.)
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Cdr);
        var session = SqliTestKit.SessionOver(ms.ToArray(), out _);

        var ex = Assert.Throws<IfxException>(session.ReceiveMessage);
        Assert.Equal(-408, ex.IfxSqlCode);
    }
}

public class SqliPamTests
{
    // Server replies to the ACK with SQ_ACCEPT immediately (no challenge) — the
    // captured behaviour when the password is supplied inline in the PDU.
    private static byte[] AcceptImmediately()
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Accept);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    // Server sends a password prompt (PROMPT_ECHO_OFF) then, after the response, SQ_ACCEPT.
    private static byte[] ChallengeThenAccept(string prompt)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Challenge);
        w.WriteInt16((short)PamMessageType.PromptEchoOff);
        var bytes = IfxWireConverter.EncodeChar(prompt);
        w.WriteInt16((short)bytes.Length);
        w.WritePadded(bytes);
        w.WriteInt16((short)SqliMessage.Eot);
        w.WriteInt16((short)SqliMessage.Accept);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    [Fact]
    public void Pam_accepts_immediately_and_sends_ack()
    {
        var session = SqliTestKit.SessionOver(AcceptImmediately(), out var duplex);
        long afterConnect = duplex.Output.Length;

        session.AuthenticatePam(new PasswordPamHandler("root", "123456"));

        // The client sent exactly SQ_ACK (128) + SQ_EOT (12).
        byte[] sent = duplex.Output.ToArray()[(int)afterConnect..];
        Assert.Equal(new byte[] { 0, 128, 0, 12 }, sent);
    }

    [Fact]
    public void Pam_answers_password_challenge_then_is_accepted()
    {
        var session = SqliTestKit.SessionOver(ChallengeThenAccept("Password:"), out var duplex);
        long afterConnect = duplex.Output.Length;

        session.AuthenticatePam(new PasswordPamHandler("root", "s3cret"));

        byte[] sent = duplex.Output.ToArray()[(int)afterConnect..];
        // ACK+EOT, then SQ_RESPONSE(130) + len(6) + "s3cret" + EOT.
        Assert.Equal(0, sent[0]); Assert.Equal(128, sent[1]);          // SQ_ACK
        int respIdx = Array.IndexOf(sent, (byte)130);
        Assert.True(respIdx > 0);
        Assert.Equal(6, sent[respIdx + 1] << 8 | sent[respIdx + 2]);   // response length
        Assert.Equal("s3cret", System.Text.Encoding.Latin1.GetString(sent, respIdx + 3, 6));
    }

    [Fact]
    public void Pam_terminate_response_aborts_without_sending_a_reply()
    {
        var session = SqliTestKit.SessionOver(ChallengeThenAccept("Password:"), out _);
        var terminating = new TerminatingPamHandler();

        Assert.Throws<IfxException>(() => session.AuthenticatePam(terminating));
    }

    private sealed class TerminatingPamHandler : IIfxPamHandler
    {
        public IfxPamResponse Respond(IfxPamChallenge challenge) => IfxPamResponse.Terminate();
    }
}
