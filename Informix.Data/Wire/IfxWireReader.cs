using System.Text;
using Informix.Data; // IfxException
using Informix.Data.Types;

namespace Informix.Data.Wire;

/// <summary>
/// Reads Informix wire primitives from a <see cref="Stream"/>.
///
/// <para>Variable-length fields (CHAR, DATETIME, INTERVAL, DECIMAL) are framed
/// as a 2-byte big-endian length followed by the payload, and are padded to an
/// even byte count — the trailing pad byte is consumed here.</para>
/// </summary>
internal sealed class IfxWireReader
{
    private readonly Stream _stream;
    // Reused for all fixed-size reads (largest is the 10-byte INT8) to avoid per-field allocations.
    private readonly byte[] _scratch = new byte[10];

    /// <summary>Database code set (DB_LOCALE) used to decode CHAR/VARCHAR text; defaults to ISO-8859-1.</summary>
    public Encoding DbEncoding { get; set; }

    public IfxWireReader(Stream stream, Encoding? dbEncoding = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        DbEncoding = dbEncoding ?? IfxWireConverter.DefaultEncoding;
    }

    /// <summary>Reads a single byte, or -1 at end of stream.</summary>
    public int Read() => _stream.ReadByte();

    // No end-of-stream check here: a closed socket yields (byte)-1 == 0xFF. Callers that must detect
    // a dropped connection use ReadFully/ReadInt16 etc., which throw EndOfStreamException.
    public byte ReadByte() => (byte)_stream.ReadByte();

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes or throws (mirrors <c>readFully</c>).</summary>
    public void ReadFully(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _stream.Read(buffer[total..]);
            if (n <= 0)
                throw new EndOfStreamException("Informix connection closed while reading.");
            total += n;
        }
    }

    /// <summary>Reads and discards <paramref name="count"/> bytes (mirrors <c>skipBytes</c>; works on non-seekable streams).</summary>
    public void Skip(int count)
    {
        Span<byte> buf = stackalloc byte[256];
        while (count > 0)
        {
            int n = _stream.Read(buf[..Math.Min(count, buf.Length)]);
            if (n <= 0)
                throw new EndOfStreamException("Informix connection closed while skipping.");
            count -= n;
        }
    }

    /// <summary>Reads <paramref name="count"/> bytes then discards the pad byte when the count is odd.</summary>
    public byte[] ReadPadded(int count)
    {
        var b = new byte[count];
        ReadFully(b);
        if ((count & 0x1) != 0)
            _stream.ReadByte();
        return b;
    }

    public short ReadInt16()
    {
        ReadFully(_scratch.AsSpan(0, 2));
        return IfxWireConverter.ReadInt16(_scratch);
    }

    public int ReadInt32()
    {
        ReadFully(_scratch.AsSpan(0, 4));
        return IfxWireConverter.ReadInt32(_scratch);
    }

    /// <summary>Reads an INT8 (10-byte Informix layout). Returns <see cref="IfxWireConverter.Int8Null"/> for NULL.</summary>
    public long ReadInt8()
    {
        ReadFully(_scratch.AsSpan(0, IfxWireConverter.Int8Size));
        return IfxWireConverter.ReadInt8(_scratch);
    }

    /// <summary>Reads a BIGINT (native 8-byte big-endian).</summary>
    public long ReadBigInt()
    {
        ReadFully(_scratch.AsSpan(0, 8));
        return IfxWireConverter.ReadBigInt(_scratch);
    }

    public float ReadReal()
    {
        ReadFully(_scratch.AsSpan(0, 4));
        return IfxWireConverter.ReadReal(_scratch);
    }

    public double ReadDouble()
    {
        ReadFully(_scratch.AsSpan(0, 8));
        return IfxWireConverter.ReadDouble(_scratch);
    }

    /// <summary>Reads a 4-byte DATE; <c>null</c> for the NULL sentinel.</summary>
    public DateTime? ReadDate()
    {
        ReadFully(_scratch.AsSpan(0, 4));
        return IfxWireConverter.ReadDate(_scratch);
    }

    /// <summary>Reads a length-prefixed CHAR/VARCHAR field and decodes it with <see cref="DbEncoding"/>.</summary>
    public string ReadChar()
    {
        short length = ReadInt16();
        if (length < 0)
            throw new IfxException("Negative length in CHAR field (-408).", -408);
        byte[] raw = ReadPadded(length);
        return IfxWireConverter.DecodeChar(raw, DbEncoding);
    }

    /// <summary>
    /// Reads a length-prefixed raw payload (used for DATETIME/INTERVAL/DECIMAL,
    /// whose bytes are later decoded by the packed-decimal engine). Port of
    /// <c>readRawDateTime</c>/<c>readDecimal</c>'s framing.
    /// </summary>
    public byte[] ReadVarLengthRaw()
    {
        short length = ReadInt16();
        if (length < 0)
            throw new IfxException("Negative length in variable-length field (-408).", -408);
        return ReadPadded(length);
    }

    /// <summary>Reads a packed DECIMAL/DATETIME/INTERVAL into the raw engine representation.</summary>
    public PackedValue ReadPackedValue(short qualifier, bool isDateTime = false) =>
        PackedValue.FromWire(ReadVarLengthRaw(), qualifier, isDateTime);

    /// <summary>Reads a DECIMAL/MONEY field; <c>null</c> for SQL NULL (port of <c>readDecimal</c>).</summary>
    public decimal? ReadDecimal(short precision)
    {
        var d = ReadPackedValue(precision);
        return d.IsNull ? null : d.ToDecimal();
    }

    /// <summary>Reads a DATETIME field; <c>null</c> for SQL NULL (port of <c>readDateTime</c>).</summary>
    public DateTime? ReadDateTime(short qualifier) =>
        ReadPackedValue(qualifier, isDateTime: true).ToDateTime();

    /// <summary>Reads an INTERVAL field; <c>null</c> for SQL NULL (port of <c>readInterval</c>).</summary>
    public IfxInterval? ReadInterval(short qualifier) =>
        ReadPackedValue(qualifier).ToInterval();

    public void Close() => _stream.Dispose();
}
