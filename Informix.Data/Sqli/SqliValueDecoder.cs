using System.Text;
using Informix.Data.Types;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// What <see cref="SqliValueDecoder"/> needs beyond the type code and the value's own
/// bytes: the encoding and CHAR/NCHAR trim policy, the packed precision/scale (or
/// DATETIME/INTERVAL qualifier) word for DECIMAL/MONEY/DATETIME/INTERVAL, and — for a
/// DECIMAL/MONEY range overflow only — enough to build the two differently-shaped error
/// messages <see cref="Informix.Data.IfxDataReader"/> and a decoded ROW/collection member
/// have always reported. Those last four fields are deliberately raw (a name, a
/// precision, a scale — not a formatted string): building one costs an allocation, and
/// <see cref="PackedValue.DecimalRangeError"/> is called from decode sites' catch handlers
/// only, so a fully-formed message is assembled there, not on every decode.
/// </summary>
internal readonly record struct SqliDecodeContext(
    Encoding? Encoding,
    bool TrimTrailingSpaces,
    short PrecisionScale,
    bool IsColumn,
    string SubjectName,
    int NumericPrecision = 0,
    int NumericScale = 0);

/// <summary>
/// The per-type SQLI wire decode (port of the <c>IfxRowColumn.loadColumnData</c> /
/// <c>IfxComplexInput</c> dispatch), shared by <see cref="SqliSession.DecodeValue"/> — a
/// tuple column, whose extent <see cref="SqliSession"/>'s <c>ColumnExtent</c> already
/// knows — and <see cref="IfxComplexDecoder.ReadElement"/> — a ROW/collection member,
/// which must discover its own extent via <see cref="Footprint"/> before it can slice a
/// value out to hand to <see cref="Decode"/>. The two calling contexts used to carry a
/// full copy of this switch each, and had already drifted apart: one tested a NULL flag
/// byte with <c>!= 0</c>, the other with <c>== 1</c>. Per <c>IfxBaseType.fromIfx</c> (and
/// its <c>IfxLvarchar</c>/<c>IfxBoolean</c> overrides, all three read from the real JDBC
/// driver source at <c>D:\informix\jdbc\src</c>), <c>== 1</c> is the only test the server
/// actually means — <see cref="Decode"/> uses that uniformly below. Unifying the switch
/// also caught a real bug this NULL-test gap was hiding: <c>IfxComplexDecoder</c>'s old
/// per-copy BOOLEAN case advanced its cursor by a hardcoded 6 bytes regardless of the
/// flag, but a NULL BOOLEAN is only ever 5 bytes on the wire (<c>IfxBoolean.toIfxTuple</c>
/// allocates <c>new byte[5]</c> for <c>isNull()</c>, 6 otherwise) — so a NULL BOOLEAN
/// member used to desynchronize every sibling read after it in the same ROW/collection.
/// <see cref="Footprint"/>'s shared opaque-family formula does not have that bug: it
/// forces the length to 0 for a NULL flag before adding it to the fixed 5-byte header,
/// the same way the LVARCHAR/UDT cases already did.
///
/// <para>ROW/SET/MULTISET/LIST/COLLECTION are absent from <see cref="Footprint"/>: nothing
/// calls it for those. <see cref="IfxComplexDecoder.ReadElement"/> reads a nested
/// ROW/collection itself (<c>ReadNestedComplex</c> — a self-delimiting <c>[size:int16]</c>
/// header, nothing like the flat types below), and <see cref="SqliSession"/> gets a
/// top-level ROW/collection's extent from <c>WalkCurrentRow</c>, not from here. Both of
/// those predate this type and are unchanged. <see cref="Decode"/> does carry a
/// ROW/SET/... case — the decode itself is a one-line handoff to
/// <see cref="IfxComplexDecoder.Decode"/>, exercised today only via
/// <see cref="SqliSession.DecodeValue"/>.</para>
///
/// <para><b>JSON/BSON as a ROW/collection member.</b> <see cref="SqliTypeCode.Json"/> and
/// <see cref="SqliTypeCode.Bson"/> (103/104) are not real wire codes — see their doc
/// comment — so <c>t.Type</c> for such a member is always <see cref="SqliTypeCode.UdtVar"/>
/// or <see cref="SqliTypeCode.UdtFixed"/> with extended id 25/26, and it is
/// <see cref="IfxComplexDecoder.ReadElement"/>'s job (not this type's) to resolve that the
/// same way <c>SqliSession.ReceiveDescribe</c> resolves it for a top-level column. It
/// deliberately resolves only the extended-id-1 LVARCHAR case (matching
/// <c>ReceiveDescribe</c>'s <c>isLvarchar</c>) and leaves 25/26 to fall into the generic
/// opaque-UDT case below, decoding to raw bytes rather than text. That is not a gap to
/// close: the real JDBC driver's direct-connection dispatch
/// (<c>IfxValue.getIfxTypeName</c>'s <c>conn.isDirect()</c> branch — the mode this port
/// targets) special-cases only extended id 5 (BOOLEAN) and 1 (LVARCHAR) for a SQLtype
/// 40/41 member; everything else, JSON and BSON included, becomes a generic opaque
/// <c>IfxUDT</c> there too. (The indirect-connection branch, <c>setClassInformationExtendedId</c>,
/// does give JSON its own <c>IfxJSON</c> class — but BSON even there stays a generic
/// <c>IfxUDT</c>, and the indirect mode is not the one this driver ports.) A JSON/BSON ROW
/// member decoding to <c>byte[]</c> instead of the top-level column's <c>string</c> is
/// therefore verified-correct, asymmetric-with-the-top-level behaviour, not a bug.</para>
/// </summary>
internal static class SqliValueDecoder
{
    /// <summary>
    /// Decodes one value already isolated to exactly its own bytes (no trailing sibling
    /// data) to a CLR value, or <see cref="DBNull.Value"/> for the type's in-band SQL NULL
    /// sentinel.
    /// </summary>
    internal static object Decode(SqliTypeCode type, ReadOnlySpan<byte> span, in SqliDecodeContext ctx)
    {
        switch (type)
        {
            case SqliTypeCode.Char:
            case SqliTypeCode.NChar:
                return span[0] == 0 ? DBNull.Value : IfxWireConverter.DecodeChar(span, ctx.Encoding, ctx.TrimTrailingSpaces);

            case SqliTypeCode.VarChar:
            case SqliTypeCode.NVarChar:
            {
                // span.Length is the column's reserved (not necessarily exact) width for
                // SqliSession.DecodeValue's caller, and exactly 1 + span[0] for
                // IfxComplexDecoder.ReadElement's (Footprint computed it that way) — the
                // Math.Min guards the former without changing the latter, which is already
                // self-consistent.
                int dataLen = Math.Min(span[0], span.Length - 1);
                if (dataLen == 1 && span[1] == 0)
                    return DBNull.Value;
                return dataLen == 0
                    ? string.Empty
                    : IfxWireConverter.DecodeChar(span.Slice(1, dataLen), ctx.Encoding);
            }

            case SqliTypeCode.LVarChar:
            {
                // [flag:1][length:int32][data]: flag == 1 marks SQL NULL.
                if (span[0] == 1)
                    return DBNull.Value;
                int len = IfxWireConverter.ReadInt32(span.Slice(1, 4));
                return len == 0
                    ? string.Empty
                    : IfxWireConverter.DecodeChar(span.Slice(5, len), ctx.Encoding);
            }

            case SqliTypeCode.Json:
            {
                // [flag:1][length:int32][data]: flag == 1 marks SQL NULL. JSON travels
                // as text, same framing as LVARCHAR.
                if (span[0] == 1)
                    return DBNull.Value;
                int len = IfxWireConverter.ReadInt32(span.Slice(1, 4));
                return len == 0
                    ? string.Empty
                    : IfxWireConverter.DecodeChar(span.Slice(5, len), ctx.Encoding);
            }

            case SqliTypeCode.Bson:
            {
                // [flag:1][length:int32][data]: flag == 1 marks SQL NULL. BSON is decoded to
                // JSON text so it reads as a string through the generic IDataReader facade.
                // The raw BSON bytes remain reachable via GetBytes / GetFieldValue<byte[]> (GetRawColumnBytes).
                if (span[0] == 1)
                    return DBNull.Value;
                int len = IfxWireConverter.ReadInt32(span.Slice(1, 4));
                return len == 0 ? string.Empty : BsonMediaTypeFormatter.ToJson(span.Slice(5, len));
            }

            case SqliTypeCode.Row:
            case SqliTypeCode.Set:
            case SqliTypeCode.MultiSet:
            case SqliTypeCode.List:
            case SqliTypeCode.Collection:
            {
                // [length:int32][data]: a self-describing ROW/collection blob (no flag byte
                // — the whole-value NULL bit lives inside the blob's own header). Structured
                // decode (IfxComplexDecoder) surfaces an IfxRow / object[] / DBNull; on any
                // non-decodable payload it returns the raw blob so the opaque-byte[]
                // behavior never regresses. Reached only from SqliSession.DecodeValue —
                // IfxComplexDecoder.ReadElement reads a nested ROW/collection itself.
                int len = IfxWireConverter.ReadInt32(span.Slice(0, 4));
                if (len <= 0)
                    return DBNull.Value;
                return IfxComplexDecoder.Decode(span.Slice(4, len).ToArray(), type, ctx.Encoding, ctx.TrimTrailingSpaces);
            }

            case SqliTypeCode.UdtVar:
            case SqliTypeCode.UdtFixed:
            {
                // [flag:1][length:int32][data]: a genuinely opaque user-defined type —
                // kept as its raw payload (only a registered custom-type mapper could do
                // more, which is out of scope). Framed like LVARCHAR, NOT like the
                // ROW/collection family: IfxUDT decodes through IfxBaseType.fromIfx,
                // which tests the flag byte and reads the length from offset + 1.
                if (span[0] == 1)
                    return DBNull.Value;
                int len = IfxWireConverter.ReadInt32(span.Slice(1, 4));
                return len <= 0 ? Array.Empty<byte>() : span.Slice(5, len).ToArray();
            }

            case SqliTypeCode.Bool:
            {
                // BOOLEAN travels opaque-framed like a UDT: [flag:1][length:int32][value:1].
                // flag == 1 marks SQL NULL; the single payload byte is 0 (false) / 1 (true).
                // Surfaced as a boxed CLR bool so GetBoolean/GetFieldValue<bool> and ORMs
                // work, matching how Npgsql serves a PostgreSQL boolean.
                if (span[0] == 1)
                    return DBNull.Value;
                int len = IfxWireConverter.ReadInt32(span.Slice(1, 4));
                return len > 0 && span[5] != 0;
            }

            case SqliTypeCode.SmallInt:
            {
                short v = IfxWireConverter.ReadInt16(span);
                return v == short.MinValue ? DBNull.Value : v;
            }

            case SqliTypeCode.Int:
            case SqliTypeCode.Serial:
            {
                int v = IfxWireConverter.ReadInt32(span);
                return v == int.MinValue ? DBNull.Value : v;
            }

            case SqliTypeCode.Float:
                return IsAllOnes(span[..8]) ? DBNull.Value : IfxWireConverter.ReadDouble(span);

            case SqliTypeCode.SmFloat:
                return IsAllOnes(span[..4]) ? DBNull.Value : IfxWireConverter.ReadReal(span);

            case SqliTypeCode.Decimal:
            case SqliTypeCode.Money:
            {
                var d = PackedValue.FromWire(span, ctx.PrecisionScale);
                if (d.IsNull)
                    return DBNull.Value;
                try
                {
                    return d.ToDecimal();
                }
                catch (OverflowException ex)
                {
                    // Column vs. ROW-member/collection-element wording, and whether a
                    // GetDouble escape hatch exists, differ by call site — see
                    // SqliDecodeContext's remarks on why these arrive unformatted.
                    string subject;
                    if (ctx.IsColumn)
                    {
                        string typeName = type == SqliTypeCode.Money ? "MONEY" : "DECIMAL";
                        string scale = ctx.NumericScale == 255 ? "" : $",{ctx.NumericScale}";
                        subject = $"Column '{ctx.SubjectName}' ({typeName}({ctx.NumericPrecision}{scale}))";
                    }
                    else
                    {
                        subject = ctx.SubjectName.Length > 0 ? $"ROW member '{ctx.SubjectName}'" : "Collection element";
                    }
                    // A server DECIMAL can hold 32 digits, more than System.Decimal; report which column/member overflowed.
                    throw d.DecimalRangeError(subject, ex, hasDoubleAccessor: ctx.IsColumn);
                }
            }

            case SqliTypeCode.Date:
                return IfxWireConverter.ReadDate(span) is { } date ? DateOnly.FromDateTime(date) : DBNull.Value;

            case SqliTypeCode.DateTime:
            {
                var packed = PackedValue.FromWire(span, ctx.PrecisionScale, isDateTime: true);
                // A time-only qualifier (start >= HOUR) surfaces as TimeOnly; a
                // date-bearing one keeps the DateTime default (see IfxColumnMetadata.ClrType).
                if (IfxQualifier.GetStartCode(ctx.PrecisionScale) >= 6)
                    return packed.ToTimeOnly() is { } time ? time : DBNull.Value;
                return packed.ToDateTime() is { } dt ? dt : DBNull.Value;
            }

            case SqliTypeCode.Interval:
                return PackedValue.FromWire(span, ctx.PrecisionScale).ToInterval() switch
                {
                    // A day-time interval surfaces as the CLR TimeSpan; a year-month one
                    // keeps IfxMonthSpan (TimeSpan cannot represent months losslessly).
                    IfxIntervalDayFraction df => (object)df.ToTimeSpan(),
                    IfxIntervalYearMonth ym => new IfxMonthSpan(ym),
                    _ => DBNull.Value,
                };

            case SqliTypeCode.Int8:
            case SqliTypeCode.Serial8:
            {
                long v = IfxWireConverter.ReadInt8(span);
                return v == IfxWireConverter.Int8Null ? DBNull.Value : v;
            }

            case SqliTypeCode.BigInt:
            case SqliTypeCode.BigSerial:
            {
                long v = IfxWireConverter.ReadBigInt(span);
                return v == long.MinValue ? DBNull.Value : v;
            }

            default:
                return span.ToArray();
        }
    }

