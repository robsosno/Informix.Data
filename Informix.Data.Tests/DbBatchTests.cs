using System.Data;
using System.Data.Common;
using Informix.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxBatch"/>/<see cref="IfxBatchCommand"/>/<see cref="IfxBatchCommandCollection"/>
/// — the standard ADO.NET <c>DbBatch</c> surface, with a pipelined <c>ExecuteNonQuery</c>.
/// Wire shapes and fail-fast semantics here are exactly what
/// InformixDataDbBatchPrompt.md §2's live probes (P1/P2/P3/P4) proved against IDS 9.59.
/// </summary>
public class DbBatchTests
{
    private static int CountPairs(byte[] b, byte hi, byte lo)
    {
        int n = 0;
        for (int i = 0; i + 1 < b.Length; i++)
            if (b[i] == hi && b[i + 1] == lo)
                n++;
        return n;
    }

    private static byte[] IntRow(int value)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, value);
        return row;
    }

    // ---- ExecuteNonQuery: chaining ----

    [Fact]
    public void ExecuteNonQuery_chains_heterogeneous_immediate_commands_into_one_flush()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Delete, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 3),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        cn.ResetFetchStats();
        long before = duplex.Output.Length;

        using var batch = cn.CreateBatch();
        var c0 = batch.BatchCommands.Add("insert into t values (1)");
        var c1 = batch.BatchCommands.Add("update t set a = 1");
        var c2 = batch.BatchCommands.Add("delete from t where a = 1");

        int total = batch.ExecuteNonQuery();

        Assert.Equal(6, total);
        Assert.Equal(1, c0.RecordsAffected);
        Assert.Equal(2, c1.RecordsAffected);
        Assert.Equal(3, c2.RecordsAffected);
        Assert.Equal(1, cn.RequestRoundTrips); // one flush for all three chained commands

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(3, CountPairs(sent, 0x00, 0x07)); // three SQ_EXECUTE(7) frames
    }

    [Fact]
    public void ExecuteNonQuery_chains_a_cached_prepared_command_with_an_immediate_one()
    {
        byte[] script = SqliStatementKit.Join(
            // Prepare()'s own round trip for the parameterized UPDATE.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 5, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // ExecuteNonQuery's own chained reply: immediate INSERT, then the cached
            // UPDATE's execute -- no second DESCRIBE, it was already described above.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.DoneFrame(rows: 7),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(
            script, out var duplex, extraSettings: "Prepared Statement Cache Size=10");
        using var batch = cn.CreateBatch();
        var cInsert = batch.BatchCommands.Add("insert into t values (1)");
        var cUpdate = batch.BatchCommands.Add("update t set a = ? where id = ?");
        cUpdate.Parameters.Add("a", 42);
        cUpdate.Parameters.Add("id", 1);

        batch.Prepare();
        cn.ResetFetchStats();
        long before = duplex.Output.Length;

        int total = batch.ExecuteNonQuery();

        Assert.Equal(8, total);
        Assert.Equal(1, cInsert.RecordsAffected);
        Assert.Equal(7, cUpdate.RecordsAffected);
        Assert.Equal(1, cn.RequestRoundTrips);

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(2, CountPairs(sent, 0x00, 0x07)); // two SQ_EXECUTE(7) frames
    }

    [Fact]
    public void Prepare_warms_the_cache_once_per_distinct_command_and_skips_parameterless_ones()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(
            script, out _, extraSettings: "Prepared Statement Cache Size=5");
        using var batch = cn.CreateBatch();
        var c0 = batch.BatchCommands.Add("insert into t values (?)");
        c0.Parameters.Add("a", 1);
        var c1 = batch.BatchCommands.Add("insert into t values (?)"); // identical shape -- collapses
        c1.Parameters.Add("a", 2);
        batch.BatchCommands.Add("delete from t where a = 1"); // parameterless -- never prepared

        cn.ResetFetchStats();
        batch.Prepare();

        Assert.Equal(1, cn.PreparedStatementCacheMisses);
        Assert.Equal(0, cn.PreparedStatementCacheHits);
    }

    [Fact]
    public void ExecuteNonQuery_consumes_a_trailing_SQ_COST_after_the_last_steps_done()
    {
        // Regression test: live IDS 9.59 traffic trails a successful SQ_DONE with a
        // SQ_COST frame -- including after the LAST step's DONE in a chained PDU, not
        // just earlier ones. A consumption loop that stops as soon as it has seen one
        // terminal per step (rather than reading everything up to the closing EOT)
        // leaves that trailing COST unread, desyncing every read for the rest of the
        // connection (caught live via tools/DbBatchWireProbe-style repro at N=3).
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.CostFrame(),
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.CostFrame(),
            SqliStatementKit.EotFrame(),
            // a follow-up statement on the same (now-desynced, if the bug were present)
            // connection proves the stream is positioned correctly afterward.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Delete, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 3),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        var c0 = batch.BatchCommands.Add("insert into t values (1)");
        var c1 = batch.BatchCommands.Add("update t set a = 1");

        int total = batch.ExecuteNonQuery();

        Assert.Equal(3, total);
        Assert.Equal(1, c0.RecordsAffected);
        Assert.Equal(2, c1.RecordsAffected);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "delete from t";
        Assert.Equal(3, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void ExecuteNonQuery_on_empty_batch_returns_zero_without_wire_io()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        using var batch = cn.CreateBatch();
        long before = duplex.Output.Length;

        Assert.Equal(0, batch.ExecuteNonQuery());
        Assert.Equal(before, duplex.Output.Length);
    }

    // ---- ExecuteNonQuery: fail-fast (P4) ----

    [Fact]
    public void ExecuteNonQuery_stops_at_the_first_failure_and_marks_later_commands_as_not_run()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 0, tupleSize: 0),
            // ErrorReply already appends its own trailing EOT -- the server sends nothing
            // for the third (never-attempted) command, matching the live P4 probe.
            SqliStatementKit.ErrorReply(-268, "duplicate value for a column with unique constraint"));

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        var c0 = batch.BatchCommands.Add("insert into t values (1)");
        var c1 = batch.BatchCommands.Add("insert into t values (1)");
        var c2 = batch.BatchCommands.Add("insert into t values (2)");

        var ex = Assert.Throws<IfxException>(() => batch.ExecuteNonQuery());

        Assert.Equal(-268, ex.IfxSqlCode);
        Assert.Same(c1, ex.BatchCommand);
        Assert.Equal(1, c0.RecordsAffected);
        Assert.Equal(-1, c1.RecordsAffected); // failed
        Assert.Equal(-1, c2.RecordsAffected); // never even sent
    }

    // ---- ExecuteNonQuery: validation ----

    [Fact]
    public void ExecuteNonQuery_without_a_connection_throws()
    {
        using var batch = new IfxBatch();
        batch.BatchCommands.Add("select 1 from systables");
        Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
    }

    [Fact]
    public void ExecuteNonQuery_with_an_unopened_connection_throws()
    {
        using var cn = new IfxConnection("Host=h;Service=9088;Database=db;Pooling=false");
        using var batch = cn.CreateBatch();
        batch.BatchCommands.Add("select 1 from systables");
        Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
    }

    [Fact]
    public void ExecuteNonQuery_no_longer_blocked_by_a_pinned_session()
    {
        // MARS: IfxBatch.RequireOpenConnection now calls AcquireSession, which steers around
        // whatever session a bulk loader (or a raw PinStatement, as scripted here) has pinned
        // and hands back a fresh sibling instead of throwing -- the batch runs there normally.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 0, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        cn.SqliSession!.PinStatement(new object());
        using var batch = cn.CreateBatch();
        var c0 = batch.BatchCommands.Add("update t set a = 1");

        int total = batch.ExecuteNonQuery();

        Assert.Equal(1, total);
        Assert.Equal(1, c0.RecordsAffected);
    }

    [Fact]
    public void ExecuteNonQuery_rejects_a_command_that_describes_a_result_set()
    {
        // A prepared SELECT sneaked into ExecuteNonQuery: the batch must refuse to run
        // it (SQ_EXECUTE against a describing statement was never wire-verified) rather
        // than guess.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        var cSelect = batch.BatchCommands.Add("select a from t where a = ?");
        cSelect.Parameters.Add("a", 1);

        var ex = Assert.Throws<IfxException>(() => batch.ExecuteNonQuery());
        Assert.Same(cSelect, ex.BatchCommand);
        Assert.Equal(-260, ex.IfxSqlCode);   // what the server itself answers for a SELECT run as a non-query
    }

    [Fact]
    public void ExecuteNonQuery_rejects_a_routine_that_returns_values_with_the_servers_code()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.ExecProc, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        var cProc = batch.BatchCommands.Add("execute procedure p(?)");
        cProc.Parameters.Add("a", 1);

        var ex = Assert.Throws<IfxException>(() => batch.ExecuteNonQuery());
        Assert.Same(cProc, ex.BatchCommand);
        Assert.Equal(-684, ex.IfxSqlCode);   // "Function returns too many values", as the server says it
    }

    // ---- ExecuteReader: sequential spanning ----

    [Fact]
    public void ExecuteReader_spans_two_select_commands()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(1)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame(), // release reply for command 0
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 4,
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(2)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply for command 1

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        batch.BatchCommands.Add("select a from t1");
        batch.BatchCommands.Add("select b from t2");

        using var reader = batch.ExecuteReader();
        Assert.Equal(1, reader.FieldCount);
        Assert.Equal("a", reader.GetName(0));
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(reader.Read());

        Assert.True(reader.NextResult());
        Assert.Equal(1, reader.FieldCount);
        Assert.Equal("b", reader.GetName(0));
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.False(reader.Read());

        Assert.False(reader.NextResult());
    }

    [Fact]
    public void ExecuteReader_mixed_nonquery_and_select_stamps_RecordsAffected()
    {
        byte[] script = SqliStatementKit.Join(
            // command 0: non-query
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame(), // release
            // command 1: select
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 4,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(9)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        var cInsert = batch.BatchCommands.Add("insert into t values (1)");
        batch.BatchCommands.Add("select a from t");

        using var reader = batch.ExecuteReader();
        Assert.Equal(0, reader.FieldCount); // command 0 is a non-query -- empty cursor
        Assert.False(reader.Read());
        Assert.Equal(1, cInsert.RecordsAffected);

        Assert.True(reader.NextResult());
        Assert.Equal(1, reader.FieldCount);
        Assert.True(reader.Read());
        Assert.Equal(9, reader.GetInt32(0));

        Assert.False(reader.NextResult());
    }

    [Fact]
    public void ExecuteScalar_returns_the_first_value_of_the_first_result_set_producing_command()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 4,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(42)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var batch = cn.CreateBatch();
        batch.BatchCommands.Add("update t set a = 1");
        batch.BatchCommands.Add("select a from t");

        Assert.Equal(42, batch.ExecuteScalar());
    }

    // ---- factory / connection / data source hookups ----

    [Fact]
    public void CanCreateBatch_is_true_and_CreateBatch_round_trips_typed_instances()
    {
        var f = InformixClientFactory.Instance;
        Assert.True(f.CanCreateBatch);
        Assert.IsType<IfxBatch>(f.CreateBatch());
        Assert.IsType<IfxBatchCommand>(f.CreateBatchCommand());

        using var cn = SqliStatementKit.OpenConnection([], out _);
        Assert.True(cn.CanCreateBatch);
        IfxBatch batch = cn.CreateBatch();
        Assert.Same(cn, batch.Connection);
    }

    [Fact]
    public void DataSource_CreateBatch_wraps_a_connection_owning_IfxBatch()
    {
        // DbDataSource.CreateBatch() has no override here (unlike CreateConnection), so
        // the framework's own DbBatchWrapper is what comes back -- it opens a fresh
        // IfxConnection (via IfxDataSource.CreateDbConnection) lazily around each execute
        // and disposes it with the wrapper. Proven by exercising it end to end rather than
        // asserting a concrete type the framework does not expose.
        using var source = IfxDataSource.Create("Host=h;Service=9088;Server=ol_test;Database=db;Pooling=false");
        using DbBatch batch = source.CreateBatch();
        Assert.NotNull(batch);
        Assert.Throws<NotSupportedException>(() => batch.Connection);
    }

    // ---- IfxBatchCommandCollection ----

    private sealed class FakeBatchCommand : DbBatchCommand
    {
        public override string CommandText { get; set; } = "";
        public override CommandType CommandType { get; set; }
        public override int RecordsAffected => -1;
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
    }

    [Fact]
    public void BatchCommandCollection_rejects_a_foreign_DbBatchCommand_subtype()
    {
        var collection = new IfxBatchCommandCollection();
        Assert.Throws<InvalidCastException>(() => collection.Add(new FakeBatchCommand()));
    }

    [Fact]
    public void BatchCommandCollection_typed_add_creates_and_returns_the_command()
    {
        var collection = new IfxBatchCommandCollection();
        IfxBatchCommand cmd = collection.Add("select 1 from systables");
        Assert.Single(collection);
        Assert.Same(cmd, collection[0]);
        Assert.Equal("select 1 from systables", cmd.CommandText);
    }

    [Fact]
    public void BatchCommand_rejects_TableDirect()
    {
        var cmd = new IfxBatchCommand();
        Assert.Throws<NotSupportedException>(() => cmd.CommandType = CommandType.TableDirect);
    }
}
