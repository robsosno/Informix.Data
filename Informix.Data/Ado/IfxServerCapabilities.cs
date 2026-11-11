namespace Informix.Data;

/// <summary>
/// A read-only view of the capability set negotiated with the server during
/// connection bring-up (version string plus the SQ_PROTOCOLS feature vector).
/// Exposed on <see cref="IfxConnection.ServerCapabilities"/> once the connection
/// is open; the individual flags describe which on-wire formats the server
/// understands (extended descriptors, wide offsets, BIGINT, var-length VARCHAR…).
/// </summary>
public sealed class IfxServerCapabilities
{
    private readonly Sqli.SqliCapabilities _caps;

    internal IfxServerCapabilities(Sqli.SqliCapabilities caps) => _caps = caps;

    /// <summary>The server version string returned by the SQLI version reply.</summary>
    public string ServerVersion => _caps.ServerVersion;

    /// <summary>Extended ("universal server") descriptor format — column owner / extended-type fields present.</summary>
    public bool IsExtendedDescriptor => _caps.IsExtendedDescriptor;

    /// <summary>Describe/error offsets and the string-table size are 4-byte ints rather than shorts.</summary>
    public bool Is4ByteOffsetSupported => _caps.Is4ByteOffsetSupported;

    /// <summary>Statement text and error offsets may exceed 64K (int-sized).</summary>
    public bool IsRemove64KLimitSupported => _caps.IsRemove64KLimitSupported;

    /// <summary>The fetch buffer size is sent as a 4-byte int.</summary>
    public bool Is2GBFetchBufferSupported => _caps.Is2GBFetchBufferSupported;

    /// <summary>The server expects a fetch-array size short after a fetch request.</summary>
    public bool IsFetchArray => _caps.IsFetchArray;

    /// <summary>VARCHAR tuples carry only the actual data after the length byte rather than the padded maximum.</summary>
    public bool IsVarcharVarLenSupported => _caps.IsVarcharVarLenSupported;

    /// <summary>XPS 8.40 wire quirks: the fetch buffer size is int-sized.</summary>
    public bool IsXps840 => _caps.IsXps840;

    /// <summary>The server understands BIGINT/BIGSERIAL.</summary>
    public bool IsBigIntSupported => _caps.IsBigIntSupported;

    /// <summary>
    /// The server supports the routine-by-name protocol extensions — including reporting a
    /// routine's identity when it declares OUT/INOUT parameters, which is what lets the
    /// driver run such a routine at all.
    /// </summary>
    public bool IsNamedParametersSupported => _caps.IsNamedParametersSupported;

    public override string ToString() => _caps.ToString();
}
