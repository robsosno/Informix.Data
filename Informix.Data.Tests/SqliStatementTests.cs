using System.Data;
using System.Data.Common;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Builds canned statement-pipeline reply frames. The wire layout matches the
/// capabilities negotiated by <see cref="SqliTestKit.BringUpReply"/>'s default
/// 9.59 protocol vector: extended (USVER) descriptors, 4-byte offsets and
/// int-sized tuple/fetch sizes.
/// </summary>
internal static class SqliStatementKit
{
    public record struct Col(string Name, short TypeWord, int Length, int StartPos,
        int ExtendedId = 0, string ExtOwner = "", string ExtName = "");

    /// <summary>A SQ_DESCRIBE frame (no trailing EOT) in the extended + 4-byte-offset layout.</summary>
    public static byte[] DescribeFrame(short stmtType, short stmtId, short tupleSize, params Col[] cols) =>
        DescribeFrame(stmtType, stmtId, tupleSize, largeTupleSize: false, cols);

    /// <summary>
    /// As above, but with <paramref name="largeTupleSize"/> the row width is written as a
    /// 4-byte int — the Informix 15 <c>isLargeTupleSize</c> shape.
    /// </summary>
    public static byte[] DescribeFrame(
        short stmtType, short stmtId, int tupleSize, bool largeTupleSize, params Col[] cols)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Describe);
        w.WriteInt16(stmtType);
        w.WriteInt16(stmtId);
        w.WriteInt32(0);          // estimated cost
        if (largeTupleSize)
            w.WriteInt32(tupleSize);
        else
            w.WriteInt16((short)tupleSize);
        w.WriteInt16((short)cols.Length);

        byte[] table = cols.SelectMany(c => IfxWireConverter.EncodeChar(c.Name + "\0")).ToArray();
        w.WriteInt32(table.Length);

        for (int i = 0; i < cols.Length; i++)
        {
            w.WriteInt32(i);                  // field index
            w.WriteInt32(cols[i].StartPos);   // column start position (USVER: int)
            w.WriteInt16(cols[i].TypeWord);
            w.WriteInt32(cols[i].ExtendedId); // extended id (LVARCHAR = 1)
            w.WriteChar(cols[i].ExtOwner);    // extended owner name
            w.WriteChar(cols[i].ExtName);     // extended type name
            w.WriteInt16(0);                  // reference
            w.WriteInt16(0);                  // alignment
            w.WriteInt32(0);                  // source type
            w.WriteInt32(cols[i].Length);
        }
        if (table.Length > 0)
            w.WritePadded(table);
        return ms.ToArray();
    }

    /// <summary>A SQ_DONE frame without the trailing EOT.</summary>
    public static byte[] DoneFrame(int rows = 0, short warnings = 0, int serial = 0)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Done);
        w.WriteInt16(warnings);
        w.WriteInt32(rows);
        w.WriteInt32(0); // rowid
        w.WriteInt32(serial);
        return ms.ToArray();
    }

    /// <summary>A SQ_COST frame (estimated rows/I-O), no trailing EOT. Live IDS 9.59
    /// traffic trails a successful SQ_DONE with one of these -- including after the
    /// LAST statement in a chained PDU, not just the first.</summary>
    public static byte[] CostFrame(int estimatedRows = 1, int estimatedIo = 1)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Cost);
        w.WriteInt32(estimatedRows);
        w.WriteInt32(estimatedIo);
        return ms.ToArray();
    }

    /// <summary>
    /// A SQ_INSERTDONE frame (SERIAL8, then BIGSERIAL when the server has granted
    /// <c>IsBigIntSupported</c> — true for the default 9.59 bring-up this kit scripts),
    /// no trailing EOT.
    /// </summary>
    public static byte[] InsertDoneFrame(long serial8, long bigSerial = 0, bool bigIntSupported = true)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.InsertDone);
        w.WriteInt8(serial8);
        if (bigIntSupported)
            w.WriteBigInt(bigSerial);
        return ms.ToArray();
    }

    /// <summary>A SQ_TUPLE frame carrying one row's bytes (int-sized length, padded).</summary>
    public static byte[] TupleFrame(byte[] row)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Tuple);
        w.WriteInt16(0); // warnings
        w.WriteInt32(row.Length);
        w.WritePadded(row);
        return ms.ToArray();
    }

    /// <summary>A SQ_TUPID frame carrying the absolute row id of the next tuple (scroll cursors).</summary>
    public static byte[] TupleIdFrame(int id)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.TupId);
        w.WriteInt32(id);
        return ms.ToArray();
    }

    /// <summary>A SQ_ERR frame + EOT in the negotiated no-64K-limit layout (int-sized offset).</summary>
    public static byte[] ErrorReply(short sqlcode, string message)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Err);
        w.WriteInt16(sqlcode);
        w.WriteInt16(0);  // isam code
        w.WriteInt32(0);  // statement offset (int: remove-64K-limit negotiated)
        w.WriteChar(message);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    public static byte[] CloseFrame() => [0, (byte)SqliMessage.Close];

    public static byte[] EotFrame() => [0, (byte)SqliMessage.Eot];

    /// <summary>
    /// A 56-byte in-tuple BYTE/TEXT descriptor in the <c>IfxBlob.fromIfx</c> layout:
    /// tb_start=int32 @8, tb_end=int32 @12, tb_size=int32 @16, tb_flags=int16 @38.
    /// A NULL blob sets the <c>BLOBISNULL</c> bit (1) of tb_flags; <paramref name="nullFlags"/>
    /// is the whole flags word a NULL descriptor carries (the server may set other bits too).
    /// </summary>
    public static byte[] BlobDescriptor(int length, bool isNull = false, short nullFlags = 1)
    {
        var d = new byte[56];
        if (isNull)
        {
            d[38] = (byte)(nullFlags >> 8);
            d[39] = (byte)nullFlags; // int16 @38, BLOBISNULL = bit 0x0001
            return d;
        }
        IfxWireConverter.WriteInt32(d.AsSpan(8, 4), 1);       // tb_start
        IfxWireConverter.WriteInt32(d.AsSpan(12, 4), length); // tb_end -> length = end-start+1
        IfxWireConverter.WriteInt32(d.AsSpan(16, 4), length); // tb_size (non-XPS echoes this back)
        return d;
    }

    /// <summary>A SQ_BLOB data frame: opcode + int16 chunk length + even-padded payload.</summary>
    public static byte[] BlobFrame(byte[] data)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.Blob);
        w.WriteInt16((short)data.Length);
        w.WritePadded(data);
        return ms.ToArray();
    }

    /// <summary>The zero-length SQ_BLOB frame the server sends to close a blob's data stream.</summary>
    public static byte[] BlobEndFrame() => [0, (byte)SqliMessage.Blob, 0, 0];

    // ---- smart-large-object (FastPath + SQ_LODATA) reply frames ----

    /// <summary>A SQ_GETROUTINE reply: the routine's db name + its FastPath handle (no EOT).</summary>
    public static byte[] RoutineHandleFrame(string db, int handle)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.GetRoutine);
        w.WriteChar(db);
        w.WriteInt32(handle);
        return ms.ToArray();
    }

    /// <summary>A SQ_FPROUTINE reply frame carrying typed return values (no EOT).</summary>
    public static byte[] FpReturnFrame(params object[] values)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.FpRoutine);
        w.WriteInt16((short)values.Length);
        foreach (object v in values)
        {
            switch (v)
            {
                case int i:
                    w.WriteInt16(2);      // INT
                    w.WriteInt16(0);      // indicator
                    w.WriteInt16(2560);   // IfxInteger.getEncodedLength magic
                    w.WriteInt32(i);
                    break;
                case long l:
                    w.WriteInt16(17);     // INT8
                    w.WriteInt16(0);
                    w.WriteInt16(4864);
                    w.WriteInt8(l);
                    break;
                case byte[] udt:
                    w.WriteInt16(40);     // UDTVAR (as captured: empty owner/name)
                    w.WriteChar("");
                    w.WriteChar("");
                    w.WriteInt16(0);
                    w.WriteInt16(0);
                    w.WriteInt32(udt.Length);
                    w.WritePadded(udt);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported FP test value {v.GetType()}");
            }
        }
        return ms.ToArray();
    }

    /// <summary>A SQ_LODATA read reply: opType 0 + total size + one chunk per call (no EOT).</summary>
    public static byte[] LoDataReadFrame(byte[] data)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.LoData);
        w.WriteInt16(0);           // read reply
        w.WriteInt32(data.Length); // total size
        w.WriteInt16((short)data.Length);
        w.WritePadded(data);
        return ms.ToArray();
    }

    /// <summary>A SQ_LODATA write acknowledgement: opType 2 + bytes written (no EOT).</summary>
    public static byte[] LoDataWriteAckFrame(int written)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.LoData);
        w.WriteInt16(2);
        w.WriteInt32(written);
        return ms.ToArray();
    }

    /// <summary>
    /// A 644-byte <c>ifx_lo_stat</c> block whose size field is set: magic at [0..4),
    /// then the size as an IfxUDTInput long [low32][high32][sign:int16] at [4..14).
    /// </summary>
    public static byte[] LoStatBlock(long size)
    {
        var b = new byte[644];
        IfxWireConverter.WriteInt32(b.AsSpan(0, 4), unchecked((int)0xFEDABABE));
        IfxWireConverter.WriteInt32(b.AsSpan(4, 4), (int)(size & 0xFFFFFFFF));
        IfxWireConverter.WriteInt32(b.AsSpan(8, 4), (int)(size >> 32));
        b[12] = 0; b[13] = 1; // sign = +1
        return b;
    }

    /// <summary>An in-tuple smart-blob field: [flag:1][len:int32][locator]; NULL when locator is null.</summary>
    public static byte[] SmartBlobField(byte[]? locator)
    {
        if (locator is null)
            return [1, 0, 0, 0, 0];
        var f = new byte[5 + locator.Length];
        IfxWireConverter.WriteInt32(f.AsSpan(1, 4), locator.Length);
        locator.CopyTo(f.AsSpan(5));
        return f;
    }

    public static byte[] Join(params byte[][] frames) => frames.SelectMany(f => f).ToArray();

    /// <summary>An open IfxConnection over a scripted duplex transport.
    /// <paramref name="extraSettings"/> appends extra connection-string keys
    /// (e.g. <c>"Fetch Buffer Size=65536"</c>).</summary>
    public static IfxConnection OpenConnection(byte[] statementScript, out DuplexStream duplex,
        string? extraSettings = null)
    {
        byte[] script = AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .Concat(statementScript)
            .ToArray();
        duplex = new DuplexStream(script);
        var local = duplex;

        // Pooling=false: each test drives its own one-shot scripted transport, so the
        // physical connection must not be returned to (and reused from) the static pool.
        var cn = new IfxConnection(
            "Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false"
            + (extraSettings is null ? "" : ";" + extraSettings));
        cn.AsfConnectionFactory = s => new AsfConnection(s, local);
        cn.Open();
        return cn;
    }
}