    /// <summary>
    /// The in-buffer byte footprint of one value starting at <paramref name="span"/>[0] —
    /// for a variable-length type, <paramref name="span"/> only needs to reach far enough
    /// to cover its self-describing header (a 1-byte or 5-byte length prefix); the value's
    /// data need not be present yet. <paramref name="declaredLength"/> is <c>ColLength</c>
    /// (the fixed byte width for CHAR/NCHAR, the packed precision/scale or DATETIME/INTERVAL
    /// qualifier word for the four packed types, unused otherwise). Called only from
    /// <see cref="IfxComplexDecoder.ReadElement"/> — <see cref="SqliSession"/> gets a
    /// column's extent from <c>ColumnExtent</c>/<c>WalkCurrentRow</c> without needing to
    /// ask a value its own footprint first.
    /// </summary>
    internal static int Footprint(SqliTypeCode type, ReadOnlySpan<byte> span, int declaredLength)
    {
        switch (type)
        {
            case SqliTypeCode.Char:
            case SqliTypeCode.NChar:
                return Math.Max(0, declaredLength);

            case SqliTypeCode.VarChar:
            case SqliTypeCode.NVarChar:
                return 1 + span[0];

            case SqliTypeCode.Bool:
                // A BOOLEAN member is a flat 6 bytes — [flag:1][length:int32][value:1] —
                // and its length word must NOT be used to size it: the server writes
                // length 0 for a NULL boolean while still emitting the 1-byte value slot.
                // Live-verified against IDS 9.59.FC5 (2026-08-08) on ROW(BOOLEAN, INT, INT):
                // the member offset table reads 0, 6, 10, and the NULL member's own bytes
                // are 01 00 00 00 00 00 — flag 1, length 0, six bytes wide. Deriving 5+0
                // here starts the next member one byte early and corrupts every sibling
                // after it.
                return 6;

            case SqliTypeCode.UdtFixed:
                // [flag:1][length:int32][data:declaredLength] — a fixed-length opaque UDT
                // (e.g. a BLOB/CLOB member's 72-byte locator) always occupies its whole
                // declared slot. Like BOOLEAN above, a NULL one has length word 0 but still
                // carries the slot: live-verified against IDS 15.0.1.0 on the stores demo's
                // catalog.advert ROW(picture BLOB, caption LVARCHAR) — for a NULL picture the
                // bytes are 01 00 00 00 00 + 72 bytes, and the row's own offset table puts
                // caption at 77. Sizing it 5 + 0 read caption 72 bytes early (-79716).
                return 5 + Math.Max(declaredLength, Math.Max(0, IfxWireConverter.ReadInt32(span.Slice(1, 4))));

            case SqliTypeCode.LVarChar:
            case SqliTypeCode.Json:
            case SqliTypeCode.Bson:
            case SqliTypeCode.UdtVar:
                // [flag:1][length:int32][data] — genuinely length-driven, unlike BOOLEAN
                // above (port of IfxComplexInput.readUDT, which takes its size from the
                // length word). Math.Max(0, ...) guards a corrupt negative length.
                return 5 + Math.Max(0, IfxWireConverter.ReadInt32(span.Slice(1, 4)));

            case SqliTypeCode.SmallInt:
                return 2;

            case SqliTypeCode.Int:
            case SqliTypeCode.Serial:
                return 4;

            case SqliTypeCode.Float:
                return 8;

            case SqliTypeCode.SmFloat:
                return 4;

            case SqliTypeCode.Decimal:
            case SqliTypeCode.Money:
            case SqliTypeCode.DateTime:
            case SqliTypeCode.Interval:
                // Port of IfxColumnInfo.getAdjustedColumnLength / IfxComplexTypeInfo.AdjustedLength.
                return declaredLength != -1 ? ((declaredLength >> 8 & 0xFF) + (declaredLength & 1) + 3) / 2 : declaredLength;

            case SqliTypeCode.Date:
                return 4;

            case SqliTypeCode.Int8:
            case SqliTypeCode.Serial8:
                return 10;

            case SqliTypeCode.BigInt:
            case SqliTypeCode.BigSerial:
                return 8;

            default:
                // Not reachable via IfxComplexDecoder.ReadElement's real dispatch: ROW/
                // SET/MULTISET/LIST/COLLECTION never call here (see class remarks), and an
                // unported element type is handled by ReadElement's own terminal default
                // (ColLength-based raw copy, or a thrown IfxException) without asking this
                // method first.
                return Math.Max(0, declaredLength);
        }
    }

    private static bool IsAllOnes(ReadOnlySpan<byte> b)
    {
        foreach (byte x in b)
            if (x != 0xFF)
                return false;
        return true;
    }
}
