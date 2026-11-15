using System.Collections;

namespace Informix.Data.Sqli;

/// <summary>
/// The negotiated server capability set and the
/// version-string / protocol-byte logic that fills it (<c>getServerVer</c>,
/// <c>enhancedProtocolMechanism</c>). Individual capabilities gate the on-wire
/// formats of describe, fetch and error frames, so they must be resolved before
/// any statement is prepared.
///
/// <para>Two sources fill the bits, in bring-up order: the version-string
/// ladder (<c>getServerVer</c>) provides the base line, then the SQ_PROTOCOLS
/// reply vector ORs in the higher feature bits (4-byte offsets, 64K limit
/// removal, 2GB fetch, var-length VARCHAR) via
/// <see cref="ApplyServerProtocol"/>.</para>
/// </summary>
internal sealed class SqliCapabilities
{
    // 72 rather than 64: the ninth protocol byte (Informix 15 / JDBC 15.0.1.3)
    // announces bits 69/70. Sized to a byte boundary so ApplyServerProtocol can
    // fill whole bytes without a partial-tail special case.
    private readonly BitArray _bits = new(72);

    // Named bits below are the ones that change message layouts; the property each backs is documented on it.
    private const int BitV600 = 0;
    private const int BitExtendedDescriptor = 2;  // "USVER" — universal-server (extended) descriptor format
    private const int BitFetchArrayA = 8;
    private const int BitFetchArrayB = 9;
    private const int BitVarcharVarLen = 13;
    private const int BitXps840 = 18;
    private const int Bit4ByteOffset = 50;
    private const int BitNamedParameters = 52;
    private const int BitBigInt = 54;
    private const int BitRemove64KLimit = 62;
    private const int Bit2GBFetchBuffer = 63;
    private const int BitLargeTupleSize = 69;
    private const int BitLongRowId = 70;

    /// <summary>The server version string returned by the SQLI SQ_VERSION reply.</summary>
    public string ServerVersion { get; private set; } = "";

    public bool this[int bit] => _bits[bit];

    /// <summary>Extended ("universal server") descriptor format — column owner/extended-type fields present.</summary>
    public bool IsExtendedDescriptor => _bits[BitExtendedDescriptor];

    /// <summary>Describe/error offsets and the string-table size are 4-byte ints rather than shorts.</summary>
    public bool Is4ByteOffsetSupported => _bits[Bit4ByteOffset];

    /// <summary>Statement text and error offsets may exceed 64K (int-sized).</summary>
    public bool IsRemove64KLimitSupported => _bits[BitRemove64KLimit];

    /// <summary>The fetch buffer size is sent as a 4-byte int.</summary>
    public bool Is2GBFetchBufferSupported => _bits[Bit2GBFetchBuffer];

    /// <summary>The server expects a fetch-array size short after SQ_NFETCH.</summary>
    public bool IsFetchArray => _bits[BitFetchArrayA] || _bits[BitFetchArrayB];

    /// <summary>
    /// VARCHAR tuples carry only the actual data after the length byte rather
    /// than the padded maximum (<c>isVarcharVarLenSupported</c>; the
    /// <c>IFX_PAD_VARCHAR</c> override is not ported, so this is bit 13 alone).
    /// </summary>
    public bool IsVarcharVarLenSupported => _bits[BitVarcharVarLen];

    /// <summary>XPS 8.40 wire quirks: the fetch buffer size is int-sized (<c>isXPSVER8_40</c>).</summary>
    public bool IsXps840 => _bits[BitXps840];

    /// <summary>The server understands BIGINT/BIGSERIAL (<c>isBigintSupported</c>, bit 54).</summary>
    public bool IsBigIntSupported => _bits[BitBigInt];

    /// <summary>
    /// The routine-by-name protocol extensions (<c>isNamedParametersSupported</c>, bit 52):
    /// SQ_GETROUTINE carries a leading "resolve by id" flag byte and its reply a routine
    /// database name, SQ_EXFPROUTINE opens with that database name, the SQ_FPARAM
    /// descriptor carries a named-flags word, and the "routine has OUT parameters"
    /// error (-9752) carries the routine's <c>db:handle:unique</c> triple.
    /// </summary>
    public bool IsNamedParametersSupported => _bits[BitNamedParameters];

