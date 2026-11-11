using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Informix.Data;

/// <summary>
/// Converts between raw BSON bytes (the wire form of an Informix <see cref="IfxType.Bson"/>
/// column/value) and JSON text, so a BSON value can be read and written as ordinary JSON
/// without a MongoDB driver dependency. <see cref="ToJson"/> / <see cref="ToJsonDocument"/>
/// are the read direction; <see cref="ToBson"/> is the write direction — named after ASP.NET's
/// <c>MediaTypeFormatter</c> family (e.g. <c>JsonMediaTypeFormatter</c>), which pairs a
/// <c>ReadFromStream</c>/<c>WriteToStream</c> the same way.
/// </summary>
/// <remarks>
/// <para>Self-contained: built on <see cref="System.Text.Json"/> only, no external BSON
/// package. Covers the BSON element types Informix's <c>genbson()</c>/<c>BSON</c> columns
/// actually produce — double, string, document, array, binary, ObjectId, boolean, UTC
/// datetime, null, regular expression, int32, timestamp, int64, min/max key — using the
/// MongoDB "Extended JSON" wrapper convention (<c>$oid</c>, <c>$date</c>, <c>$binary</c>,
/// <c>$regularExpression</c>, <c>$timestamp</c>, <c>$numberLong</c>/<c>$numberInt</c>/
/// <c>$numberDouble</c>, <c>$minKey</c>/<c>$maxKey</c>) for the values plain JSON cannot
/// represent losslessly, so <c>ToBson(ToJson(bson))</c> reproduces the original bytes.</para>
/// <para>Decimal128 and the deprecated/rare element types (Undefined, DBPointer,
/// JavaScript, JavaScript-with-scope, Symbol) are not supported: decoding one throws
/// <see cref="NotSupportedException"/> naming the element type, rather than silently
/// losing precision or fidelity.</para>
/// </remarks>
public static class BsonMediaTypeFormatter
{
    // ---- BSON element type bytes (canonical, from the BSON spec) ----
    private const byte TDouble = 0x01;
    private const byte TString = 0x02;
    private const byte TDocument = 0x03;
    private const byte TArray = 0x04;
    private const byte TBinary = 0x05;
    private const byte TUndefined = 0x06;
    private const byte TObjectId = 0x07;
    private const byte TBoolean = 0x08;
    private const byte TDateTime = 0x09;
    private const byte TNull = 0x0A;
    private const byte TRegex = 0x0B;
    private const byte TDbPointer = 0x0C;
    private const byte TJavaScript = 0x0D;
    private const byte TSymbol = 0x0E;
    private const byte TJavaScriptScope = 0x0F;
    private const byte TInt32 = 0x10;
    private const byte TTimestamp = 0x11;
    private const byte TInt64 = 0x12;
    private const byte TDecimal128 = 0x13;
    private const byte TMinKey = 0xFF;
    private const byte TMaxKey = 0x7F;

