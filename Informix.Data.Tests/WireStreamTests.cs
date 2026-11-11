using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

public class WireStreamTests
{
    private static (IfxWireWriter writer, MemoryStream stream) NewWriter()
    {
        var ms = new MemoryStream();
        return (new IfxWireWriter(ms), ms);
    }

    private static IfxWireReader ReaderOver(MemoryStream ms)
    {
        ms.Position = 0;
        return new IfxWireReader(ms);
    }

    [Fact]
    public void Int_primitives_round_trip_through_the_stream()
    {
        var (w, ms) = NewWriter();
        w.WriteInt16(0x1234);
        w.WriteInt32(-987654);
        w.WriteBigInt(9_000_000_000_000L);
        w.WriteInt8(-123_456_789L);

        var r = ReaderOver(ms);
        Assert.Equal((short)0x1234, r.ReadInt16());
        Assert.Equal(-987654, r.ReadInt32());
        Assert.Equal(9_000_000_000_000L, r.ReadBigInt());
        Assert.Equal(-123_456_789L, r.ReadInt8());
    }

    [Fact]
    public void Char_field_round_trips_with_length_prefix_and_even_padding()
    {
        var (w, ms) = NewWriter();
        w.WriteChar("odd");  // length 3 => one pad byte
        w.WriteChar("even"); // length 4 => no pad

        // 2-byte len + 3 data + 1 pad = 6 ; then 2 + 4 = 6 ; total 12
        Assert.Equal(12, ms.Length);

        var r = ReaderOver(ms);
        Assert.Equal("odd", r.ReadChar());
        Assert.Equal("even", r.ReadChar());
    }

    [Fact]
    public void Date_round_trips_through_the_stream()
    {
        var (w, ms) = NewWriter();
        w.WriteDate(new DateTime(2026, 7, 6));

        var r = ReaderOver(ms);
        Assert.Equal(new DateTime(2026, 7, 6), r.ReadDate());
    }

    [Fact]
    public void Written_counter_tracks_bytes_and_eot_delta()
    {
        var (w, _) = NewWriter();
        w.WriteInt32(0);            // 4
        w.SetByteCountAtEot();
        w.WriteInt16(0);            // 2
        Assert.Equal(6, w.Written);
        Assert.Equal(2, w.BytesSentSinceLastEot);
    }

    [Fact]
    public void WritePadded_appends_pad_byte_only_for_odd_length()
    {
        var (w, ms) = NewWriter();
        w.WritePadded(new byte[] { 1, 2, 3 }); // odd => 4 bytes
        Assert.Equal(4, ms.Length);

        var (w2, ms2) = NewWriter();
        w2.WritePadded(new byte[] { 1, 2, 3, 4 }); // even => 4 bytes
        Assert.Equal(4, ms2.Length);
    }
}
