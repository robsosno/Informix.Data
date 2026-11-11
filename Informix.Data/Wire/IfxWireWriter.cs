using System.Text;
using Informix.Data.Types;

namespace Informix.Data.Wire;

/// <summary>
/// Writes Informix wire primitives to a <see cref="Stream"/>.
///
/// <para><see cref="Written"/> tracks the running byte count and
/// <see cref="SetByteCountAtEot"/>/<see cref="BytesSentSinceLastEot"/> reproduce
/// the EOT accounting the SQLI layer relies on to size messages.</para>
/// </summary>
internal sealed class IfxWireWriter
{
    private readonly Stream _stream;
    // Value of Written at the previous SQ_EOT; BytesSentSinceLastEot is the size of the PDU being built.
    private int _byteCountAtEot;

    /// <summary>Database code set (DB_LOCALE) used to encode CHAR/VARCHAR text; defaults to ISO-8859-1.</summary>
    public Encoding DbEncoding { get; set; }

    /// <summary>Total bytes written since construction.</summary>
    public int Written { get; private set; }

    public IfxWireWriter(Stream stream, Encoding? dbEncoding = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        DbEncoding = dbEncoding ?? IfxWireConverter.DefaultEncoding;
    }

    public void Write(byte value)
    {
        _stream.WriteByte(value);
        Written++;
    }

    public void Write(ReadOnlySpan<byte> b)
    {
        _stream.Write(b);
        Written += b.Length;
    }

    /// <summary>Writes the payload then a pad byte when its length is odd (mirrors <c>writePadded</c>).</summary>
    public void WritePadded(ReadOnlySpan<byte> b)
    {
        Write(b);
        if ((b.Length & 0x1) != 0)
            Write(0);
    }

    public void WriteInt16(short value) => Write(IfxWireConverter.ToInt16(value));

    public void WriteInt32(int value) => Write(IfxWireConverter.ToInt32(value));

    public void WriteInt8(long value) => Write(IfxWireConverter.ToInt8(value));

    public void WriteBigInt(long value) => Write(IfxWireConverter.ToBigInt(value));

    // Fixed-width, so WritePadded never actually adds a pad byte (4 and 8 bytes are even).
    public void WriteReal(float value) => WritePadded(IfxWireConverter.ToReal(value));

    public void WriteDouble(double value) => WritePadded(IfxWireConverter.ToDouble(value));

    public void WriteDate(DateTime value) => Write(IfxWireConverter.ToDate(value));

    /// <summary>Writes a length-prefixed CHAR field: a 2-byte length, the encoded bytes, then an even-alignment pad.</summary>
    public void WriteChar(string value)
    {
        byte[] data = IfxWireConverter.EncodeChar(value, DbEncoding);
        WriteInt16((short)data.Length);
        Write(data);
        if ((data.Length & 0x1) != 0)
            Write(0);
    }

    /// <summary>Writes a packed value in its full bind format plus the even-alignment pad.</summary>
    public void WritePackedValue(PackedValue value)
    {
        byte[] b = value.ToWireBytes();
        Write(b);
        if ((b.Length & 0x1) != 0)
            Write(0);
    }

    /// <summary>Writes a DECIMAL/MONEY field (port of <c>writeDecimal</c>).</summary>
    public void WriteDecimal(decimal value) => WritePackedValue(PackedValue.FromDecimal(value));

    /// <summary>Writes a DATETIME YEAR TO FRACTION(5) field (port of <c>writeDateTime</c>).</summary>
    public void WriteDateTime(DateTime value) => WritePackedValue(PackedValue.FromDateTime(value));

    /// <summary>Writes a day-time INTERVAL field (port of <c>writeInterval(IntervalDF)</c>).</summary>
    public void WriteInterval(IfxIntervalDayFraction value) => WritePackedValue(PackedValue.FromInterval(value));

    /// <summary>Writes a year-month INTERVAL field (port of <c>writeInterval(IntervalYM)</c>).</summary>
    public void WriteInterval(IfxIntervalYearMonth value) => WritePackedValue(PackedValue.FromInterval(value));

    public void Flush() => _stream.Flush();

    /// <summary>Marks the current byte count as the last end-of-transmission boundary.</summary>
    public void SetByteCountAtEot() => _byteCountAtEot = Written;

    /// <summary>Bytes written since the last <see cref="SetByteCountAtEot"/>.</summary>
    public int BytesSentSinceLastEot => Written - _byteCountAtEot;
}