public class SqliPrepareDescribeTests
{
    [Fact]
    public void Prepare_parses_the_extended_describe_reply()
    {
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 7, tupleSize: 12,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("name", (short)SqliTypeCode.Char, 8, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5").Concat(reply).ToArray(), out _);
        session.BringUp();

        session.Prepare("select id, name from t");

        Assert.Equal(7, session.StatementId);
        Assert.Equal((short)SqliStatementType.Select, session.StatementType);
        Assert.True(session.DescribesResultSet);
        Assert.Equal(12, session.TupleSize);
        Assert.Equal(2, session.Columns.Count);
        Assert.Equal("id", session.Columns[0].Name);
        Assert.Equal(SqliTypeCode.Int, session.Columns[0].SqliTypeCode);
        Assert.Equal(0, session.Columns[0].StartPosition);
        Assert.Equal("name", session.Columns[1].Name);
        Assert.Equal(SqliTypeCode.Char, session.Columns[1].SqliTypeCode);
        Assert.Equal(4, session.Columns[1].StartPosition);
        Assert.Equal(8, session.Columns[1].ByteLength);
    }

    [Fact]
    public void Prepare_reads_a_four_byte_tuple_size_under_large_tuple_size()
    {
        // Grant only 0x04 (isLargeTupleSize) so SQ_DONE keeps its 4-byte shape: the two
        // ninth-byte bits are independent, and this isolates the describe change.
        // 40000 is past the signed-short range, so a 2-byte read could not produce it.
        const int wideRow = 40000;
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 7, tupleSize: wideRow, largeTupleSize: true,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());
        byte[] bringUp = SqliTestKit.ProtocolsReply([0xbd, 0xbe, 0x9f, 0xfe, 0x7f, 0xbf, 0xff, 0xef, 0x04])
            .Concat(SqliTestKit.VersionReply("15.0.FC1"))
            .ToArray();

        var session = SqliTestKit.SessionOver(
            bringUp.Concat(reply).ToArray(), out _, requestLargeRowFormats: true);
        session.BringUp();
        Assert.True(session.Capabilities.IsLargeTupleSize);
        Assert.False(session.Capabilities.IsLongRowId);

        session.Prepare("select id from t");

        Assert.Equal(wideRow, session.TupleSize);
        Assert.Single(session.Columns);
    }

    [Fact]
    public void Prepare_reads_the_classic_describe_when_the_server_sets_the_large_row_bits_unasked()
    {
        // The Informix 15.0.1.0 case: both ninth-byte bits come back although the client
        // sent the eight-byte vector, and the frames stay in the classic shape (2-byte
        // tuple size, 4-byte SQ_DONE counts). The prepare must parse them as such.
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 7, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());
        byte[] bringUp = SqliTestKit.ProtocolsReply(SqliTestKit.LargeRowGrantVector)
            .Concat(SqliTestKit.VersionReply("15.0.1.0"))
            .ToArray();

        var session = SqliTestKit.SessionOver(bringUp.Concat(reply).ToArray(), out _);
        session.BringUp();

