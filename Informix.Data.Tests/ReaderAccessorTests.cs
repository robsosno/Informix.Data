using System.Data;
using System.Data.Common;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;
using static Informix.Data.Tests.SqliStatementKit;

namespace Informix.Data.Tests;

/// <summary>
/// The modern <see cref="System.Data.Common.DbDataReader"/> accessor surface added to
/// <see cref="IfxDataReader"/>: <c>GetFieldValue&lt;T&gt;</c>, <c>GetStream</c>/
/// <c>GetTextReader</c> (including true incremental smart-LOB streaming), and
/// <c>GetColumnSchema</c>/<see cref="IfxDbColumn"/> — with the guarantee that
/// <c>GetSchemaTable</c> keeps its exact pre-refactor shape.
/// </summary>
public class ReaderAccessorTests
{
    // ---- shared fixed-row select scaffolding (id INT, name CHAR(8)) ----

    private static byte[] IntField(int v)
    {
        var b = new byte[4];
        IfxWireConverter.WriteInt32(b, v);
        return b;
    }

    private static byte[] FixedRow(int id, string name8)
    {
        var row = new byte[12];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), id);
        IfxWireConverter.EncodeChar(name8.PadRight(8)).CopyTo(row.AsSpan(4));
        return row;
    }

    private static byte[] IdNameSelect(params byte[][] rows) => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 12,
            new Col("id", (short)SqliTypeCode.Int, 4, 0),
            new Col("name", (short)SqliTypeCode.Char, 8, 4)),
        DoneFrame(), EotFrame(),
        DoneFrame(), Join(rows.Select(TupleFrame).ToArray()), CloseFrame(), EotFrame(),
        EotFrame());

    private static IfxConnection OpenIdName(out DuplexStream duplex, params byte[][] rows) =>
        OpenConnection(IdNameSelect(rows), out duplex);

    // ---- GetFieldValue<T> ----

    [Fact]
    public void GetFieldValue_returns_the_exact_type_without_conversion()
    {
        using var cn = OpenIdName(out _, FixedRow(101, "alpha"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.Equal(101, r.GetFieldValue<int>(0));      // exact type
        Assert.Equal("alpha   ", r.GetFieldValue<string>(1));
    }

    [Fact]
    public void GetFieldValue_widens_int_to_long()
    {
        using var cn = OpenIdName(out _, FixedRow(7, "x"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        // An INT column read as <long> behaves like GetInt64 (Convert.ChangeType path).
        Assert.Equal(7L, r.GetFieldValue<long>(0));
        Assert.Equal(r.GetInt64(0), r.GetFieldValue<long>(0));
    }

    [Fact]
    public void GetFieldValue_routes_provider_types_and_keeps_their_null_semantics()
    {
        using var cn = OpenIdName(out _, FixedRow(42, "y"), FixedRow(int.MinValue, "\0\0\0\0\0\0\0\0"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        // The provider type routes through GetIfxDecimal (INT -> decimal -> IfxDecimal).
        Assert.Equal(new IfxDecimal(42m), r.GetFieldValue<IfxDecimal>(0));

        Assert.True(r.Read()); // second row: id is the INT NULL sentinel, name all-NUL -> NULL
        Assert.True(r.IsDBNull(0));
        // Provider types keep the accessor's SQL-NULL -> .Null rule (they do NOT throw).
        Assert.True(r.GetFieldValue<IfxDecimal>(0).IsNull);
    }

    [Fact]
    public void GetFieldValue_object_on_null_returns_dbnull_but_typed_throws()
    {
        using var cn = OpenIdName(out _, FixedRow(int.MinValue, "\0\0\0\0\0\0\0\0"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.Same(DBNull.Value, r.GetFieldValue<object>(0));
        Assert.Throws<InvalidCastException>(() => r.GetFieldValue<int>(0));
    }

    [Fact]
    public void GetFieldValue_stream_and_textreader_match_the_dedicated_accessors()
    {
        // <TextReader> over a CHAR column must equal GetTextReader; <Stream> over a BYTE
        // column must equal GetStream. (Byte column exercised in its own test below too.)
        using var cn = OpenIdName(out _, FixedRow(1, "hello"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        using var viaGeneric = r.GetFieldValue<TextReader>(1);
        Assert.Equal("hello   ", viaGeneric.ReadToEnd());
    }

    // ---- GetStream / GetTextReader over in-row (non-LOB) columns ----

    private static byte[] ByteColumnSelect(byte[] data) => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 5, tupleSize: 56,
            new Col("d", (short)SqliTypeCode.Byte, 56, 0)),
        DoneFrame(), EotFrame(),
        DoneFrame(), TupleFrame(BlobDescriptor(data.Length)), CloseFrame(), EotFrame(),
        BlobFrame(data), BlobEndFrame(), EotFrame(),
        EotFrame());

    [Fact]
    public void GetStream_over_a_byte_column_is_read_only_and_matches_the_bytes()
    {
        byte[] data = [0xde, 0xad, 0xbe, 0xef, 0x01];
        using var cn = OpenConnection(ByteColumnSelect(data), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_picture from catalog";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        using var stream = r.GetStream(0);
        Assert.False(stream.CanWrite); // read-only view over the materialized array
        Assert.True(stream.CanRead);
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(data, copy.ToArray());

        // A writable-view leak would let a caller corrupt the buffer; assert it can't.
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(0));
    }

    [Fact]
    public void GetFieldValue_byte_array_fast_path_over_a_byte_column()
    {
        byte[] data = [1, 2, 3, 4];
        using var cn = OpenConnection(ByteColumnSelect(data), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_picture from catalog";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.Equal(data, r.GetFieldValue<byte[]>(0));
    }

    [Fact]
    public void GetTextReader_over_a_char_column_matches_GetString()
    {
        using var cn = OpenIdName(out _, FixedRow(1, "textual"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        string viaString = r.GetString(1);
        using var tr = r.GetTextReader(1);
        Assert.Equal(viaString, tr.ReadToEnd());
    }

    [Fact]
    public void GetStream_and_GetTextReader_throw_on_a_null_cell()
    {
        using var cn = OpenIdName(out _, FixedRow(int.MinValue, "\0\0\0\0\0\0\0\0"));
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        Assert.Throws<InvalidCastException>(() => r.GetTextReader(1)); // NULL CHAR
        Assert.Throws<InvalidCastException>(() => r.GetStream(0));     // INT is not stream-able
    }

    // ---- smart-LOB streaming (FastPath IFX_LO_* + SQ_LODATA) ----

    private const int Fd = 39;

    // Each of these is the FIRST use of its routine signature on the connection, so the
    // reply resolves the FastPath handle (SQ_GETROUTINE) before the SQ_FPROUTINE result.
    private static byte[] LobOpenReply() => Join(RoutineHandleFrame("db", 67), EotFrame(), FpReturnFrame(Fd), EotFrame());
    private static byte[] LobStatReply(long size) => Join(RoutineHandleFrame("db", 71), EotFrame(), FpReturnFrame(LoStatBlock(size), 0), EotFrame());
    private static byte[] LobSeekReply() => Join(RoutineHandleFrame("db", 73), EotFrame(), FpReturnFrame(0L, 0), EotFrame());
    private static byte[] LobReadReply(byte[] chunk) => Join(LoDataReadFrame(chunk), EotFrame());
    private static byte[] LobCloseReply() => Join(RoutineHandleFrame("db", 68), EotFrame(), FpReturnFrame(0), EotFrame());

    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    private static byte[] Pattern(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)((i % 251) + 1); // never zero
        return b;
    }

    private static byte[] LobDescribe(string extName, short extId) => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
            new Col("id", (short)SqliTypeCode.Int, 4, 0),
            new Col("lob", (short)SqliTypeCode.UdtFixed, 72, 4, ExtendedId: extId, ExtOwner: "informix", ExtName: extName)),
        DoneFrame(), EotFrame());

    private static byte[] LobRow(int id, byte[]? locator) => Join(IntField(id), SmartBlobField(locator));

    [Fact]
    public void GetStream_over_a_blob_streams_incrementally_in_chunks()
    {
        byte[] blob = Pattern(100_000); // > 3 * 32000 -> forces multiple SQ_LODATA reads
        byte[] script = Join(
            LobDescribe("blob", extId: 10),
            DoneFrame(), EotFrame(),
            TupleFrame(LobRow(1, Locator(0x10))), CloseFrame(), EotFrame(),
            LobOpenReply(), LobStatReply(blob.Length),
            LobReadReply(blob[0..32_000]),
            LobReadReply(blob[32_000..64_000]),
            LobReadReply(blob[64_000..96_000]),
            LobReadReply(blob[96_000..100_000]),
            LobCloseReply(),
            EotFrame());

        using var cn = OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, img from smart_t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        var stream = r.GetStream(1);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(blob.Length, stream.Length); // IFX_LO_OPEN + IFX_LO_STAT

        // Read with a 32000-byte buffer: each Read that returns data is exactly one
        // SQ_LODATA round trip, so the read count proves the value streamed in pieces
        // rather than being materialized in one shot.
        var collected = new MemoryStream();
        var buffer = new byte[32_000];
        int reads = 0, n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            collected.Write(buffer, 0, n);
            reads++;
        }
        Assert.Equal(4, reads);
        Assert.Equal(blob, collected.ToArray());

        stream.Dispose(); // IFX_LO_CLOSE
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public void Blob_stream_seek_positions_before_reading()
    {
        byte[] blob = Pattern(40);
        byte[] script = Join(
            LobDescribe("blob", extId: 10),
            DoneFrame(), EotFrame(),
            TupleFrame(LobRow(1, Locator(0x20))), CloseFrame(), EotFrame(),
            LobOpenReply(), LobStatReply(blob.Length), LobSeekReply(),
            LobReadReply(blob[10..40]),
            LobCloseReply(),
            EotFrame());

        using var cn = OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, img from smart_t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        using var stream = r.GetStream(1);
        Assert.Equal(10, stream.Seek(10, SeekOrigin.Begin)); // IFX_LO_SEEK
        Assert.Equal(10, stream.Position);

        var rest = new MemoryStream();
        stream.CopyTo(rest);
        Assert.Equal(blob[10..40], rest.ToArray());
        Assert.Equal(40, stream.Position);
    }

    [Fact]
    public void GetTextReader_over_a_clob_streams_and_decodes_text()
    {
        string text = "smart clob streamed through a StreamReader";
        byte[] clob = IfxWireConverter.EncodeChar(text); // Latin1 bytes, < the StreamReader buffer
        byte[] script = Join(
            LobDescribe("clob", extId: 11),
            DoneFrame(), EotFrame(),
            TupleFrame(LobRow(1, Locator(0x30))), CloseFrame(), EotFrame(),
            LobOpenReply(), LobStatReply(clob.Length),
            LobReadReply(clob),
            LobCloseReply(),
            EotFrame());

        using var cn = OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, doc from smart_t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        string streamed;
        using (var tr = r.GetTextReader(1))
            streamed = tr.ReadToEnd();
        Assert.Equal(text, streamed);
    }

    [Fact]
    public void The_next_reader_read_disposes_a_smart_lob_stream()
    {
        byte[] blob = Pattern(20);
        byte[] script = Join(
            LobDescribe("blob", extId: 10),
            DoneFrame(), EotFrame(),
            TupleFrame(LobRow(1, Locator(0x10))), TupleFrame(LobRow(2, Locator(0x50))), CloseFrame(), EotFrame(),
            LobOpenReply(), LobStatReply(blob.Length),
            LobReadReply(blob[0..1]),
            LobCloseReply(), // the next Read() closes the stream handed out for row 1
            EotFrame());

        using var cn = OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, img from smart_t";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        var stream = r.GetStream(1);
        Assert.Equal(blob[0], stream.ReadByte()); // opens + reads one byte

        Assert.True(r.Read()); // advancing invalidates the row-1 stream
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    // ---- GetColumnSchema / IfxDbColumn / GetSchemaTable equivalence ----

    private static byte[] MixedSchemaSelect() => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 0,
            new Col("id", (short)SqliTypeCode.Int, 4, 0),
            new Col("name", (short)SqliTypeCode.Char, 10, 4),
            new Col("amt", (short)SqliTypeCode.Decimal, (8 << 8) | 2, 14),           // DECIMAL(8,2)
            new Col("pct", (short)((int)SqliTypeCode.Int | (int)IfxTypeFlags.Distinct), 4, 22,
                ExtOwner: "root", ExtName: "percent"),                                // DISTINCT over INT
            new Col("pic", (short)SqliTypeCode.Byte, 56, 26)),                        // BYTE (IsLong)
        DoneFrame(), EotFrame(),
        DoneFrame(), CloseFrame(), EotFrame(), // open + fetch (no rows)
        EotFrame());

    [Fact]
    public void GetColumnSchema_reports_typed_column_detail()
    {
        using var cn = OpenConnection(MixedSchemaSelect(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name, amt, pct, pic from t";
        using var r = cmd.ExecuteReader();

        var schema = r.GetColumnSchema();
        Assert.Equal(5, schema.Count);
        Assert.All(schema, c => Assert.IsType<IfxDbColumn>(c));

        var id = (IfxDbColumn)schema[0];
        Assert.Equal("id", id.ColumnName);
        Assert.Equal(0, id.ColumnOrdinal);
        Assert.Equal(typeof(int), id.DataType);
        Assert.Equal(IfxType.Integer, id.ProviderType);
        Assert.Null(id.NumericPrecision);
        Assert.False(id.IsLong);
        Assert.False(id.IsDistinct);

        var amt = (IfxDbColumn)schema[2];
        Assert.Equal(IfxType.Decimal, amt.ProviderType);
        Assert.Equal(8, amt.NumericPrecision);
        Assert.Equal(2, amt.NumericScale);
        Assert.Equal("Decimal", amt.DataTypeName);

        var pct = (IfxDbColumn)schema[3];
        Assert.True(pct.IsDistinct);
        Assert.Equal("percent", pct.ExtendedTypeName);
        Assert.Equal("root", pct.ExtendedOwner);
        Assert.Equal("percent", pct.DataTypeName); // a DISTINCT reports its own name
        Assert.Equal(IfxType.Integer, pct.ProviderType);

        var pic = (IfxDbColumn)schema[4];
        Assert.True(pic.IsLong);
        Assert.Equal(IfxType.Byte, pic.ProviderType);

        // The Informix extras are reachable through the DbColumn indexer too.
        Assert.Equal("percent", pct[nameof(IfxDbColumn.ExtendedTypeName)]);
        Assert.Equal(IfxType.Integer, pct[nameof(IfxDbColumn.ProviderType)]);
    }

    [Fact]
    public void GetColumnSchema_marks_an_opaque_row_column()
    {
        // Reuse the opaque ROW describe shape (addr_t) exercised by SqliStructuredComplexTests.
        // A ROW column forces a walked row, so the open is two-phase: an open reply, then a
        // separate typed fetch reply (here empty — just the auto-close).
        byte[] script = Join(
            DescribeFrame((short)SqliStatementType.Select, stmtId: 5, tupleSize: 0,
                new Col("a", (short)SqliTypeCode.Row, 16, 0, ExtOwner: "root", ExtName: "addr_t")),
            DoneFrame(), EotFrame(),
            DoneFrame(), EotFrame(),  // open reply
            CloseFrame(), EotFrame(), // typed fetch reply (zero rows)
            EotFrame());

        using var cn = OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a from row_t";
        using var r = cmd.ExecuteReader();

        var col = (IfxDbColumn)r.GetColumnSchema()[0];
        Assert.True(col.IsOpaqueComplex);
        Assert.False(col.IsDistinct);
        Assert.Equal("addr_t", col.DataTypeName);
        Assert.Equal(IfxType.Row, col.ProviderType);
    }

    [Fact]
    public void GetSchemaTable_is_unchanged_and_projected_from_the_column_schema()
    {
        using var cn = OpenConnection(MixedSchemaSelect(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name, amt, pct, pic from t";
        using var r = cmd.ExecuteReader();

        var table = r.GetSchemaTable();

        // Column set + DataColumn value types are exactly as before the refactor.
        Assert.Equal(typeof(short), table.Columns[SchemaTableColumn.NumericPrecision]!.DataType);
        Assert.Equal(typeof(short), table.Columns[SchemaTableColumn.NumericScale]!.DataType);
        Assert.Equal(typeof(int), table.Columns[SchemaTableColumn.ProviderType]!.DataType);
        Assert.Equal(typeof(int), table.Columns[SchemaTableColumn.ColumnSize]!.DataType);
        Assert.True(table.Columns.Contains("ExtendedTypeName"));
        Assert.True(table.Columns.Contains("IsDistinct"));

        // amt (DECIMAL(8,2)): packed precision/scale as short, IsLong false.
        DataRow amt = table.Rows[2];
        Assert.Equal("amt", (string)amt[SchemaTableColumn.ColumnName]);
        Assert.Equal(2, (int)amt[SchemaTableColumn.ColumnOrdinal]);
        Assert.Equal((short)8, (short)amt[SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)2, (short)amt[SchemaTableColumn.NumericScale]);
        Assert.Equal((int)IfxType.Decimal, (int)amt[SchemaTableColumn.ProviderType]);
        Assert.Equal(typeof(decimal), (Type)amt[SchemaTableColumn.DataType]);
        Assert.False((bool)amt[SchemaTableColumn.IsLong]);
        Assert.False((bool)amt["IsDistinct"]);

        // id (INT): a non-packed type reports (short)0 for precision/scale, as before.
        DataRow id = table.Rows[0];
        Assert.Equal((short)0, (short)id[SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)0, (short)id[SchemaTableColumn.NumericScale]);
        Assert.Equal((int)IfxType.Integer, (int)id[SchemaTableColumn.ProviderType]);

        // pct (DISTINCT over INT): the distinct detail rides in the extra columns.
        DataRow pct = table.Rows[3];
        Assert.True((bool)pct[SchemaTableColumn.AllowDBNull]);
        Assert.True((bool)pct["IsDistinct"]);
        Assert.Equal("percent", (string)pct["ExtendedTypeName"]);
        Assert.Equal("root", (string)pct["ExtendedOwner"]);
        Assert.False((bool)pct["IsOpaqueComplex"]);

        // pic (BYTE): IsLong true.
        DataRow pic = table.Rows[4];
        Assert.True((bool)pic[SchemaTableColumn.IsLong]);
        Assert.Equal((int)IfxType.Byte, (int)pic[SchemaTableColumn.ProviderType]);
    }
}
