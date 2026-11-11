using System;
using System.Collections.Generic;
using System.Data;
using Informix.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Offline coverage for <see cref="IfxBulkLoader"/> — the streaming insert-cursor loader.
/// Everything runs over the scripted duplex transport (<see cref="SqliStatementKit"/>): a
/// buffered flush is <c>SqliSession.ExecuteBatch</c> (one SQ_DONE per row + EOT), a
/// PlainInserts row is <c>ExecuteStatement</c> (SQ_DONE + EOT), and Complete/Discard release
/// the prepared statement (a bare EOT).
/// </summary>
public class BulkLoaderTests
{
    // ---- scripted reply builders ----

    private static byte[] PrepareInsert(short stmtId = 9) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] PrepareSelect(short stmtId = 9) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    // A buffered flush reply: one SQ_DONE per row, then a single EOT.
    private static byte[] Flush(int rows)
    {
        var frames = new List<byte[]>();
        for (int i = 0; i < rows; i++)
            frames.Add(SqliStatementKit.DoneFrame(rows: 1));
        frames.Add(SqliStatementKit.EotFrame());
        return SqliStatementKit.Join(frames.ToArray());
    }

    // A single PlainInserts execute reply.
    private static byte[] Exec() => SqliStatementKit.Join(
        SqliStatementKit.DoneFrame(rows: 1), SqliStatementKit.EotFrame());

    // A statement-release reply (SQ_RELEASE round trip).
    private static byte[] Release() => SqliStatementKit.EotFrame();

    // ================================================================= //
    //  DefineParameters / Open validation                               //
    // ================================================================= //

    [Fact]
    public void Open_without_DefineParameters_throws()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        Assert.Throws<InvalidOperationException>(() => loader.Open());
    }

    [Fact]
    public void DefineParameters_with_no_types_throws()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        Assert.Throws<ArgumentException>(() => loader.DefineParameters());
    }

    [Fact]
    public void DefineParameters_with_unsupported_type_throws()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        Assert.Throws<NotSupportedException>(() => loader.DefineParameters(IfxType.Clob));
    }

    [Fact]
    public void Open_with_placeholder_count_mismatch_throws()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Char); // 2 declared, 1 '?'
        var ex = Assert.Throws<InvalidOperationException>(() => loader.Open());
        Assert.Contains("2", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void Open_with_named_placeholder_throws()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        using var loader = cn.CreateBulkLoader("insert into t values (:a)");
        loader.DefineParameters(IfxType.Integer);
        Assert.Throws<IfxException>(() => loader.Open());
    }

    [Fact]
    public void Open_of_a_select_is_rejected()
    {
        byte[] script = SqliStatementKit.Join(PrepareSelect(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("select id from t where id = ?");
        loader.DefineParameters(IfxType.Integer);
        Assert.Throws<InvalidOperationException>(() => loader.Open());
    }

    // ================================================================= //
    //  Put arity / type / overflow, all three overloads                 //
    // ================================================================= //

    [Fact]
    public void Put_arity_mismatch_throws_with_row_number()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Char);
        var ex = Assert.Throws<ArgumentException>(() => loader.Put(1)); // 1 value, 2 declared
        Assert.Contains("row 1", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Put_wrong_type_throws_InvalidCast_with_row_and_column()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        var ex = Assert.Throws<InvalidCastException>(() => loader.Put("not an int"));
        Assert.Contains("row 1", ex.Message);
        Assert.Contains("column 1", ex.Message);
    }

    [Fact]
    public void Put_narrowing_overflow_throws_Overflow_with_row_and_column()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.SmallInt);
        var ex = Assert.Throws<OverflowException>(() => loader.Put(70000)); // > short.MaxValue
        Assert.Contains("row 1", ex.Message);
        Assert.Contains("column 1", ex.Message);
    }

    [Fact]
    public void Put_accepts_cheap_checked_casts()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(1), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?, ?, ?, ?, ?)");
        loader.DefineParameters(IfxType.SmallInt, IfxType.Integer, IfxType.Int8, IfxType.Decimal, IfxType.Float);
        // byte->SmallInt, short->Integer, int->Int8, int->Decimal, float->Float — all cheap casts.
        loader.Put((byte)7, (short)300, 40, 12, 1.5f);
        Assert.Equal(1, loader.Complete());
    }

    [Fact]
    public void Put_array_overload_buffers_a_row()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(1), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Char);
        loader.Put(new object?[] { 42, "abc" });
        Assert.Equal(1, loader.PendingRows);
        Assert.Equal(1, loader.Complete());
    }

    [Fact]
    public void Put_IDataRecord_maps_dbnull_and_honours_field_count()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(1), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Char);

        using var twoCols = new DataTable();
        twoCols.Columns.Add("a", typeof(int));
        twoCols.Columns.Add("b", typeof(string));
        twoCols.Rows.Add(1, DBNull.Value); // second field NULL
        using var reader = twoCols.CreateDataReader();
        Assert.True(reader.Read());
        loader.Put(reader);
        Assert.Equal(1, loader.RowsPut);
        Assert.Equal(1, loader.Complete());
    }

    [Fact]
    public void Put_IDataRecord_with_wrong_field_count_throws()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Char);

        using var threeCols = new DataTable();
        threeCols.Columns.Add("a", typeof(int));
        threeCols.Columns.Add("b", typeof(int));
        threeCols.Columns.Add("c", typeof(int));
        threeCols.Rows.Add(1, 2, 3);
        using var reader = threeCols.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Throws<ArgumentException>(() => loader.Put(reader));
    }

    // ================================================================= //
    //  Auto-open, buffering thresholds, accounting                      //
    // ================================================================= //

    [Fact]
    public void First_Put_opens_and_buffers_without_sending()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(1), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        loader.Put(1);
        Assert.Equal(1, loader.RowsPut);
        Assert.Equal(1, loader.PendingRows);
        Assert.Equal(0, loader.RowsInserted); // nothing sent yet

        Assert.Equal(1, loader.Complete());
        Assert.Equal(1, loader.RowsInserted);
        Assert.Equal(0, loader.PendingRows);
    }

    [Fact]
    public void Wire_size_guard_triggers_a_flush()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(1), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Char);

        loader.Put(new string('x', 30000));   // one row past the ~30 KB guard
        Assert.Equal(0, loader.PendingRows);   // already flushed
        Assert.Equal(1, loader.RowsInserted);

        loader.Complete();
    }

    [Fact]
    public void PlainInserts_mode_executes_each_row_immediately()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Exec(), Exec(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.Mode = IfxBulkLoaderMode.PlainInserts;
        loader.DefineParameters(IfxType.Integer);

        loader.Put(1);
        Assert.Equal(1, loader.RowsInserted); // sent immediately, never buffered
        Assert.Equal(0, loader.PendingRows);
        loader.Put(2);
        Assert.Equal(2, loader.RowsInserted);

        loader.Complete();
        Assert.Equal(2, loader.RowsInserted);
    }

    // ================================================================= //
    //  HoldCursor                                                       //
    // ================================================================= //

    [Fact]
    public void HoldCursor_cannot_be_changed_after_open()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        loader.Open();
        Assert.Throws<InvalidOperationException>(() => loader.HoldCursor = true);
    }

    // ================================================================= //
    //  Commit / rollback while rows are buffered (fallback SQ_BIND       //
    //  engine — the insert-cursor engine's own cursor-close behaviour    //
    //  is covered in BulkLoaderInsertCursorTests)                        //
    // ================================================================= //

    [Fact]
    public void Commit_auto_flushes_pending_rows_first()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(), // begin reply
            PrepareInsert(),
            Flush(1),                     // auto-flush inside OnBeforeCommit
            SqliStatementKit.EotFrame(), // commit reply
            Release());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = cn.BeginTransaction();
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        loader.Transaction = tx;

        loader.Put(1);
        Assert.Equal(1, loader.PendingRows);

        tx.Commit();
        Assert.Equal(0, loader.PendingRows);
        Assert.Equal(1, loader.RowsInserted); // the commit already covers this row

        loader.Complete(); // nothing left to flush
        Assert.Equal(1, loader.RowsInserted);
    }

    [Fact]
    public void Rollback_drops_pending_rows_without_sending_them()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(), // begin reply
            PrepareInsert(),
            SqliStatementKit.EotFrame(), // rollback reply — no flush precedes it
            Release());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = cn.BeginTransaction();
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        loader.Transaction = tx;

        loader.Put(1);
        Assert.Equal(1, loader.PendingRows);

        tx.Rollback();
        Assert.Equal(0, loader.PendingRows); // abandoned, not sent
        Assert.Equal(0, loader.RowsInserted);

        loader.Complete();
        Assert.Equal(0, loader.RowsInserted);
    }

    // ================================================================= //
    //  Execute template                                                 //
    // ================================================================= //

    [Fact]
    public void Execute_normal_return_flushes_and_returns_total()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(2), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        long inserted = loader.Execute(l =>
        {
            l.Put(1);
            l.Put(2);
        });

        Assert.Equal(2, inserted);
        Assert.Equal(2, loader.RowsInserted);
    }

    [Fact]
    public void Execute_body_exception_discards_and_rethrows_unwrapped()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        var boom = new InvalidOperationException("boom");
        var thrown = Assert.Throws<InvalidOperationException>(() => loader.Execute(l =>
        {
            l.Put(1);           // buffered, not yet flushed
            throw boom;
        }));

        Assert.Same(boom, thrown);        // rethrown unwrapped
        Assert.Equal(0, loader.RowsInserted); // buffer discarded, nothing sent
    }

    // ================================================================= //
    //  Completion / fault / discard / dispose state machine             //
    // ================================================================= //

    [Fact]
    public void Dispose_flushes_the_tail()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(2), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        using (loader)
        {
            loader.Put(1);
            loader.Put(2);
        } // Dispose == Complete: flushes the tail

        Assert.Equal(2, loader.RowsInserted);
    }

    [Fact]
    public void Discard_drops_buffered_rows_without_sending()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        loader.Put(1);
        loader.Discard();
        Assert.Equal(0, loader.RowsInserted);
        Assert.Equal(0, loader.PendingRows);

        loader.Dispose(); // no-op after discard
    }

    [Fact]
    public void A_faulted_loader_refuses_more_rows_and_does_not_flush_on_dispose()
    {
        // A row past the ~30 KB wire-size guard flushes on the first Put; the server
        // rejects it (-268) -> fault.
        byte[] script = SqliStatementKit.Join(
            PrepareInsert(),
            SqliStatementKit.ErrorReply(-268, "Unique constraint violated."));
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Char);

        var ifx = Assert.Throws<IfxException>(() => loader.Put(new string('x', 30000)));
        Assert.Equal(-268, ifx.IfxSqlCode);

        // Further Puts are refused, and Dispose releases without a second (masking) flush.
        Assert.Throws<InvalidOperationException>(() => loader.Put("y"));
        loader.Dispose(); // faulted: unpin only, no wire — the script has no more frames
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(1), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        loader.Put(1);
        loader.Dispose(); // flush + release
        loader.Dispose(); // no-op
        Assert.Equal(1, loader.RowsInserted);
    }

    // ================================================================= //
    //  Connection pinning while a loader is open                        //
    // ================================================================= //

    [Fact]
    public void An_open_loader_no_longer_blocks_an_unrelated_ifxcommand()
    {
        // MARS: the loader keeps exclusive use of the session it pinned, but IfxCommand no
        // longer collides with it -- IfxConnection.AcquireSession steers the unrelated command
        // to a freshly-minted sibling session sharing the same connection instead of throwing.
        // (IfxRowBatch is a different, bulk-style path that still targets the pinned session
        // directly -- see An_open_loader_blocks_ifxbatch below -- so that one is unaffected.)
        byte[] script = SqliStatementKit.Join(
            PrepareInsert(),
            // the sibling session's own prepare + open + fetch for "select 1 from systables"
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(OneIntRow(1)),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame(),  // close reply
            SqliStatementKit.EotFrame(),  // release reply
            Release());                  // the loader's own release on Dispose
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        loader.Open();

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select 1 from systables";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
    }

    private static byte[] OneIntRow(int n)
    {
        var row = new byte[4];
        Informix.Data.Wire.IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    [Fact]
    public void An_open_loader_blocks_ifxbatch()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        loader.Open();

        var batch = cn.CreateRowBatch("insert into t values (?)");
        batch.AddRow(1);
        Assert.Throws<InvalidOperationException>(() => batch.Execute());
    }

    [Fact]
    public void A_second_loader_cannot_open_on_a_pinned_connection()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var first = cn.CreateBulkLoader("insert into t values (?)");
        first.DefineParameters(IfxType.Integer);
        first.Open();

        using var second = cn.CreateBulkLoader("insert into t values (?)");
        second.DefineParameters(IfxType.Integer);
        Assert.Throws<InvalidOperationException>(() => second.Open());
    }

    // ================================================================= //
    //  Wire shape: prepare once, chain an execute per row               //
    // ================================================================= //

    [Fact]
    public void A_buffered_flush_chains_one_execute_per_row_over_one_prepare()
    {
        byte[] script = SqliStatementKit.Join(PrepareInsert(), Flush(3), Release());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);

        loader.Put(1);
        loader.Put(2);
        loader.Put(3);
        loader.Complete();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        // Three chained SQ_EXECUTE(7) frames — one per row (INT 1/2/3 binds never
        // contain the 00 07 pair). Only one prepare/describe reply is scripted, so a
        // re-prepare per flush would have failed reading: prepare-once is structural.
        Assert.Equal(3, CountPairs(sent, 0x00, 0x07));
    }

    private static int CountPairs(byte[] b, byte hi, byte lo)
    {
        int n = 0;
        for (int i = 0; i + 1 < b.Length; i++)
            if (b[i] == hi && b[i + 1] == lo)
                n++;
        return n;
    }
}
