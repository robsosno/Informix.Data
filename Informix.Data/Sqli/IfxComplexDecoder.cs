using System.Text;
using Informix.Data.Types;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// One node of a decoded complex-type descriptor — the .NET counterpart of the
/// per-attribute <c>IfxColumnInfo</c> carried in the
/// self-describing ROW/collection blob. A ROW/collection root holds its member
/// (or element) descriptors in <see cref="Children"/>.
/// </summary>
internal sealed class IfxComplexTypeInfo
{
    public string Name = "";           // ColName — member name, or the row/collection type name at the root
    public SqliTypeCode Type;          // base SQLtype (the descriptor type word masked to its low byte)
    public short TypeWord;             // the raw descriptor type word (flag bits included)
    public int ColLength;              // ColLength — byte length or the packed precision/qualifier word
    public int ExtendedId;             // ExtendedId (xid)
    public string ExtendedName = "";   // ExtendedName — opaque/row type name (e.g. "addr_t")
    public string ExtendedOwner = "";  // ExtendedOwner — owner/schema ("informix" is normalized to "")
    public int SourceType;             // SourceType — DISTINCT base type id
    public short Alignment;
    public short Levelno;              // nesting depth used to rebuild the child tree
    public bool IsDistinct;
    public bool IsNamedRow;
    public List<IfxComplexTypeInfo>? Children;

    /// <summary>True when the attribute is declared NOT NULL (the <c>0x100</c> type-word flag).</summary>
    public bool IsNotNull => (TypeWord & (short)IfxTypeFlags.NotNullable) != 0;

    /// <summary>
    /// The in-buffer byte footprint of a fixed-layout attribute — the adjusted
    /// length for the packed types (DECIMAL/MONEY/DATETIME/INTERVAL), otherwise
    /// <see cref="ColLength"/> (port of <c>IfxColumnInfo.getAdjustedColumnLength</c>).
    /// </summary>
    public int AdjustedLength =>
        ColLength != -1 && Type is SqliTypeCode.Decimal or SqliTypeCode.Money or SqliTypeCode.DateTime or SqliTypeCode.Interval
            ? ((ColLength >> 8 & 0xFF) + (ColLength & 1) + 3) / 2
            : ColLength;
}

/// <summary>
/// Structured decode of a self-describing ROW / collection (SET/MULTISET/LIST)
/// SENDRECV payload (descriptor crack + child-tree build) followed by
/// <c>IfxComplexInput</c> (value-block reader).
///
/// <para><b>Wire format</b> (long-identifier server, <c>identsize == 128</c>):
/// the blob opens with a fixed header — <c>totalSize:int32</c>, two reserved
/// words, an <see cref="IdentSize"/>-byte ident pad, then the member count
/// (<c>int16</c>). That is followed by <c>count</c> fixed-width (414-byte)
/// attribute descriptors carrying the level number, name, type word, length,
/// extended id / name / owner and DISTINCT source type. After the descriptors
/// come a reserved word, the value-block length (<c>int32</c>) and a flags word
/// whose sign bit (<c>0x80000000</c>) marks a whole-value SQL NULL. The value
/// block itself holds the attribute values in declared order; variable-length
/// collections append a tail table of per-element offsets terminated by
/// <c>-1</c>.</para>
///
/// <para>Scalar attributes are decoded through the same converters as ordinary
/// tuple columns (<see cref="IfxWireConverter"/>, <see cref="PackedValue"/>) so
/// there is a single decode path. A ROW/collection nested inside another
/// ROW/collection recurses to arbitrary depth (capped) and decodes to
/// <see cref="IfxRow"/> or <c>object[]</c>, the same as at the top level.</para>
/// </summary>
internal static class IfxComplexDecoder
{
    // Long-identifier servers (IDS 9.x, the port's target) pad every name field
    // in a complex descriptor to 128 bytes. The pre-9 short-id layout (18) is not
    // targeted, mirroring the smart-blob code's hard-coded long-id constants.
    private const int IdentSize = 128;

    // Header up to the member-count word (CLROW_HDRSZ = 136 on a long-id server):
    // the blob's leading totalSize(4) is stripped as the SENDRECV field length, so
    // this payload opens with reserved(4) + reserved(2) + ident-pad(IdentSize),
    // then the member count int16, then the member array.
    private const int MemberCountOffset = 6 + IdentSize;         // 134
    private const int MemberArrayOffset = MemberCountOffset + 2; // 136
    private const int MemberRecordSize = 30 + 3 * IdentSize;     // 414 (CLROW_ROWSZ)