        session.Prepare("select id from t");

        Assert.Equal(4, session.TupleSize);
        Assert.Single(session.Columns);
        Assert.Equal("id", session.Columns[0].Name);
    }

    [Fact]
    public void Prepare_of_a_non_query_reports_no_result_set()
    {
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 3, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5").Concat(reply).ToArray(), out _);
        session.BringUp();

        session.Prepare("insert into t values (1)");

        Assert.False(session.DescribesResultSet);
        Assert.Empty(session.Columns);
    }
}

public class SqliQueryExecutionTests
{
    private static byte[] FixedRow(int id, string name8)
    {
        var row = new byte[12];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), id);
        IfxWireConverter.EncodeChar(name8.PadRight(8)).CopyTo(row.AsSpan(4));
        return row;
    }

    private static byte[] NullRow()
    {
        var row = new byte[12];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), int.MinValue); // INT null sentinel
        return row; // name bytes stay 0 -> CHAR null sentinel
    }

    private static byte[] SelectScript(params byte[][] rows)
    {
        byte[] tuples = SqliStatementKit.Join(rows.Select(SqliStatementKit.TupleFrame).ToArray());
        return SqliStatementKit.Join(
            // prepare reply: describe + done + eot
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 12,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("name", (short)SqliTypeCode.Char, 8, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // open + fetch reply (single round trip for fixed-layout rows)
            SqliStatementKit.DoneFrame(),
            tuples,
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());
    }

    [Fact]
    public void ExecuteReader_streams_rows_and_decodes_nulls()
    {
        using var cn = SqliStatementKit.OpenConnection(
            SelectScript(FixedRow(101, "alpha"), NullRow()), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";

        using var reader = cmd.ExecuteReader();

        Assert.Equal(2, reader.FieldCount);
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal(typeof(int), reader.GetFieldType(0));

        Assert.True(reader.Read());
        Assert.Equal(101, reader.GetInt32(0));
        Assert.Equal("alpha   ", reader.GetString(1));

        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));

        Assert.False(reader.Read());
    }

    [Fact]
    public void ExecuteScalar_returns_the_first_value()
    {
        using var cn = SqliStatementKit.OpenConnection(
            SelectScript(FixedRow(42, "x")), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, name from t";

        Assert.Equal(42, cmd.ExecuteScalar());
    }

    [Fact]
    public void Varchar_rows_are_walked_per_row()
    {
        byte[] describe = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 21,
                new SqliStatementKit.Col("v", (short)SqliTypeCode.VarChar, 20, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        // Var-length rows: [len][data...] with only the actual bytes on the wire.
        byte[] rowAbc = [3, (byte)'a', (byte)'b', (byte)'c'];
        byte[] rowNull = [1, 0];
        byte[] rowEmpty = [0];

        byte[] script = SqliStatementKit.Join(
            describe,
            // open reply (separate round trip when the row layout is variable)
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // typed-fetch reply
            SqliStatementKit.TupleFrame(rowAbc),
            SqliStatementKit.TupleFrame(rowNull),
            SqliStatementKit.TupleFrame(rowEmpty),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long beforeCommand = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("abc", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.Read());
        Assert.Equal("", reader.GetString(0));
        Assert.False(reader.Read());

        // The fetch was typed: the client announced SQ_RETTYPE (100) before SQ_NFETCH (9).
        byte[] sent = duplex.Output.ToArray()[(int)beforeCommand..];
        Assert.Contains((byte)SqliMessage.RetType, sent);
    }
}

public class SqliNonQueryTests
{
    [Fact]
    public void ExecuteNonQuery_without_parameters_takes_the_single_trip_command_path()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 3),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";

        Assert.Equal(3, cmd.ExecuteNonQuery());

        // One PDU: SQ_COMMAND(1) + 0 values + text + SQ_NDESCRIBE(22) + SQ_EXECUTE(7) + SQ_RELEASE(11) + SQ_EOT.
        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(1, sent[0] << 8 | sent[1]);
        Assert.Equal(0, sent[2] << 8 | sent[3]);
        byte[] tail = sent[^8..];
        Assert.Equal(new byte[] { 0, 22, 0, 7, 0, 11, 0, 12 }, tail);
    }

    [Fact]
    public void ExecuteNonQuery_with_parameters_prepares_binds_and_executes()
    {
        byte[] script = SqliStatementKit.Join(
            // prepare reply
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // execute reply
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?, ?)";
        cmd.Parameters.Add("p1", 42);
        cmd.Parameters.Add("p2", "abc");

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // The execute PDU carries SQ_ID(4)+id(9), then SQ_BIND(5)+count(2), the
        // INT value (type 2, indicator 0, precision 4, 4 bytes) and the CHAR
        // value (type 0, indicator 0, precision 0, then the length-prefixed
        // "abc" [00 03 'a' 'b' 'c'] padded to even).
        byte[] expectedBind =
        [
            0, 4, 0, 9,
            0, 5, 0, 2,
            0, 2, 0, 0, 0, 4, 0, 0, 0, 42,
            0, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 0,
            0, 7,
        ];
        Assert.Contains(
            BitConverter.ToString(expectedBind),
            BitConverter.ToString(sent));
    }

    [Fact]
    public void ExecuteNonQuery_snapshots_LastSerial_from_the_done_frame()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1, serial: 77),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t(a) values (1)";

        cmd.ExecuteNonQuery();

        Assert.Equal(77, cmd.LastSerial);
        Assert.Equal(0L, cmd.LastSerial8);
        Assert.Equal(0L, cmd.LastBigSerial);
        Assert.Equal(IfxSerialKind.Serial, cmd.LastSerialKind);
    }

    [Fact]
    public void LastSerialKind_is_None_before_execution_and_for_a_statement_without_a_serial()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 3),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        Assert.Equal(IfxSerialKind.None, cmd.LastSerialKind);

        cmd.CommandText = "update t set a = 2";
        cmd.ExecuteNonQuery();

        Assert.Equal(IfxSerialKind.None, cmd.LastSerialKind);
    }

    [Fact]
    public void LastSerialKind_reports_Serial8_when_only_a_serial8_comes_back()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 4, tupleSize: 0),
            SqliStatementKit.InsertDoneFrame(serial8: 500, bigSerial: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t(a) values (1)";
        cmd.ExecuteNonQuery();

        Assert.Equal(IfxSerialKind.Serial8, cmd.LastSerialKind);
    }

    [Fact]
    public void ExecuteNonQuery_snapshots_LastSerial8_and_LastBigSerial_from_insertdone()
    {
        byte[] script = SqliStatementKit.Join(
            // prepare reply
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // execute reply: SQ_INSERTDONE then SQ_DONE, per the wire order the bulk
            // loader's close sequence already documents (SQ_INSERTDONE + SQ_DONE(#rows)).
            SqliStatementKit.InsertDoneFrame(serial8: 500, bigSerial: 9_000_000_000),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?, ?)";
        cmd.Parameters.Add("p1", 42);
        cmd.Parameters.Add("p2", "abc");

        cmd.ExecuteNonQuery();

        Assert.Equal(500L, cmd.LastSerial8);
        Assert.Equal(9_000_000_000L, cmd.LastBigSerial);
        Assert.Equal(IfxSerialKind.BigSerial, cmd.LastSerialKind);
    }

    [Fact]
    public void ExecuteNonQuery_does_not_leak_a_stale_serial8_from_a_prior_insert_into_a_later_update()
    {
        // Regression test: Serial8Inserted/BigSerialInserted only arrive via
        // SQ_INSERTDONE, which the server sends solely for an insert into a
        // SERIAL8/BIGSERIAL column. Without an explicit reset at the start of each
        // execute path, a later statement whose reply carries no SQ_INSERTDONE (like
        // this UPDATE) would keep surfacing the INSERT's stale values.
        byte[] script = SqliStatementKit.Join(
            // INSERT (single-trip immediate path): describe + insertdone + done + eot
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 4, tupleSize: 0),
            SqliStatementKit.InsertDoneFrame(serial8: 500, bigSerial: 9_000_000_000),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // UPDATE (single-trip immediate path): describe + done (no insertdone) + eot
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 5, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var insertCmd = cn.CreateCommand();
        insertCmd.CommandText = "insert into t(a) values (1)";
        insertCmd.ExecuteNonQuery();
        Assert.Equal(500L, insertCmd.LastSerial8);
        Assert.Equal(9_000_000_000L, insertCmd.LastBigSerial);

        using var updateCmd = cn.CreateCommand();
        updateCmd.CommandText = "update t set a = 2";
        updateCmd.ExecuteNonQuery();

        Assert.Equal(0L, updateCmd.LastSerial8);
        Assert.Equal(0L, updateCmd.LastBigSerial);
        Assert.Equal(IfxSerialKind.None, updateCmd.LastSerialKind);
    }

    [Fact]
    public void Bound_packed_values_keep_their_length_prefix()
    {
        // PackedValue.toIfx == convIfx(prependLength: true): the SQ_BIND payload
        // for DECIMAL/DATETIME starts with a 2-byte length word. Sending the
        // bare packed bytes makes the server mis-read the frame and hang
        // (live-diagnosed against IDS 9.59).
        var dec = SqliBindValue.From(123.45m, IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.Decimal, dec.Type);
        Assert.Equal(2, dec.Precision); // scale
        Assert.Equal(new byte[] { 0x00, 0x04, 0xc2, 0x01, 0x17, 0x2d }, dec.Data);

        var dt = SqliBindValue.From(new DateTime(2026, 7, 7, 12, 30, 15), IfxWireConverter.DefaultEncoding);
        Assert.Equal(SqliTypeCode.DateTime, dt.Type);
        int declaredLength = (dt.Data![0] << 8) | dt.Data[1];
        Assert.Equal(dt.Data.Length - 2, declaredLength);
    }

    [Fact]
    public void Server_error_during_prepare_surfaces_and_releases_cleanly()
    {
        byte[] script = SqliStatementKit.ErrorReply(-206, "The specified table (t) is not in the database.");

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select * from t";

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteReader());
        Assert.Equal(-206, ex.IfxSqlCode);
    }
}

