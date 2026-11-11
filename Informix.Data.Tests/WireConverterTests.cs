using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

public class WireConverterTests
{
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)-1)]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void Int16_round_trips_big_endian(short value)
    {
        var b = IfxWireConverter.ToInt16(value);
        Assert.Equal(2, b.Length);
        Assert.Equal(value, IfxWireConverter.ReadInt16(b));
    }

    [Fact]
    public void Int16_is_big_endian()
    {
        // 0x0102 => high byte first
        Assert.Equal(new byte[] { 0x01, 0x02 }, IfxWireConverter.ToInt16(0x0102));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(123456789)]
    public void Int32_round_trips_big_endian(int value)
    {
        var b = IfxWireConverter.ToInt32(value);
        Assert.Equal(new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }, b);
        Assert.Equal(value, IfxWireConverter.ReadInt32(b));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(-9_000_000_000_000_000_000L)]
    public void BigInt_round_trips(long value)
    {
        var b = IfxWireConverter.ToBigInt(value);
        Assert.Equal(8, b.Length);
        Assert.Equal(value, IfxWireConverter.ReadBigInt(b));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    [InlineData(123_456_789_012_345L)]
    [InlineData(-123_456_789_012_345L)]
    [InlineData(long.MaxValue)]
    public void Int8_round_trips_through_informix_layout(long value)
    {
        var b = IfxWireConverter.ToInt8(value);
        Assert.Equal(IfxWireConverter.Int8Size, b.Length);
        Assert.Equal(value, IfxWireConverter.ReadInt8(b));
    }

    [Fact]
    public void Int8_all_zero_bytes_decode_as_null_sentinel()
    {
        var nullBytes = new byte[IfxWireConverter.Int8Size];
        Assert.Equal(IfxWireConverter.Int8Null, IfxWireConverter.ReadInt8(nullBytes));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(-3.25f)]
    [InlineData(float.MaxValue)]
    public void Real_round_trips(float value)
    {
        Assert.Equal(value, IfxWireConverter.ReadReal(IfxWireConverter.ToReal(value)));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(3.14159265358979d)]
    [InlineData(-2.718281828d)]
    [InlineData(double.MaxValue)]
    public void Double_round_trips(double value)
    {
        Assert.Equal(value, IfxWireConverter.ReadDouble(IfxWireConverter.ToDouble(value)));
    }

    [Fact]
    public void Date_zero_is_1899_12_31_the_informix_epoch()
    {
        var d = IfxWireConverter.ConvertDaysToDate(0);
        Assert.Equal(new DateTime(1899, 12, 31), d);
        Assert.Equal(0, IfxWireConverter.ConvertDateToDays(new DateTime(1899, 12, 31)));
    }

    [Fact]
    public void Date_one_is_1900_01_01()
    {
        Assert.Equal(new DateTime(1900, 1, 1), IfxWireConverter.ConvertDaysToDate(1));
        Assert.Equal(1, IfxWireConverter.ConvertDateToDays(new DateTime(1900, 1, 1)));
    }

    public static readonly TheoryData<DateTime> DateSamples =
    [
        new DateTime(1899, 12, 31),
        new DateTime(1900, 1, 1),
        new DateTime(2000, 1, 1),
        new DateTime(2020, 2, 29),  // leap day
        new DateTime(2026, 7, 6),
        new DateTime(2100, 3, 1),   // century non-leap boundary
        new DateTime(1600, 2, 29),  // 400-year leap
    ];

    [Theory]
    [MemberData(nameof(DateSamples))]
    public void Date_round_trips_through_day_count(DateTime date)
    {
        int days = IfxWireConverter.ConvertDateToDays(date);
        Assert.Equal(date, IfxWireConverter.ConvertDaysToDate(days));
    }

    [Theory]
    [InlineData(1899, false)]
    [InlineData(1900, false)]
    [InlineData(2000, true)]
    [InlineData(2004, true)]
    [InlineData(2100, false)]
    [InlineData(1600, true)]
    public void Leap_year_matches_informix_rleapyear(int year, bool expected)
    {
        Assert.Equal(expected, IfxWireConverter.IsLeapYear(year));
    }

    [Fact]
    public void DecodeChar_strips_trailing_nul_padding()
    {
        var raw = "AB\0\0"u8.ToArray();
        Assert.Equal("AB", IfxWireConverter.DecodeChar(raw));
    }

    [Fact]
    public void EncodeDecode_char_round_trips_latin1()
    {
        const string s = "Café éüñ"; // accented chars within the ISO-8859-1 range
        var bytes = IfxWireConverter.EncodeChar(s);
        Assert.Equal(s, IfxWireConverter.DecodeChar(bytes));
    }
}
