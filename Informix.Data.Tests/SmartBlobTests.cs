using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Smart large objects (CLOB/BLOB in an sbspace): tuple decode of the 72-byte
/// locator, the FastPath IFX_LO_* call chain and the SQ_LODATA bulk transfer.
/// All frame layouts mirror a captured JDBC reference run against IDS 9.59.
/// </summary>
public class SmartBlobTests
{
    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    // The prepare reply describing (id INT, doc CLOB, img BLOB) — CLOB/BLOB arrive
    // as UdtFixed(41) with extended ids 11/10 and owner/name informix/clob|blob.
    private static byte[] PrepareReply() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
            new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0),
            new SqliStatementKit.Col("doc", (short)SqliTypeCode.UdtFixed, 72, 4,
                ExtendedId: 11, ExtOwner: "informix", ExtName: "clob"),
            new SqliStatementKit.Col("img", (short)SqliTypeCode.UdtFixed, 72, 80,
                ExtendedId: 10, ExtOwner: "informix", ExtName: "blob")),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] Row(int id, byte[]? docLocator, byte[]? imgLocator)
    {
        var idField = new byte[4];
        IfxWireConverter.WriteInt32(idField, id);
        return SqliStatementKit.Join(
            idField,
            SqliStatementKit.SmartBlobField(docLocator),
            SqliStatementKit.SmartBlobField(imgLocator));
    }

    // One complete lazy-fetch reply set for a single smart blob value. The first
    // value on a connection also resolves the four routine handles; subsequent
    // values reuse them (handles are cached per session, like the JDBC FPCache).
    private static byte[] BlobFetchReplies(byte[] data, bool firstOnConnection)
    {
        var frames = new List<byte[]>();
        void Fp(params object[] rets)
        {
            frames.Add(SqliStatementKit.FpReturnFrame(rets));
            frames.Add(SqliStatementKit.EotFrame());
        }
        void Handle(int h)
        {
            if (!firstOnConnection)
                return;
            frames.Add(SqliStatementKit.RoutineHandleFrame("db", h));
            frames.Add(SqliStatementKit.EotFrame());
        }

        Handle(67); Fp(39);                                   // ifx_lo_open -> fd 39
        Handle(71); Fp(SqliStatementKit.LoStatBlock(data.Length), 0); // ifx_lo_stat -> (stat, rc)
        Handle(73); Fp(0L, 0);                                // ifx_lo_seek -> (pos, rc)
        frames.Add(SqliStatementKit.LoDataReadFrame(data));   // SQ_LODATA read
        frames.Add(SqliStatementKit.EotFrame());
        Handle(68); Fp(0);                                    // ifx_lo_close -> rc
        return SqliStatementKit.Join(frames.ToArray());
    }

    private static byte[] SelectScript(byte[] clobData, byte[] blobData)
    {
        return SqliStatementKit.Join(
            PrepareReply(),
            // open reply (two-phase: the row layout is variable)
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // typed-fetch reply: one row with locators + one all-NULL row
            SqliStatementKit.TupleFrame(Row(1, Locator(0x10), Locator(0x50))),
            SqliStatementKit.TupleFrame(Row(2, null, null)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // lazy fetches: doc (resolves handles), then img (cached handles)
            BlobFetchReplies(clobData, firstOnConnection: true),
            BlobFetchReplies(blobData, firstOnConnection: false),
            // release reply
            SqliStatementKit.EotFrame());
    }

    [Fact]
    public void Clob_and_blob_columns_materialize_via_fastpath_and_lodata()
    {
        byte[] clobData = IfxWireConverter.EncodeChar("smart clob payload");
        byte[] blobData = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(clobData, blobData), out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, doc, img from smart_t";
        using var r = cmd.ExecuteReader();

        Assert.Equal(typeof(string), r.GetFieldType(1));
        Assert.Equal(typeof(byte[]), r.GetFieldType(2));
        Assert.Equal("Clob", r.GetDataTypeName(1));
        Assert.Equal("Blob", r.GetDataTypeName(2));

        Assert.True(r.Read());
        Assert.Equal(1, r.GetInt32(0));
        Assert.Equal("smart clob payload", r.GetString(1));
        Assert.Equal(blobData, (byte[])r.GetValue(2));
        // Repeated access must hit the per-row cache (the script has no more replies).
        Assert.Equal("smart clob payload", r.GetString(1));

        Assert.True(r.Read());
        long before = duplex.Output.Length;
        Assert.True(r.IsDBNull(1));
        Assert.True(r.IsDBNull(2));
        Assert.Equal(before, duplex.Output.Length); // NULL: no round trips

        Assert.False(r.Read());

        // The typed fetch announced the smart blob columns as SENDRECV(44) +
        // "informix"/"clob" + length 72 (IfxTypes.FromIfxTypeToSendRecv).
        byte[] sent = duplex.Output.ToArray();
        var expected = new List<byte> { 0, 44, 0, 8 };
        expected.AddRange(IfxWireConverter.EncodeChar("informix"));
        expected.AddRange([0, 4]);
        expected.AddRange(IfxWireConverter.EncodeChar("clob"));
        expected.AddRange([0, 0, 0, 72]);
        Assert.Contains(BitConverter.ToString(expected.ToArray()), BitConverter.ToString(sent));

        // The LO_READ request: SQ_LODATA(97) + subCom 0 + fd 39 + nbytes + bufsize 32000.
        var loRead = new byte[] { 0, 97, 0, 0, 0, 39, 0, 0, 0, (byte)clobData.Length, 0x7D, 0x00 };
        Assert.Contains(BitConverter.ToString(loRead), BitConverter.ToString(sent));
    }

    [Fact]
    public void First_fetch_resolves_routine_handles_then_reuses_them()
    {
        byte[] clobData = IfxWireConverter.EncodeChar("x");
        byte[] blobData = [1];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(clobData, blobData), out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, doc, img from smart_t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        _ = r.GetString(1);
        _ = r.GetValue(2);

        // Exactly four SQ_GETROUTINE(101) requests were sent (open/stat/seek/close),
        // all during the first value's fetch; the second value reused the handles.
        byte[] sent = duplex.Output.ToArray();
        int getRoutineCount = 0;
        string sig = "informix.ifx_lo_"; // appears once per GETROUTINE signature
        string hay = System.Text.Encoding.Latin1.GetString(sent);
        for (int i = 0; (i = hay.IndexOf(sig, i, StringComparison.Ordinal)) >= 0; i += sig.Length)
            getRoutineCount++;
        Assert.Equal(4, getRoutineCount);
    }

    // The insert reply script: LO create+write+close for one parameter, then the
    // ordinary prepare/execute/release exchange.
    private static byte[] InsertScript(byte[] locator, int payloadLength) => SqliStatementKit.Join(
        // ifx_lo_create: handle + (locator, fd)
        SqliStatementKit.RoutineHandleFrame("db", 66),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.FpReturnFrame(locator, 39),
        SqliStatementKit.EotFrame(),
        // LO_WRITE ack
        SqliStatementKit.LoDataWriteAckFrame(payloadLength),
        SqliStatementKit.EotFrame(),
        // ifx_lo_close: handle + rc
        SqliStatementKit.RoutineHandleFrame("db", 68),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.FpReturnFrame(0),
        SqliStatementKit.EotFrame(),
        // prepare reply
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        // execute reply
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        // release reply
        SqliStatementKit.EotFrame());

    [Fact]
    public void Clob_parameter_uploads_then_binds_the_locator()
    {
        byte[] locator = Locator(0x20);
        string text = "clob to insert";
        byte[] payload = IfxWireConverter.EncodeChar(text);

        using var cn = SqliStatementKit.OpenConnection(InsertScript(locator, payload.Length), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into smart_t values (1, ?, NULL)";
        cmd.Parameters.Add(new IfxParameter("doc", text) { IfxType = IfxType.Clob });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        string hex = BitConverter.ToString(sent);
        string hay = System.Text.Encoding.Latin1.GetString(sent);

        // 1. The create call went out with the 596-byte default lo spec.
        Assert.Contains("function informix.ifx_lo_create( ifx_lo_spec,integer,blob)", hay);
        byte[] specHead = { 0xDA, 0xDA, 0xFE, 0xED };
        Assert.Contains(BitConverter.ToString(specHead), hex);

        // 2. The payload streamed under SQ_LODATA subCom 2 (write), fd 39.
        var loWrite = new byte[] { 0, 97, 0, 2, 0, 39 };
        Assert.Contains(BitConverter.ToString(loWrite), hex);
        Assert.Contains(BitConverter.ToString(payload), hex);

        // 3. The INSERT bound the locator: type SENDRECV(44) + "informix"/"clob"
        //    + indicator 0 + precision 72 + [len:int32 = 72][locator].
        var bind = new List<byte> { 0, 44, 0, 8 };
        bind.AddRange(IfxWireConverter.EncodeChar("informix"));
        bind.AddRange([0, 4]);
        bind.AddRange(IfxWireConverter.EncodeChar("clob"));
        bind.AddRange([0, 0, 0, 72, 0, 0, 0, 72]);
        bind.AddRange(locator);
        Assert.Contains(BitConverter.ToString(bind.ToArray()), hex);

        // 4. Without SmartLobColumn the column's storage clause is not consulted.
        Assert.DoesNotContain("ifx_lo_col_sbinfo", hay);
    }

    [Fact]
    public void SmartLobColumn_creates_the_lob_with_the_spec_the_server_resolves_for_that_column()
    {
        // Live-verified: with the default spec a LOB lands in the default sbspace even when the
        // column says PUT b IN (sbspace2); with the spec ifx_lo_col_sbinfo returns it lands in
        // sbspace2. The server's answer is what must reach ifx_lo_create, untouched.
        byte[] locator = Locator(0x41);
        string text = "doc for sbspace2";
        byte[] payload = IfxWireConverter.EncodeChar(text);
        byte[] resolved = SqliSession.BuildDefaultLobDescriptor();
        IfxWireConverter.EncodeChar("sbspace2").CopyTo(resolved, 40);   // the sbspace name slot

        byte[] script = SqliStatementKit.Join(
            // ifx_lo_col_sbinfo: handle + (resolved spec, status)
            SqliStatementKit.RoutineHandleFrame("db", 65),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(resolved, 0),
            SqliStatementKit.EotFrame(),
            // then the usual create + write + close
            SqliStatementKit.RoutineHandleFrame("db", 66),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(locator, 39),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.LoDataWriteAckFrame(payload.Length),
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

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into smart_t values (1, ?, NULL)";
        cmd.Parameters.Add(new IfxParameter("doc", text) { IfxType = IfxType.Clob, SmartLobColumn = "smart_t.doc" });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        string hay = System.Text.Encoding.Latin1.GetString(sent);

        // the column was resolved first, by name, with the default spec as the template...
        int sbinfo = hay.IndexOf("function informix.ifx_lo_col_sbinfo(lvarchar, ifx_lo_spec)", StringComparison.Ordinal);
        int create = hay.IndexOf("function informix.ifx_lo_create( ifx_lo_spec,integer,blob)", StringComparison.Ordinal);
        Assert.True(sbinfo >= 0 && create > sbinfo, "col_sbinfo must be called before ifx_lo_create");
        Assert.Contains("smart_t.doc", hay[sbinfo..create]);
        Assert.DoesNotContain("sbspace2", hay[sbinfo..create]);            // the request carried the default spec

        // ...and ifx_lo_create received the server's resolved spec, not the default one.
        Assert.Contains("sbspace2", hay[create..]);
    }

    [Fact]
    public void An_unknown_column_fails_the_command_instead_of_silently_using_the_default_sbspace()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.RoutineHandleFrame("db", 65),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.ErrorReply(-206, "no_such.col"),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into smart_t values (1, ?, NULL)";
        cmd.Parameters.Add(new IfxParameter("doc", "x") { IfxType = IfxType.Clob, SmartLobColumn = "no_such.col" });

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(-206, ex.IfxSqlCode);
    }

    [Fact]
    public void Null_smart_blob_parameter_binds_a_typed_null_without_uploading()
    {
        // No LO create in the script: a NULL parameter must not touch FastPath.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into smart_t values (2, NULL, ?)";
        cmd.Parameters.Add(new IfxParameter("img", null) { IfxType = IfxType.Blob });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        // The bind is a typed NULL: type 44 + informix/blob + indicator -1 + precision 0.
        byte[] sent = duplex.Output.ToArray()[(int)before..];
        var expected = new List<byte> { 0, 44, 0, 8 };
        expected.AddRange(IfxWireConverter.EncodeChar("informix"));
        expected.AddRange([0, 4]);
        expected.AddRange(IfxWireConverter.EncodeChar("blob"));
        expected.AddRange([0xFF, 0xFF, 0, 0]);
        Assert.Contains(BitConverter.ToString(expected.ToArray()), BitConverter.ToString(sent));
    }

    [Fact]
    public void Server_error_during_fastpath_call_surfaces_as_IfxException()
    {
        // ifx_lo_open replies SQ_ERR (e.g. the LO was deleted): the exception must
        // carry the server sqlcode.
        byte[] script = SqliStatementKit.Join(
            PrepareReply(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(Row(1, Locator(0x10), Locator(0x50))),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // GETROUTINE(open) succeeds, the call itself errors
            SqliStatementKit.RoutineHandleFrame("db", 67),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.ErrorReply(-9810, "Smart-large-object error."),
            // release reply for the reader dispose
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, doc, img from smart_t";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        var ex = Assert.Throws<IfxException>(() => r.GetString(1));
        Assert.Equal(-9810, ex.IfxSqlCode);
    }

    [Fact]
    public void Default_lob_descriptor_matches_the_captured_jdbc_frame()
    {
        // Spot-check the 596-byte ifx_lo_spec against the captured reference:
        // magic, the two [1][0][-1] UDT longs, extent -1, and the two trailing -1s.
        byte[] spec = SqliSession.BuildDefaultLobDescriptor();
        Assert.Equal(596, spec.Length);
        Assert.Equal(new byte[] { 0xDA, 0xDA, 0xFE, 0xED }, spec[..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0xFF, 0xFF }, spec[12..22]); // est = -1
        Assert.Equal(new byte[] { 0, 0 }, spec[22..24]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0xFF, 0xFF }, spec[24..34]); // limit = -1
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, spec[36..40]);             // extent = -1
        Assert.All(spec[40..572], b => Assert.Equal(0, b));                            // name + col desc
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, spec[572..580]);
        Assert.All(spec[580..], b => Assert.Equal(0, b));
    }
}
