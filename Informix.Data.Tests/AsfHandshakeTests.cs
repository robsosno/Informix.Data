using Informix.Data.Asf;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Test-side helpers: a duplex stream standing in for the socket and a builder
/// producing server response frames byte-for-byte per <c>DecodeAscBinary</c>'s
/// expected layout.
/// </summary>
internal static class AsfTestKit
{
    public static AsfConnectionSettings Settings(string? database = "stores") => new()
    {
        Host = "localhost",
        Port = 9088,
        UserName = "usr",
        Password = "pwd",
        ServerName = "ol_test",
        DatabaseName = database,
        ProcessId = 111,
        ThreadId = 222,
        HostName = "testhost",
        CurrentDirectory = @"C:\work",
        ApplicationName = "TestApp",
    };

    /// <summary>Builds a complete server response frame (length-prefixed SL frame + ASC binary body).</summary>
    public static byte[] BuildResponse(
        byte slType,
        string version = "9.28.FC4",
        string serial = "AAA#B000000",
        string applId = "sqli",
        int cap1 = 1, int cap2 = 2, int cap3 = 3,
        short tailSegment = AsfProtocolMirror.AscEot,
        short svcError = 0, short osError = 0, string errorMessage = "",
        string redirectDetail = "")
    {
        var body = new MemoryStream();
        var o = new IfxWireWriter(body);

        o.Write(slType);
        o.Write(0);        // SL attribute
        o.WriteInt16(0);   // SL options

        o.WriteInt16(100); // SQ_ASSOC
        o.WriteInt16(101); // SQ_ASCBINARY

        o.WriteInt32(61);
        WriteCounted(o, "IEEEM");             // short 6 + "IEEEM" + NUL
        o.WriteInt16(108);                    // SQ_ASCBPARMS
        o.Write("sqlexec\0\0\0\0\0"u8);       // applType, 12 bytes
        WriteCounted(o, version);
        WriteCounted(o, serial);
        WriteCounted(o, applId);
        o.WriteInt32(cap1);
        o.WriteInt32(cap2);
        o.WriteInt32(cap3);
        o.WriteInt16(1);
        o.WriteInt16(0);                      // user-name echo (empty)
        o.WriteInt16(0);                      // password echo (empty)
        o.Write("ol\0\0\0\0\0\0"u8);          // 8 bytes
        o.WriteInt32(61);
        o.Write("tlitcp\0\0"u8);              // 8 bytes
        o.WriteInt32(1);

        o.WriteInt16(tailSegment);
        switch (tailSegment)
        {
            case AsfProtocolMirror.AscInitResponse:
                o.Write(new byte[6]);
                o.WriteInt16(svcError);
                o.WriteInt16(osError);
                o.WriteInt16(7);              // warnings
                if (errorMessage.Length > 0)
                {
                    o.WriteInt16(1);          // numErrors
                    o.WriteInt16(0);          // offset
                    o.WriteChar(errorMessage);
                }
                else
                {
                    o.WriteInt16(0);
                }
                break;

            case AsfProtocolMirror.AscDbList:
                o.WriteInt16(0);
                o.WriteChar(redirectDetail);
                break;
        }

        var frame = new MemoryStream();
        var f = new IfxWireWriter(frame);
        f.WriteInt16((short)(body.Length + 2)); // total length includes the length short
        f.Write(body.GetBuffer().AsSpan(0, (int)body.Length));
        return frame.ToArray();
    }

    private static void WriteCounted(IfxWireWriter o, string s)
    {
        byte[] bytes = IfxWireConverter.EncodeChar(s);
        o.WriteInt16((short)(bytes.Length + 1));
        o.Write(bytes);
        o.Write(0);
    }
}

/// <summary>Public mirrors of the internal AsfProtocol codes the tests reference.</summary>
internal static class AsfProtocolMirror
{
    public const byte Accept = 2;
    public const byte Reject = 3;
    public const byte Redirect = 13;
    public const short AscInitResponse = 102;
    public const short AscDbList = 103;
    public const short AscEot = 127;
}

/// <summary>A duplex stream: reads from a canned input, captures writes.</summary>
internal sealed class DuplexStream(byte[] input) : Stream
{
    private readonly MemoryStream _input = new(input);

