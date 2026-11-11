using System;
using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Creating a smart large object from the connection, the Informix.Net.Core way:
/// <c>GetIfxBlob()</c> only prepares a handle, the create-time properties and an optional
/// table/column make up the create spec, and <c>Open()</c> is what calls <c>ifx_lo_create</c>.
///
/// <para>Measured against Informix.Net.Core 4.0.2.0: its GetIfxBlob() returns an object with
/// IsNull true and IsOpen false, Write() before Open() fails, Open() calls its internal
/// CreateSmartLOB, the (table, column) constructors store both names and CreateSmartLOB turns
/// them into "table.column" for the column-spec lookup, and the property setters write into the
/// create spec. The spec layout and flag values are JDBC's IfxLobDescriptor (see SqliLobSpec).</para>
/// </summary>
public class SmartLobCreateSpecTests
{
    private const string CreateSignature = "function informix.ifx_lo_create( ifx_lo_spec,integer,blob)";
    private const string ColSbinfoSignature = "function informix.ifx_lo_col_sbinfo(lvarchar, ifx_lo_spec)";

    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    // ifx_lo_create: the routine handle, then (locator, fd).
    private static byte[] CreateReplies(byte[] locator, int fd) => SqliStatementKit.Join(
        SqliStatementKit.RoutineHandleFrame("db", 66),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.FpReturnFrame(locator, fd),
        SqliStatementKit.EotFrame());

    private static byte[] Sent(DuplexStream duplex, long before) => duplex.Output.ToArray()[(int)before..];

    private static string Text(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    // ---- the lifecycle ------------------------------------------------------------------------

    [Fact]
    public void GetIfxBlob_prepares_a_handle_without_touching_the_server()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        long before = duplex.Output.Length;

        IfxBlob blob = cn.GetIfxBlob();

        Assert.True(blob.IsNull);
        Assert.False(blob.IsOpen);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void Writing_before_Open_says_that_Open_creates_the_object()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        IfxBlob blob = cn.GetIfxBlob();

        var ex = Assert.Throws<InvalidOperationException>(() => blob.Write([1, 2, 3]));

        Assert.Contains("Open() creates it", ex.Message);
    }

    [Fact]
    public void A_locator_is_not_available_before_Open()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        IfxClob clob = cn.GetIfxClob();

        var ex = Assert.Throws<InvalidOperationException>(() => clob.GetLocator());

