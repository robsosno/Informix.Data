using System;
using System.Linq;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxSmartLOBLocator"/> as a value type of its own: <see cref="IfxSmartLOBLocator.Length"/>
/// is a static property of the type (72, as in Informix.Net.Core, whose getter is
/// <c>ldc.i4.s 72; ret</c>, and as JDBC's single <c>IFX_LOCATOR_SIZE</c>), and the constructors
/// hold the type to it. Informix.Net.Core's constructors accept anything; rejecting a wrong
/// length here is deliberate -- such a locator cannot point at anything.
/// </summary>
public class SmartLobLocatorTests
{
    private static byte[] Bytes(int length, byte seed = 0x10) =>
        Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void Length_is_a_static_property_of_the_type_and_is_72()
    {
        var property = typeof(IfxSmartLOBLocator).GetProperty(nameof(IfxSmartLOBLocator.Length))!;

        Assert.True(property.GetMethod!.IsStatic);
        Assert.Equal(72, IfxSmartLOBLocator.Length);
    }

    [Fact]
    public void Seventy_two_bytes_make_a_locator()
    {
        var locator = new IfxSmartLOBLocator(Bytes(72));

        Assert.Equal(Bytes(72), locator.ToBytes());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(71)]
    [InlineData(73)]
    public void Any_other_byte_count_is_rejected(int length)
    {
        var ex = Assert.Throws<ArgumentException>(() => new IfxSmartLOBLocator(Bytes(length)));

        Assert.Equal("locator", ex.ParamName);
        Assert.Contains("72 bytes", ex.Message);
        Assert.Contains($"got {length}", ex.Message);
    }

    [Fact]
    public void The_hex_text_round_trips()
    {
        var original = new IfxSmartLOBLocator(Bytes(72, 0xA0));

        string hex = original.ToString();
        var parsed = new IfxSmartLOBLocator(hex);

        Assert.Equal(144, hex.Length);
        Assert.Equal(original.ToBytes(), parsed.ToBytes());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(142)]
    [InlineData(146)]
    public void Hex_text_of_the_wrong_length_is_rejected(int length)
    {
        var ex = Assert.Throws<ArgumentException>(() => new IfxSmartLOBLocator(new string('A', length)));

        Assert.Equal("locator", ex.ParamName);
        Assert.Contains("144 hex digits", ex.Message);
    }

    [Fact]
    public void Hex_text_with_a_non_hex_character_is_an_argument_error_not_a_format_error()
    {
        string bad = new string('A', 143) + "Z";

        var ex = Assert.Throws<ArgumentException>(() => new IfxSmartLOBLocator(bad));

        Assert.IsType<FormatException>(ex.InnerException);
        Assert.Contains("hex digits", ex.Message);
    }

    [Fact]
    public void The_locator_is_immutable()
    {
        byte[] source = Bytes(72);
        var locator = new IfxSmartLOBLocator(source);

        source[0] = 0xFF;                       // the constructor copied
        locator.ToBytes()[1] = 0xFF;            // ToBytes hands out a copy

        Assert.Equal(Bytes(72), locator.ToBytes());
    }

    [Fact]
    public void Null_is_rejected_as_null()
    {
        Assert.Throws<ArgumentNullException>(() => new IfxSmartLOBLocator((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => new IfxSmartLOBLocator((string)null!));
    }
}