    public MemoryStream Output { get; } = new();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public class AsfRequestEncodingTests
{
    private static IfxWireReader EncodeAndOpen(AsfConnectionSettings settings, out long totalLength)
    {
        var ms = new MemoryStream();
        AsfFrameCodec.WriteConnectionRequest(new IfxWireWriter(ms), settings);
        totalLength = ms.Length;
        ms.Position = 0;
        return new IfxWireReader(ms);
    }

    private static string ReadCounted(IfxWireReader r)
    {
        short len = r.ReadInt16();
        var b = new byte[len];
        r.ReadFully(b);
        return IfxWireConverter.DecodeChar(b);
    }

    private static string ReadConstant(IfxWireReader r, int length)
    {
        var b = new byte[length];
        r.ReadFully(b);
        return IfxWireConverter.DecodeChar(b);
    }

    [Fact]
    public void Connection_request_matches_the_EncodeAscBinary_layout()
    {
        var settings = AsfTestKit.Settings();
        settings.EnvironmentVariables["DB_LOCALE"] = "en_US.819";
        settings.EnvironmentVariables["DBDATE"] = "Y4MD-";

        var r = EncodeAndOpen(settings, out long totalLength);

        // ---- SL header ----
        Assert.Equal((short)totalLength, r.ReadInt16()); // total = PDU + 6
        Assert.Equal(1, r.ReadByte());                   // SLTYPE_CONREQ
        Assert.Equal(60, r.ReadByte());                  // PF_PROT_SQLI_0600
        Assert.Equal(0, r.ReadInt16());                  // options

        // ---- PDU prologue ----
        Assert.Equal(100, r.ReadInt16());                // SQ_ASSOC
        Assert.Equal(101, r.ReadInt16());                // SQ_ASCBINARY
        Assert.Equal(61, r.ReadInt32());
        Assert.Equal("IEEEM", ReadCounted(r));

        // ---- SQ_ASCBPARMS ----
        Assert.Equal(108, r.ReadInt16());
        Assert.Equal("sqlexec", ReadConstant(r, 12));    // NUL padding stripped by decode
        Assert.Equal("9.280", ReadCounted(r));           // driver version
        Assert.Equal("RDS#R000000", ReadCounted(r));     // serial
        Assert.Equal("sqli", ReadCounted(r));            // application id
        Assert.Equal(316, r.ReadInt32());                // capabilities
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1, r.ReadInt16());
        Assert.Equal("usr", ReadCounted(r));
        Assert.Equal("pwd", ReadCounted(r));
        Assert.Equal("ol", ReadConstant(r, 8));
        Assert.Equal(61, r.ReadInt32());
        Assert.Equal("tlitcp", ReadConstant(r, 8));
        Assert.Equal(1, r.ReadInt32());

        // ---- SQ_ASCINITREQ ----
        Assert.Equal(104, r.ReadInt16());
        Assert.Equal(11, r.ReadInt16());                 // ASF_XCONNECT
        Assert.Equal(0x3, r.ReadInt32());                // base statement options
        Assert.Equal("ol_test", ReadCounted(r));
        Assert.Equal("stores", ReadCounted(r));
        Assert.Equal(0, r.ReadInt16());
        Assert.Equal(0, r.ReadInt16());
        Assert.Equal(0, r.ReadInt16());
        Assert.Equal(0, r.ReadInt16());

        // ---- SQ_ASCENV ----
        Assert.Equal(106, r.ReadInt16());
        short envCount = r.ReadInt16();
        Assert.Equal(2, envCount);
        var env = new Dictionary<string, string>();
        for (int i = 0; i < envCount; i++)
            env[ReadCounted(r)] = ReadCounted(r);
        Assert.Equal("en_US.819", env["DB_LOCALE"]);
        Assert.Equal("Y4MD-", env["DBDATE"]);

        // ---- SQ_ASCPINFO ----
        Assert.Equal(107, r.ReadInt16());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(111, r.ReadInt32());                // process id
        Assert.Equal(222, r.ReadInt32());                // thread id
        Assert.Equal("testhost", ReadCounted(r));
        Assert.Equal(0, r.ReadInt16());
        Assert.Equal(@"C:\work", ReadCounted(r));

        // ---- SQ_ASCMISC_60 ----
        Assert.Equal(116, r.ReadInt16());
        Assert.Equal(10 + "TestApp".Length + 1, r.ReadInt16());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal("TestApp", ReadCounted(r));

        // ---- SQ_ASCEOT ----
        Assert.Equal(127, r.ReadInt16());
    }

