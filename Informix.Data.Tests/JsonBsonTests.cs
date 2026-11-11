using System.Text.Json;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// JSON/BSON built-in columns (extended-type ids JSON(25)/BSON(26) on a UdtVar/UdtFixed
/// descriptor). Both travel inline with the LVARCHAR-family <c>[flag:1][len:int32][data]</c>
/// framing — the fix for the bug where they decoded to <see cref="DBNull"/> (the framing was
/// mistaken for the flagless SENDRECV/complex-type shape). The BSON payload in
/// <see cref="CapturedManuCodeBson"/> is the exact bytes captured live from
/// <c>genbson(row(manu_code),0,1)</c> against IDS 9.59.FC5 (2026-07-12).
/// </summary>
public class JsonBsonTests
{
    // select genbson(row(manu_code),0,1) from catalog where manu_code='HRO'
    private static readonly byte[] CapturedManuCodeBson =
    [
        0x18, 0x00, 0x00, 0x00, 0x02, 0x6d, 0x61, 0x6e, 0x75, 0x5f, 0x63, 0x6f, 0x64, 0x65, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x48, 0x52, 0x4f, 0x00, 0x00,
    ];

    // A JSON/BSON column describes as UdtVar/UdtFixed(40/41) with extended id 25/26 and
    // owner/name "informix"/"json"|"bson" — the same shape LVARCHAR/CLOB/BLOB use.
    private static SqliStatementKit.Col JsonCol(string name, int startPos) =>
        new(name, (short)SqliTypeCode.UdtVar, 0, startPos, ExtendedId: 25, ExtOwner: "informix", ExtName: "json");

    private static SqliStatementKit.Col BsonCol(string name, int startPos) =>
        new(name, (short)SqliTypeCode.UdtFixed, 0, startPos, ExtendedId: 26, ExtOwner: "informix", ExtName: "bson");

