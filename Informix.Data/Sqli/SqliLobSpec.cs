using System.Text;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// Field access to the create-time smart-LOB spec (<c>ifx_lo_spec</c>) as it travels to
/// <c>ifx_lo_create</c> and <c>ifx_lo_col_sbinfo</c>: 596 bytes on a long-ID server, laid out
/// as JDBC's <c>IfxLobDescriptor</c> (LO_DESCRIPTOR_SIZE 596, DISK_DESCRIPTOR_SIZE 168) --
/// field order taken from that class, values from its constants.
/// </summary>
/// <remarks>
/// <code>
///   [  0..  4)  MagicNumber        0xDADAFEED
///   [  4..  8)  CreateTimeFlags    LO_LOG 1, LO_NOLOG 2, LO_DELAY_LOG 4, LO_KEEP_LASTACCESS_TIME 8,
///                                  LO_NOKEEP_LASTACCESS_TIME 16, LO_HIGH_INTEG 32,
///                                  LO_MODERATE_INTEG 64, LO_TEMP 128
///   [  8.. 12)  CreateTimeFlags1   (not exposed)
///   [ 12.. 22)  estimatedNoBytes   UDT long; [22..24) pad
///   [ 24.. 34)  sizeLimit          UDT long; [34..36) pad
///   [ 36.. 40)  extentSize         int32
///   [ 40..168)  spaceName          the sbspace, NUL-padded; empty = the server's default
///   [168..596)  rawColDescriptor   column-level part; filled by ifx_lo_col_sbinfo
/// </code>
/// The flag values are also those of <see cref="IfxSmartLOBCreateTimeFlags"/> (Informix.Net.Core's
/// public enum), so <see cref="IfxSmartLOB.Flags"/> goes on the wire unchanged. -1 in a size or
/// the extent means "the server's default", which is what <see cref="SqliSession.BuildDefaultLobDescriptor"/>
/// puts there.
/// </remarks>
internal static class SqliLobSpec
{
    internal const int Size = 596;
    internal const int FlagsOffset = 4;
    internal const int EstimatedBytesOffset = 12;
    internal const int MaxBytesOffset = 24;
    internal const int ExtentSizeOffset = 36;
    internal const int SbspaceOffset = 40;
    internal const int SbspaceLength = 128;

    public static int GetFlags(byte[] spec) => IfxWireConverter.ReadInt32(spec.AsSpan(FlagsOffset, 4));

    public static void SetFlags(byte[] spec, int flags) => IfxWireConverter.WriteInt32(spec.AsSpan(FlagsOffset, 4), flags);

    public static long GetEstimatedBytes(byte[] spec) => SqliSession.ReadUdtLong(spec.AsSpan(EstimatedBytesOffset));

    public static void SetEstimatedBytes(byte[] spec, long bytes) => SqliSession.WriteUdtLong(spec.AsSpan(EstimatedBytesOffset), bytes);

    public static long GetMaxBytes(byte[] spec) => SqliSession.ReadUdtLong(spec.AsSpan(MaxBytesOffset));

    public static void SetMaxBytes(byte[] spec, long bytes) => SqliSession.WriteUdtLong(spec.AsSpan(MaxBytesOffset), bytes);

    public static int GetExtentSize(byte[] spec) => IfxWireConverter.ReadInt32(spec.AsSpan(ExtentSizeOffset, 4));

    public static void SetExtentSize(byte[] spec, int extentSize) => IfxWireConverter.WriteInt32(spec.AsSpan(ExtentSizeOffset, 4), extentSize);

    /// <summary>The sbspace name, up to its first NUL; empty means the server's default sbspace.</summary>
    public static string GetSbspace(byte[] spec)
    {
        var field = spec.AsSpan(SbspaceOffset, SbspaceLength);
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>
    /// Writes the sbspace name NUL-padded into its 128-byte slot. An sbspace name is an Informix
    /// identifier (ASCII, at most 128 bytes); <c>null</c> or empty restores the server's default.
    /// </summary>
    public static void SetSbspace(byte[] spec, string? name)
    {
        var field = spec.AsSpan(SbspaceOffset, SbspaceLength);
        field.Clear();
        if (string.IsNullOrEmpty(name))
            return;
        if (name.Length > SbspaceLength || !Ascii.IsValid(name))
            throw new ArgumentException(
                $"An sbspace name is an ASCII identifier of at most {SbspaceLength} characters; got '{name}'.", nameof(name));
        Encoding.ASCII.GetBytes(name, field);
    }
}

/// <summary>
/// Field offsets in the IFX_LO_STAT block (<c>ifx_lo_stat</c>, 644 bytes on a long-ID server),
/// laid out as JDBC's <c>IfxLoStat</c> declares its fields.
/// </summary>
/// <remarks>
/// Confirmed live (IDS 14.10) by changing one thing at a time: the size moved only on a write,
/// the access time on a read, the modification time only on a write, the status time on a write
/// and on every change of the reference count, and the reference count went 0, 1, 2 as rows
/// were inserted pointing at the object.
/// <code>
///   [ 0.. 4)  magic              0xFEDABABE
///   [ 4..14)  lobSize            UDT long; [14..16) pad
///   [16..20)  lobUID             owner's user id
///   [20..24)  lastAccessTime     Unix seconds
///   [24..28)  lastModifyTimeS    Unix seconds
///   [28..32)  lastModifyTimeM    microseconds part
///   [32..36)  lastStatusTime     Unix seconds: last change of status (reference count, ...)
///   [36..40)  refCount           rows referencing the object
///   [40..636) the create spec    a full ifx_lo_spec, see <see cref="SqliLobSpec"/>
/// </code>
/// </remarks>
internal static class SqliLobStat
{
    internal const int SizeOffset = 4;
    internal const int LastAccessTimeOffset = 20;
    internal const int LastModificationTimeOffset = 24;
    internal const int LastStatusTimeOffset = 32;
    internal const int ReferenceCountOffset = 36;
    internal const int SpecOffset = 40;

    public static long GetSize(byte[] stat) => SqliSession.ReadUdtLong(stat.AsSpan(SizeOffset));

    public static int GetInt32(byte[] stat, int offset) => IfxWireConverter.ReadInt32(stat.AsSpan(offset, 4));
}