    // Field offsets within one 414-byte attribute descriptor (see class remarks).
    private const int FieldLevelno = 2;
    private const int FieldNameLen = 6;
    private const int FieldName = 8;
    private const int FieldIfxType = 8 + IdentSize + 2;            // 138
    private const int FieldColLength = FieldIfxType + 2;           // 140
    private const int FieldExtendedId = FieldColLength + 2;        // 142
    private const int FieldExtNameLen = FieldExtendedId + 4 + 2;   // 148
    private const int FieldExtName = FieldExtNameLen + 2;          // 150
    private const int FieldExtOwnerLen = FieldExtName + IdentSize; // 278
    private const int FieldExtOwner = FieldExtOwnerLen + 2;        // 280
    private const int FieldAlignment = FieldExtOwner + IdentSize;  // 408
    private const int FieldSourceType = FieldAlignment + 2;        // 410

    /// <summary>
    /// Decodes a complete self-describing ROW/collection blob (the SENDRECV field
    /// payload, without the outer 4-byte length prefix) to a CLR value: an
    /// <see cref="IfxRow"/> for a ROW, an <c>object[]</c> for a collection,
    /// <see cref="DBNull.Value"/> for a whole-value SQL NULL, or the raw
    /// <paramref name="blob"/> as <c>byte[]</c> when the payload cannot be decoded
    /// structurally (the opaque fallback — nothing regresses for such callers).
    /// </summary>
    public static object Decode(byte[] blob, SqliTypeCode columnType, Encoding? encoding, bool trimTrailingSpaces = false)
    {
        try
        {
            var root = ParseDescriptor(blob, out int valueStart, out int dataLen, out bool isNull);
            if (root is null)
                return blob;
            if (isNull || dataLen <= 0)
                return DBNull.Value;

            bool isRow = root.Type == SqliTypeCode.Row || columnType == SqliTypeCode.Row;
            if (isRow)
                return DecodeRow(root, blob, valueStart, dataLen, encoding, trimTrailingSpaces);
            if (root.Type is SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List or SqliTypeCode.Collection
                || columnType is SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List or SqliTypeCode.Collection)
                return DecodeCollection(root, blob, valueStart, dataLen, encoding, trimTrailingSpaces);
            return blob;
        }
        catch (IfxException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            // A malformed/truncated descriptor must not leak a raw framework
            // exception through the reader — surface it as a driver error.
            throw new IfxException("Malformed complex-type payload (-79716).", -79716, inner: ex);
        }
    }

    // ===================================================================== //
    //  Descriptor crack                                                     //
    // ===================================================================== //

    // Layout after the member records: 4 skipped bytes, int32 data length, int32 flags (top bit = NULL), then the values.
    private static IfxComplexTypeInfo? ParseDescriptor(byte[] b, out int valueStart, out int dataLen, out bool isNull)
    {
        valueStart = 0;
        dataLen = 0;
        isNull = false;
        if (b.Length < MemberArrayOffset)
            return null;

        int count = IfxWireConverter.ReadInt16(b.AsSpan(MemberCountOffset, 2));
        if (count <= 0 || b.Length < MemberArrayOffset + count * MemberRecordSize + 12)
            return null;

        var flat = new IfxComplexTypeInfo[count];
        for (int i = 0; i < count; i++)
            flat[i] = ReadMember(b, MemberArrayOffset + i * MemberRecordSize);

        int after = MemberArrayOffset + count * MemberRecordSize;
        dataLen = IfxWireConverter.ReadInt32(b.AsSpan(after + 4, 4));   // reserved int32 skipped
        int flags = IfxWireConverter.ReadInt32(b.AsSpan(after + 8, 4));
        isNull = (flags & unchecked((int)0x80000000)) != 0;
        valueStart = after + 12;

        // Rebuild the level-numbered flat list into a tree; the root is flat[0].
        int index = 0;
        var top = new List<IfxComplexTypeInfo>();
        BuildTree(flat, ref index, top, flat[0].Levelno);
        return top.Count > 0 ? top[0] : null;
    }

    // Siblings share a level, a
    // deeper level becomes the previous sibling's child subtree.
    private static void BuildTree(IfxComplexTypeInfo[] flat, ref int index, List<IfxComplexTypeInfo> siblings, int level)
    {
        while (index < flat.Length)
        {
            if (flat[index].Levelno == level)
            {
                siblings.Add(flat[index]);
                index++;
            }
            else if (flat[index].Levelno < level)
            {
                return;
            }
            else
            {
                var parent = siblings[^1];
                parent.Children ??= new List<IfxComplexTypeInfo>();
                int childLevel = flat[index].Levelno;
                BuildTree(flat, ref index, parent.Children, childLevel);
            }
        }
    }