    [Fact]
    public void Null_database_encodes_a_zero_length_field()
    {
        var r = EncodeAndOpen(AsfTestKit.Settings(database: null), out _);
        // Skip to the init request by scanning for the 104 segment: SL(6) is fixed,
        // so instead just re-parse the fixed prologue quickly.
        r.Skip(6);
        Assert.Equal(100, r.ReadInt16());
        Assert.Equal(101, r.ReadInt16());
        r.ReadInt32();
        _ = ReadCounted(r);
        Assert.Equal(108, r.ReadInt16());
        r.Skip(12);
        _ = ReadCounted(r); _ = ReadCounted(r); _ = ReadCounted(r);
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt16();
        _ = ReadCounted(r); _ = ReadCounted(r);
        r.Skip(24);
        Assert.Equal(104, r.ReadInt16());
        r.ReadInt16(); r.ReadInt32();
        _ = ReadCounted(r);          // server name
        Assert.Equal(0, r.ReadInt16()); // database: zero-length
    }

    [Fact]
    public void User_without_password_is_rejected()
    {
        var settings = new AsfConnectionSettings
        {
            Host = "h", Port = 1, ServerName = "s", UserName = "usr", Password = null,
        };
        var ex = Assert.Throws<IfxException>(
            () => AsfFrameCodec.WriteConnectionRequest(new IfxWireWriter(new MemoryStream()), settings));
        Assert.Equal(-952, ex.IfxSqlCode);
    }

    [Fact]
    public void Password_without_user_is_rejected()
    {
        var settings = new AsfConnectionSettings
        {
            Host = "h", Port = 1, ServerName = "s", UserName = null, Password = "pwd",
        };
        var ex = Assert.Throws<IfxException>(
            () => AsfFrameCodec.WriteConnectionRequest(new IfxWireWriter(new MemoryStream()), settings));
        Assert.Equal(-25563, ex.IfxSqlCode);
    }

    [Fact]
    public void Missing_user_is_rejected_instead_of_falling_back_to_the_OS_user()
    {
        var settings = new AsfConnectionSettings
        {
            Host = "h", Port = 1, ServerName = "s", UserName = null, Password = null,
        };
        var ex = Assert.Throws<IfxException>(
            () => AsfFrameCodec.WriteConnectionRequest(new IfxWireWriter(new MemoryStream()), settings));
        Assert.Equal(-25563, ex.IfxSqlCode);
        Assert.Contains("User ID", ex.Message);
    }
}

public class AsfResponseDecodingTests
{
    private static IfxServerInfo Decode(byte[] frame) =>
        AsfFrameCodec.ReadConnectionResponse(new IfxWireReader(new MemoryStream(frame)));

    [Fact]
    public void Accept_frame_yields_the_server_identity()
    {
        var info = Decode(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept));