public class SqliBlobTests
{
    // Assembles a one-blob-column result set. The SQ_FETCHBLOB reply is placed
    // after the select reply because a simple large object is fetched lazily, on
    // first column access. Layout is fixed (the 56-byte descriptor) so the open +
    // fetch take a single round trip, exactly like any other fixed-width row.
    private static byte[] BlobSelectScript(short ifxType, byte[] descriptor, params byte[][] blobReplyFrames)
    {
        return SqliStatementKit.Join(
            // prepare reply
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 5, tupleSize: 56,
                new SqliStatementKit.Col("d", ifxType, 56, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // open + fetch reply
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(descriptor),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // blob fetch reply (empty for a NULL blob — the driver must not ask)
            SqliStatementKit.Join(blobReplyFrames),
            // release reply
            SqliStatementKit.EotFrame());
    }

    [Fact]
    public void Text_blob_column_is_fetched_and_decoded_to_string()
    {
        byte[] text = IfxWireConverter.EncodeChar("Brown leather. Specify style.");
        byte[] script = BlobSelectScript(
            (short)SqliTypeCode.Text,
            SqliStatementKit.BlobDescriptor(text.Length),
            SqliStatementKit.BlobFrame(text),
            SqliStatementKit.BlobEndFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_descr from catalog";
        using var r = cmd.ExecuteReader();

        Assert.Equal(typeof(string), r.GetFieldType(0));
        Assert.Equal("Text", r.GetDataTypeName(0));
        Assert.True(r.Read());
        Assert.False(r.IsDBNull(0));
        Assert.Equal("Brown leather. Specify style.", r.GetString(0));
        Assert.False(r.Read());
    }

    [Fact]
    public void Byte_blob_column_is_fetched_and_decoded_to_bytes()
    {
        byte[] data = [0xde, 0xad, 0xbe, 0xef, 0x01]; // odd length exercises the pad byte
        byte[] script = BlobSelectScript(
            (short)SqliTypeCode.Byte,
            SqliStatementKit.BlobDescriptor(data.Length),
            SqliStatementKit.BlobFrame(data),
            SqliStatementKit.BlobEndFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_picture from catalog";
        using var r = cmd.ExecuteReader();

        Assert.Equal(typeof(byte[]), r.GetFieldType(0));
        Assert.True(r.Read());
        Assert.Equal(data, (byte[])r.GetValue(0));

        // GetBytes streams a slice of the materialized buffer.
        byte[] slice = new byte[2];
        long n = r.GetBytes(0, 1, slice, 0, 2);
        Assert.Equal(2, n);
        Assert.Equal(new byte[] { 0xad, 0xbe }, slice);
    }

    [Fact]
    public void Null_blob_returns_dbnull_without_a_fetch_round_trip()
    {
        // A NULL descriptor (tb_flags == BLOBISNULL) must short-circuit: issuing
        // SQ_FETCHBLOB for it would block the server on a phantom locator.
        byte[] script = BlobSelectScript(
            (short)SqliTypeCode.Text,
            SqliStatementKit.BlobDescriptor(0, isNull: true));

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_descr from catalog";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        long before = duplex.Output.Length;
        Assert.True(r.IsDBNull(0));
        Assert.Equal(DBNull.Value, r.GetValue(0));
        Assert.Equal(before, duplex.Output.Length); // nothing sent: no SQ_FETCHBLOB
        Assert.False(r.Read());
    }

    [Fact]
    public void Null_blob_is_recognised_when_tb_flags_carries_other_bits_too()
    {
        // Informix 15.0.1.0 marks a NULL BYTE/TEXT with tb_flags 0x0021 in its 68-byte
        // descriptor: BLOBISNULL plus 0x20. Comparing the word to 1 missed it, and the
        // SQ_FETCHBLOB that followed came back as -19986. Only the flags word is the
        // point here, so the frames stay in the classic 56-byte shape.
        byte[] script = BlobSelectScript(
            (short)SqliTypeCode.Text,
            SqliStatementKit.BlobDescriptor(0, isNull: true, nullFlags: 0x0021));

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_descr from catalog";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        long before = duplex.Output.Length;
        Assert.True(r.IsDBNull(0));
        Assert.Equal(DBNull.Value, r.GetValue(0));
        Assert.Equal(before, duplex.Output.Length); // nothing sent: no SQ_FETCHBLOB
        Assert.False(r.Read());
    }

    [Fact]
    public void Repeated_access_fetches_the_blob_once()
    {
        byte[] text = IfxWireConverter.EncodeChar("cached");
        byte[] script = BlobSelectScript(
            (short)SqliTypeCode.Text,
            SqliStatementKit.BlobDescriptor(text.Length),
            SqliStatementKit.BlobFrame(text),
            SqliStatementKit.BlobEndFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_descr from catalog";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        Assert.Equal("cached", r.GetString(0));
        long afterFirstFetch = duplex.Output.Length;
        // The script provides exactly one blob reply; a second fetch would run
        // off the end of it. Both the value and the byte count prove the cache.
        Assert.Equal("cached", r.GetString(0));
        Assert.Equal("cached", r.GetString(0));
        Assert.Equal(afterFirstFetch, duplex.Output.Length);
    }

    // The execute reply used by the bind tests: prepare -> execute(1 row) -> release.
    private static byte[] InsertScript() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    [Fact]
    public void Byte_parameter_binds_descriptor_then_sq_bbind_payload()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add("b", new byte[] { 0x01, 0x02, 0x03 });

        Assert.Equal(1, cmd.ExecuteNonQuery());

        // The 56-byte descriptor is all zero except tb_size (=3) at [16..20).
        var descriptor = new byte[56];
        IfxWireConverter.WriteInt32(descriptor.AsSpan(16, 4), 3);
        var expected = new List<byte>();
        expected.AddRange(new byte[] { 0, 4, 0, 9 });      // SQ_ID + statement id 9
        expected.AddRange(new byte[] { 0, 5, 0, 1 });      // SQ_BIND + count 1
        expected.AddRange(new byte[] { 0, 11, 0, 0, 0, 56 }); // type BYTE(11), indicator 0, precision 56
        expected.AddRange(descriptor);                     // 56-byte descriptor (even -> no pad)
        expected.AddRange(new byte[] { 0, 41, 0, 1 });     // SQ_BBIND(41) + blob count 1
        expected.AddRange(new byte[] { 0, 39, 0, 3, 1, 2, 3, 0 }); // SQ_BLOB(39) + size 3 + data + pad
        expected.AddRange(new byte[] { 0, 39, 0, 0 });     // SQ_BLOB terminator
        expected.AddRange(new byte[] { 0, 7 });            // SQ_EXECUTE

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Contains(BitConverter.ToString(expected.ToArray()), BitConverter.ToString(sent));
    }

    [Fact]
    public void Large_blob_payload_is_chunked_into_1024_byte_frames()
    {
        byte[] payload = new byte[1500]; // 1024 + 476 -> two SQ_BLOB data frames
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add("b", payload);

        cmd.ExecuteNonQuery();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        string hex = BitConverter.ToString(sent);
        // frame headers: SQ_BLOB + 1024 (0x0400), SQ_BLOB + 476 (0x01DC), SQ_BLOB + 0.
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 39, 0x04, 0x00 }), hex);
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 39, 0x01, 0xDC }), hex);
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 39, 0, 0 }), hex);
    }

    [Fact]
    public void Text_string_binds_as_a_text_large_object_when_hinted()
    {
        byte[] text = IfxWireConverter.EncodeChar("note");
        using var cn = SqliStatementKit.OpenConnection(InsertScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add(new IfxParameter("t", "note") { IfxType = IfxType.Text });

        cmd.ExecuteNonQuery();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        string hex = BitConverter.ToString(sent);
        // TEXT param type is 12, and its payload rides in the SQ_BBIND SQ_BLOB frame.
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 12, 0, 0, 0, 56 }), hex); // type TEXT, ind 0, prec 56
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 41, 0, 1 }), hex);        // SQ_BBIND + count 1
        Assert.Contains(BitConverter.ToString(SqliStatementKit.BlobFrame(text)), hex);  // SQ_BLOB + "note"
    }

    [Fact]
    public void Server_error_during_blob_fetch_surfaces()
    {
        // A server-side error during the fetch arrives as SQ_ERR on the blob
        // round trip (sqlcode is the 16-bit wire field, not a client -797xx code).
        byte[] script = BlobSelectScript(
            (short)SqliTypeCode.Text,
            SqliStatementKit.BlobDescriptor(10),
            SqliStatementKit.ErrorReply(-242, "could not open database table"));

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select cat_descr from catalog";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        var ex = Assert.Throws<IfxException>(() => r.GetString(0));
        Assert.Equal(-242, ex.IfxSqlCode);
    }
}

