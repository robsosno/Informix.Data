using Informix.Data.Wire;

namespace Informix.Data.Asf;

/// <summary>
/// Pure encoder/decoder of the ASF handshake frames, split out of the socket
/// transport so it is unit-testable against in-memory streams. Ports the
/// private frame methods of <c>com.informix.asf.Connection</c>:
/// <c>EncodeSLheader</c>, <c>EncodeAscBinary</c>, <c>encodeEnv</c>,
/// <c>encodeEnvPInfo</c>, <c>recvConnectionResponse</c> and
/// <c>DecodeAscBinary</c>. Only the modern binary handshake
/// (capabilities &gt; 0) is ported; the pre-6.00 ASCII form
/// (<c>EncodeAscString</c>) is intentionally left behind.
/// </summary>
internal static class AsfFrameCodec
{
    // ===================================================================== //
    //  Request                                                              //
    // ===================================================================== //

    /// <summary>
    /// Writes the complete connection request: the 6-byte SL header followed by
    /// the connection PDU (port of <c>sendConnectionRequest</c>'s binary path).
    /// </summary>
    public static void WriteConnectionRequest(IfxWireWriter writer, AsfConnectionSettings settings)
    {
        string userName = settings.ResolveUserName();

        // Build the PDU first — the SL header needs its size.
        var pduStream = new MemoryStream(2048);
        var pdu = new IfxWireWriter(pduStream);
        EncodeAscBinary(pdu, settings, userName);

        // EncodeSLheader(o, pduSize, SLTYPE_CONREQ, PF_PROT_SQLI_0600, 0)
        writer.WriteInt16((short)(pduStream.Length + AsfProtocol.SlHeaderSize));
        writer.Write(AsfProtocol.SlTypeConnectionRequest);
        writer.Write(AsfProtocol.ProtocolSqli0600);
        writer.WriteInt16(0);

        writer.Write(pduStream.GetBuffer().AsSpan(0, (int)pduStream.Length));
    }

    // Port of EncodeAscBinary: the binary connection PDU.
    private static void EncodeAscBinary(IfxWireWriter o, AsfConnectionSettings s, string userName)
    {
        o.WriteInt16(AsfProtocol.Assoc);
        o.WriteInt16(AsfProtocol.AscBinary);
        o.WriteInt32(AsfProtocol.ProtocolSqliWithCss); // protocol level word, always 61

        // Counted strings (below) are [len:int16 = bytes + 1][bytes][NUL].
        WriteNulTerminated(o, AsfProtocol.FloatType);

        o.WriteInt16(AsfProtocol.AscBindParams);
        WriteConstant(o, AsfProtocol.ApplicationType);
        WriteNulTerminated(o, s.DriverVersion);
        WriteNulTerminated(o, s.SerialNumber);
        WriteNulTerminated(o, AsfProtocol.ApplicationId);

        // Capability word, two reserved zero ints and the constant int16 1, copied from the reference client.
        o.WriteInt32(s.Capabilities);
        o.WriteInt32(0);
        o.WriteInt32(0);
        o.WriteInt16(1);

        WriteNulTerminated(o, userName);
        // An absent password is a bare zero length, not an empty counted string.
        if (s.Password is null)
            o.WriteInt16(0);
        else
            WriteNulTerminated(o, s.Password);

        WriteConstant(o, AsfProtocol.ProtocolName);
        o.WriteInt32(AsfProtocol.ProtocolSqliWithCss);
        WriteConstant(o, AsfProtocol.NetworkType);
        // Closes the BINDPARAMS segment (network type "tlitcp" followed by a constant 1).
        o.WriteInt32(1);

        o.WriteInt16(AsfProtocol.AscInitRequest);
        o.WriteInt16(AsfProtocol.XConnect);

        // Base value 3, plus optional flags for sqlhosts groups and trusted contexts.
        int options = AsfProtocol.OptionsBase;
        if (s.SqlhostsGroup)
            options |= AsfProtocol.OptionsGroupPreference;
        if (s.TrustedContext)
            options |= AsfProtocol.OptionsTrustedContext;
        o.WriteInt32(options);

        WriteNulTerminated(o, s.ServerName);
        // No database: a bare zero length; otherwise the database is opened by the handshake itself.
        if (s.DatabaseName is null)
            o.WriteInt16(0);
        else
            WriteNulTerminated(o, s.DatabaseName);

        // Four reserved zero-length fields.
        o.WriteInt16(0);
        o.WriteInt16(0);
        o.WriteInt16(0);
        o.WriteInt16(0);

        EncodeEnv(o, s);
        EncodeEnvProcessInfo(o, s);

        // SQ_ASCMISC_60: the application name block.
        o.WriteInt16(AsfProtocol.AscMisc60);
        // Segment length: two reserved int32 words (8) + the 2-byte string length + string + NUL = 10 + name + 1.
        o.WriteInt16((short)(10 + s.ApplicationName.Length + 1));
        o.WriteInt32(0);
        o.WriteInt32(0);
        WriteNulTerminated(o, s.ApplicationName);

        o.WriteInt16(AsfProtocol.AscEot);
    }