        Assert.Equal("9.28.FC4", info.VersionNumber);
        Assert.Equal("AAA#B000000", info.SerialNumber);
        Assert.Equal("sqli", info.ApplicationId);
        Assert.Equal(1, info.Capability1);
        Assert.Equal(2, info.Capability2);
        Assert.Equal(3, info.Capability3);
    }

    [Fact]
    public void Accept_with_init_response_captures_warnings()
    {
        var info = Decode(AsfTestKit.BuildResponse(
            AsfProtocolMirror.Accept, tailSegment: AsfProtocolMirror.AscInitResponse));
        Assert.Equal(7, info.Warnings);
    }

    [Fact]
    public void Version_950_is_rewritten_to_1000()
    {
        var info = Decode(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept, version: "IDS 9.50.UC1"));
        Assert.Equal("IDS 10.00.UC1", info.VersionNumber);
    }

    [Fact]
    public void Reject_frame_throws_the_server_error()
    {
        var frame = AsfTestKit.BuildResponse(
            AsfProtocolMirror.Reject, tailSegment: AsfProtocolMirror.AscInitResponse,
            svcError: -951, osError: 13, errorMessage: "Incorrect password");

        var ex = Assert.Throws<AsfRemoteException>(() => Decode(frame));
        Assert.Equal(-951, ex.IfxSqlCode);
        Assert.Equal(13, ex.IsamCode);
        Assert.Contains("Incorrect password", ex.Message);
    }

    [Fact]
    public void Redirect_frame_throws_with_the_target_detail()
    {
        var frame = AsfTestKit.BuildResponse(
            AsfProtocolMirror.Redirect, tailSegment: AsfProtocolMirror.AscDbList,
            redirectDetail: "ol_secondary,192.168.1.7,9089");

        var ex = Assert.Throws<AsfRedirectException>(() => Decode(frame));
        Assert.Equal("ol_secondary,192.168.1.7,9089", ex.RedirectDetail);
        Assert.Equal(-79998, ex.IfxSqlCode);
    }

    [Fact]
    public void Garbage_frame_throws_minus_408()
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16(12);
        w.Write(new byte[10]); // slType 0 => unrecognized
        var ex = Assert.Throws<IfxException>(() => Decode(ms.ToArray()));
        Assert.Equal(-408, ex.IfxSqlCode);
    }
}

public class AsfConnectionTests
{
    [Fact]
    public void Connect_over_duplex_stream_completes_the_handshake()
    {
        var duplex = new DuplexStream(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept));
        using var conn = new AsfConnection(AsfTestKit.Settings(), duplex);

        var info = conn.Connect();

        Assert.True(conn.IsConnected);
        Assert.Equal("9.28.FC4", info.VersionNumber);
        Assert.Same(info, conn.ServerInfo);
        Assert.NotNull(conn.Reader);
        Assert.NotNull(conn.Writer);

        // The captured request is a well-formed CONREQ frame.
        byte[] sent = duplex.Output.ToArray();
        Assert.Equal((short)(sent[0] << 8 | sent[1]), sent.Length);
        Assert.Equal(1, sent[2]);  // SLTYPE_CONREQ
        Assert.Equal(60, sent[3]); // PF_PROT_SQLI_0600
    }

    [Fact]
    public void Connect_reject_surfaces_the_remote_error_and_stays_disconnected()
    {
        var frame = AsfTestKit.BuildResponse(
            AsfProtocolMirror.Reject, tailSegment: AsfProtocolMirror.AscInitResponse,
            svcError: -908, osError: 2, errorMessage: "no such server");
        var duplex = new DuplexStream(frame);
        using var conn = new AsfConnection(AsfTestKit.Settings(), duplex);

        Assert.Throws<AsfRemoteException>(() => conn.Connect());
        Assert.False(conn.IsConnected);
    }

    [Fact]
    public void Truncated_response_maps_to_lost_connection()
    {
        var duplex = new DuplexStream([0, 50, 2]); // announces 50 bytes, delivers 1
        using var conn = new AsfConnection(AsfTestKit.Settings(), duplex);

        var ex = Assert.Throws<IfxException>(() => conn.Connect());
        Assert.Equal(-908, ex.IfxSqlCode);
    }

    [Fact]
    public void DisconnectOrderly_sends_exit_and_drains_xactstat()
    {
        // Server script: accept frame, then one SQ_XACTSTAT (99 + 3 shorts), then SQ_EXIT ack.
        var script = new MemoryStream();
        script.Write(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept));
        var w = new IfxWireWriter(script);
        w.WriteInt16(99);
        w.WriteInt16(1); w.WriteInt16(2); w.WriteInt16(3);
        w.WriteInt16(56);

        var duplex = new DuplexStream(script.ToArray());
        var conn = new AsfConnection(AsfTestKit.Settings(), duplex);
        conn.Connect();
        long requestLength = duplex.Output.Length;

        conn.DisconnectOrderly();

        Assert.False(conn.IsConnected);
        byte[] sent = duplex.Output.ToArray();
        Assert.Equal(requestLength + 2, sent.Length);       // exactly one extra short
        Assert.Equal(56, sent[^2] << 8 | sent[^1]);         // SQ_EXIT
    }
}