public class SqliLvarcharTests
{
    // LVARCHAR tuple field: [flag:1][length:int32 big-endian][data]. flag 1 => NULL.
    private static byte[] Lvc(string? value)
    {
        if (value is null)
            return [1, 0, 0, 0, 0];
        byte[] data = IfxWireConverter.EncodeChar(value);
        var field = new byte[5 + data.Length];
        IfxWireConverter.WriteInt32(field.AsSpan(1, 4), data.Length);
        data.CopyTo(field.AsSpan(5));
        return field;
    }

    [Fact]
    public void Lvarchar_column_is_decoded_and_the_fetch_is_typed()
    {
        // LVARCHAR arrives as the opaque built-in UdtVar(40) + extended id LVARCHAR(1).
        byte[] describe = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 0,
                new SqliStatementKit.Col("v", (short)SqliTypeCode.UdtVar, 500, 0,
                    ExtendedId: 1, ExtOwner: "informix", ExtName: "lvarchar")),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        byte[] script = SqliStatementKit.Join(
            describe,
            // open reply (separate round trip: the row layout is variable)
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // typed-fetch reply
            SqliStatementKit.TupleFrame(Lvc("short one")),
            SqliStatementKit.TupleFrame(Lvc(new string('a', 150))),
            SqliStatementKit.TupleFrame(Lvc(null)),
            SqliStatementKit.TupleFrame(Lvc("")),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long beforeCommand = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from lvctest";

        using var reader = cmd.ExecuteReader();
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("LVarChar", reader.GetDataTypeName(0));

        Assert.True(reader.Read());
        Assert.Equal("short one", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal(150, reader.GetString(0).Length);
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.Read());
        Assert.Equal("", reader.GetString(0));
        Assert.False(reader.Read());

        // The typed fetch announced SQ_RETTYPE with LVARCHAR(43) and the extended
        // owner/type-name strings, then the length — the exact wire the server needs.
        byte[] sent = duplex.Output.ToArray()[(int)beforeCommand..];
        string hex = BitConverter.ToString(sent);
        var retTypeExt = new List<byte> { 0, 43 };                 // type LVARCHAR(43)
        retTypeExt.AddRange(new byte[] { 0, 8 });                  // owner length 8
        retTypeExt.AddRange(IfxWireConverter.EncodeChar("informix"));
        retTypeExt.AddRange(new byte[] { 0, 8 });                  // type-name length 8
        retTypeExt.AddRange(IfxWireConverter.EncodeChar("lvarchar"));
        retTypeExt.AddRange(new byte[] { 0, 0, 0x01, 0xF4 });      // length 500
        Assert.Contains(BitConverter.ToString(retTypeExt.ToArray()), hex);
    }
}

