using System.Buffers.Binary;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="BsonMediaTypeFormatter"/> BSON&lt;-&gt;JSON codec. The first test uses the
/// exact bytes captured live from <c>genbson(row(manu_code),0,1)</c> against IDS 9.59.FC5
/// (2026-07-12) as ground truth — the same payload that exposed the JSON/BSON decode bug.
/// </summary>
public class BsonMediaTypeFormatterTests
{
    // select genbson(row(manu_code),0,1) from catalog where manu_code='HRO'
    private static readonly byte[] CapturedManuCodeBson =
    [
        0x18, 0x00, 0x00, 0x00, 0x02, 0x6d, 0x61, 0x6e, 0x75, 0x5f, 0x63, 0x6f, 0x64, 0x65, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x48, 0x52, 0x4f, 0x00, 0x00,
    ];

    [Fact]
    public void Decodes_the_live_captured_genbson_document()
    {
        string json = BsonMediaTypeFormatter.ToJson(CapturedManuCodeBson);
        Assert.Equal("{\"manu_code\":\"HRO\"}", json);
    }

    [Fact]
    public void ToJsonDocument_parses_directly_and_matches_the_ToJson_text()
    {
        // The direct BSON -> JsonDocument path (streaming UTF-8 transcode + parse, no
        // intermediate string) must produce exactly the text ToJson produces — both sit
        // on the same Utf8JsonWriter walk, so GetRawText() is byte-identical.
        using System.Text.Json.JsonDocument doc = BsonMediaTypeFormatter.ToJsonDocument(CapturedManuCodeBson);
        Assert.Equal("HRO", doc.RootElement.GetProperty("manu_code").GetString());
        Assert.Equal(BsonMediaTypeFormatter.ToJson(CapturedManuCodeBson), doc.RootElement.GetRawText());
    }

    [Fact]
    public void Round_trips_the_live_captured_document_byte_for_byte()
    {
        string json = BsonMediaTypeFormatter.ToJson(CapturedManuCodeBson);
        byte[] reencoded = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(CapturedManuCodeBson, reencoded);
    }

    [Fact]
    public void Empty_document_round_trips()
    {
        byte[] empty = [5, 0, 0, 0, 0]; // int32 len=5 + terminator, no elements
        Assert.Equal("{}", BsonMediaTypeFormatter.ToJson(empty));
        Assert.Equal(empty, BsonMediaTypeFormatter.ToBson("{}"));
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("{\"a\":-7}")]
    [InlineData("{\"a\":true,\"b\":false}")]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":1.5}")]
    [InlineData("{\"a\":\"hello world\"}")]
    [InlineData("{\"a\":{\"b\":{\"c\":1}}}")]
    [InlineData("{\"a\":[1,2,3]}")]
    [InlineData("{\"a\":[]}")]
    [InlineData("{\"a\":[\"x\",\"y\"]}")]
    public void Scalar_and_container_shapes_round_trip_through_json(string json)
    {
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        string decoded = BsonMediaTypeFormatter.ToJson(bson);
        byte[] reencoded = BsonMediaTypeFormatter.ToBson(decoded);
        Assert.Equal(bson, reencoded); // second pass must be a fixed point
    }

    [Fact]
    public void Int32_boundary_stays_int32_and_int64_boundary_promotes()
    {
        // int.MaxValue fits Int32; int.MaxValue+1 must promote to Int64 on the wire —
        // verified by checking the encoded element's type byte directly.
        byte[] asInt32 = BsonMediaTypeFormatter.ToBson("{\"a\":2147483647}");
        Assert.Equal(0x10, asInt32[4]); // BSON int32 element type

        byte[] asInt64 = BsonMediaTypeFormatter.ToBson("{\"a\":2147483648}");
        Assert.Equal(0x12, asInt64[4]); // BSON int64 element type
    }

    [Fact]
    public void Large_long_round_trips_without_losing_precision()
    {
        const string json = "{\"a\":9999999999}"; // exceeds int32 and exceeds double's exact-integer range boundary style value
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal("{\"a\":9999999999}", BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void ObjectId_round_trips_via_the_dollar_oid_wrapper()
    {
        const string json = "{\"id\":{\"$oid\":\"507f1f77bcf86cd799439011\"}}";
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(0x07, bson[4]); // ObjectId element type
        Assert.Equal(json, BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void ObjectId_wrong_length_throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => BsonMediaTypeFormatter.ToBson("{\"id\":{\"$oid\":\"deadbeef\"}}"));
        Assert.Contains("24 hex characters", ex.Message);
    }

    [Fact]
    public void UtcDateTime_round_trips_via_the_dollar_date_wrapper()
    {
        const string json = "{\"when\":{\"$date\":\"2024-01-02T03:04:05.678Z\"}}";
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(0x09, bson[4]); // UTC datetime element type
        Assert.Equal(json, BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void Binary_round_trips_via_the_dollar_binary_wrapper()
    {
        const string json = "{\"bin\":{\"$binary\":{\"base64\":\"AQIDBA==\",\"subType\":\"00\"}}}";
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(0x05, bson[4]); // Binary element type
        Assert.Equal(json, BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void MinKey_and_MaxKey_round_trip()
    {
        const string json = "{\"lo\":{\"$minKey\":1},\"hi\":{\"$maxKey\":1}}";
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(json, BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void Timestamp_round_trips_via_the_dollar_timestamp_wrapper()
    {
        const string json = "{\"ts\":{\"$timestamp\":{\"t\":1700000000,\"i\":3}}}";
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(0x11, bson[4]); // Timestamp element type
        Assert.Equal(json, BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void RegularExpression_round_trips()
    {
        const string json = "{\"re\":{\"$regularExpression\":{\"pattern\":\"^a.*z$\",\"options\":\"i\"}}}";
        byte[] bson = BsonMediaTypeFormatter.ToBson(json);
        Assert.Equal(json, BsonMediaTypeFormatter.ToJson(bson));
    }

    [Fact]
    public void ToBson_requires_a_json_object_at_the_root()
    {
        Assert.Throws<ArgumentException>(() => BsonMediaTypeFormatter.ToBson("[1,2,3]"));
        Assert.Throws<ArgumentException>(() => BsonMediaTypeFormatter.ToBson("\"just a string\""));
    }

    [Theory]
    [InlineData(0x06)] // Undefined
    [InlineData(0x0C)] // DBPointer
    [InlineData(0x0D)] // JavaScript
    [InlineData(0x0E)] // Symbol
    [InlineData(0x0F)] // JavaScript with scope
    [InlineData(0x13)] // Decimal128
    public void Unsupported_element_types_throw_NotSupportedException(byte elementType)
    {
        // A minimal document containing one element of the unsupported type: a 1-char
        // name "a" then whatever tail bytes ReadValue would try to consume — for the
        // purpose of this test we only need the dispatch to be reached before it throws,
        // so 16 zero bytes is enough padding for any of these fixed/variable readers to
        // not run off the end before the throw happens for the fixed-size ones; the
        // variable-length ones (String-shaped: 0x0C-0x0F) throw immediately on dispatch,
        // before reading their body, since Unsupported() is thrown directly from the switch.
        byte[] body = [elementType, (byte)'a', 0, .. new byte[16]];
        byte[] doc = new byte[4 + body.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(doc.AsSpan(0, 4), doc.Length);
        body.CopyTo(doc, 4);
        // doc[^1] stays 0 (terminator) but is never reached — the throw happens first.

        var ex = Assert.Throws<NotSupportedException>(() => BsonMediaTypeFormatter.ToJson(doc));
        Assert.Contains("BsonMediaTypeFormatter does not support", ex.Message);
    }
}
