using System;
using System.Collections.Generic;
using Informix.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Offline coverage for the phase-2 SQ_PUT insert-cursor engine. Eligibility hinges on
/// the prepared INSERT's describe carrying all fixed-width columns, so these scripts —
/// unlike <see cref="BulkLoaderTests"/> — describe real columns. The engine then opens
/// an insert cursor (SQ_ID+SQ_CURNAME+SQ_HOLD+SQ_OPEN, reply a bare EOT), streams rows
/// (SQ_ID+SQ_PUT, reply SQ_DONE), and closes (SQ_CLOSE).
/// </summary>
public class BulkLoaderInsertCursorTests
{
    // A prepared-INSERT describe with two INT columns (tuple size 8) — fixed width,
    // so the loader picks the insert-cursor engine.
    private static byte[] PrepareTwoInts() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Insert, stmtId: 1, tupleSize: 8,
            new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
            new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    // A describe with an INT and a VARCHAR column -> variable width -> not eligible,
    // so the loader falls back to the SQ_BIND/SQ_EXECUTE batch engine.
    private static byte[] PrepareIntVarchar() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Insert, stmtId: 1, tupleSize: 24,
            new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
            new SqliStatementKit.Col("s", (short)SqliTypeCode.VarChar, 20, 4)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] Eot() => SqliStatementKit.EotFrame();

    // SQ_PUT reply: one SQ_DONE carrying the batch's row count, then EOT.
    private static byte[] PutReply(int rows) => SqliStatementKit.Join(
        SqliStatementKit.DoneFrame(rows: rows), SqliStatementKit.EotFrame());

    // A SQ_PUTERR frame: rows-inserted, sqlcode, isam, offset(int32), message.
    private static byte[] PutErr(short sqlcode, int rowsInserted, string message)
    {
        var ms = new System.IO.MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.PutErr);
        w.WriteInt32(rowsInserted);
        w.WriteInt16(sqlcode);
        w.WriteInt16(0);   // isam
        w.WriteInt32(0);   // statement offset (remove-64K negotiated)
        w.WriteChar(message);
        w.WriteInt16((short)SqliMessage.Eot);
        return ms.ToArray();
    }

    private static int CountPairs(byte[] b, byte hi, byte lo)
    {
        int n = 0;
        for (int i = 0; i + 1 < b.Length; i++)
            if (b[i] == hi && b[i + 1] == lo)
                n++;
        return n;
    }

    [Fact]
    public void A_fixed_width_insert_uses_the_put_engine()
    {
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot() /*open*/, PutReply(2), Eot() /*close*/, Eot() /*release*/);
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.Open();

        Assert.True(cn.SqliSession!.InsertCursorOpen); // engine chosen + cursor opened

        loader.Put(1, 10);
        loader.Put(2, 20);
        Assert.Equal(2, loader.Complete());
        Assert.False(cn.SqliSession!.InsertCursorOpen); // closed on complete
    }

    [Fact]
    public void The_put_engine_sends_curname_open_and_put_frames_with_no_hold_by_default()
    {
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot(), PutReply(2), Eot(), Eot());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        // HoldCursor defaults to false, matching ESQL/C's own default.

        // Values chosen so no tuple byte-pair collides with an asserted opcode
        // (e.g. the value 10 would put a 00-0A pair — SQ_CLOSE — inside the tuple).
        loader.Put(1, 300);
        loader.Put(2, 400);
        loader.Complete();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(1, CountPairs(sent, 0x00, 0x03)); // SQ_CURNAME once
        Assert.Equal(0, CountPairs(sent, 0x00, 0x2b)); // no SQ_HOLD — HoldCursor is false
        Assert.Equal(1, CountPairs(sent, 0x00, 0x11)); // SQ_PUT once (one flush)
        Assert.Equal(1, CountPairs(sent, 0x00, 0x0a)); // SQ_CLOSE once
    }

    [Fact]
    public void HoldCursor_true_sends_SQ_HOLD_at_open()
    {
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot(), PutReply(2), Eot(), Eot());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.HoldCursor = true;

        loader.Put(1, 300);
        loader.Put(2, 400);
        loader.Complete();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(1, CountPairs(sent, 0x00, 0x2b)); // SQ_HOLD once (WITH HOLD)
    }

    [Fact]
    public void The_put_frame_carries_rowsize_and_the_tuple_images()
    {
        byte[] script = SqliStatementKit.Join(PrepareTwoInts(), Eot(), PutReply(1), Eot(), Eot());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);

        loader.Put(0x11223344, 0x55667788);
        loader.Complete();

        byte[] sent = duplex.Output.ToArray();
        // SQ_PUT(0x0011) + rowsize:int32=8 + nrows:int16=1 + tuple [11 22 33 44 55 66 77 88].
        byte[] expected =
        [
            0x00, 0x11,                   // SQ_PUT
            0x00, 0x00, 0x00, 0x08,       // rowsize = 8 (int32)
            0x00, 0x01,                   // nrows = 1 (int16)
            0x11, 0x22, 0x33, 0x44,       // column a
            0x55, 0x66, 0x77, 0x88,       // column b
        ];
        Assert.Contains(BitConverter.ToString(expected), BitConverter.ToString(sent));
    }

    [Fact]
    public void A_null_int_encodes_as_the_min_value_sentinel()
    {
        byte[] script = SqliStatementKit.Join(PrepareTwoInts(), Eot(), PutReply(1), Eot(), Eot());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);

        loader.Put(7, null); // second column NULL
        loader.Complete();

        byte[] sent = duplex.Output.ToArray();
        // tuple = [00 00 00 07][80 00 00 00] — INT NULL sentinel is int.MinValue.
        Assert.Contains(
            BitConverter.ToString(new byte[] { 0x00, 0x00, 0x00, 0x07, 0x80, 0x00, 0x00, 0x00 }),
            BitConverter.ToString(sent));
    }

    [Fact]
    public void A_variable_length_column_falls_back_to_the_batch_engine()
    {
        // Fallback path: prepare + ExecuteBatch (one DONE per row) + release; no cursor.
        byte[] script = SqliStatementKit.Join(
            PrepareIntVarchar(),
            SqliStatementKit.DoneFrame(rows: 1), SqliStatementKit.DoneFrame(rows: 1), SqliStatementKit.EotFrame(),
            Eot() /*release*/);
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length; // exclude the ASF/bring-up bytes from the scan
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.VarChar);
        loader.Open();

        Assert.False(cn.SqliSession!.InsertCursorOpen); // fell back, no insert cursor

        loader.Put(1, "hello");
        loader.Put(2, "world");
        Assert.Equal(2, loader.Complete());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(0, CountPairs(sent, 0x00, 0x11)); // no SQ_PUT frame at all
    }

    // ---- insert-buffer sizing (FET_BUF_SIZE applies to insert cursors, as in ESQL/C) ----

    // Drives SqliSession.PutRows directly (prepare + open + put) so the PDU chunking is
    // observable without the loader's own client-side flush cadence in the way.
    private static int PutRowsAndCountPutFrames(
        string? extraSettings, int rowCount, params int[] ackedPerChunk)
    {
        byte[] script = SqliStatementKit.Join([
            PrepareTwoInts(), Eot() /*open*/,
            .. ackedPerChunk.Select(PutReply)]);
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex, extraSettings);
        var session = cn.SqliSession!;
        session.Prepare("insert into t values (?, ?)", 2);
        session.OpenInsertCursor();
        long before = duplex.Output.Length; // scan only the PUT traffic

        var row = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; // 8-byte tuple, no 0x00-0x11 pair
        var tuples = Enumerable.Repeat(row, rowCount).ToList();
        Assert.Equal(rowCount, session.PutRows(tuples, 8));

        return CountPairs(duplex.Output.ToArray()[(int)before..], 0x00, 0x11); // SQ_PUT frames
    }

    [Fact]
    public void Put_pdus_split_at_the_default_insert_buffer()
    {
        // 5000 × 8 bytes = 40 000 > the classic 32 000 guard → two SQ_PUT PDUs (4000 + 1000).
        Assert.Equal(2, PutRowsAndCountPutFrames(null, 5000, 4000, 1000));
    }

    [Fact]
    public void A_configured_fetch_buffer_size_packs_more_rows_per_put()
    {
        // Same 40 000 bytes with FET_BUF_SIZE=65536 → a single SQ_PUT PDU. Informix
        // FET_BUF_SIZE governs insert-cursor buffers too, not just fetches.
        Assert.Equal(1, PutRowsAndCountPutFrames("Fetch Buffer Size=65536", 5000, 5000));
    }

    [Fact]
    public void Rows_per_put_are_capped_at_the_int16_nrows_word()
    {
        // A large buffer may not overflow SQ_PUT's nrows:int16 — 40 000 rows fit the
        // 1 MB buffer byte-wise but must still split at 32 767 rows per PDU.
        Assert.Equal(2, PutRowsAndCountPutFrames("Fetch Buffer Size=1000000", 40000, 32767, 7233));
    }

    [Fact]
    public void The_loader_flush_cadence_follows_the_configured_insert_buffer()
    {
        // With the default ~32 KB buffer the loader flushes mid-load (2 SQ_PUTs, previous
        // test); with FET_BUF_SIZE=65536 the same 5000 rows travel as one SQ_PUT.
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot() /*open*/, PutReply(5000), Eot() /*close*/, Eot() /*release*/);
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex, "Fetch Buffer Size=65536");
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.Open();
        long before = duplex.Output.Length;

        for (int i = 0; i < 5000; i++)
            loader.Put(0x01020304, 0x05060708);
        Assert.Equal(5000, loader.Complete());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(1, CountPairs(sent, 0x00, 0x11)); // one SQ_PUT — no mid-load flush
    }

    [Fact]
    public void The_loader_default_flush_cadence_still_splits_at_32k()
    {
        // Regression guard for the classic behaviour: no FET_BUF_SIZE → the loader's
        // size-triggered flush fires at ~30 KB (3750 × 8 bytes), then the tail flushes
        // on Complete → exactly two SQ_PUT PDUs.
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot(), PutReply(3750), PutReply(1250), Eot(), Eot());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.Open();
        long before = duplex.Output.Length;

        for (int i = 0; i < 5000; i++)
            loader.Put(0x01020304, 0x05060708);
        Assert.Equal(5000, loader.Complete());

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(2, CountPairs(sent, 0x00, 0x11));
    }

    [Fact]
    public void A_put_error_surfaces_and_leaves_the_loader_faulted()
    {
        // On a SQ_PUTERR the loader closes the cursor (SQ_ID+SQ_CLOSE, reply EOT) so the
        // caller can still resolve the transaction, then faults and rethrows.
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot() /*open*/,
            PutErr(-239, rowsInserted: 1, "Unique constraint violated."),
            Eot() /*close reply*/);
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);

        loader.Put(1, 10);
        loader.Put(2, 20);
        var ex = Assert.Throws<IfxException>(() => loader.Complete());
        Assert.Equal(-239, ex.IfxSqlCode);

        // Cursor closed on the clean drain; faulted, so further Puts are refused and a
        // faulted dispose only unpins (no wire).
        Assert.False(cn.SqliSession!.InsertCursorOpen);
        Assert.Throws<InvalidOperationException>(() => loader.Put(3, 30));
        loader.Dispose();
    }

    // ================================================================= //
    //  Commit / rollback vs. a non-HOLD insert cursor                   //
    //  (live-verified against IDS 9.59, 2026-07-23: COMMIT and ROLLBACK //
    //  close a non-HOLD cursor symmetrically — see IfxBulkLoader.       //
    //  HoldCursor remarks)                                              //
    // ================================================================= //

    [Fact]
    public void Commit_auto_flushes_then_closes_a_non_hold_cursor()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(), // begin reply
            PrepareTwoInts(), Eot() /*open, no hold*/,
            PutReply(1) /*auto-flush inside OnBeforeCommit*/,
            SqliStatementKit.EotFrame(), // commit reply
            Eot() /*release for Complete() — no SQ_CLOSE: already closed server-side*/);

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var tx = cn.BeginTransaction();
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.Transaction = tx;

        // Values chosen so no tuple byte-pair collides with an asserted opcode
        // (e.g. 10 would put a 00-0A pair — SQ_CLOSE — inside the tuple).
        loader.Put(1, 300);
        Assert.Equal(1, loader.PendingRows);

        long before = duplex.Output.Length;
        tx.Commit();
        Assert.Equal(0, loader.PendingRows);
        Assert.Equal(1, loader.RowsInserted); // the commit already covers this row
        Assert.False(cn.SqliSession!.InsertCursorOpen); // server closed it — no HOLD

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(1, CountPairs(sent, 0x00, 0x11)); // the auto-flush's SQ_PUT
        Assert.Equal(0, CountPairs(sent, 0x00, 0x0a)); // no SQ_CLOSE — nothing to close yet

        var thrown = Assert.Throws<InvalidOperationException>(() => loader.Put(2, 400));
        Assert.Contains("HoldCursor", thrown.Message);

        Assert.Equal(1, loader.Complete()); // Complete() still works; skips CloseInsertCursor
    }

    [Fact]
    public void Rollback_drops_pending_rows_then_closes_a_non_hold_cursor()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(), // begin reply
            PrepareTwoInts(), Eot() /*open, no hold*/,
            SqliStatementKit.EotFrame(), // rollback reply — no flush precedes it
            Eot() /*release for Complete()*/);

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = cn.BeginTransaction();
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.Transaction = tx;

        loader.Put(1, 10);
        Assert.Equal(1, loader.PendingRows);

        tx.Rollback();
        Assert.Equal(0, loader.PendingRows); // abandoned, never sent
        Assert.Equal(0, loader.RowsInserted);
        Assert.False(cn.SqliSession!.InsertCursorOpen); // rollback closes it too — symmetric with commit

        Assert.Throws<InvalidOperationException>(() => loader.Put(2, 20));
        Assert.Equal(0, loader.Complete());
    }

    [Fact]
    public void HoldCursor_true_survives_commit_and_the_load_continues()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(), // begin reply
            PrepareTwoInts(), Eot() /*open with hold*/,
            PutReply(1) /*auto-flush inside OnBeforeCommit*/,
            SqliStatementKit.EotFrame(), // commit reply
            PutReply(1) /*Complete()'s flush of the row Put after the commit*/,
            Eot() /*close*/, Eot() /*release*/);

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = cn.BeginTransaction();
        using var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);
        loader.HoldCursor = true;
        loader.Transaction = tx;

        loader.Put(1, 10);
        tx.Commit();
        Assert.True(cn.SqliSession!.InsertCursorOpen); // HOLD: survives the commit

        loader.Put(2, 20); // does not throw — the cursor is still open
        Assert.Equal(1, loader.PendingRows);
        Assert.Equal(2, loader.Complete());
    }

    [Fact]
    public void A_faulted_loader_does_not_interfere_with_a_later_commit()
    {
        // A PUT error faults + unpins the loader; a subsequent commit on the same
        // connection must not try to run the (now unpinned) loader's OnBeforeCommit.
        byte[] script = SqliStatementKit.Join(
            PrepareTwoInts(), Eot() /*open*/,
            PutErr(-239, rowsInserted: 0, "Unique constraint violated."),
            Eot() /*close reply*/,
            SqliStatementKit.EotFrame(), // begin reply
            SqliStatementKit.EotFrame()); // commit reply

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var loader = cn.CreateBulkLoader("insert into t values (?, ?)");
        loader.DefineParameters(IfxType.Integer, IfxType.Integer);

        loader.Put(1, 10);
        Assert.Throws<IfxException>(() => loader.Complete()); // faults + unpins

        using var tx = cn.BeginTransaction();
        tx.Commit(); // must not throw, must not touch the faulted loader
    }
}