public class SqliReaderMetadataTests
{
    private static byte[] TwoIntRow(int a, int b)
    {
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), a);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), b);
        return row;
    }

    private static byte[] SelectScript(bool includeExitReply, params byte[][] rows)
    {
        byte[] tuples = SqliStatementKit.Join(rows.Select(SqliStatementKit.TupleFrame).ToArray());
        var frames = new List<byte[]>
        {
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),          // open reply
            tuples,
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame(),            // release reply
        };
        if (includeExitReply)
            frames.Add(SqliStatementKit.EotFrame()); // reply to SQ_EXIT from Close()
        return SqliStatementKit.Join(frames.ToArray());
    }

    [Fact]
    public void HasRows_reflects_the_first_fetch()
    {
        using var cnRows = SqliStatementKit.OpenConnection(SelectScript(false, TwoIntRow(1, 2)), out _);
        using var cmdRows = cnRows.CreateCommand();
        cmdRows.CommandText = "select a, b from t";
        using (var r = cmdRows.ExecuteReader())
            Assert.True(r.HasRows);

        using var cnEmpty = SqliStatementKit.OpenConnection(SelectScript(false), out _);
        using var cmdEmpty = cnEmpty.CreateCommand();
        cmdEmpty.CommandText = "select a, b from t";
        using (var r = cmdEmpty.ExecuteReader())
        {
            Assert.False(r.HasRows);
            Assert.False(r.Read());
        }
    }

    [Fact]
    public void GetSchemaTable_describes_the_columns()
    {
        using var cn = SqliStatementKit.OpenConnection(SelectScript(false, TwoIntRow(1, 2)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";
        using var reader = cmd.ExecuteReader();

        DataTable schema = reader.GetSchemaTable();
        Assert.Equal(2, schema.Rows.Count);
        Assert.Equal("a", schema.Rows[0][SchemaTableColumn.ColumnName]);
        Assert.Equal(0, schema.Rows[0][SchemaTableColumn.ColumnOrdinal]);
        Assert.Equal(typeof(int), schema.Rows[0][SchemaTableColumn.DataType]);
        Assert.Equal((int)IfxType.Integer, schema.Rows[1][SchemaTableColumn.ProviderType]);
        Assert.False((bool)schema.Rows[0][SchemaTableColumn.IsLong]);
    }

    [Fact]
    public void SingleRow_behavior_surfaces_only_the_first_row()
    {
        using var cn = SqliStatementKit.OpenConnection(
            SelectScript(false, TwoIntRow(1, 2), TwoIntRow(3, 4)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";
        using var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(reader.Read()); // second row withheld by SingleRow
    }

    [Fact]
    public void SchemaOnly_describes_without_opening_a_cursor()
    {
        // Only a prepare + release reply — no open/fetch is scripted, so the reader
        // must not attempt to open a server cursor.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";
        using var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly);

        Assert.Equal(2, reader.FieldCount);
        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
        DataTable? schema = reader.GetSchemaTable();
        Assert.NotNull(schema);
        Assert.Equal(2, schema.Rows.Count);
    }

    [Fact]
    public void CloseConnection_behavior_closes_the_connection_with_the_reader()
    {
        using var cn = SqliStatementKit.OpenConnection(SelectScript(includeExitReply: true, TwoIntRow(1, 2)), out _);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";

        var reader = cmd.ExecuteReader(CommandBehavior.CloseConnection);
        Assert.Equal(ConnectionState.Open, cn.State);
        reader.Dispose();
        Assert.Equal(ConnectionState.Closed, cn.State);
    }
}

public class SqliBatchTests
{
    private static int CountPairs(byte[] b, byte hi, byte lo)
    {
        int n = 0;
        for (int i = 0; i + 1 < b.Length; i++)
            if (b[i] == hi && b[i + 1] == lo)
                n++;
        return n;
    }

    [Fact]
    public void Batch_prepares_once_and_chains_execute_per_row()
    {
        byte[] script = SqliStatementKit.Join(
            // prepare reply
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // batch execute reply: one SQ_DONE per row, then a single EOT
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        var batch = cn.CreateRowBatch("insert into t values (?)");
        batch.AddRow(1);
        batch.AddRow(2);
        batch.AddRow(3);

        int[] counts = batch.Execute();

        Assert.Equal(new[] { 1, 1, 1 }, counts);
        // Three SQ_EXECUTE(7) frames were chained (int binds never contain 00 07).
        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(3, CountPairs(sent, 0x00, 0x07));
    }

    [Fact]
    public void Empty_batch_returns_no_counts()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        var batch = cn.CreateRowBatch("insert into t values (?)");
        Assert.Empty(batch.Execute()); // returns before any wire I/O
    }
}

public class SqliStoredProcedureTests
{
    [Fact]
    public void StoredProcedure_command_is_wrapped_as_execute_procedure()
    {
        // A non-returning procedure describes as ExecProc with no columns.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.ExecProc, stmtId: 3, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 0),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "do_work";
        cmd.Parameters.Add("a", 1);
        cmd.Parameters.Add("b", 2);

        cmd.ExecuteNonQuery();

        string sent = System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("execute procedure do_work(?, ?)", sent);
    }

    [Fact]
    public void Returning_procedure_fills_output_parameters_and_counts_only_inputs()
    {
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), 19);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), 21);

        byte[] script = SqliStatementKit.Join(
            // prepare reply: ExecProc with two returned INT columns
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.ExecProc, stmtId: 4, tupleSize: 8,
                new SqliStatementKit.Col("lo", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("hi", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // open + fetch reply (fixed layout -> single round trip)
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "minmax";
        cmd.Parameters.Add("x", 20); // the only input -> one placeholder
        var lo = new IfxParameter("lo", 0) { Direction = ParameterDirection.Output };
        var hi = new IfxParameter("hi", 0) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(lo);
        cmd.Parameters.Add(hi);

        int n = cmd.ExecuteNonQuery();

        Assert.Equal(1, n);
        Assert.Equal(19, lo.Value);
        Assert.Equal(21, hi.Value);
        // Output parameters are excluded from the placeholder count.
        string sent = System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("execute procedure minmax(?)", sent);
    }

    [Fact]
    public void Returning_procedure_is_readable_and_scalar()
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), 7);

        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.ExecProc, stmtId: 6, tupleSize: 4,
                new SqliStatementKit.Col("r", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "add_two";
        cmd.Parameters.Add("a", 3);
        cmd.Parameters.Add("b", 4);

        Assert.Equal(7, cmd.ExecuteScalar());
    }
}

