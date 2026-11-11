namespace Informix.Data.Asf;

/// <summary>
/// Constants of the ASF (Association Services Facility) connection handshake: session-layer frame types,
/// the numbered PDU segments and the fixed identity strings sent in the connection request.
/// See "ASF handshake" in docs/wire-protocol.md.
/// </summary>
internal static class AsfProtocol
{
    // ---- Session-layer (SL) frame types, byte 2 of the 6-byte SL header.
    public const byte SlTypeConnectionRequest = 1;
    public const byte SlTypeConnectionAccept = 2;
    public const byte SlTypeConnectionReject = 3;
    public const byte SlTypeRedirect = 13;

    /// <summary>SQLI 6.00 protocol level, sent as the SL attribute (<c>PF_PROT_SQLI_0600</c>).</summary>
    public const byte ProtocolSqli0600 = 60;

    /// <summary>SQLI with communication-support (CSM/encryption) sublayer (<c>PF_PROT_SQLI_WITH_CSS</c>).</summary>
    public const byte ProtocolSqliWithCss = 61;

    // [totalLength:int16][slType:byte][attribute:byte][options:int16]; totalLength includes these 6 bytes.
    public const int SlHeaderSize = 6;               // SL_HEADER_SIZE

    // ---- ASF segment codes inside the connection PDU (SQ_ASC*) ----
    public const short Assoc = 100;                  // SQ_ASSOC
    public const short AscBinary = 101;              // SQ_ASCBINARY
    public const short AscInitResponse = 102;        // SQ_ASCINITRESP
    public const short AscDbList = 103;              // SQ_ASCDBLIST (carries redirect detail)
    public const short AscInitRequest = 104;         // SQ_ASCINITREQ
    public const short AscEnv = 106;                 // SQ_ASCENV
    public const short AscProcessInfo = 107;         // SQ_ASCPINFO
    public const short AscBindParams = 108;          // SQ_ASCBPARMS
    // Defined for completeness; the handshake implemented here does not send or expect these two.
    public const short AssocBind = 110;              // SQ_ASSOCBIND (CSM/encrypted logins)
    public const short AssocResponse = 111;          // SQ_ASSOCRESP
    public const short AscMisc60 = 116;              // SQ_ASCMISC_60 (application name)
    public const short AscEot = 127;                 // SQ_ASCEOT

    /// <summary>Connection type sent in the init request (<c>ASF_XCONNECT</c>).</summary>
    public const short XConnect = 11;

    // ---- Init-request statement-option bits ----
    // Option bits of SQ_ASCINITREQ: always 0x3; the group-preference bit is set for sqlhosts groups,
    // the trusted-context bit when the connection asks for a trusted context.
    public const int OptionsBase = 0x3;
    public const int OptionsGroupPreference = 0x2000000;   // ASF_GRPREF
    public const int OptionsTrustedContext = 0x4000000;    // ASF_TRUSTCTXT

    // Fixed-width identity fields (12 / 8 / 8 bytes), NUL-padded and written raw, without a length prefix.
    public const string ApplicationType = "sqlexec\0\0\0\0\0";  // applType, 12 bytes
    public const string ApplicationId = "sqli";                  // applID
    public const string ProtocolName = "ol\0\0\0\0\0\0";         // PROT_SQLIOL, 8 bytes
    public const string NetworkType = "tlitcp\0\0";              // NET_TLITCP, 8 bytes
    // Floating-point representation: IEEE, big-endian ("M" for Motorola byte order).
    public const string FloatType = "IEEEM";                     // floatType

    // The single TCP-urgent byte that makes the server interrupt the running statement (reply: SQ_ERR -213).
    public const byte OutOfBandByte = 66;

    // ---- Values the driver sends for its identity ----
    public const string DefaultDriverVersion = "9.280";       // sDriverVersion
    public const string DefaultSerialNumber = "RDS#R000000";
    public const int DefaultCapabilities = 316;               // IntVerToBeSent
}
