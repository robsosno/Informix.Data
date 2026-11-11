using System;
using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The create-spec properties of a smart LOB that already exists report what the server holds
/// for it, read from IFX_LO_STAT -- the JDBC model (<c>IfxBblob.getLoStat().getLobDescriptor()</c>),
/// and the path IBM's own ODBC sample <c>loinfo.c</c> takes (ifx_lo_open, ifx_lo_stat,
/// ifx_lo_stat_cspec). Measured against IDS 14.10, both report the real spec for objects read
/// from a row, where Informix.Net.Core reports the defaults (and throws for ExtentSize). Setting
/// a property of an existing object throws: its spec is fixed.
/// </summary>
public class SmartLobExistingSpecTests
{
    private const string OpenSignature = "function informix.ifx_lo_open(blob ,integer)";
    private const string StatSignature = "function informix.ifx_lo_stat(int, ifx_lo_stat)";
    private const string CloseSignature = "function informix.ifx_lo_close(integer)";
    private const string CreateSignature = "function informix.ifx_lo_create( ifx_lo_spec,integer,blob)";

    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    // The spec the server reports for an object: every field differs from the defaults.
    private static byte[] ServerSpec()
    {
        byte[] spec = SqliSession.BuildDefaultLobDescriptor();
        SqliLobSpec.SetSbspace(spec, "sbspace2");
        SqliLobSpec.SetExtentSize(spec, 48);
        SqliLobSpec.SetFlags(spec, 42);
        SqliLobSpec.SetMaxBytes(spec, 10_000_000);
        SqliLobSpec.SetEstimatedBytes(spec, 2_000_000);
        return spec;
    }

    private static byte[] StatWith(byte[] spec)
    {
        var stat = new byte[644];
        IfxWireConverter.WriteInt32(stat.AsSpan(0, 4), unchecked((int)0xFEDABABE));
        spec.CopyTo(stat, 40);
        return stat;
    }

    private static byte[] Call(int handle, params object[] returns) => SqliStatementKit.Join(
        SqliStatementKit.RoutineHandleFrame("db", handle),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.FpReturnFrame(returns),
        SqliStatementKit.EotFrame());

    private static string Text(DuplexStream duplex, long before) =>
        System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);

    [Fact]
    public void An_object_read_from_a_row_reports_the_spec_the_server_holds()
    {
        // open (read-only, just for this) + stat + close
        byte[] script = SqliStatementKit.Join(Call(67, 39), Call(69, StatWith(ServerSpec())), Call(68, 0));
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        Assert.Equal("sbspace2", blob.SBSpace);
        Assert.Equal(48, blob.ExtentSize);
        Assert.Equal(42, blob.Flags);
        Assert.Equal(10_000_000, blob.MaxBytes);
        Assert.Equal(2_000_000, blob.EstimatedSize);

        string sent = Text(duplex, before);
        int open = sent.IndexOf(OpenSignature, StringComparison.Ordinal);
        int stat = sent.IndexOf(StatSignature, StringComparison.Ordinal);
        int close = sent.IndexOf(CloseSignature, StringComparison.Ordinal);
        Assert.True(open >= 0 && stat > open && close > stat, "open, stat, close -- in that order");
        Assert.False(blob.IsOpen);                              // left as it was found
    }

    [Fact]
    public void The_spec_is_read_once_and_kept()
    {
        // one open/stat/close for all five properties; a second stat would find no reply
        byte[] script = SqliStatementKit.Join(Call(67, 39), Call(69, StatWith(ServerSpec())), Call(68, 0));
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        _ = blob.SBSpace;
        long after = duplex.Output.Length;
        _ = blob.ExtentSize; _ = blob.Flags; _ = blob.MaxBytes; _ = blob.EstimatedSize; _ = blob.SBSpace;

        Assert.Equal(after, duplex.Output.Length);
    }

    [Fact]
    public void An_open_object_is_asked_through_its_own_descriptor()
    {
        // ifx_lo_open by Open(), then only ifx_lo_stat for the property -- no second open, no close
        byte[] script = SqliStatementKit.Join(Call(67, 39), Call(69, StatWith(ServerSpec())));
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        blob.Open(IfxSmartLOBOpenMode.ReadOnly);
        long before = duplex.Output.Length;

        Assert.Equal(48, blob.ExtentSize);

        string sent = Text(duplex, before);
        Assert.Contains(StatSignature, sent);
        Assert.DoesNotContain(OpenSignature, sent);
        Assert.DoesNotContain(CloseSignature, sent);
        Assert.True(blob.IsOpen);
    }

    [Fact]
    public void After_Open_creates_the_object_the_properties_report_the_servers_values_not_the_ones_sent()
    {
        // sent: the defaults; the server fills in its own -- the sbspace name, extent 0, its flags
        byte[] serverSide = SqliSession.BuildDefaultLobDescriptor();
        SqliLobSpec.SetSbspace(serverSide, "sbspace");
        SqliLobSpec.SetExtentSize(serverSide, 0);
        SqliLobSpec.SetFlags(serverSide, 50);
        byte[] script = SqliStatementKit.Join(Call(66, Locator(0x31), 39), Call(69, StatWith(serverSide)));
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        IfxBlob blob = cn.GetIfxBlob();
        Assert.Equal("", blob.SBSpace);                         // before: the spec to be sent

        blob.Open(IfxSmartLOBOpenMode.ReadWrite);

        Assert.Equal("sbspace", blob.SBSpace);                  // after: what the server holds
        Assert.Equal(0, blob.ExtentSize);
        Assert.Equal(50, blob.Flags);
    }

    [Fact]
    public void Setting_a_property_of_an_object_read_from_a_row_throws_without_touching_the_server()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        long before = duplex.Output.Length;
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        var ex = Assert.Throws<InvalidOperationException>(() => blob.ExtentSize = 32);

        Assert.Contains("already exists", ex.Message);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void Setting_a_property_after_Open_created_the_object_throws()
    {
        using var cn = SqliStatementKit.OpenConnection(Call(66, Locator(0x31), 39), out _);
        IfxBlob blob = cn.GetIfxBlob();
        blob.SBSpace = "sbspace";                               // fine: still to be created
        blob.Open(IfxSmartLOBOpenMode.ReadWrite);

        Assert.Throws<InvalidOperationException>(() => blob.SBSpace = "sbspace2");
        Assert.Throws<InvalidOperationException>(() => blob.MaxBytes = 1);
        Assert.Throws<InvalidOperationException>(() => blob.EstimatedSize = 1);
        Assert.Throws<InvalidOperationException>(() => blob.Flags = 1);
    }

    [Fact]
    public void A_CLOB_read_from_a_row_behaves_the_same()
    {
        byte[] script = SqliStatementKit.Join(Call(67, 40), Call(69, StatWith(ServerSpec())), Call(68, 0));
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));

        Assert.Equal("sbspace2", clob.SBSpace);
        Assert.Throws<InvalidOperationException>(() => clob.SBSpace = "x");
    }

    [Fact]
    public void Nothing_is_created_when_a_property_of_an_existing_object_is_read()
    {
        byte[] script = SqliStatementKit.Join(Call(67, 39), Call(69, StatWith(ServerSpec())), Call(68, 0));
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        _ = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10))).MaxBytes;

        Assert.DoesNotContain(CreateSignature, Text(duplex, before));
    }
}