public class SqliTransactionTests
{
    [Fact]
    public void Begin_commit_send_the_transaction_opcodes()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),  // begin reply
            SqliStatementKit.EotFrame()); // commit reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        using (var tx = cn.BeginTransaction())
            tx.Commit();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // SQ_BEGIN(35)+EOT then SQ_CMMTWORK(19)+EOT.
        Assert.Equal(new byte[] { 0, 35, 0, 12, 0, 19, 0, 12 }, sent);
    }

    [Fact]
    public void Rollback_sends_sq_rbwork()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),  // begin reply
            SqliStatementKit.EotFrame()); // rollback reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        using (var tx = cn.BeginTransaction())
            tx.Rollback();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // SQ_BEGIN(35)+EOT then SQ_RBWORK(20) + savepoint word 0 + EOT — unlike commit,
        // rollback carries the extra int16 (sendRollback); the server hangs without it.
        Assert.Equal(new byte[] { 0, 35, 0, 12, 0, 20, 0, 0, 0, 12 }, sent);
    }

    [Fact]
    public void Savepoint_operations_send_the_named_opcodes()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),  // begin reply
            SqliStatementKit.EotFrame(),  // set savepoint reply
            SqliStatementKit.EotFrame(),  // rollback-to-savepoint reply
            SqliStatementKit.EotFrame(),  // release savepoint reply
            SqliStatementKit.EotFrame()); // commit reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        using (var tx = cn.BeginTransaction())
        {
            tx.Save("sp1");
            tx.Rollback("sp1");
            tx.Release("sp1");
            tx.Commit();
        }

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        // writeChar("sp1") = 00 03 's' 'p' '1' 00(pad). 137/139/138 = 0x89/0x8B/0x8A.
        byte[] name = { 0, 3, (byte)'s', (byte)'p', (byte)'1', 0 };
        Assert.Contains(BitConverter.ToString([0, 0x89, .. name, 0, 0]), hex); // SET + uniq flag 0
        Assert.Contains(BitConverter.ToString([0, 0x8B, .. name]), hex);       // ROLLBACK TO
        Assert.Contains(BitConverter.ToString([0, 0x8A, .. name]), hex);       // RELEASE
    }

    [Fact]
    public void Isolation_level_issues_set_transaction()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),                      // begin reply
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(), // SET TRANSACTION reply
            SqliStatementKit.EotFrame());                     // commit reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        using (var tx = cn.BeginTransaction(IsolationLevel.ReadCommitted))
            tx.Commit();

        string sent = System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", sent);
    }
}

/// <summary>
/// Fetch-tuning knobs: the FetchBufferSize wire value and the OPTOFC piggyback that
/// folds the first fetch into the open PDU. Canned-frame tests over the in-memory
/// session (the 9.59 vector negotiates int-sized fetch buffers and USVER describes).
/// </summary>
public class SqliFetchTuningTests
{
    // An LVARCHAR tuple field: [flag:1][length:int32][data]; a var-length column
    // forces the SQ_RET_TYPE typed fetch.
    private static byte[] Lvc(string value)
    {
        byte[] data = IfxWireConverter.EncodeChar(value);
        var field = new byte[5 + data.Length];
        IfxWireConverter.WriteInt32(field.AsSpan(1, 4), data.Length);
        data.CopyTo(field.AsSpan(5));
        return field;
    }

