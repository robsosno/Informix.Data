using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// LIVE-FIX (bug 2): an empty smart LOB write. The server never answers an SQ_LODATA
/// LO_WRITE whose length is 0 (live-measured, both drivers hung beyond CommandTimeout), so
/// the driver must not send one: an empty BLOB/CLOB parameter creates an empty object
/// (create + close, no write) and <c>IfxBlob.Write(new byte[0])</c> returns 0 at once.
/// Every test runs under a time limit so a regression fails instead of hanging the run.
/// </summary>
public class SmartLobEmptyWriteTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    private static void Within(Action action)
    {
        var task = System.Threading.Tasks.Task.Run(action);
        Assert.True(task.Wait(Limit), $"the operation did not finish within {Limit.TotalSeconds} s");
    }

    // SQ_LODATA(97) + subCom 2 (LO_WRITE) + fd 39: any write request on the test's descriptor.
    private static readonly string LoWriteHeader = BitConverter.ToString(new byte[] { 0, 97, 0, 2, 0, 39 });

    // ... and the zero-length one the server never answers: + nbytes 0 + bufsize 32000.
    private static readonly string ZeroLengthLoWrite =
        BitConverter.ToString(new byte[] { 0, 97, 0, 2, 0, 39, 0, 0, 0, 0, 0x7D, 0x00 });

    // ifx_lo_create (handle + locator, fd 39), ifx_lo_close (handle + rc) -- no LO_WRITE ack --
    // then the INSERT's prepare / execute / release.
    private static byte[] EmptyInsertScript(byte[] locator) => SqliStatementKit.Join(
        SqliStatementKit.RoutineHandleFrame("db", 66),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.FpReturnFrame(locator, 39),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.RoutineHandleFrame("db", 68),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.FpReturnFrame(0),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    private static void AssertEmptyLobInserted(object value, IfxType type, string typeName)
    {
        byte[] locator = Locator(0x30);
        using var cn = SqliStatementKit.OpenConnection(EmptyInsertScript(locator), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into smart_t values (1, ?)";
        cmd.Parameters.Add(new IfxParameter("v", value) { IfxType = type });

        int rows = 0;
        Within(() => rows = cmd.ExecuteNonQuery());
        Assert.Equal(1, rows);

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        string hex = BitConverter.ToString(sent);
        string hay = System.Text.Encoding.Latin1.GetString(sent);
        Assert.Contains("function informix.ifx_lo_create( ifx_lo_spec,integer,blob)", hay);
        Assert.Contains("informix.ifx_lo_close", hay);
        Assert.DoesNotContain(ZeroLengthLoWrite, hex);
        Assert.DoesNotContain(LoWriteHeader, hex);

        // The INSERT bound the new (empty) object's locator, not a NULL.
        var bind = new List<byte> { 0, 44, 0, 8 };
        bind.AddRange(IfxWireConverter.EncodeChar("informix"));
        bind.AddRange([0, 4]);
        bind.AddRange(IfxWireConverter.EncodeChar(typeName));
        bind.AddRange([0, 0, 0, 72, 0, 0, 0, 72]);
        bind.AddRange(locator);
        Assert.Contains(BitConverter.ToString(bind.ToArray()), hex);
    }

    [Fact]
    public void An_empty_blob_parameter_creates_an_empty_object_without_a_zero_length_write()
        => AssertEmptyLobInserted(Array.Empty<byte>(), IfxType.Blob, "blob");

    [Fact]
    public void An_empty_clob_parameter_creates_an_empty_object_without_a_zero_length_write()
        => AssertEmptyLobInserted(string.Empty, IfxType.Clob, "clob");

    [Fact]
    public void IfxBlob_Write_of_no_bytes_returns_zero_at_once_without_a_round_trip()
    {
        // ifx_lo_open (handle + fd 39), then ifx_lo_close (handle + rc); nothing in between.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.RoutineHandleFrame("db", 67),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(39),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.RoutineHandleFrame("db", 68),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(0),
            SqliStatementKit.EotFrame());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        blob.Open(IfxSmartLOBOpenMode.ReadWrite);
        long before = duplex.Output.Length;

        long written = -1;
        Within(() => written = blob.Write(new byte[0]));

        Assert.Equal(0, written);
        Assert.Equal(before, duplex.Output.Length); // nothing went to the server
        Assert.Equal(0, blob.Position);
        blob.Close();
        Assert.DoesNotContain(LoWriteHeader, BitConverter.ToString(duplex.Output.ToArray()));
    }
}
