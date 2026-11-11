using System.Data;
using System.Linq;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The opt-in <see cref="IfxConnectionStringBuilder.SelectOnly"/> connection-string flag
/// (<c>Select Only=true</c>): a client-side guard that rejects every statement but a plain
/// SELECT with SQLCODE -878 ("Invalid operation for a READ-ONLY transaction"), before the
/// caller ever gets a handle that could execute it. Classification comes from the server's
/// own SQ_DESCRIBE reply (<see cref="SqliStatementType"/>), not from parsing SQL text — see
/// <see cref="SqliSession.Prepare"/>.
///
/// <para>This keyword was originally spelled <c>Read Only</c>; that spelling has since been
/// reused for a different, server-enforced keyword —
/// <see cref="IfxConnectionStringBuilder.ReadOnly"/> (see <see cref="ReadOnlyTests"/>). The
/// two are independent; see either property's doc comment for how they compare.</para>
/// </summary>
public class SelectOnlyTests
{
    [Fact]
    public void Prepare_of_a_select_succeeds_when_the_session_is_selectonly()
    {
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5").Concat(reply).ToArray(), out _);
        session.BringUp();
        session.SelectOnly = true;

        var handle = session.Prepare("select id from t");

        Assert.True(handle.DescribesResultSet);
    }

    [Theory]
    [InlineData((short)SqliStatementType.Insert)]
    [InlineData((short)SqliStatementType.Update)]
    [InlineData((short)SqliStatementType.Delete)]
    [InlineData((short)SqliStatementType.CreateTable)]
    [InlineData((short)SqliStatementType.ExecProc)]
    // SELECT ... INTO TEMP creates a table -- it must not slip through just because its
    // text starts with the SELECT keyword. The server reports it as its own distinct type.
    [InlineData((short)SqliStatementType.SelectInto)]
    public void Prepare_of_a_non_select_is_rejected_when_the_session_is_selectonly(short type)
    {
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(type, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply: the rejected statement is freed server-side

        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5").Concat(reply).ToArray(), out _);
        session.BringUp();
        session.SelectOnly = true;

        var ex = Assert.Throws<IfxException>(() => session.Prepare("<statement text is irrelevant here>"));

        Assert.Equal(SqliSession.SelectOnlyViolationSqlCode, ex.IfxSqlCode);
        Assert.Equal(-878, ex.IfxSqlCode);
    }

    [Fact]
    public void Prepare_is_not_restricted_when_selectonly_is_off()
    {
        byte[] reply = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 4, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        var session = SqliTestKit.SessionOver(SqliTestKit.BringUpReply("9.59.FC5").Concat(reply).ToArray(), out _);
        session.BringUp();

        var handle = session.Prepare("insert into t values (1)");

        Assert.False(handle.DescribesResultSet);
    }

    // The zero-parameter, non-prepared IfxCommand fast path (SQ_COMMAND: describe + execute
    // + release folded into a single round trip, see IfxCommand.ExecuteNonQueryCore) must not
    // be used under SelectOnly, since by the time its reply describes the statement's type the
    // EXECUTE would already be in flight. This proves ExecuteNonQuery is routed through the
    // prepared path instead, where Prepare() gets to reject the statement before anything
    // resembling an execute reaches the wire -- the script below contains no execute reply at
    // all, so the test would hang/desync if the fast path were still taken.
    [Fact]
    public void ExecuteNonQuery_of_a_parameterless_update_is_rejected_when_selectonly()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Select Only=true");
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());

        Assert.Equal(-878, ex.IfxSqlCode);
    }

    [Fact]
    public void ExecuteReader_of_a_select_still_works_when_selectonly()
    {
        byte[] row = new byte[4];
        IfxWireConverter.WriteInt32(row, 7);
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Select Only=true");
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id from t";

        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(7, reader.GetInt32(0));
    }

    // Select Only and Read Only are independent (§2.5): the guard doesn't inspect the
    // driver's own ExecuteImmediate calls (e.g. the SET TRANSACTION READ ONLY
    // BeginTransaction(isReadOnly: true) sends), so a SELECT still works under both at once.
    [Fact]
    public void SelectOnly_plus_a_read_only_transaction_still_allows_a_select()
    {
        byte[] row = new byte[4];
        IfxWireConverter.WriteInt32(row, 7);
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(), // SQ_BEGIN reply
            SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame(), // SET TRANSACTION READ ONLY reply
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: 4,
                new SqliStatementKit.Col("id", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Select Only=true");
        using var tx = cn.BeginTransaction(IsolationLevel.Unspecified, isReadOnly: true);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id from t";
        cmd.Transaction = tx;

        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(7, reader.GetInt32(0));
    }

    // A hand-written SET TRANSACTION is itself rejected under Select Only -- it isn't a
    // SELECT -- which is correct, not a bug (§2.5).
    [Fact]
    public void A_hand_written_SET_TRANSACTION_is_rejected_under_selectonly()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Isolate, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Select Only=true");
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED";

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());

        Assert.Equal(-878, ex.IfxSqlCode);
    }
}