    private static byte[] IntDescribe(short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] LvarcharDescribe(short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 0,
            new SqliStatementKit.Col("v", (short)SqliTypeCode.UdtVar, 500, 0,
                ExtendedId: 1, ExtOwner: "informix", ExtName: "lvarchar")),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    // Same VARCHAR-shaped row as LvarcharDescribe, but described as ExecProc — the
    // shape live-verified against cap_getparam(?,?,?,?) on IDS 9.61 (2026-08-03).
    private static byte[] LvarcharExecProcDescribe(short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.ExecProc, stmtId, tupleSize: 0,
            new SqliStatementKit.Col("v", (short)SqliTypeCode.UdtVar, 500, 0,
                ExtendedId: 1, ExtOwner: "informix", ExtName: "lvarchar")),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] IntRow(int n)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    [Fact]
    public void Default_fetch_sends_the_tuple_size_derived_buffer()
    {
        // A 4-byte tuple derives the classic 4096-byte (0x0000_1000) fetch buffer.
        byte[] script = SqliStatementKit.Join(
            IntDescribe(1),
            SqliStatementKit.DoneFrame(), SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        session.Prepare("select n from t");
        long before = duplex.Output.Length;
        session.OpenCursor();

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        // SQ_NFETCH(9) + int32 buffer 4096 (2GB-fetch server => int-sized size word).
        Assert.Contains("00-09-00-00-10-00", hex);
    }

    [Fact]
    public void FetchBufferSize_override_travels_on_the_wire()
    {
        byte[] script = SqliStatementKit.Join(
            IntDescribe(1),
            SqliStatementKit.DoneFrame(), SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        session.Prepare("select n from t");
        long before = duplex.Output.Length;
        session.OpenCursor(fetchBufferSize: 262_144);

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        // SQ_NFETCH(9) + int32 buffer 262144 (0x0004_0000).
        Assert.Contains("00-09-00-04-00-00", hex);
    }

    [Fact]
    public void ConnectionFetchBufferSize_bumps_the_default_when_larger()
    {
        byte[] script = SqliStatementKit.Join(
            IntDescribe(1),
            SqliStatementKit.DoneFrame(), SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        session.ConnectionFetchBufferSize = 65_536; // FET_BUF_SIZE > the 4096 default
        session.Prepare("select n from t");
        long before = duplex.Output.Length;
        session.OpenCursor();

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        // SQ_NFETCH(9) + int32 buffer 65536 (0x0001_0000).
        Assert.Contains("00-09-00-01-00-00", hex);
    }

    [Fact]
    public void Optofc_piggybacks_the_fetch_into_the_open_pdu_for_variable_length_rows()
    {
        // OPTOFC: SQ_OPEN(6) is immediately followed by SQ_ID(4)+SQ_RET_TYPE(100)+
        // SQ_NFETCH in ONE PDU — the first tuple buffer rides back with the open reply.
        byte[] script = SqliStatementKit.Join(
            LvarcharDescribe(2),
            // combined open+fetch reply: one EOT.
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(Lvc("hi")),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        session.Optofc = true;
        session.Prepare("select v from t");
        long before = duplex.Output.Length;
        session.ResetFetchStats();
        session.OpenCursor();

        Assert.Equal(1, session.RequestRoundTrips); // single flush: open + fetch together
        Assert.Equal(1, session.FetchMessagesSent);

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        // SQ_OPEN(6) directly into SQ_ID(4) id=2, then SQ_RET_TYPE(100) — no EOT between.
        Assert.Contains("00-06-00-04-00-02-00-64", hex);
        Assert.DoesNotContain("00-06-00-0C", hex); // open is NOT flushed on its own

        Assert.True(session.NextRow());
        Assert.Equal("hi", (string)session.DecodeColumn(0));
        Assert.False(session.NextRow());
    }

    [Fact]
    public void Without_optofc_a_variable_length_open_takes_two_round_trips()
    {
        byte[] script = SqliStatementKit.Join(
            LvarcharDescribe(2),
            // open reply on its own (first phase)…
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(),
            // …then the typed-fetch reply (second phase).
            SqliStatementKit.TupleFrame(Lvc("hi")),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        Assert.False(session.Optofc); // off by default, as in the JDBC driver
        session.Prepare("select v from t");
        long before = duplex.Output.Length;
        session.ResetFetchStats();
        session.OpenCursor();

        Assert.Equal(2, session.RequestRoundTrips); // two-phase: open flushed, then fetch
        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("00-06-00-0C", hex); // SQ_OPEN(6) then SQ_EOT(12): open on its own

        Assert.True(session.NextRow());
        Assert.Equal("hi", (string)session.DecodeColumn(0));
    }

    [Fact]
    public void Optofc_also_piggybacks_the_fetch_for_execute_procedure_variable_length_rows()
    {
        // Extension beyond the JDBC driver's Select-only gate: EXECUTE PROCEDURE
        // cursors get the same open+fetch fusion (live-verified against cap_getparam
        // on IDS 9.61, 2026-08-03).
        byte[] script = SqliStatementKit.Join(
            LvarcharExecProcDescribe(2),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(Lvc("hi")),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        session.Optofc = true;
        session.Prepare("execute procedure cap_getparam(?,?,?,?)");
        long before = duplex.Output.Length;
        session.ResetFetchStats();
        session.OpenCursor();

        Assert.Equal((short)SqliStatementType.ExecProc, session.StatementType);
        Assert.Equal(1, session.RequestRoundTrips); // single flush: open + fetch together
        Assert.Equal(1, session.FetchMessagesSent);

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("00-06-00-04-00-02-00-64", hex);
        Assert.DoesNotContain("00-06-00-0C", hex); // open is NOT flushed on its own

        Assert.True(session.NextRow());
        Assert.Equal("hi", (string)session.DecodeColumn(0));
        Assert.False(session.NextRow());
    }

    [Fact]
    public void Without_optofc_an_execute_procedure_variable_length_open_still_takes_two_round_trips()
    {
        byte[] script = SqliStatementKit.Join(
            LvarcharExecProcDescribe(2),
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(Lvc("hi")),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp();
        Assert.False(session.Optofc); // off by default
        session.Prepare("execute procedure cap_getparam(?,?,?,?)");
        session.ResetFetchStats();
        session.OpenCursor();

        Assert.Equal((short)SqliStatementType.ExecProc, session.StatementType);
        Assert.Equal(2, session.RequestRoundTrips); // two-phase: open flushed, then fetch

        Assert.True(session.NextRow());
        Assert.Equal("hi", (string)session.DecodeColumn(0));
    }

    [Fact]
    public void Connection_string_exposes_fetch_buffer_size_and_optofc()
    {
        var b = new IfxConnectionStringBuilder(
            "Host=h;Service=9088;Database=db;Fetch Buffer Size=131072;Optofc=true");
        Assert.Equal(131072, b.FetchBufferSize);
        Assert.True(b.Optofc);
    }
}

/// <summary>
/// DISTINCT / ROW / collection / opaque-UDT decoding at the reader/metadata level. A DISTINCT
/// type decodes as its base type but reports its own name; a ROW column reports the
/// <see cref="IfxRow"/> CLR type and asks the server for the column as SENDRECV(44). The
/// structured value decode itself is covered by <see cref="SqliStructuredComplexTests"/> over
/// real captured payloads. Canned-frame layouts captured from IDS 9.59 reference runs.
/// </summary>
public class SqliComplexTypeTests
{
    private static byte[] Int32Row(int v)
    {
        var b = new byte[4];
        IfxWireConverter.WriteInt32(b, v);
        return b;
    }

    // A complex/opaque tuple field: [length:int32][data] (no flag byte).
    private static byte[] ComplexField(byte[] data)
    {
        var f = new byte[4 + data.Length];
        IfxWireConverter.WriteInt32(f.AsSpan(0, 4), data.Length);
        data.CopyTo(f.AsSpan(4));
        return f;
    }

    [Fact]
    public void Distinct_type_decodes_as_its_base_and_reports_its_name()
    {
        // typeWord 0x0802 = DISTINCT(0x800) over INT(2); owner/name "root"/"myid".
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 5, tupleSize: 4,
                new SqliStatementKit.Col("v", unchecked((short)0x0802), 4, 0, ExtOwner: "root", ExtName: "myid")),
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(), SqliStatementKit.TupleFrame(Int32Row(42)),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var r = cmd.ExecuteReader();

        Assert.Equal("myid", r.GetDataTypeName(0)); // the distinct name, not "Int"
        Assert.Equal(typeof(int), r.GetFieldType(0)); // decodes as its INT base
        Assert.True(r.Read());
        Assert.Equal(42, r.GetInt32(0));
        Assert.False(r.Read());
    }

    [Fact]
    public void Complex_column_reports_row_type_asks_for_sendrecv_and_keeps_raw_bytes_reachable()
    {
        byte[] describe = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 3, tupleSize: 0,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Row, 0, 0, ExtOwner: "root", ExtName: "addr_t")),
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame());
        // A stub payload too short to be a real descriptor: the structured decoder
        // returns it unchanged (the opaque fallback), still reachable via GetBytes.
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] script = SqliStatementKit.Join(
            describe,
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(), // open reply (var-length two-phase)
            SqliStatementKit.TupleFrame(ComplexField(payload)),
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a from t";
        using var r = cmd.ExecuteReader();

        Assert.Equal("addr_t", r.GetDataTypeName(0)); // named row type
        Assert.Equal(typeof(IfxRow), r.GetFieldType(0)); // a ROW column now reports IfxRow
        Assert.True(r.Read());
        // Undecodable payload -> raw bytes, still reachable through the GetBytes escape hatch.
        long n = r.GetBytes(0, 0, null, 0, 0);
        byte[] raw = new byte[n];
        r.GetBytes(0, 0, raw, 0, (int)n);
        Assert.Equal(payload, raw);
        Assert.False(r.Read());

        // The typed fetch asked the server for the column as SENDRECV(44) + owner/name.
        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("00-2C-00-04-72-6F-6F-74-00-06-61-64-64-72-5F-74", hex); // 44 + "root" + "addr_t"
    }

    [Fact]
    public void Zero_length_complex_field_decodes_as_dbnull()
    {
        byte[] describe = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 3, tupleSize: 0,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Set, 0, 0, ExtOwner: "informix", ExtName: "set")),
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame());
        byte[] script = SqliStatementKit.Join(
            describe,
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(ComplexField([])), // length 0 -> NULL (outer-length guard)
            SqliStatementKit.CloseFrame(), SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a from t";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
        Assert.Equal(DBNull.Value, r.GetValue(0));
    }
}