    /// <summary>Decodes a BSON document to JSON text.</summary>
    public static string ToJson(ReadOnlySpan<byte> bson, bool writeIndented = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteUtf8Json(bson, buffer, writeIndented);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Decodes a BSON document straight to a parsed <see cref="JsonDocument"/>: one streaming
    /// BSON → UTF-8 JSON transcode followed by one parse, with no intermediate UTF-16 string
    /// or DOM tree. (A <see cref="JsonDocument"/> is by design a parse-only view over UTF-8
    /// JSON text, so the text must be materialized exactly once — this is the minimal
    /// pipeline.) The returned document is the caller's to dispose.
    /// </summary>
    public static JsonDocument ToJsonDocument(ReadOnlySpan<byte> bson)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteUtf8Json(bson, buffer, writeIndented: false);
        // Parse(ReadOnlyMemory) references the buffer for the document's lifetime; the
        // ArrayBufferWriter's array is unshared from here on, so handing it over is safe.
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    // The one BSON decoder: a streaming walk of the BSON bytes emitted through a
    // Utf8JsonWriter. Both ToJson and ToJsonDocument sit on top of it.
    private static void WriteUtf8Json(ReadOnlySpan<byte> bson, ArrayBufferWriter<byte> destination, bool writeIndented)
    {
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = writeIndented });
        new BsonReader(bson.ToArray()).WriteDocument(writer);
        writer.Flush();
    }

    /// <summary>Encodes JSON text to a BSON document. The root must be a JSON object.</summary>
    public static byte[] ToBson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (JsonNode.Parse(json) is not JsonObject obj)
            throw new ArgumentException("A BSON document must be encoded from a JSON object at the root.", nameof(json));
        var writer = new BsonWriter();
        writer.WriteDocument(obj);
        return writer.ToArray();
    }

    private static NotSupportedException Unsupported(string what) =>
        new($"BsonMediaTypeFormatter does not support the BSON {what} element type.");

    // ===================================================================== //
    //  Decode: BSON bytes -> UTF-8 JSON (one streaming pass)                //
    // ===================================================================== //

    private sealed class BsonReader(byte[] buf)
    {
        private int _pos;

        public void WriteDocument(Utf8JsonWriter w)
        {
            _ = ReadInt32(); // total length — the 0x00 terminator ends the loop, so unused here
            w.WriteStartObject();
            while (true)
            {
                byte type = buf[_pos++];
                if (type == 0)
                    break;
                w.WritePropertyName(ReadCString());
                WriteValue(type, w);
            }
            w.WriteEndObject();
        }

        private void WriteArray(Utf8JsonWriter w)
        {
            _ = ReadInt32();
            w.WriteStartArray();
            while (true)
            {
                byte type = buf[_pos++];
                if (type == 0)
                    break;
                _ = ReadCString(); // "0","1",... index key — array order is positional, key is discarded
                WriteValue(type, w);
            }
            w.WriteEndArray();
        }

        private void WriteValue(byte type, Utf8JsonWriter w)
        {
            switch (type)
            {
                case TDouble: w.WriteNumberValue(ReadDouble()); break;
                case TString: w.WriteStringValue(ReadString()); break;
                case TDocument: WriteDocument(w); break;
                case TArray: WriteArray(w); break;
                case TBinary: WriteBinary(w); break;
                case TObjectId: WriteObjectId(w); break;
                case TBoolean: w.WriteBooleanValue(buf[_pos++] != 0); break;
                case TDateTime: WriteDateTime(w); break;
                case TNull: w.WriteNullValue(); break;
                case TRegex: WriteRegex(w); break;
                case TInt32: w.WriteNumberValue(ReadInt32()); break;
                case TTimestamp: WriteTimestamp(w); break;
                case TInt64: w.WriteNumberValue(ReadInt64()); break;
                case TMinKey: WriteMinMaxKey(w, min: true); break;
                case TMaxKey: WriteMinMaxKey(w, min: false); break;
                case TUndefined: throw Unsupported("Undefined (0x06)");
                case TDbPointer: throw Unsupported("DBPointer (0x0C)");
                case TJavaScript: throw Unsupported("JavaScript (0x0D)");
                case TSymbol: throw Unsupported("Symbol (0x0E)");
                case TJavaScriptScope: throw Unsupported("JavaScript-with-scope (0x0F)");
                case TDecimal128: throw Unsupported("Decimal128 (0x13)");
                default: throw Unsupported($"unknown (0x{type:X2})");
            }
        }

        private static void WriteMinMaxKey(Utf8JsonWriter w, bool min)
        {
            w.WriteStartObject();
            w.WriteNumber(min ? "$minKey" : "$maxKey", 1);
            w.WriteEndObject();
        }

        private void WriteBinary(Utf8JsonWriter w)
        {
            int len = ReadInt32();
            byte subType = buf[_pos++];
            ReadOnlySpan<byte> data = buf.AsSpan(_pos, len);
            _pos += len;
            w.WriteStartObject();
            w.WritePropertyName("$binary");
            w.WriteStartObject();
            w.WriteString("base64", Convert.ToBase64String(data));
            w.WriteString("subType", subType.ToString("x2", CultureInfo.InvariantCulture));
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private void WriteObjectId(Utf8JsonWriter w)
        {
            ReadOnlySpan<byte> id = buf.AsSpan(_pos, 12);
            _pos += 12;
            w.WriteStartObject();
            w.WriteString("$oid", Convert.ToHexString(id).ToLowerInvariant());
            w.WriteEndObject();
        }

        private void WriteDateTime(Utf8JsonWriter w)
        {
            long ms = ReadInt64();
            var dto = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            w.WriteStartObject();
            w.WriteString("$date", dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        private void WriteRegex(Utf8JsonWriter w)
        {
            string pattern = ReadCString();
            string options = ReadCString();
            w.WriteStartObject();
            w.WritePropertyName("$regularExpression");
            w.WriteStartObject();
            w.WriteString("pattern", pattern);
            w.WriteString("options", options);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private void WriteTimestamp(Utf8JsonWriter w)
        {
            uint increment = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(_pos, 4));
            uint seconds = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(_pos + 4, 4));
            _pos += 8;
            w.WriteStartObject();
            w.WritePropertyName("$timestamp");
            w.WriteStartObject();
            w.WriteNumber("t", seconds);
            w.WriteNumber("i", increment);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private int ReadInt32()
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        private long ReadInt64()
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(_pos, 8));
            _pos += 8;
            return v;
        }

        private double ReadDouble()
        {
            double v = BinaryPrimitives.ReadDoubleLittleEndian(buf.AsSpan(_pos, 8));
            _pos += 8;
            return v;
        }

        // A BSON "string": [len:int32 incl. trailing NUL][utf8 bytes][NUL].
        private string ReadString()
        {
            int len = ReadInt32();
            string s = Encoding.UTF8.GetString(buf, _pos, len - 1);
            _pos += len;
            return s;
        }

        // A BSON "cstring" (element names, regex parts): utf8 bytes terminated by NUL, no length prefix.
        private string ReadCString()
        {
            int start = _pos;
            while (buf[_pos] != 0)
                _pos++;
            string s = Encoding.UTF8.GetString(buf, start, _pos - start);
            _pos++; // skip the NUL
            return s;
        }
    }

    // ===================================================================== //
    //  Encode: JsonNode -> BSON bytes                                       //
    // ===================================================================== //

    private sealed class BsonWriter
    {
        private readonly MemoryStream _ms = new();

        public byte[] ToArray() => _ms.ToArray();

        public void WriteDocument(JsonObject obj)
        {
            long lenPos = _ms.Position;
            WriteInt32(0); // placeholder, patched below
            foreach (var (key, value) in obj)
                WriteElement(key, value);
            _ms.WriteByte(0); // terminator
            PatchLength(lenPos);
        }

        private void WriteArrayAsDocument(JsonArray arr)
        {
            long lenPos = _ms.Position;
            WriteInt32(0);
            for (int i = 0; i < arr.Count; i++)
                WriteElement(i.ToString(CultureInfo.InvariantCulture), arr[i]);
            _ms.WriteByte(0);
            PatchLength(lenPos);
        }

        private void PatchLength(long lenPos)
        {
            long end = _ms.Position;
            int len = checked((int)(end - lenPos));
            long save = _ms.Position;
            _ms.Position = lenPos;
            WriteInt32(len);
            _ms.Position = save;
        }

        private void WriteElement(string name, JsonNode? value)
        {
            if (value is JsonObject wrapper && TryWriteWrapperElement(name, wrapper))
                return;
            switch (value)
            {
                case null:
                    _ms.WriteByte(TNull);
                    WriteCString(name);
                    break;
                case JsonObject o:
                    _ms.WriteByte(TDocument);
                    WriteCString(name);
                    WriteDocument(o);
                    break;
                case JsonArray a:
                    _ms.WriteByte(TArray);
                    WriteCString(name);
                    WriteArrayAsDocument(a);
                    break;
                case JsonValue v:
                    WriteScalarElement(name, v);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported JSON node kind for BSON encoding: {value.GetType()}.");
            }
        }

        // Recognizes a single-key "$..." Extended-JSON wrapper object and writes it as
        // the scalar BSON value it represents. Returns false (write nothing) for any
        // other object, including a genuine one-property document like {"a":1}.
        private bool TryWriteWrapperElement(string name, JsonObject o)
        {
            if (o.Count != 1)
                return false;
            var (key, val) = o.First();
            switch (key)
            {
                case "$oid" when val is JsonValue oid && oid.TryGetValue(out string? hex):
                    if (hex!.Length != 24)
                        throw new ArgumentException($"$oid must be 24 hex characters, got {hex.Length}.");
                    _ms.WriteByte(TObjectId);
                    WriteCString(name);
                    _ms.Write(Convert.FromHexString(hex));
                    return true;

                case "$date" when val is JsonValue date:
                    _ms.WriteByte(TDateTime);
                    WriteCString(name);
                    WriteInt64(ReadDateMillis(date));
                    return true;

                case "$binary" when val is JsonObject bin:
                    _ms.WriteByte(TBinary);
                    WriteCString(name);
                    WriteBinaryValue(bin);
                    return true;

                case "$regularExpression" when val is JsonObject re:
                    _ms.WriteByte(TRegex);
                    WriteCString(name);
                    WriteCString(re["pattern"]?.GetValue<string>() ?? "");
                    WriteCString(re["options"]?.GetValue<string>() ?? "");
                    return true;

                case "$timestamp" when val is JsonObject ts:
                    _ms.WriteByte(TTimestamp);
                    WriteCString(name);
                    WriteUInt32(ts["i"]?.GetValue<uint>() ?? 0);
                    WriteUInt32(ts["t"]?.GetValue<uint>() ?? 0);
                    return true;

                case "$numberLong" when val is JsonValue nl && nl.TryGetValue(out string? ls):
                    _ms.WriteByte(TInt64);
                    WriteCString(name);
                    WriteInt64(long.Parse(ls!, CultureInfo.InvariantCulture));
                    return true;

                case "$numberInt" when val is JsonValue ni && ni.TryGetValue(out string? is32):
                    _ms.WriteByte(TInt32);
                    WriteCString(name);
                    WriteInt32(int.Parse(is32!, CultureInfo.InvariantCulture));
                    return true;

                case "$numberDouble" when val is JsonValue nd && nd.TryGetValue(out string? ds):
                    _ms.WriteByte(TDouble);
                    WriteCString(name);
                    WriteDouble(double.Parse(ds!, CultureInfo.InvariantCulture));
                    return true;

                case "$minKey":
                    _ms.WriteByte(TMinKey);
                    WriteCString(name);
                    return true;

                case "$maxKey":
                    _ms.WriteByte(TMaxKey);
                    WriteCString(name);
                    return true;

                default:
                    return false; // not a recognized wrapper — encode as a plain nested document
            }
        }

        private static long ReadDateMillis(JsonValue date)
        {
            if (date.TryGetValue(out long ms))
                return ms;
            if (date.TryGetValue(out string? iso) &&
                DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.ToUnixTimeMilliseconds();
            throw new ArgumentException($"Cannot parse $date value '{date.ToJsonString()}'.");
        }

        private void WriteBinaryValue(JsonObject bin)
        {
            string base64 = bin["base64"]?.GetValue<string>()
                ?? throw new ArgumentException("$binary requires a 'base64' field.");
            byte[] data = Convert.FromBase64String(base64);
            byte subType = 0;
            if (bin["subType"] is JsonValue st && st.TryGetValue(out string? sts))
                subType = Convert.ToByte(sts, 16);
            WriteInt32(data.Length);
            _ms.WriteByte(subType);
            _ms.Write(data);
        }

        // A plain (non-wrapped) JSON scalar: bool/string pass through directly; a number
        // is inferred as Int32/Int64/Double from its literal shape (no '.'/exponent =>
        // integer; narrowest of Int32/Int64 that fits), matching MongoDB's relaxed
        // Extended JSON convention for un-annotated numbers.
        private void WriteScalarElement(string name, JsonValue v)
        {
            if (v.TryGetValue(out bool b))
            {
                _ms.WriteByte(TBoolean);
                WriteCString(name);
                _ms.WriteByte((byte)(b ? 1 : 0));
                return;
            }
            if (v.TryGetValue(out string? s))
            {
                _ms.WriteByte(TString);
                WriteCString(name);
                WriteBsonString(s!);
                return;
            }
            if (v.GetValueKind() == JsonValueKind.Number)
            {
                string raw = v.ToJsonString();
                bool isIntegral = raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0;
                if (isIntegral && v.TryGetValue(out long l))
                {
                    if (l is >= int.MinValue and <= int.MaxValue)
                    {
                        _ms.WriteByte(TInt32);
                        WriteCString(name);
                        WriteInt32((int)l);
                    }
                    else
                    {
                        _ms.WriteByte(TInt64);
                        WriteCString(name);
                        WriteInt64(l);
                    }
                    return;
                }
                if (v.TryGetValue(out double d))
                {
                    _ms.WriteByte(TDouble);
                    WriteCString(name);
                    WriteDouble(d);
                    return;
                }
            }
            throw new NotSupportedException($"Cannot encode JSON value '{v.ToJsonString()}' as a BSON scalar.");
        }

        private void WriteBsonString(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            WriteInt32(utf8.Length + 1);
            _ms.Write(utf8);
            _ms.WriteByte(0);
        }

        private void WriteCString(string s)
        {
            _ms.Write(Encoding.UTF8.GetBytes(s));
            _ms.WriteByte(0);
        }

        private void WriteInt32(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        private void WriteUInt32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        private void WriteInt64(long v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, v);
            _ms.Write(b);
        }

        private void WriteDouble(double v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(b, v);
            _ms.Write(b);
        }
    }
}