    private static IfxComplexTypeInfo ReadMember(byte[] b, int p)
    {
        var info = new IfxComplexTypeInfo
        {
            Levelno = IfxWireConverter.ReadInt16(b.AsSpan(p + FieldLevelno, 2)),
            Name = ReadIdent(b, p + FieldName, IfxWireConverter.ReadInt16(b.AsSpan(p + FieldNameLen, 2))),
            ColLength = IfxWireConverter.ReadInt16(b.AsSpan(p + FieldColLength, 2)),
            ExtendedId = IfxWireConverter.ReadInt32(b.AsSpan(p + FieldExtendedId, 4)),
            Alignment = IfxWireConverter.ReadInt16(b.AsSpan(p + FieldAlignment, 2)),
            SourceType = IfxWireConverter.ReadInt32(b.AsSpan(p + FieldSourceType, 4)),
        };

        short typeWord = IfxWireConverter.ReadInt16(b.AsSpan(p + FieldIfxType, 2));
        info.TypeWord = typeWord;
        info.IsDistinct = (typeWord & (short)IfxTypeFlags.Distinct) != 0;
        info.IsNamedRow = (typeWord & (short)IfxTypeFlags.NamedRow) != 0;
        info.Type = (SqliTypeCode)(short)(typeWord & IfxTypeCodes.BaseTypeMask);

        int extNameLen = IfxWireConverter.ReadInt16(b.AsSpan(p + FieldExtNameLen, 2));
        // The server only carries an extended name for a named ROW or a non-complex
        // extended/opaque type (IfxComplex.fromIfx gate); complex collections leave it blank.
        if (extNameLen > 0 && (info.IsNamedRow || !IsComplexType(info.Type)))
            info.ExtendedName = ReadIdent(b, p + FieldExtName, extNameLen);

        int extOwnerLen = IfxWireConverter.ReadInt16(b.AsSpan(p + FieldExtOwnerLen, 2));
        string owner = ReadIdent(b, p + FieldExtOwner, extOwnerLen);
        // The built-in owner is not interesting to callers; only user-defined owners are kept.
        info.ExtendedOwner = owner == "informix" ? "" : owner;
        return info;
    }

    private static string ReadIdent(byte[] b, int offset, int length)
    {
        if (length <= 0)
            return "";
        length = Math.Min(length, IdentSize);
        return IfxWireConverter.DecodeChar(b.AsSpan(offset, length)).TrimEnd('\0');
    }

    private static bool IsComplexType(SqliTypeCode t) =>
        t is SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List or SqliTypeCode.Row or SqliTypeCode.Collection;

    // ===================================================================== //
    //  Value-block readers                                                  //
    // ===================================================================== //

    // Depth cap for nested ROW/collection recursion (readNestedComplex) — a
    // malformed/adversarial blob must fail cleanly rather than blow the stack.
    private const int MaxNestingDepth = 16;

    // Members are stored one after another in declaration order.
    private static IfxRow DecodeRow(IfxComplexTypeInfo root, byte[] b, int valueStart, int dataLen, Encoding? encoding,
        bool trimTrailingSpaces, int depth = 0)
    {
        var members = root.Children ?? new List<IfxComplexTypeInfo>();
        var values = new object[members.Count];
        // A ROW is read sequentially in declared order.
        // The tail offset table the server also emits is unused for rows).
        int pos = valueStart;
        int end = valueStart + dataLen;
        for (int i = 0; i < members.Count; i++)
            values[i] = ReadElement(members[i], b, valueStart, ref pos, end, encoding, trimTrailingSpaces, depth);
        return new IfxRow(root.ExtendedName, members, values);
    }

