using System.Reflection;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="SqliValueDecoder"/> — the value decode merged out of
/// <c>SqliSession.DecodeValue</c> and <c>IfxComplexDecoder.ReadElement</c>. These tests
/// target exactly what the merge changed: the NULL-flag test for the opaque
/// (LVARCHAR/JSON/BSON/UDT/BOOLEAN) family adopted <c>== 1</c> (matching the real JDBC
/// driver's <c>IfxBaseType.fromIfx</c> / <c>IfxLvarchar.fromIfx</c> / <c>IfxBoolean.fromIfx</c>,
/// read from <c>D:\informix\jdbc\src</c>) in place of <c>DecodeValue</c>'s old <c>!= 0</c>,
/// and unifying the footprint math fixed a real bug where a NULL BOOLEAN nested in a
/// ROW/collection desynchronized every sibling read after it. Existing coverage
/// (<c>SqliStructuredComplexTests</c>, <c>JsonBsonTests</c>, <c>BooleanColumnTests</c>) is
/// all built from live-captured bytes whose flag byte is always exactly 0 or 1 — it
/// cannot, by construction, distinguish <c>!= 0</c> from <c>== 1</c>, which is why these
/// more synthetic tests exist alongside it.
/// </summary>
public class SqliValueDecoderTests
{
    // The same bytes are decoded as a top-level column and as a ROW/collection member; NULL handling must not depend on which.
    private static readonly SqliDecodeContext ColumnCtx =
        new(Encoding: null, TrimTrailingSpaces: false, PrecisionScale: 0, IsColumn: true, SubjectName: "col");

    private static readonly SqliDecodeContext RowMemberCtx =
        new(Encoding: null, TrimTrailingSpaces: false, PrecisionScale: 0, IsColumn: false, SubjectName: "member");

    // ---- the NULL-flag gate: == 1, not != 0 -------------------------------------------

    // SqliTypeCode is internal, and a public [Theory] method cannot expose an internal
    // type in its own signature (CS0051) even with InternalsVisibleTo -- hence separate
    // [Fact]s per type rather than [Theory]/[InlineData(SqliTypeCode.X)].

    private static void AssertFlagByte1IsNullForBothContexts(SqliTypeCode type)
    {
        byte[] span = [1, 0, 0, 0, 0]; // [flag=1][len=0] -- the universal NULL encoding
        Assert.Equal(DBNull.Value, SqliValueDecoder.Decode(type, span, ColumnCtx));
        Assert.Equal(DBNull.Value, SqliValueDecoder.Decode(type, span, RowMemberCtx));
    }

    [Fact]
    public void Flag_byte_1_is_null_for_lvarchar() => AssertFlagByte1IsNullForBothContexts(SqliTypeCode.LVarChar);

    [Fact]
    public void Flag_byte_1_is_null_for_json() => AssertFlagByte1IsNullForBothContexts(SqliTypeCode.Json);

    [Fact]
    public void Flag_byte_1_is_null_for_udtvar() => AssertFlagByte1IsNullForBothContexts(SqliTypeCode.UdtVar);

    [Fact]
    public void Flag_byte_1_is_null_for_udtfixed() => AssertFlagByte1IsNullForBothContexts(SqliTypeCode.UdtFixed);

    // IfxBaseType.fromIfx / IfxLvarchar.fromIfx test "b[offset] == 1" for NULL -- any
    // other byte, 2 included, falls into the ordinary decode path. The old DecodeValue
    // used "!= 0", which would have called this NULL. This is the exact divergence the
    // design doc's case (2) called out ("a flag byte of 2 is NULL on one path and
    // non-NULL on the other").
    private static void AssertFlagByte2IsNotNull(SqliTypeCode type)
    {
        byte[] span = [2, 0, 0, 0, 1, (byte)'x'];
        object decoded = SqliValueDecoder.Decode(type, span, ColumnCtx);
        Assert.NotEqual(DBNull.Value, decoded);
    }

    // Only the value 1 means NULL, as in the JDBC driver; any other flag byte is treated as "not null".
    [Fact]
    public void Flag_byte_2_is_NOT_null_for_lvarchar_per_JDBC() => AssertFlagByte2IsNotNull(SqliTypeCode.LVarChar);

    [Fact]
    public void Flag_byte_2_is_NOT_null_for_udtvar_per_JDBC() => AssertFlagByte2IsNotNull(SqliTypeCode.UdtVar);

    [Fact]
    public void Boolean_flag_byte_1_is_null_flag_byte_0_is_false_matching_JDBC()
    {
        Assert.Equal(DBNull.Value, SqliValueDecoder.Decode(SqliTypeCode.Bool, [1, 0, 0, 0, 0], ColumnCtx));
        Assert.Equal(false, SqliValueDecoder.Decode(SqliTypeCode.Bool, [0, 0, 0, 0, 1, 0], ColumnCtx));
        Assert.Equal(true, SqliValueDecoder.Decode(SqliTypeCode.Bool, [0, 0, 0, 0, 1, 1], ColumnCtx));
    }