    // SQ_ASCENV: the number of pairs, then name/value counted strings. A PAM listener rejects clients that do
    // not send CLNT_PAM_CAPABLE=1 (-1809), so the caller always includes it.
    private static void EncodeEnv(IfxWireWriter o, AsfConnectionSettings s)
    {
        o.WriteInt16(AsfProtocol.AscEnv);
        o.WriteInt16((short)s.EnvironmentVariables.Count);
        foreach (var (name, value) in s.EnvironmentVariables)
        {
            WriteNulTerminated(o, name);
            WriteNulTerminated(o, value);
        }
    }

    // SQ_ASCPINFO with process id, thread id, host and cwd.
    private static void EncodeEnvProcessInfo(IfxWireWriter o, AsfConnectionSettings s)
    {
        o.WriteInt16(AsfProtocol.AscProcessInfo);
        o.WriteInt32(0);
        o.WriteInt32(s.ProcessId);
        o.WriteInt32(s.ThreadId);
        WriteNulTerminated(o, s.HostName);
        o.WriteInt16(0);
        WriteNulTerminated(o, s.CurrentDirectory);
    }

    /// <summary>Writes <c>short(byteLen+1)</c>, the encoded characters, then a NUL — the recurring ASF string shape.</summary>
    private static void WriteNulTerminated(IfxWireWriter o, string value)
    {
        byte[] bytes = IfxWireConverter.EncodeChar(value, o.DbEncoding);
        o.WriteInt16((short)(bytes.Length + 1));
        o.Write(bytes);
        o.Write(0);
    }

    /// <summary>Writes the raw encoded characters with no length prefix (mirrors <c>writeBytesConstant</c>).</summary>
    private static void WriteConstant(IfxWireWriter o, string value) =>
        o.Write(IfxWireConverter.EncodeChar(value, o.DbEncoding));

    // ===================================================================== //
    //  Response                                                             //
    // ===================================================================== //

    /// <summary>
    /// Reads and parses the server's connection response (port of
    /// <c>recvConnectionResponse</c>). Returns the decoded server identity on
    /// accept; throws <see cref="AsfRemoteException"/> on reject,
    /// <see cref="AsfRedirectException"/> on redirect, and a -408
    /// <see cref="IfxException"/> on an unrecognizable frame.
    /// </summary>
    public static IfxServerInfo ReadConnectionResponse(IfxWireReader reader)
    {
        short length = reader.ReadInt16();
        // The frame length includes its own 2 bytes. Parsing a private copy keeps a malformed frame from desyncing the socket.
        var body = new byte[length - 2];
        reader.ReadFully(body);

        var input = new IfxWireReader(new MemoryStream(body), reader.DbEncoding);
        byte slType = input.ReadByte();
        input.Skip(3); // rest of the SL header: attribute byte + options short

        switch (slType)
        {
            case AsfProtocol.SlTypeConnectionAccept:
            {
                if (input.ReadInt16() != AsfProtocol.Assoc)
                    break;
                if (input.ReadInt16() != AsfProtocol.AscBinary)
                    break;
                var (info, error) = DecodeAscBinary(input);
                // An accept frame carries no error segment worth failing on.
                _ = error;
                return info;
            }

            case AsfProtocol.SlTypeConnectionReject:
            {
                if (input.ReadInt16() != AsfProtocol.Assoc)
                    break;
                if (input.ReadInt16() != AsfProtocol.AscBinary)
                    break;
                var (_, error) = DecodeAscBinary(input);
                if (error is { SvcError: not 0 })
                    throw new AsfRemoteException(error.Value.SvcError, error.Value.OsError, error.Value.Message);
                break;
            }

            case AsfProtocol.SlTypeRedirect:
            {
                if (input.ReadInt16() != AsfProtocol.Assoc)
                    break;
                if (input.ReadInt16() != AsfProtocol.AscBinary)
                    break;
                var (_, error) = DecodeAscBinary(input);
                throw new AsfRedirectException(error?.RedirectDetail ?? "");
            }
        }

        throw new IfxException("Unrecognized ASF connection response (-408).", -408);
    }

