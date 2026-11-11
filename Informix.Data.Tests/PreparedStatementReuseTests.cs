using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxCommand.Prepare()"/> (Stage 1 of the prepared-statement cache work):
/// an explicit <c>Prepare()</c> call holds its statement across executions instead of
/// preparing and releasing it every time, and drops it when the held shape goes stale
/// (CommandText/Connection change, Dispose). Proven mostly by script exhaustion — an
/// accidental re-prepare or re-release tries to read/send frames that were never
/// scripted for it and fails loudly, exactly as a real desync would.
/// </summary>
public class PreparedStatementReuseTests
{
    [Fact]
    public void Prepare_forces_the_prepared_path_even_with_zero_parameters()
    {
        byte[] script = SqliStatementKit.Join(
            // Prepare() round trip
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // ExecuteNonQuery #1 — a prepared-path execute (SQ_ID+SQ_EXECUTE), not the
            // single-round-trip SQ_COMMAND immediate path a zero-param statement would
            // otherwise take.
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // ExecuteNonQuery #2 — reactivated, not re-prepared: if the driver mistakenly
            // re-prepared here it would try to read a describe frame from what is really
            // just another execute reply, and fail.
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1 where id = 1";
        cmd.Prepare();

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(2, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Prepare_with_parameters_reuses_the_statement_across_executions_with_different_values()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?)";
        cmd.Parameters.Add("p1", 1);
        cmd.Prepare();

        Assert.Equal(1, cmd.ExecuteNonQuery());

        cmd.Parameters[0].Value = 2; // rebind on the reactivated (not re-prepared) statement
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Prepared_ExecuteReader_reuses_the_statement_across_open_close_cycles()
    {
        byte[] row1 = new byte[4];
        IfxWireConverter.WriteInt32(row1, 101);
        byte[] row2 = new byte[4];
        IfxWireConverter.WriteInt32(row2, 202);

        byte[] script = SqliStatementKit.Join(
            // Prepare()
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // ExecuteReader #1: open + fetch + server auto-close at end of rows
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row1),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // ExecuteReader #2: reactivated (no SQ_PREPARE), opened again
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row2),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "select id from t";
        cmd.Prepare();

        using (var reader1 = cmd.ExecuteReader())
        {
            Assert.True(reader1.Read());
            Assert.Equal(101, reader1.GetInt32(0));
            Assert.False(reader1.Read());
        } // closes the cursor but must not release the held statement

        using var reader2 = cmd.ExecuteReader();
        Assert.True(reader2.Read());
        Assert.Equal(202, reader2.GetInt32(0));
        Assert.False(reader2.Read());
    }

    [Fact]
    public void Changing_CommandText_after_Prepare_releases_the_old_statement()
    {
        byte[] script = SqliStatementKit.Join(
            // Prepare() over "update t set a = 1" (zero params)
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // release reply, sent eagerly by the CommandText setter
            SqliStatementKit.EotFrame(),
            // ExecuteNonQuery("update t set a = 2") -- no longer held, so it takes the
            // single-round-trip immediate path (describe+execute+release folded into one PDU)
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 2, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 5),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        long beforeChange = duplex.Output.Length;

        cmd.CommandText = "update t set a = 2";

        byte[] releaseBytes = duplex.Output.ToArray()[(int)beforeChange..];
        Assert.Equal(new byte[] { 0, 4, 0, 1, 0, 11, 0, 12 }, releaseBytes); // SQ_ID(1) + SQ_RELEASE + EOT

        Assert.Equal(5, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Reassigning_CommandText_to_the_same_text_does_not_release_the_held_statement()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        long before = duplex.Output.Length;

        cmd.CommandText = "update t set a = 1"; // same text -- must be a no-op, no release

        Assert.Equal(before, duplex.Output.Length);
        Assert.Equal(1, cmd.ExecuteNonQuery()); // still reuses the held statement
    }

    [Fact]
    public void Disposing_a_prepared_command_releases_the_held_statement()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 3, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        long before = duplex.Output.Length;

        cmd.Dispose();

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, 4, 0, 3, 0, 11, 0, 12 }, sent); // SQ_ID(3) + SQ_RELEASE + EOT
    }

    [Fact]
    public void Reassigning_Connection_releases_the_handle_held_on_the_old_connection()
    {
        byte[] scriptA = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply, triggered by the Connection reassignment
        using var cnA = SqliStatementKit.OpenConnection(scriptA, out var duplexA);
        using var cnB = SqliStatementKit.OpenConnection([], out _); // no statement activity expected

        var cmd = cnA.CreateCommand();
        cmd.CommandText = "update t set a = 1";
        cmd.Prepare();
        long before = duplexA.Output.Length;

        cmd.Connection = cnB;

        byte[] sent = duplexA.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, 4, 0, 1, 0, 11, 0, 12 }, sent); // SQ_ID(1) + SQ_RELEASE + EOT
    }
}