    // ---- opaque-family footprint: the length word is authoritative -------------------

    [Fact]
    public void Footprint_of_a_boolean_member_is_a_flat_6_regardless_of_its_length_word()
    {
        // Ground truth, dumped off the wire from IDS 9.59.FC5 on 2026-08-08 for
        // ROW(BOOLEAN, INT, INT): the member offset table reads 0, 6, 10, and the NULL
        // boolean's own six bytes are 01 00 00 00 00 00 -- flag 1, length *0*, plus a
        // value slot the length word does not account for. So a boolean must NOT be sized
        // from its length word; 5 + 0 starts the next member a byte early and corrupts
        // every sibling after it.
        byte[] nullBoolean = [1, 0, 0, 0, 0, 0];   // flag 1, length 0 -- still 6 wide
        Assert.Equal(6, SqliValueDecoder.Footprint(SqliTypeCode.Bool, nullBoolean, declaredLength: 0));

        byte[] falseBoolean = [0, 0, 0, 0, 1, 0];  // flag 0, length 1
        Assert.Equal(6, SqliValueDecoder.Footprint(SqliTypeCode.Bool, falseBoolean, declaredLength: 0));
    }

    [Fact]
    public void Footprint_of_the_length_driven_opaque_family_is_5_plus_the_length_word()
    {
        // The contrast case to the boolean above: LVARCHAR/UDT/JSON/BSON really are sized
        // by their length word (IfxComplexInput.readUDT).
        byte[] lvarchar = [0, 0, 0, 0, 4, (byte)'a', (byte)'b', (byte)'c', (byte)'d'];
        Assert.Equal(9, SqliValueDecoder.Footprint(SqliTypeCode.LVarChar, lvarchar, declaredLength: 0));

        byte[] nullLvarchar = [1, 0, 0, 0, 0];
        Assert.Equal(5, SqliValueDecoder.Footprint(SqliTypeCode.LVarChar, nullLvarchar, declaredLength: 0));
    }

    // ---- NULL-boolean sibling recovery in a ROW --------------------------------------

    private static readonly MethodInfo ReadElementMethod = typeof(IfxComplexDecoder)
        .GetMethod("ReadElement", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object ReadElement(IfxComplexTypeInfo t, byte[] b, ref int pos) =>
        InvokeReadElement(t, b, ref pos, b.Length);

    private static object InvokeReadElement(IfxComplexTypeInfo t, byte[] b, ref int pos, int end)
    {
        object?[] args = [t, b, 0, pos, end, null, false, 0];
        object result = ReadElementMethod.Invoke(null, args)!;
        pos = (int)args[3]!;
        return result;
    }

    // A BOOLEAN ROW member arrives as UdtVar/UdtFixed carrying extended id 5, NOT as
    // SqliTypeCode.Bool -- ReceiveDescribe's collapse only runs for a top-level column.
    // These use the real member shape (live-observed against IDS 9.59.FC5), which is
    // also what exercises ReadElement's xid-5 resolution.
    private static IfxComplexTypeInfo BooleanMember(string name) =>
        new() { Name = name, Type = SqliTypeCode.UdtFixed, ExtendedId = 5 };

    [Fact]
    public void ReadElement_null_boolean_member_does_not_desync_the_following_sibling()
    {
        // Live-verified wire shape (IDS 9.59.FC5, 2026-08-08): a NULL BOOLEAN member is
        // flag=1 with length=*0* yet still occupies 6 bytes. Sizing it from the length word
        // starts the INT one byte short and decodes garbage -- exactly what the live probe
        // caught (n1 came back 0, n2 came back 1862270976 = 111 << 24, the tell-tale
        // one-byte shift).
        byte[] value = [
            1, 0, 0, 0, 0, 0, // BOOLEAN member: flag=1 => NULL, length=0, still 6 bytes
            0, 0, 0, 42,      // INT member: 42, big-endian, 4 bytes
        ];
        var intMember = new IfxComplexTypeInfo { Name = "n", Type = SqliTypeCode.Int };

        int pos = 0;
        Assert.Equal(DBNull.Value, ReadElement(BooleanMember("flag"), value, ref pos));
        Assert.Equal(6, pos);
        Assert.Equal(42, ReadElement(intMember, value, ref pos));
        Assert.Equal(10, pos);
    }

    [Fact]
    public void ReadElement_non_null_boolean_member_decodes_as_bool_then_sibling_still_works()
    {
        // The xid-5 resolution: without it this member decodes as a generic opaque UDT and
        // comes back as byte[] where the equivalent top-level column comes back as a bool
        // (live-observed before the fix).
        byte[] value = [
            0, 0, 0, 0, 1, 1, // BOOLEAN member: true, 6 bytes total
            0, 0, 0, 7,       // INT member: 7
        ];
        var intMember = new IfxComplexTypeInfo { Name = "n", Type = SqliTypeCode.Int };

        int pos = 0;
        Assert.Equal(true, ReadElement(BooleanMember("flag"), value, ref pos));
        Assert.Equal(6, pos);
        Assert.Equal(7, ReadElement(intMember, value, ref pos));
    }

    [Fact]
    public void ReadElement_false_boolean_member_is_false_not_null()
    {
        byte[] value = [0, 0, 0, 0, 1, 0]; // flag=0, length=1, value byte 0 => false
        int pos = 0;
        Assert.Equal(false, ReadElement(BooleanMember("flag"), value, ref pos));
        Assert.Equal(6, pos);
    }

    // ---- JSON/BSON as a ROW/collection member: verified-correct, not a gap -----------

    [Fact]
    public void ReadElement_json_hat_udtvar_member_decodes_to_raw_bytes_not_text()
    {
        // t.Type is UdtVar here, never the synthetic SqliTypeCode.Json -- ReceiveDescribe's
        // collapse never runs on a nested descriptor (see SqliValueDecoder's remarks). The
        // real JDBC driver's direct-connection dispatch does not special-case extended id 25
        // for a ROW/collection member either, so this member falls into the generic opaque-
        // UDT case and comes back as raw bytes, not the parsed-text a top-level JSON column
        // would give. This test locks that in so it is not "fixed" into the wrong behaviour
        // by a future reader who (reasonably) expects ROW-member and column decode to match.
        byte[] payload = "{\"a\":1}"u8.ToArray();
        byte[] value = new byte[5 + payload.Length];
        value[0] = 0; // not null
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(1, 4), payload.Length);
        payload.CopyTo(value.AsSpan(5));

        var jsonHatMember = new IfxComplexTypeInfo { Name = "doc", Type = SqliTypeCode.UdtVar, ExtendedId = 25 };

        int pos = 0;
        object decoded = ReadElement(jsonHatMember, value, ref pos);
        var raw = Assert.IsType<byte[]>(decoded);
        Assert.Equal(payload, raw);
        Assert.Equal(value.Length, pos);
    }