    // Collections hold one element type. Variable-length element types store an offset table at the end of the data,
    // read backwards from the last 4 bytes until a -1 terminator; fixed-width elements are simply sequential.
    private static object[] DecodeCollection(IfxComplexTypeInfo root, byte[] b, int valueStart, int dataLen,
        Encoding? encoding, bool trimTrailingSpaces, int depth = 0)
    {
        var element = (root.Children is { Count: > 0 }) ? root.Children[0] : null;
        if (element is null)
            return [];

        int end = valueStart + dataLen;
        // Variable-length element types (VARCHAR, LVARCHAR/UDT/BOOL, nested complex)
        // carry a -1-terminated tail table of per-element offsets;
        // fixed-length elements are read back-to-back until the value block drains.
        var offsets = HasElementOffsetTable(element.Type) ? ReadOffsetTable(b, valueStart, dataLen) : null;
        var result = new List<object>();
        if (offsets is not null)
        {
            foreach (int off in offsets)
            {
                int pos = valueStart + off;
                result.Add(ReadElement(element, b, valueStart, ref pos, end, encoding, trimTrailingSpaces, depth));
            }
        }
        else
        {
            int pos = valueStart;
            while (pos < end)
                result.Add(ReadElement(element, b, valueStart, ref pos, end, encoding, trimTrailingSpaces, depth));
        }
        return result.ToArray();
    }

    // Reads the -1-terminated offset table from the value-block tail. Offsets are
    // value-block-relative and ordered by element index.
    private static List<int>? ReadOffsetTable(byte[] b, int valueStart, int dataLen)
    {
        int pos = valueStart + dataLen - 4;
        var offsets = new List<int>();
        while (pos >= valueStart)
        {
            int off = IfxWireConverter.ReadInt32(b.AsSpan(pos, 4));
            if (off == -1)
                break;
            offsets.Add(off);
            pos -= 4;
        }
        return offsets.Count > 0 ? offsets : null;
    }

    // The element types the server prefixes with a per-element offset table:
    // VARCHAR, the UDT family (UdtVar/UdtFixed/LVarChar/Bool) and nested complex
    // types — the exact set of `type==13 || isUDT || isComplexType` in
    // IfxComplexInput's constructor. Everything else is fixed-width and read
    // sequentially.
    private static bool HasElementOffsetTable(SqliTypeCode t) =>
        t is SqliTypeCode.VarChar or SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed or SqliTypeCode.LVarChar or SqliTypeCode.Bool
            or SqliTypeCode.Row or SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List or SqliTypeCode.Collection;

    // Reads one attribute/element at <paramref name="pos"/>, advancing it by the
    // value's footprint.
    // <paramref name="containerStart"/> is the value-block start of the enclosing
    // ROW/collection — readNestedComplex's alignment math is relative to it, not
    // to the top-level blob (see IfxSQLInput.currentPosition, which is relative to
    // the byte[] handed to that particular IfxComplexInput instance).
    //
    // The per-type wire decode itself is shared with SqliSession.DecodeValue via
    // SqliValueDecoder (see its remarks) — Take() below asks it for the value's
    // footprint, slices exactly that many bytes, decodes, and advances pos. What
    // stays local to this method is what the shared decoder cannot know: resolving
    // the LVARCHAR-wearing-UdtVar-hat case before dispatch, the nested-ROW/collection
    // recursion, and the terminal fallback for a genuinely unported element type.
    private static object ReadElement(IfxComplexTypeInfo t, byte[] b, int containerStart, ref int pos, int end,
        Encoding? encoding, bool trimTrailingSpaces = false, int depth = 0)
    {
        var ctx = new SqliDecodeContext(encoding, trimTrailingSpaces, (short)t.ColLength,
            IsColumn: false, SubjectName: t.Name);

        // A ref parameter of the enclosing method (pos) cannot be captured by a local
        // function (CS1628) — p is this local function's own ref parameter, fed pos
        // explicitly by every call below.
        object Take(SqliTypeCode effectiveType, ref int p)
        {
            int footprint = SqliValueDecoder.Footprint(effectiveType, b.AsSpan(p), t.ColLength);
            object value = SqliValueDecoder.Decode(effectiveType, b.AsSpan(p, footprint), ctx);
            p += footprint;
            return value;
        }

        switch (t.Type)
        {
            case SqliTypeCode.Char:
            case SqliTypeCode.NChar:
            case SqliTypeCode.VarChar:
            case SqliTypeCode.NVarChar:
            case SqliTypeCode.LVarChar:
            case SqliTypeCode.SmallInt:
            case SqliTypeCode.Int:
            case SqliTypeCode.Serial:
            case SqliTypeCode.Float:
            case SqliTypeCode.SmFloat:
            case SqliTypeCode.Decimal:
            case SqliTypeCode.Money:
            case SqliTypeCode.Date:
            case SqliTypeCode.DateTime:
            case SqliTypeCode.Interval:
            case SqliTypeCode.Int8:
            case SqliTypeCode.Serial8:
            case SqliTypeCode.BigInt:
            case SqliTypeCode.BigSerial:
            case SqliTypeCode.Bool:
                return Take(t.Type, ref pos);

            case SqliTypeCode.UdtVar:
            case SqliTypeCode.UdtFixed:
                // A ROW/collection member never goes through ReceiveDescribe, so the
                // extended-id collapse it does for a top-level column (xid 1 -> LVARCHAR,
                // 5 -> BOOLEAN) has to happen here instead, or the member decodes as a
                // generic opaque UDT and comes back as raw bytes where the equivalent
                // column comes back as a string/bool. These two ids are resolved
                // for a SQLtype 40/41 member:
                // IfxComplexInput.readElement's xid==5 -> readBoolean branch;
                // IfxValue.getIfxTypeName's isDirect() branch, which special-cases 5 and 1
                // and nothing else. The BOOLEAN half was live-verified against IDS
                // 9.59.FC5 (2026-08-08): before it, a BOOLEAN ROW member read as byte[].
                //
                // JSON/BSON (xid 25/26) deliberately do NOT get the same treatment — see
                // SqliValueDecoder's remarks for why raw bytes are correct for those.
                return Take(
                    t.ExtendedId == 1 || t.SourceType == 1 ? SqliTypeCode.LVarChar
                    : t.ExtendedId == 5 || t.SourceType == 5 ? SqliTypeCode.Bool
                    : t.Type,
                    ref pos);

            case SqliTypeCode.Row:
            case SqliTypeCode.Set:
            case SqliTypeCode.MultiSet:
            case SqliTypeCode.List:
            case SqliTypeCode.Collection:
                return ReadNestedComplex(t, b, containerStart, ref pos, encoding, trimTrailingSpaces, depth);

            default:
                // An unported fixed-width element type still has a knowable footprint
                // (AdjustedLength); anything else is genuinely unknowable and must
                // fail loudly rather than silently swallow the remaining siblings.
                if (t.ColLength > 0)
                {
                    int len = t.AdjustedLength;
                    var data = b.AsSpan(pos, len).ToArray();
                    pos += len;
                    return data;
                }
                throw new IfxException(
                    $"Malformed complex-type payload (-79716): unsupported element type '{t.Type}'.", -79716);
        }
    }