        Assert.Contains("Open() creates it", ex.Message);
    }

    [Fact]
    public void Binding_an_object_that_was_never_created_throws_instead_of_storing_NULL()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("v", cn.GetIfxBlob()) { IfxType = IfxType.Blob });

        var ex = Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());

        Assert.Contains("Open() creates it", ex.Message);
    }

    [Fact]
    public void Open_creates_the_object_and_leaves_it_open()
    {
        byte[] locator = Locator(0x31);
        using var cn = SqliStatementKit.OpenConnection(CreateReplies(locator, 39), out var duplex);
        long before = duplex.Output.Length;
        IfxBlob blob = cn.GetIfxBlob();

        blob.Open(IfxSmartLOBOpenMode.ReadWrite);

        Assert.False(blob.IsNull);
        Assert.True(blob.IsOpen);
        Assert.Equal(locator, blob.GetLocator().ToBytes());
        string sent = Text(Sent(duplex, before));
        Assert.Contains(CreateSignature, sent);
        Assert.DoesNotContain(ColSbinfoSignature, sent);
    }

    [Fact]
    public void With_nothing_set_the_create_spec_is_the_servers_default()
    {
        using var cn = SqliStatementKit.OpenConnection(CreateReplies(Locator(0x31), 39), out var duplex);
        long before = duplex.Output.Length;

        cn.GetIfxBlob().Open(IfxSmartLOBOpenMode.ReadWrite);

        Assert.Contains(Convert.ToHexString(SqliSession.BuildDefaultLobDescriptor()), Convert.ToHexString(Sent(duplex, before)));
    }

    [Fact]
    public void A_fresh_handle_reports_the_default_spec_values()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        IfxBlob blob = cn.GetIfxBlob();

        Assert.Equal(-1, blob.EstimatedSize);
        Assert.Equal(-1, blob.MaxBytes);
        Assert.Equal(-1, blob.ExtentSize);
        Assert.Equal(0, blob.Flags);
        Assert.Equal("", blob.SBSpace);
    }

    // ---- the create-time properties -------------------------------------------------------------

    [Fact]
    public void Properties_set_before_Open_are_the_spec_ifx_lo_create_receives()
    {
        using var cn = SqliStatementKit.OpenConnection(CreateReplies(Locator(0x31), 39), out var duplex);
        long before = duplex.Output.Length;
        IfxBlob blob = cn.GetIfxBlob();
        blob.SBSpace = "sbspace2";
        blob.ExtentSize = 64;
        blob.MaxBytes = 10_000_000;
        blob.EstimatedSize = 2_000_000;
        blob.Flags = (int)(IfxSmartLOBCreateTimeFlags.Log | IfxSmartLOBCreateTimeFlags.KeepAccessTime);

        blob.Open(IfxSmartLOBOpenMode.ReadWrite);

        byte[] expected = SqliSession.BuildDefaultLobDescriptor();
        SqliLobSpec.SetSbspace(expected, "sbspace2");
        SqliLobSpec.SetExtentSize(expected, 64);
        SqliLobSpec.SetMaxBytes(expected, 10_000_000);
        SqliLobSpec.SetEstimatedBytes(expected, 2_000_000);
        SqliLobSpec.SetFlags(expected, 9);
        Assert.Contains(Convert.ToHexString(expected), Convert.ToHexString(Sent(duplex, before)));
    }

    [Fact]
    public void The_spec_fields_sit_where_JDBCs_IfxLobDescriptor_puts_them()
    {
        byte[] spec = SqliSession.BuildDefaultLobDescriptor();

        SqliLobSpec.SetFlags(spec, 9);                 // CreateTimeFlags, int32 at 4
        SqliLobSpec.SetEstimatedBytes(spec, 0x1234_5678_9ABCL);
        SqliLobSpec.SetMaxBytes(spec, 10_000_000);     // sizeLimit, UDT long at 24
        SqliLobSpec.SetExtentSize(spec, 64);           // extentSize, int32 at 36
        SqliLobSpec.SetSbspace(spec, "sbspace2");      // spaceName, 128 bytes at 40

        Assert.Equal(Convert.FromHexString("DADAFEED"), spec[0..4]);
        Assert.Equal(Convert.FromHexString("00000009"), spec[4..8]);
        // UDT long: [low32][high32][sign16]
        Assert.Equal(Convert.FromHexString("56789ABC" + "00001234" + "0001"), spec[12..22]);
        Assert.Equal(Convert.FromHexString("00989680" + "00000000" + "0001"), spec[24..34]);
        Assert.Equal(Convert.FromHexString("00000040"), spec[36..40]);
        Assert.Equal(IfxWireConverter.EncodeChar("sbspace2"), spec[40..48]);
        Assert.Equal(0, spec[48]);                     // NUL-padded
        Assert.Equal(SqliSession.BuildDefaultLobDescriptor()[168..], spec[168..]); // column part untouched
    }

    [Fact]
    public void Clearing_SBSpace_restores_the_servers_default()
    {
        byte[] spec = SqliSession.BuildDefaultLobDescriptor();
        SqliLobSpec.SetSbspace(spec, "sbspace2");

        SqliLobSpec.SetSbspace(spec, "");

        Assert.Equal(SqliSession.BuildDefaultLobDescriptor(), spec);
    }

    [Theory]
    [InlineData(129)]
    public void An_sbspace_name_longer_than_an_identifier_is_rejected(int length)
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        IfxBlob blob = cn.GetIfxBlob();

        Assert.Throws<ArgumentException>(() => blob.SBSpace = new string('s', length));
    }

    [Fact]
    public void A_non_ASCII_sbspace_name_is_rejected()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        IfxBlob blob = cn.GetIfxBlob();

        Assert.Throws<ArgumentException>(() => blob.SBSpace = "przestrzeń");
    }

    // ---- table / column -------------------------------------------------------------------------

    [Fact]
    public void Table_and_column_fold_the_columns_storage_into_the_spec_before_the_create()
    {
        byte[] locator = Locator(0x41);
        byte[] resolved = SqliSession.BuildDefaultLobDescriptor();
        SqliLobSpec.SetSbspace(resolved, "colspace");          // what the column's PUT clause says
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.RoutineHandleFrame("db", 65),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(resolved, 0),
            SqliStatementKit.EotFrame(),
            CreateReplies(locator, 39));

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        IfxBlob blob = cn.GetIfxBlob("docs", "body");
        blob.SBSpace = "mine";                                  // the template the server merges into

        blob.Open(IfxSmartLOBOpenMode.ReadWrite);

        string sent = Text(Sent(duplex, before));
        int sbinfo = sent.IndexOf(ColSbinfoSignature, StringComparison.Ordinal);
        int create = sent.IndexOf(CreateSignature, StringComparison.Ordinal);
        Assert.True(sbinfo >= 0 && create > sbinfo, "the column must be resolved before ifx_lo_create");
        Assert.Contains("docs.body", sent[sbinfo..create]);
        Assert.Contains("mine", sent[sbinfo..create]);          // the properties went in as the template
        Assert.Contains("colspace", sent[create..]);            // the server's answer is what was created
        Assert.DoesNotContain("mine", sent[create..]);
        Assert.Equal("colspace", blob.SBSpace);                 // and what the object now reports
    }

    [Fact]
    public void The_public_constructor_with_a_column_behaves_like_the_connection_factory()
    {
        byte[] resolved = SqliSession.BuildDefaultLobDescriptor();
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.RoutineHandleFrame("db", 65),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(resolved, 0),
            SqliStatementKit.EotFrame(),
            CreateReplies(Locator(0x51), 40));

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        var clob = new IfxClob(cn, "docs", "summary");

        Assert.True(clob.IsNull);
        clob.Open(IfxSmartLOBOpenMode.ReadWrite);

        Assert.Contains("docs.summary", Text(Sent(duplex, before)));
        Assert.True(clob.IsOpen);
    }

    [Theory]
    [InlineData("docs", "")]
    [InlineData("", "body")]
    [InlineData("docs", null)]
    [InlineData(null, "body")]
    public void Half_a_column_name_is_rejected_rather_than_silently_ignored(string? table, string? column)
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);

        Assert.Throws<ArgumentException>(() => cn.GetIfxBlob(table!, column!));
    }

    [Fact]
    public void Neither_table_nor_column_is_the_same_as_none()
    {
        using var cn = SqliStatementKit.OpenConnection(CreateReplies(Locator(0x61), 41), out var duplex);
        long before = duplex.Output.Length;

        new IfxBlob(cn, "", "").Open(IfxSmartLOBOpenMode.ReadWrite);

        Assert.DoesNotContain(ColSbinfoSignature, Text(Sent(duplex, before)));
    }

    // ---- the SQL NULL sentinel ------------------------------------------------------------------

    [Fact]
    public void The_SQL_NULL_sentinel_cannot_be_opened_or_given_a_spec()
    {
        Assert.Throws<InvalidOperationException>(() => IfxBlob.Null.Open(IfxSmartLOBOpenMode.ReadWrite));
        Assert.Throws<InvalidOperationException>(() => IfxBlob.Null.SBSpace = "sbspace");
        Assert.Throws<InvalidOperationException>(() => _ = IfxClob.Null.ExtentSize);
    }

    [Fact]
    public void An_existing_object_opens_by_its_locator_and_creates_nothing()
    {
        // ifx_lo_open: handle + fd
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.RoutineHandleFrame("db", 67),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(42),
            SqliStatementKit.EotFrame());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x71)), "docs", "body");

        blob.Open(IfxSmartLOBOpenMode.ReadWrite);

        string sent = Text(Sent(duplex, before));
        Assert.DoesNotContain(CreateSignature, sent);
        Assert.DoesNotContain(ColSbinfoSignature, sent);
        Assert.True(blob.IsOpen);
    }
}