    [Fact]
    public void ReadElement_lvarchar_hat_udtvar_member_still_decodes_to_string()
    {
        // Contrast case: extended id 1 (LVARCHAR) is the one hat ReadElement does resolve,
        // unchanged by this refactor.
        byte[] payload = "hello"u8.ToArray();
        byte[] value = new byte[5 + payload.Length];
        value[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(1, 4), payload.Length);
        payload.CopyTo(value.AsSpan(5));

        var lvarcharHatMember = new IfxComplexTypeInfo { Name = "s", Type = SqliTypeCode.UdtVar, ExtendedId = 1 };

        int pos = 0;
        Assert.Equal("hello", ReadElement(lvarcharHatMember, value, ref pos));
        Assert.Equal(value.Length, pos);
    }

    // ---- the DECIMAL/MONEY overflow message stays context-specific -------------------

    // 9e29 -- a valid Informix DECIMAL(32), above System.Decimal's ~7.92e28 maximum
    // (mirrors PackedValueTests.Magnitude_beyond_decimal_range_throws_and_the_rich_error_names_the_subject).
    private static byte[] OverflowingDecimalSpan() =>
        PackedValue.Parse("9" + new string('0', 29)).ToWireBytes().AsSpan(2).ToArray();

    // A 30-digit value does not fit System.Decimal; for a column the message names it and its declared DECIMAL(32,2) and advises reading it as double.
    [Fact]
    public void Decimal_overflow_message_names_the_column_with_type_and_precision_for_a_column_context()
    {
        var ctx = new SqliDecodeContext(null, false, PrecisionScale: 0, IsColumn: true, SubjectName: "price",
            NumericPrecision: 32, NumericScale: 2);
        var ex = Assert.Throws<OverflowException>(() => SqliValueDecoder.Decode(SqliTypeCode.Decimal, OverflowingDecimalSpan(), ctx));
        Assert.Contains("Column 'price' (DECIMAL(32,2))", ex.Message);
        Assert.Contains("Read it as a double instead", ex.Message);
    }

    // A ROW member has no GetDouble, so the "read as double" advice is omitted.
    [Fact]
    public void Decimal_overflow_message_names_the_row_member_without_a_double_accessor_hint()
    {
        var ctx = new SqliDecodeContext(null, false, PrecisionScale: 0, IsColumn: false, SubjectName: "price");
        var ex = Assert.Throws<OverflowException>(() => SqliValueDecoder.Decode(SqliTypeCode.Decimal, OverflowingDecimalSpan(), ctx));
        Assert.Contains("ROW member 'price'", ex.Message);
        Assert.DoesNotContain("Read it as a double instead", ex.Message);
    }

    // Unnamed members of collections are described as "Collection element".
    [Fact]
    public void Decimal_overflow_message_says_collection_element_when_the_member_has_no_name()
    {
        var ctx = new SqliDecodeContext(null, false, PrecisionScale: 0, IsColumn: false, SubjectName: "");
        var ex = Assert.Throws<OverflowException>(() => SqliValueDecoder.Decode(SqliTypeCode.Decimal, OverflowingDecimalSpan(), ctx));
        Assert.Contains("Collection element", ex.Message);
    }
}