    /// <summary>
    /// The tuple size in SQ_DESCRIBE / SQ_PREPARE is a 4-byte int rather than a
    /// short, lifting the 32 KB row limit (<c>isLargeTupleSize</c>, protocol byte 9
    /// bit 0x04 — Informix 15). In effect only when the client asked for it via the
    /// ninth request byte <em>and</em> the server set it in its reply; a server may
    /// set the bit unasked (15.0.1.0 does), which <see cref="ApplyServerProtocol"/>
    /// discards.
    /// </summary>
    public bool IsLargeTupleSize => _bits[BitLargeTupleSize];

    /// <summary>
    /// Row ids and SQ_DONE / SQ_PUTERR row counts are 8-byte, and the in-tuple
    /// simple-large-object descriptor grows from 56 to 68 bytes with its
    /// <c>tb_size</c> field moving from offset 16 to 32 (<c>isLongRowId</c>,
    /// protocol byte 9 bit 0x02 — Informix 15). Opt-in, as above.
    /// </summary>
    public bool IsLongRowId => _bits[BitLongRowId];

    /// <summary>
    /// Populates the capability bits from the SQLI version string.
    /// </summary>
    public void ApplyVersionString(string version)
    {
        ServerVersion = version;
        _bits.SetAll(false);

        int Cmp(string other) => string.CompareOrdinal(version, other);

        if (Cmp("9.03") == 0)
            Set(4, 3, 2, 49, 0, 51);
        else if (Cmp("9.02") >= 0)
            Set(3, 2, 49, 0, 51);
        else if (Cmp("9.00") >= 0)
            Set(2, 49, 0, 51);
        else if (Cmp("8.40") >= 0)
            Set(49, 0, 51, 18);
        else if (Cmp("8.20") >= 0)
            Set(49, 0, 51);
        else if (Cmp("8.10") >= 0)
            Set(49, 0);
        else if (Cmp("7.31") >= 0 && Cmp("7.z") <= 0)
            Set(50, 9, 49, 6, 1, 0, 51);
        else if (Cmp("7.30") >= 0 && Cmp("7.z") <= 0)
            Set(9, 49, 6, 1, 0, 51);
        else if (Cmp("7.23") >= 0 && Cmp("7.z") <= 0)
            Set(6, 1, 49, 0, 51);
        else if (Cmp("7.22") >= 0 && Cmp("7.z") <= 0)
            Set(1, 49, 0, 51);
        else if (Cmp("7.20") >= 0 && Cmp("7.z") <= 0)
            Set(49, 0, 51);
        else if (Cmp("7.00") >= 0)
            Set(49, 0);
        else if (Cmp("6.00") >= 0)
            Set(0);
        // else: pre-6.00, leave all clear.
    }

    /// <summary>
    /// ORs in the feature bits announced by the server's SQ_PROTOCOLS reply:
    /// byte <c>i</c> maps MSB-first onto bits <c>8i..8i+7</c>. Bytes past the
    /// ninth are ignored. Must run after <see cref="ApplyVersionString"/>,
    /// which resets the vector.
    ///
    /// <para>The two large-row bits (69, 70) are negotiated, not announced: they
    /// count only when <paramref name="largeRowFormatsRequested"/> says the client
    /// asked for them with the ninth request byte. Informix 15.0.1.0 sets both in
    /// its reply even to a client that sent the eight-byte vector, and then keeps
    /// the classic frame layouts for that client — taking the bits at face value
    /// reads a 4-byte tuple size where the server sent two bytes and the describe
    /// never completes.</para>
    /// </summary>
    public void ApplyServerProtocol(ReadOnlySpan<byte> serverProtocol, bool largeRowFormatsRequested)
    {
        int count = Math.Min(serverProtocol.Length, 9);
        for (int i = 0; i < count; i++)
            for (int j = 0; j < 8; j++)
                if ((serverProtocol[i] & (0x80 >> j)) != 0)
                    _bits[8 * i + j] = true;

        if (!largeRowFormatsRequested)
        {
            _bits[BitLargeTupleSize] = false;
            _bits[BitLongRowId] = false;
        }
    }

    private void Set(params int[] bits)
    {
        foreach (int b in bits)
            _bits[b] = true;
    }

    public override string ToString()
    {
        var on = new List<int>();
        for (int i = 0; i < _bits.Length; i++)
            if (_bits[i]) on.Add(i);
        return $"'{ServerVersion}' bits[{string.Join(",", on)}]";
    }
}
