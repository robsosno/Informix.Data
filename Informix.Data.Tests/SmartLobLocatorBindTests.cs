using System;
using System.Collections.Generic;
using System.Text;
using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Binding a smart large object the caller already created -- IfxConnection.GetIfxBlob /
/// GetIfxClob, a reader's GetIfxBlob, or a bare locator -- instead of creating a new one from
/// a payload. The locator travels as the opaque-UDT shape the create path already used:
/// SENDRECV(44), owner "informix", name "blob"/"clob", [len:int32][72 locator bytes].
///
/// <para>Two things these pin down. First, that the create path is NOT taken: a parameter
/// already holding a locator must cost no ifx_lo_create round trip. Second, that the
/// "blob"/"clob" name is the one the caller asked for -- live-verified against IDS 14.10, the
/// server refuses to cast one to the other (SQLCODE -9634 "blob to clob" / "clob to blob"),
/// so a wrong name is a failed statement, never a silent coercion.</para>
/// </summary>
public class SmartLobLocatorBindTests
{
    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    private static byte[] InsertScript() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    // SQ_BIND bytes of a non-null opaque UDT: type 44 + owner + name + indicator(0)
    // + int16 precision + [len:int32][payload].
    private static byte[] ExpectedUdtBind(string name, byte[] payload)
    {
        var b = new List<byte> { 0, 44 };
        b.AddRange([0, 8]);
        b.AddRange(IfxWireConverter.EncodeChar("informix"));
        b.AddRange([0, (byte)name.Length]);
        b.AddRange(IfxWireConverter.EncodeChar(name));
        if (name.Length % 2 == 1)
            b.Add(0);
        b.AddRange([0, 0]);
        b.AddRange([(byte)(payload.Length >> 8), (byte)payload.Length]);
        b.AddRange([0, 0, (byte)(payload.Length >> 8), (byte)payload.Length]);
        return [.. b, .. payload];
    }

    private static byte[] ExpectedUdtNullBind(string name)
    {
        var b = new List<byte> { 0, 44 };
        b.AddRange([0, 8]);
        b.AddRange(IfxWireConverter.EncodeChar("informix"));
        b.AddRange([0, (byte)name.Length]);
        b.AddRange(IfxWireConverter.EncodeChar(name));
        if (name.Length % 2 == 1)
            b.Add(0);
        b.AddRange([0xFF, 0xFF, 0, 0]);
        return b.ToArray();
    }

    /// <summary>One INSERT with the parameter value the factory builds; returns what was sent.</summary>
    private static byte[] Send(Func<IfxConnection, object?> value, IfxType type)
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", value(cn)) { IfxType = type });
        Assert.Equal(1, cmd.ExecuteNonQuery());
        return duplex.Output.ToArray()[(int)before..];
    }

    private static byte[] Send(object? value, IfxType type) => Send(_ => value, type);

    [Fact]
    public void An_IfxBlob_binds_its_own_locator_under_the_blob_name()
    {
        byte[] sent = Send(cn => new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10))), IfxType.Blob);

        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("blob", Locator(0x10))), Convert.ToHexString(sent));
    }

    [Fact]
    public void An_IfxClob_binds_its_own_locator_under_the_clob_name()
    {
        byte[] sent = Send(cn => new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20))), IfxType.Clob);

        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("clob", Locator(0x20))), Convert.ToHexString(sent));
    }

    [Fact]
    public void The_object_decides_the_name_even_when_IfxType_disagrees()
    {
        // An IfxClob is a clob whatever the caller left in IfxType: the object knows, the enum guesses.
        byte[] sent = Send(cn => new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20))), IfxType.Blob);

        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("clob", Locator(0x20))), Convert.ToHexString(sent));
    }

    [Fact]
    public void Binding_a_locator_costs_no_ifx_lo_create_round_trip()
    {
        byte[] sent = Send(cn => new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10))), IfxType.Blob);

        // The create path announces itself by looking the routine up by signature.
        Assert.DoesNotContain("ifx_lo_create", Encoding.Latin1.GetString(sent));
    }

    [Theory]
    [InlineData(IfxType.Clob, "clob")]
    [InlineData(IfxType.Blob, "blob")]
    [InlineData(IfxType.SmartLOBLocator, "blob")]
    public void An_IfxSmartLOBLocator_takes_its_name_from_IfxType(IfxType type, string name)
    {
        byte[] sent = Send(new IfxSmartLOBLocator(Locator(0x30)), type);

        Assert.Contains(Convert.ToHexString(ExpectedUdtBind(name, Locator(0x30))), Convert.ToHexString(sent));
    }

    [Fact]
    public void Bare_locator_bytes_bind_as_a_locator_under_SmartLOBLocator()
    {
        byte[] sent = Send(Locator(0x40), IfxType.SmartLOBLocator);

        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("blob", Locator(0x40))), Convert.ToHexString(sent));
    }

    [Fact]
    public void The_hex_text_of_a_locator_binds_as_that_locator()
    {
        string hex = new IfxSmartLOBLocator(Locator(0x50)).ToString();
        byte[] sent = Send(hex, IfxType.SmartLOBLocator);

        Assert.Contains(Convert.ToHexString(ExpectedUdtBind("blob", Locator(0x50))), Convert.ToHexString(sent));
    }

    [Theory]
    [InlineData(IfxType.Blob, "blob")]
    [InlineData(IfxType.Clob, "clob")]
    public void A_null_smart_lob_binds_a_typed_null(IfxType type, string name)
    {
        object nullLob = type == IfxType.Blob ? IfxBlob.Null : IfxClob.Null;
        byte[] sent = Send(nullLob, type);

        Assert.Contains(Convert.ToHexString(ExpectedUdtNullBind(name)), Convert.ToHexString(sent));
    }

    [Fact]
    public void A_locator_of_the_wrong_length_is_rejected_before_anything_is_sent()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", new byte[12]) { IfxType = IfxType.SmartLOBLocator });

        var ex = Assert.Throws<ArgumentException>(() => cmd.ExecuteNonQuery());

        Assert.Contains("72 bytes", ex.Message);
        Assert.Contains("got 12", ex.Message);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void A_value_that_is_not_a_locator_names_every_accepted_shape()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", 12345) { IfxType = IfxType.SmartLOBLocator });

        var ex = Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());

        Assert.Contains("IfxBlob/IfxClob", ex.Message);
        Assert.Contains("IfxSmartLOBLocator", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void A_non_hex_string_under_SmartLOBLocator_says_what_the_string_should_be()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", "nie-hex") { IfxType = IfxType.SmartLOBLocator });

        var ex = Assert.Throws<ArgumentException>(() => cmd.ExecuteNonQuery());

        Assert.Contains("hex text", ex.Message);
    }

    [Fact]
    public void A_byte_array_under_Blob_is_still_content_so_it_takes_the_create_path()
    {
        // The deliberate asymmetry: only IfxType.SmartLOBLocator reads a bare byte[] as a
        // locator. Under Clob/Blob a byte[] is the new object's content, as it always was --
        // so the driver goes looking for ifx_lo_create, which this mock has no reply for.
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", Locator(0x60)) { IfxType = IfxType.Blob });

        Assert.ThrowsAny<Exception>(() => cmd.ExecuteNonQuery());

        Assert.Contains("ifx_lo_create", Encoding.Latin1.GetString(duplex.Output.ToArray()));
    }
}
