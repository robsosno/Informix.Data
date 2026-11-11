using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// In-tuple framing of the opaque-UDT family (base type UDTVAR(40)/UDTFIXED(41)).
///
/// These columns are framed <c>[flag:1][len:int32][data]</c> — like LVARCHAR, and
/// *unlike* the ROW/collection family's <c>[len:int32][data]</c>. The distinction is
/// IfxRowColumn.setOffsetInTuple's: colType 40/41/43/45/46 read the length at
/// <c>offset + 1</c>, only IfxTypes.isComplexType (ROW and the collections) reads it at
/// <c>offset</c>. Getting this wrong does not merely garble the UDT — it desynchronizes
/// the walk, so every later column in the row reads the wrong bytes.
///
/// Every row image below was captured live from IDS 14.10.FC5DE.
/// </summary>
public class OpaqueUdtFramingTests
{
    // select first 2 indexkeys, idxname from sysindices
    //   indexkeys: UDTVAR(40), xid 7 "informix"."indexkeyarray", SENDRECV text form
    //   idxname:   VARCHAR(128), var-len framed [len:byte][data]
    // Row image, 25 bytes: 00 | 00 00 00 0c | "1 [1], 2 [1]" | 07 | "tabname"
    private static readonly byte[] CapturedIndexkeyRow =
    [
        0x00, 0x00, 0x00, 0x00, 0x0c,
        0x31, 0x20, 0x5b, 0x31, 0x5d, 0x2c, 0x20, 0x32, 0x20, 0x5b, 0x31, 0x5d,
        0x07, 0x74, 0x61, 0x62, 0x6e, 0x61, 0x6d, 0x65,
    ];

    // select first 1 'hello-llvc-value'::informix.longlvarchar as c, 'MARKER7' as m from systables
    // Row image, 28 bytes: 00 | 00 00 00 10 | "hello-llvc-value" | "MARKER7"
    private static readonly byte[] CapturedLlvcRow =
    [
        0x00, 0x00, 0x00, 0x00, 0x10,
        0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x2d, 0x6c, 0x6c, 0x76, 0x63, 0x2d, 0x76, 0x61, 0x6c, 0x75, 0x65,
        0x4d, 0x41, 0x52, 0x4b, 0x45, 0x52, 0x37,
    ];

    private static SqliStatementKit.Col UdtCol(string name, int xid, string extName) =>
        new(name, (short)SqliTypeCode.UdtVar, 0, 0, ExtendedId: xid, ExtOwner: "informix", ExtName: extName);

    private static byte[] SelectScript(SqliStatementKit.Col[] cols, params byte[][] rows) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 0, cols),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        // open reply (separate round trip — an opaque UDT makes the row layout variable)
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.Join(rows.Select(SqliStatementKit.TupleFrame).ToArray()),
        SqliStatementKit.CloseFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    [Fact]
    public void Opaque_udt_payload_skips_the_flag_byte_and_the_next_column_still_walks()
    {
        SqliStatementKit.Col[] cols =
        [
            UdtCol("indexkeys", 7, "indexkeyarray"),
            new("idxname", (short)SqliTypeCode.VarChar, 128, 0),
        ];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, CapturedIndexkeyRow), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select indexkeys, idxname from sysindices";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        // The payload is the 12 bytes after [flag][len] — not the length-prefixed blob,
        // and not DBNull (which is what reading the length at offset 0 used to produce).
        Assert.Equal("1 [1], 2 [1]", IfxWireConverter.DecodeChar((byte[])reader.GetValue(0), null));
        // The walk consumed 5 + 12 bytes, so the VARCHAR that follows is intact. Reading
        // the length at offset 0 advanced only 4 bytes and made this column return
        // "1 [1], 2 [1]" — the UDT's own text.
        Assert.Equal("tabname", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Null_opaque_udt_decodes_to_DBNull_without_shifting_the_rest_of_the_row()
    {
        SqliStatementKit.Col[] cols =
        [
            UdtCol("u", 7, "indexkeyarray"),
            new("m", (short)SqliTypeCode.VarChar, 20, 0),
        ];
        // flag 1 + len 0 => SQL NULL, a 5-byte footprint.
        byte[] row = [0x01, 0x00, 0x00, 0x00, 0x00, 0x02, (byte)'o', (byte)'k'];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, row), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select u, m from t";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("ok", reader.GetString(1));
    }

    [Fact]
    public void Row_and_collection_columns_keep_the_flagless_framing()
    {
        // Regression guard for the other half of the distinction: ROW/SET/LIST/MULTISET
        // carry [len:int32][data] with no flag byte, so a leading 0x00 is part of the
        // length, not a null flag. Verified live: a ROW column's footprint is 4 + len.
        SqliStatementKit.Col[] cols =
        [
            new("r", (short)SqliTypeCode.Row, 0, 0),
            new("m", (short)SqliTypeCode.VarChar, 20, 0),
        ];
        // [len:int32 = 3]["abc"] then the VARCHAR — 7 bytes total, no flag byte anywhere.
        byte[] row = [0x00, 0x00, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x02, (byte)'o', (byte)'k'];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, row), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select r, m from t";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        // "abc" is not a decodable complex blob, so the decoder falls back to the raw
        // payload — what matters here is that it is exactly the 3 bytes after the length
        // and that the column behind it still lands on its own bytes.
        Assert.Equal("ok", reader.GetString(1));
    }

    [Fact]
    public void Llvc_collapses_to_lvarchar_and_decodes_as_a_string()
    {
        SqliStatementKit.Col[] cols =
        [
            UdtCol("c", (int)IfxExtendedType.Llvc, "longlvarchar"),
            new("m", (short)SqliTypeCode.Char, 7, 0),
        ];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, CapturedLlvcRow), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select c, m from t";
        using var reader = cmd.ExecuteReader();

        // LLVC(24) is "long lvarchar" — same framing and same inline character payload as
        // LVARCHAR(1), so it collapses to LVarChar and reads as a string, not a byte[].
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("LVarChar", reader.GetDataTypeName(0));
        Assert.True(reader.Read());
        Assert.Equal("hello-llvc-value", reader.GetString(0));
        Assert.Equal("MARKER7", reader.GetString(1));
    }

    [Fact]
    public void Llvc_keeps_its_server_type_name_in_the_column_schema()
    {
        SqliStatementKit.Col[] cols = [UdtCol("c", (int)IfxExtendedType.Llvc, "longlvarchar")];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, CapturedLlvcRow[..21]), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select c from t";
        using var reader = cmd.ExecuteReader();

        var col = reader.GetColumnSchema()[0];
        Assert.Equal("longlvarchar", col["ExtendedTypeName"]);
        Assert.Equal("informix", col["ExtendedOwner"]);
        Assert.False((bool)col["IsOpaqueComplex"]!);
    }
}