    // Detail of an SQ_ASCINITRESP (service/OS error, messages) or SQ_ASCDBLIST (redirect) tail segment.
    private readonly record struct AscError(short SvcError, short OsError, string Message, string? RedirectDetail);

    // Mirrors the request layout. Most fields (protocol word, float type, echoed credentials, network block) are
    // skipped; what is kept is the server version string, serial, application id and capability words.
    private static (IfxServerInfo Info, AscError? Error) DecodeAscBinary(IfxWireReader input)
    {
        var info = new IfxServerInfo();

        input.Skip(4);                    // the protocol level word (61)
        short length = input.ReadInt16(); // float-type field ("IEEEM\0")
        input.Skip(length);

        if (input.ReadInt16() != AsfProtocol.AscBindParams)
            throw new IfxException("Malformed ASF response: missing SQ_ASCBPARMS (-408).", -408);

        input.Skip(12);                   // applType "sqlexec\0\0\0\0\0"

        info.VersionNumber = ReadCountedString(input);

        // The Java driver rewrites a "9.50" internal version to "10.00".
        int index95 = info.VersionNumber.IndexOf("9.5", StringComparison.Ordinal);
        if (info.VersionNumber.IndexOf("9.50", StringComparison.Ordinal) > 0)
        {
            info.VersionNumber =
                info.VersionNumber[..index95] + "10.00" + info.VersionNumber[(index95 + 4)..];
        }

        info.SerialNumber = ReadCountedString(input);
        info.ApplicationId = ReadCountedString(input);
        info.Capability1 = input.ReadInt32();
        info.Capability2 = input.ReadInt32();
        info.Capability3 = input.ReadInt32();

        input.Skip(2);                    // the short 1

        short skipLen = input.ReadInt16(); // user-name echo
        if (skipLen > 0)
            input.Skip(skipLen);
        skipLen = input.ReadInt16();       // password echo
        if (skipLen > 0)
            input.Skip(skipLen);

        // Echoed protocol name (8) + protocol level (4) + network type (8) + constant (4).
        //                 "ol"(8) + int + "tlitcp"(8) + int
        input.Skip(24);

        // Tail segment: INITRESPONSE (accept or reject detail), DBLIST (redirect) or EOT (plain accept).
        short segment = input.ReadInt16();
        switch (segment)
        {
            case AsfProtocol.AscInitResponse:
            {
                // Reserved header bytes of SQ_ASCINITRESP.
                input.Skip(6);
                short svcError = input.ReadInt16();
                short osError = input.ReadInt16();
                info.Warnings = input.ReadInt16();
                string message = "";

                short numErrors = input.ReadInt16();
                for (int i = 0; i < numErrors; i++)
                {
                    short offset = input.ReadInt16();
                    // A negative offset terminates the message list early.
                    if (offset < 0)
                        break;
                    message = input.ReadChar();
                }
                return (info, new AscError(svcError, osError, message, null));
            }

            case AsfProtocol.AscDbList:
            {
                input.ReadInt16();
                string detail = input.ReadChar();
                return (info, new AscError(0, 0, "", detail));
            }

            case AsfProtocol.AscEot:
                return (info, null);
        }

        throw new IfxException($"Malformed ASF response: unexpected segment {segment} (-408).", -408);
    }

    /// <summary>Reads <c>short len</c> then <c>len</c> raw bytes and decodes them, stripping trailing NULs.</summary>
    private static string ReadCountedString(IfxWireReader input)
    {
        short len = input.ReadInt16();
        var bytes = new byte[len];
        input.ReadFully(bytes);
        return IfxWireConverter.DecodeChar(bytes, input.DbEncoding);
    }
}
