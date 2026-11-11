using System.Collections.Generic;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxCommand.Unprepare"/>/<see cref="IfxCommand.IsPrepared"/>: an explicit way to
/// drop a <see cref="IfxCommand.Prepare()"/>-held statement early, without waiting for
/// <see cref="IfxCommand.Dispose"/> or a <see cref="IfxCommand.CommandText"/>/
/// <see cref="IfxCommand.Connection"/> change. Also covers the pooled-session identity guard
/// (<c>_preparedSession</c>) that <c>Unprepare</c>/<c>AcquireStatement</c> rely on: a pooled
/// <see cref="IfxConnection"/> can go through Close()+Open() and come back with a *different*
/// physical session without this command's CommandText/Connection ever changing.
/// </summary>
public class IfxCommandUnprepareTests
{
    [Fact]
    public void Unprepare_releases_the_held_statement_and_flips_IsPrepared()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // Unprepare()'s release reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        Assert.True(cmd.IsPrepared);
        long before = duplex.Output.Length;

        cmd.Unprepare();

        Assert.False(cmd.IsPrepared);
        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, 4, 0, 4, 0, 11, 0, 12 }, sent); // SQ_ID(4) + SQ_RELEASE + EOT
    }

    [Fact]
    public void Unprepare_on_a_never_prepared_command_is_a_no_op()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        long before = duplex.Output.Length;

        cmd.Unprepare();

        Assert.False(cmd.IsPrepared);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void A_second_Unprepare_call_is_a_no_op()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // Unprepare()'s release reply -- only sent once

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        cmd.Unprepare();
        long before = duplex.Output.Length;

        cmd.Unprepare(); // nothing left to release

        Assert.False(cmd.IsPrepared);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void Changing_CommandText_then_calling_Unprepare_is_a_no_op()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply, sent eagerly by the CommandText setter

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        cmd.CommandText = "update t set a = 2"; // already releases the old handle
        Assert.False(cmd.IsPrepared);
        long before = duplex.Output.Length;

        cmd.Unprepare(); // the handle is already gone -- nothing left to do

        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void Prepare_Unprepare_then_ExecuteNonQuery_takes_the_normal_path_and_Prepare_works_again()
    {
        byte[] script = SqliStatementKit.Join(
            // cmd.Prepare()
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // cmd.Unprepare()'s release
            SqliStatementKit.EotFrame(),
            // ExecuteNonQuery(): no held statement and no cache -> single-round-trip immediate path
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 2, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 5),
            SqliStatementKit.EotFrame(),
            // cmd.Prepare() again
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 3, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // cmd Dispose() at the end of the test: releases the still-held statement 3
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        cmd.Unprepare();

        Assert.Equal(5, cmd.ExecuteNonQuery());
        Assert.False(cmd.IsPrepared); // an ordinary execute never re-establishes a held prepare

        cmd.Prepare();
        Assert.True(cmd.IsPrepared);
    }

    [Fact]
    public void Unprepare_throws_while_a_reader_is_open_then_succeeds_once_it_closes()
    {
        byte[] row = new byte[4];
        IfxWireConverter.WriteInt32(row, 101);

        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 6, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // ExecuteReader: open + one-row fetch + server auto-close. CursorOpen still reads
            // true until the reader is actually closed client-side (CloseCursor is a local
            // no-op once the server's close already arrived).
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // Unprepare()'s release, after the reader is closed
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id from t";
        cmd.Prepare();

        var reader = cmd.ExecuteReader();
        var ex = Assert.Throws<InvalidOperationException>(() => cmd.Unprepare());
        Assert.Contains("data reader", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(cmd.IsPrepared); // the throw must not have torn down the held state

        reader.Dispose(); // closes the cursor (no wire cost -- the server already auto-closed)
        cmd.Unprepare();
        Assert.False(cmd.IsPrepared);
    }

    [Fact]
    public void Unprepare_throws_while_a_bulk_loader_owns_the_session()
    {
        byte[] script = SqliStatementKit.Join(
            // cmd.Prepare()
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // loader.Open() -> its own session.Prepare(); zero described columns keeps it off
            // the buffered insert-cursor engine, so Open() needs no more wire traffic.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 2, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // loader Dispose() releases its pinned (never-completed) statement
            SqliStatementKit.EotFrame(),
            // cmd Dispose() -> ReleaseHeldHandle(), now that the loader has unpinned the session
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();

        using var loader = cn.CreateBulkLoader("insert into t values (?)");
        loader.DefineParameters(IfxType.Integer);
        loader.Open();

        var ex = Assert.Throws<InvalidOperationException>(() => cmd.Unprepare());
        Assert.Contains("bulk loader", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(cmd.IsPrepared);
    }

    [Fact]
    public void Unprepare_on_a_closed_connection_clears_locally_without_throwing_or_sending_wire_traffic()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());
            // No release reply scripted: Close() drops the physical (Pooling=false in the
            // default OpenConnection connection string), and Unprepare() on the now-closed
            // connection must not attempt one.

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();

        cn.Close();

        cmd.Unprepare(); // must not throw, must not touch the wire
        Assert.False(cmd.IsPrepared);
    }

    [Fact]
    public void Prepare_and_Unprepare_never_touch_the_session_prepared_statement_cache()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // Unprepare()'s release

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Prepared Statement Cache Size=2");
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";

        cmd.Prepare();
        cmd.Unprepare();

        Assert.Equal(0, cn.PreparedStatementCacheHits);
        Assert.Equal(0, cn.PreparedStatementCacheMisses);
    }

    // ================================================================= //
    //  Session-identity: a pooled Close()+Open() drawing a *different*  //
    //  physical connection must never Activate/release a foreign        //
    //  statement id on the newly live session.                          //
    // ================================================================= //

    private static byte[] BringUpScript() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    // A connection whose physical-connection factory hands out one scripted duplex stream
    // per call, in order -- so the test controls exactly what each successive physical
    // connection (pre- and post-reconnect) sees on the wire.
    private static IfxConnection ConnWithPhysicals(string cs, Queue<byte[]> physicalScripts, List<DuplexStream> duplexes)
    {
        var cn = new IfxConnection(cs);
        cn.AsfConnectionFactory = settings =>
        {
            var duplex = new DuplexStream(physicalScripts.Dequeue());
            duplexes.Add(duplex);
            return new AsfConnection(settings, duplex);
        };
        return cn;
    }

    [Fact]
    public void Unprepare_after_a_pooled_reconnect_to_a_different_physical_clears_locally_with_no_wire_traffic()
    {
        string cs = "Host=h;Service=9088;Server=ol_test;Database=pool_unprep_a;User ID=usr;Password=pwd";

        byte[] physicalA = SqliStatementKit.Join(
            BringUpScript(),
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0), // cmd.Prepare()
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // cn.Close(): releases the still-current stmt 1 for real

        byte[] physicalB = BringUpScript(); // nothing else -- Unprepare() must send nothing here

        var scripts = new Queue<byte[]>([physicalA, physicalB]);
        var duplexes = new List<DuplexStream>();
        using var cn = ConnWithPhysicals(cs, scripts, duplexes);

        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        Assert.True(cmd.IsPrepared);

        cn.Close();
        IfxConnection.ClearPool(cn); // discards the idle physical A -- next Open() must build a fresh one
        cn.Open(); // physical B: a genuinely different SqliSession instance

        long beforeB = duplexes[1].Output.Length;
        cmd.Unprepare();

        Assert.False(cmd.IsPrepared);
        Assert.Equal(beforeB, duplexes[1].Output.Length); // stmt 1 was never mentioned to session B
    }

    [Fact]
    public void Executing_a_held_prepare_after_a_pooled_reconnect_re_prepares_instead_of_activating_a_foreign_handle()
    {
        string cs = "Host=h;Service=9088;Server=ol_test;Database=pool_unprep_b;User ID=usr;Password=pwd";

        byte[] physicalA = SqliStatementKit.Join(
            BringUpScript(),
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0), // cmd.Prepare()
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // cn.Close(): releases the still-current stmt 1 for real

        byte[] physicalB = SqliStatementKit.Join(
            BringUpScript(),
            // A fresh prepare on the new session (stmt id 7, unrelated to the old handle's 1) --
            // if the driver mistakenly tried to Activate(1) here it would send SQ_ID(1) instead
            // of SQ_PREPARE, and this describe reply would desync it.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 7, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // no cache configured -> released for real after the execute

        var scripts = new Queue<byte[]>([physicalA, physicalB]);
        var duplexes = new List<DuplexStream>();
        using var cn = ConnWithPhysicals(cs, scripts, duplexes);

        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();

        cn.Close();
        IfxConnection.ClearPool(cn);
        cn.Open(); // a different SqliSession instance now backs this connection

        Assert.True(cmd.IsPrepared); // still (stale-)holding stmt 1 from session A

        Assert.Equal(1, cmd.ExecuteNonQuery()); // must re-prepare fresh on session B, not Activate(1)

        Assert.False(cmd.IsPrepared); // the implicit re-prepare is one-shot, not held
    }
}
