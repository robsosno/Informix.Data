using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// byte[] parameters for the Database Extensions binary UDTs. A plain byte[] binds as BYTE, which the
/// server cannot cast to BINARYVAR/BINARY18 (-9634); IfxType.BinaryVar / IfxType.Binary18 bind the
/// opaque-UDT shape instead (SENDRECV(44), owner "informix", type name, [len:int32][raw bytes]) --
/// the shape live-probed on IDS in tools/BinaryTypesProbe. These tests pin the bytes on the wire;
/// the live round trip is in tools/BinaryTypesProbe and Informix.Data.SpecTests.
/// </summary>
public class BinaryParameterTests
{
    private static byte[] InsertScript() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    // SQ_BIND bytes of a non-null opaque UDT bind: type 44 + owner + name + indicator(0) + int16 precision + [len:int32][payload].
    private static byte[] ExpectedUdtBind(string name, byte[] payload)
    {
        var b = new List<byte> { 0, 44 };
        b.AddRange([0, 8]);
        b.AddRange(IfxWireConverter.EncodeChar("informix"));
        b.AddRange([0, (byte)name.Length]);
        b.AddRange(IfxWireConverter.EncodeChar(name));
        if (name.Length % 2 == 1)
            b.Add(0); // SQLI pads a string of odd length to an even byte count
        b.AddRange([0, 0]);
        b.AddRange([(byte)(payload.Length >> 8), (byte)payload.Length]);
        b.AddRange([0, 0, (byte)(payload.Length >> 8), (byte)payload.Length]);
        b.AddRange(payload);
        return b.ToArray();
    }

    private static byte[] ExpectedUdtNullBind(string name)
    {
        var b = new List<byte> { 0, 44 };
        b.AddRange([0, 8]);
        b.AddRange(IfxWireConverter.EncodeChar("informix"));
        b.AddRange([0, (byte)name.Length]);
        b.AddRange(IfxWireConverter.EncodeChar(name));
        if (name.Length % 2 == 1)
            b.Add(0); // SQLI pads a string of odd length to an even byte count
        b.AddRange([0xFF, 0xFF, 0, 0]);
        return b.ToArray();
    }

    private static byte[] Send(object? value, IfxType type)
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", value) { IfxType = type });
        Assert.Equal(1, cmd.ExecuteNonQuery());
        return duplex.Output.ToArray()[(int)before..];
    }

    [Fact]
    public void BinaryVar_binds_the_raw_bytes_as_the_binaryvar_udt()
    {
        byte[] payload = [0x00, 0x00, 0x12, 0xDF, 0x4F, 0x6C];
        byte[] sent = Send(payload, IfxType.BinaryVar);
        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("binaryvar", payload)), Convert.ToHexString(sent));
    }

    [Fact]
    public void Binary18_pads_a_short_value_with_zero_bytes_to_18()
    {
        byte[] payload = [0x00, 0x00, 0x12, 0xDF, 0x4F, 0x6C];
        byte[] padded = [.. payload, .. new byte[12]];
        byte[] sent = Send(payload, IfxType.Binary18);
        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("binary18", padded)), Convert.ToHexString(sent));
    }

    [Fact]
    public void Binary18_binds_an_exact_18_byte_value_unchanged()
    {
        byte[] payload = Convert.FromHexString("49424D434F52504F524154494F4E32303036");
        Assert.Equal(18, payload.Length);
        byte[] sent = Send(payload, IfxType.Binary18);
        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("binary18", payload)), Convert.ToHexString(sent));
    }

    [Theory]
    [InlineData(IfxType.BinaryVar, "binaryvar")]
    [InlineData(IfxType.Binary18, "binary18")]
    public void Null_and_DBNull_bind_a_typed_null_under_the_type_name(IfxType type, string name)
    {
        foreach (object? nothing in new object?[] { null, DBNull.Value })
        {
            byte[] sent = Send(nothing, type);
            Assert.Contains(Convert.ToHexString(ExpectedUdtNullBind(name)), Convert.ToHexString(sent));
        }
    }

    [Fact]
    public void Binary18_over_18_bytes_throws_minus_9636_before_sending_anything()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", new byte[19]) { IfxType = IfxType.Binary18 });

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(-9636, ex.IfxSqlCode);
        Assert.Contains("Opaque type exceeded its maximum length", ex.Message);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void An_empty_BinaryVar_value_is_not_rejected()
    {
        byte[] sent = Send(Array.Empty<byte>(), IfxType.BinaryVar);
        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("binaryvar", [])), Convert.ToHexString(sent));
    }

    [Theory]
    [InlineData(IfxType.BinaryVar)]
    [InlineData(IfxType.Binary18)]
    public void A_value_that_is_not_a_byte_array_throws_NotSupportedException(IfxType type)
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", "0102") { IfxType = type });
        Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void A_plain_byte_array_parameter_still_binds_as_BYTE()
    {
        byte[] sent = Send(new byte[] { 1, 2, 3, 4 }, IfxType.Byte);
        Assert.DoesNotContain(Convert.ToHexString(IfxWireConverter.EncodeChar("binaryvar")), Convert.ToHexString(sent));
    }
}