    // Two-phase select script (open reply, then the typed-fetch reply) — required because
    // JSON/BSON are walked (variable-length) columns, exactly like ROW/collection/LVARCHAR.
    private static byte[] SelectScript(SqliStatementKit.Col col, byte[] fieldBytes) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 0, col),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.TupleFrame(fieldBytes),
        SqliStatementKit.CloseFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    // Field framing is [flag][len:int32][data], like LVARCHAR; GetDataTypeName still reports "Json".
    [Fact]
    public void Json_column_decodes_to_a_string()
    {
        byte[] payload = IfxWireConverter.EncodeChar("{\"manu_code\":\"HRO\"}");
        byte[] field = SqliStatementKit.SmartBlobField(payload); // [flag:1][len:int32][data]

        using var cn = SqliStatementKit.OpenConnection(SelectScript(JsonCol("doc", 0), field), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select doc from catalog";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("Json", reader.GetDataTypeName(0));
        Assert.True(reader.Read());
        Assert.Equal("{\"manu_code\":\"HRO\"}", reader.GetValue(0));
        Assert.Equal("{\"manu_code\":\"HRO\"}", reader.GetString(0));
        Assert.False(reader.Read());
    }

    // The BSON document is converted to JSON text so portable code (GetString/GetValue) works; the type name stays "Bson".
    [Fact]
    public void Bson_column_decodes_to_a_json_string_through_the_generic_facade()
    {
        // The convention-conforming shape: a BSON
        // column reads as a JSON-text string; GetDataTypeName still reports the real type.
        byte[] field = SqliStatementKit.SmartBlobField(CapturedManuCodeBson);

        using var cn = SqliStatementKit.OpenConnection(SelectScript(BsonCol("doc", 0), field), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select doc from catalog";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("Bson", reader.GetDataTypeName(0));
        Assert.True(reader.Read());
        Assert.Equal("{\"manu_code\":\"HRO\"}", reader.GetValue(0));
        Assert.Equal("{\"manu_code\":\"HRO\"}", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Bson_raw_bytes_stay_reachable_via_GetBytes_and_GetFieldValue_byte_array()
    {
        byte[] field = SqliStatementKit.SmartBlobField(CapturedManuCodeBson);

        using var cn = SqliStatementKit.OpenConnection(SelectScript(BsonCol("doc", 0), field), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select doc from catalog";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        // GetBytes escape hatch: length query then full read.
        long n = reader.GetBytes(0, 0, null, 0, 0);
        Assert.Equal(CapturedManuCodeBson.Length, n);
        byte[] raw = new byte[n];
        reader.GetBytes(0, 0, raw, 0, (int)n);
        Assert.Equal(CapturedManuCodeBson, raw);

        // GetFieldValue<byte[]> yields the same raw BSON.
        Assert.Equal(CapturedManuCodeBson, reader.GetFieldValue<byte[]>(0));
    }

    // For BSON the document is built straight from the raw bytes.
    [Fact]
    public void GetFieldValue_JsonDocument_and_JsonElement_work_for_json_and_bson()
    {
        // JSON column
        byte[] jsonField = SqliStatementKit.SmartBlobField(IfxWireConverter.EncodeChar("{\"manu_code\":\"HRO\",\"qty\":5}"));
        using (var cn = SqliStatementKit.OpenConnection(SelectScript(JsonCol("doc", 0), jsonField), out _))
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "select doc from catalog";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            using JsonDocument doc = reader.GetFieldValue<JsonDocument>(0);
            Assert.Equal("HRO", doc.RootElement.GetProperty("manu_code").GetString());
            Assert.Equal(5, doc.RootElement.GetProperty("qty").GetInt32());
        }

        // BSON column (decoded to JSON first, then parsed)
        byte[] bsonField = SqliStatementKit.SmartBlobField(CapturedManuCodeBson);
        using (var cn = SqliStatementKit.OpenConnection(SelectScript(BsonCol("doc", 0), bsonField), out _))
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "select doc from catalog";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            JsonElement el = reader.GetFieldValue<JsonElement>(0);
            Assert.Equal("HRO", el.GetProperty("manu_code").GetString());
        }
    }

    [Fact]
    public void Null_json_and_bson_columns_decode_to_DBNull()
    {
        byte[] nullField = SqliStatementKit.SmartBlobField(null); // [1,0,0,0,0]

        using var cnJson = SqliStatementKit.OpenConnection(SelectScript(JsonCol("doc", 0), nullField), out _);
        using var cmdJson = cnJson.CreateCommand();
        cmdJson.CommandText = "select doc from catalog";
        using var rJson = cmdJson.ExecuteReader();
        Assert.True(rJson.Read());
        Assert.True(rJson.IsDBNull(0));

        using var cnBson = SqliStatementKit.OpenConnection(SelectScript(BsonCol("doc", 0), nullField), out _);
        using var cmdBson = cnBson.CreateCommand();
        cmdBson.CommandText = "select doc from catalog";
        using var rBson = cmdBson.ExecuteReader();
        Assert.True(rBson.Read());
        Assert.True(rBson.IsDBNull(0));
        // GetFieldValue<JsonDocument> on a NULL cell throws (ADO.NET contract; check IsDBNull first).
        Assert.Throws<InvalidCastException>(() => rBson.GetFieldValue<JsonDocument>(0));
    }

    // Asking for a JsonDocument of an INT column is a type error.
    [Fact]
    public void GetFieldValue_JsonDocument_throws_InvalidCastException_for_a_non_json_column()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame([0, 0, 0, 42]),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from t";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<JsonDocument>(0));
    }

    // ---- write / bind path ----

    private static byte[] InsertScript() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    // Builds the expected SQ_BIND bytes for a non-null opaque UDT bind: type SENDRECV(44) +
    // owner + name + indicator(0) + precision(payload.Length) + [len:int32][payload].
    private static byte[] ExpectedUdtBind(string name, byte[] payload)
    {
        var b = new List<byte> { 0, 44 };
        b.AddRange([0, 8]);
        b.AddRange(IfxWireConverter.EncodeChar("informix"));
        b.AddRange([0, (byte)name.Length]);
        b.AddRange(IfxWireConverter.EncodeChar(name));
        b.AddRange([0, 0]); // indicator = 0 (not null)
        b.AddRange([(byte)(payload.Length >> 8), (byte)payload.Length]); // precision, big-endian int16
        b.AddRange([0, 0, 0, (byte)payload.Length]); // data length prefix, big-endian int32
        b.AddRange(payload);
        return b.ToArray();
    }

    // A NULL parameter keeps the type name but has indicator -1 and no data.
    private static byte[] ExpectedUdtNullBind(string name)
    {
        var b = new List<byte> { 0, 44 };
        b.AddRange([0, 8]);
        b.AddRange(IfxWireConverter.EncodeChar("informix"));
        b.AddRange([0, (byte)name.Length]);
        b.AddRange(IfxWireConverter.EncodeChar(name));
        b.AddRange([0xFF, 0xFF, 0, 0]); // indicator = -1 (NULL), precision = 0
        return b.ToArray();
    }

    // The JSON string is encoded to BSON by the driver; the payload equals the document captured from a real server.
    [Fact]
    public void Bson_parameter_from_a_json_string_auto_encodes_and_binds_the_bson_bytes()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into catalog values (?)";
        cmd.Parameters.Add(new IfxParameter("doc", "{\"manu_code\":\"HRO\"}") { IfxType = IfxType.Bson });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        byte[] expectedPayload = BsonMediaTypeFormatter.ToBson("{\"manu_code\":\"HRO\"}");
        Assert.Equal(CapturedManuCodeBson, expectedPayload); // sanity: matches the live-captured ground truth
        byte[] expected = ExpectedUdtBind("bson", expectedPayload);
        Assert.Contains(Convert.ToHexString(expected), Convert.ToHexString(sent));
    }

    // An already encoded byte[] is sent untouched.
    [Fact]
    public void Bson_parameter_from_raw_bytes_binds_them_directly_without_json_conversion()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into catalog values (?)";
        cmd.Parameters.Add(new IfxParameter("doc", CapturedManuCodeBson) { IfxType = IfxType.Bson });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        byte[] expected = ExpectedUdtBind("bson", CapturedManuCodeBson);
        Assert.Contains(Convert.ToHexString(expected), Convert.ToHexString(sent));
    }

    [Fact]
    public void Json_parameter_binds_utf8_text_bytes_under_the_json_extended_name()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into catalog values (?)";
        cmd.Parameters.Add(new IfxParameter("doc", "{\"a\":1}") { IfxType = IfxType.Json });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        byte[] expected = ExpectedUdtBind("json", System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"));
        Assert.Contains(Convert.ToHexString(expected), Convert.ToHexString(sent));
    }

    [Fact]
    public void Null_bson_parameter_binds_a_typed_null_without_encoding()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into catalog values (?)";
        cmd.Parameters.Add(new IfxParameter("doc", null) { IfxType = IfxType.Bson });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        byte[] expected = ExpectedUdtNullBind("bson");
        Assert.Contains(Convert.ToHexString(expected), Convert.ToHexString(sent));
    }

    // The payload travels inline with an int16 precision, so it is limited to 32 767 bytes; the check happens before any byte is written.
    [Fact]
    public void Oversized_bson_payload_throws_before_sending_anything()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into catalog values (?)";
        // A raw byte[] payload larger than the driver's int16 precision field can carry.
        cmd.Parameters.Add(new IfxParameter("doc", new byte[short.MaxValue + 1]) { IfxType = IfxType.Bson });

        var ex = Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("exceeds the driver's inline bind limit", ex.Message);
        Assert.Equal(before, duplex.Output.Length); // nothing was written to the wire
    }

    [Fact]
    public void Bson_parameter_with_an_unsupported_value_type_throws_NotSupportedException()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into catalog values (?)";
        cmd.Parameters.Add(new IfxParameter("doc", 42) { IfxType = IfxType.Bson });

        Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
    }
}