    // A ROW/collection member nested
    // inside another ROW/collection. Wire framing: [size:int16][size bytes]. size==1
    // marks SQL NULL (a 1-byte 0x01 marker follows, footprint 3 total); size<=0 is
    // malformed. Otherwise the size bytes open with an alignment/flags header whose
    // footprint is <c>GetUdtOffset(sizeWordOffset) + 6</c> bytes (the reader and
    // writer disagree by 4 bytes on exactly where the flags word sits inside that
    // header, but neither side's flags value carries information — the writer
    // always emits a constant 1 — so the header is skipped wholesale), followed by
    // the nested value block itself, decoded via the child subtree already built by
    // <see cref="BuildTree"/> (the nested payload does not repeat the descriptor).
    private static object ReadNestedComplex(IfxComplexTypeInfo t, byte[] b, int containerStart, ref int pos,
        Encoding? encoding, bool trimTrailingSpaces, int depth)
    {
        if (depth >= MaxNestingDepth)
            throw new IfxException("Malformed complex-type payload (-79716): nesting too deep.", -79716);

        int sizeWordOffset = pos - containerStart;
        short size = IfxWireConverter.ReadInt16(b.AsSpan(pos, 2));
        pos += 2;
        if (size <= 0)
            throw new IfxException("Malformed complex-type payload (-79716): bad nested-complex size.", -79716);
        if (size == 1)
        {
            pos += 1; // the 1-byte NULL marker
            return DBNull.Value;
        }

        int headerLen = GetUdtOffset(sizeWordOffset) + 6;
        int dataLen = size - headerLen;
        if (dataLen < 0)
            throw new IfxException("Malformed complex-type payload (-79716): bad nested-complex size.", -79716);
        int nestedStart = pos + headerLen;

        object value = t.Type == SqliTypeCode.Row
            ? DecodeRow(t, b, nestedStart, dataLen, encoding, trimTrailingSpaces, depth + 1)
            : DecodeCollection(t, b, nestedStart, dataLen, encoding, trimTrailingSpaces, depth + 1);
        pos = nestedStart + dataLen;
        return value;
    }

    // The alignment padding (in bytes) that
    // pads a nested-complex header out to an 8-byte boundary. dataStart is the
    // value-block-relative offset of the 2-byte size word that opens the header.
    private static int GetUdtOffset(int dataStart)
    {
        int m = (dataStart + 3) % 8;
        return m == 0 ? 3 : 11 - m;
    }
}
