using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Direct <see cref="SqliSession"/>-level coverage of the prepared-statement
/// handle primitives (<see cref="SqliSession.Prepare"/> / <see cref="SqliSession.Activate"/> /
/// <see cref="SqliSession.ReleaseStatementHandle"/>) that Stage 1's command-held
/// <c>Prepare()</c> and Stage 2's session cache are both built on.
/// </summary>
public class SqliStatementHandleTests
{
    private static SqliSession PreparedSession(byte[] script, out DuplexStream duplex)
    {
        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out duplex);
        session.BringUp();
        return session;
    }

    private static byte[] SelectDescribe(short stmtId, string column) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col(column, (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    [Fact]
    public void Prepare_returns_a_handle_snapshotting_the_current_statement()
    {
        var session = PreparedSession(SelectDescribe(7, "id"), out _);

        var handle = session.Prepare("select id from t", parameterCount: 2);

        Assert.Equal(7, handle.StatementId);
        Assert.Equal((short)SqliStatementType.Select, handle.StatementType);
        Assert.Equal(2, handle.ParameterCount);
        Assert.Equal(4, handle.TupleSize);
        Assert.Single(handle.Columns);
        Assert.Equal("id", handle.Columns[0].Name);
        Assert.True(handle.DescribesResultSet);
    }

    [Fact]
    public void Activate_reinstates_a_stashed_handle_without_any_wire_traffic()
    {
        byte[] script = SqliStatementKit.Join(SelectDescribe(1, "id"), SelectDescribe(2, "v"));
        var session = PreparedSession(script, out var duplex);

        var handleA = session.Prepare("select id from t");
        session.Prepare("select v from u"); // becomes current; A is now stashed-but-valid on the server
        Assert.Equal(2, session.StatementId);

        long before = duplex.Output.Length;
        session.Activate(handleA);

        Assert.Equal(before, duplex.Output.Length); // pure local bookkeeping, no wire traffic
        Assert.Equal(1, session.StatementId);
        Assert.Single(session.Columns);
        Assert.Equal("id", session.Columns[0].Name);
    }

    [Fact]
    public void Activate_of_the_already_current_handle_is_a_no_op()
    {
        var session = PreparedSession(SelectDescribe(1, "id"), out var duplex);
        var handle = session.Prepare("select id from t");

        long before = duplex.Output.Length;
        session.Activate(handle);

        Assert.Equal(before, duplex.Output.Length);
        Assert.Equal(1, session.StatementId);
    }

    [Fact]
    public void ReleaseStatementHandle_of_a_non_current_handle_does_not_disturb_the_active_statement()
    {
        byte[] script = SqliStatementKit.Join(
            SelectDescribe(1, "id"),
            SelectDescribe(2, "v"),
            SqliStatementKit.EotFrame()); // release reply for A (bare EOT)
        var session = PreparedSession(script, out var duplex);

        var handleA = session.Prepare("select id from t");
        session.Prepare("select v from u"); // B becomes current
        long before = duplex.Output.Length;

        session.ReleaseStatementHandle(handleA);

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, 4, 0, 1, 0, 11, 0, 12 }, sent); // SQ_ID(1) + SQ_RELEASE + EOT
        Assert.Equal(2, session.StatementId); // B is still active, untouched
        Assert.Single(session.Columns);
        Assert.Equal("v", session.Columns[0].Name);
    }

    [Fact]
    public void ReleaseStatementHandle_of_the_current_handle_clears_current_statement_bookkeeping()
    {
        byte[] script = SqliStatementKit.Join(
            SelectDescribe(5, "id"),
            SqliStatementKit.EotFrame()); // release reply
        var session = PreparedSession(script, out _);
        var handle = session.Prepare("select id from t");

        session.ReleaseStatementHandle(handle);

        Assert.Equal(-1, session.StatementId);
        Assert.True(session.IsIdle);
        Assert.Empty(session.Columns);
    }
}
