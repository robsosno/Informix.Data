using System.Data;
using Informix.Data.Sqli;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxCommand"/>'s <see cref="ICloneable"/> implementation: copies the public
/// settings and deep-clones the parameter collection, but never the command's own execution
/// state (a held <see cref="IfxCommand.Prepare"/>d statement, the cached named-placeholder
/// parse) — that state belongs to this command's own lifetime, not to a copy of it.
/// </summary>
public class IfxCommandCloneTests
{
    [Fact]
    public void Command_implements_the_non_generic_ICloneable()
    {
        Assert.IsAssignableFrom<ICloneable>(new IfxCommand());
    }

    [Fact]
    public void Clone_copies_every_public_setting_and_deep_clones_parameters()
    {
        using var cn = new IfxConnection("Host=h;Service=9088;Database=db");
        var txn = new IfxTransaction(cn, IsolationLevel.RepeatableRead, isReadOnly: false); // internal ctor -- test seam

        using var source = new IfxCommand("select * from t where a = ?", cn)
        {
            CommandType = CommandType.Text,
            CommandTimeout = 99,
            DesignTimeVisible = true,
            UpdatedRowSource = UpdateRowSource.OutputParameters,
            HoldCursor = true,
            FetchBufferSize = 4096,
            Transaction = txn,
        };
        source.Parameters.Add("@a", 42);

        using var clone = source.Clone();

        Assert.Equal(source.CommandText, clone.CommandText);
        Assert.Equal(source.CommandType, clone.CommandType);
        Assert.Equal(source.CommandTimeout, clone.CommandTimeout);
        Assert.Equal(source.DesignTimeVisible, clone.DesignTimeVisible);
        Assert.Equal(source.UpdatedRowSource, clone.UpdatedRowSource);
        Assert.Equal(source.HoldCursor, clone.HoldCursor);
        Assert.Equal(source.FetchBufferSize, clone.FetchBufferSize);
        Assert.Same(cn, clone.Connection);
        Assert.Same(txn, clone.Transaction);

        Assert.Equal(1, clone.Parameters.Count);
        Assert.NotSame(source.Parameters[0], clone.Parameters[0]);
        Assert.Equal(42, ((IfxParameter)clone.Parameters[0]!).Value);
    }

    [Fact]
    public void Clone_through_the_ICloneable_facade_returns_an_independent_IfxCommand()
    {
        using var source = new IfxCommand("select 1 from t");

        object copy = ((ICloneable)source).Clone();

        var clone = Assert.IsType<IfxCommand>(copy);
        Assert.NotSame(source, clone);
        Assert.Equal(source.CommandText, clone.CommandText);
    }

    [Fact]
    public void Mutating_a_parameter_after_cloning_does_not_touch_the_clone()
    {
        using var source = new IfxCommand();
        source.Parameters.Add("@p", "original");

        using var clone = source.Clone();
        ((IfxParameter)source.Parameters[0]!).Value = "changed";

        Assert.Equal("original", ((IfxParameter)clone.Parameters[0]!).Value);
    }

    [Fact]
    public void Clone_parses_named_placeholders_independently_of_the_original()
    {
        using var original = new IfxCommand("select * from t where a = @a");
        original.ParseForTest(); // populate the original's cached parse
        Assert.Equal(1, original.ParseInvocationCount);

        using var clone = original.Clone();
        Assert.Equal(0, clone.ParseInvocationCount); // fresh cache, not copied from the original

        clone.ParseForTest();
        Assert.Equal(1, clone.ParseInvocationCount);
        Assert.Equal(1, original.ParseInvocationCount); // the clone's parse did not touch the original
    }

    [Fact]
    public void Clone_does_not_inherit_the_original_s_prepared_statement()
    {
        byte[] script = SqliStatementKit.Join(
            // original.Prepare()
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // clone.ExecuteNonQuery() must take the single-round-trip immediate path (a fresh
            // describe+execute), proving it holds no prepared statement of its own -- if it
            // had inherited the original's handle it would try to reactivate stmtId 1 instead
            // and desync against this script.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 2, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 7),
            SqliStatementKit.EotFrame(),
            // original.Dispose() still holds stmtId 1 -- releases it on the way out.
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var original = cn.CreateCommand();
        original.CommandText = "update t set a = 1";
        original.Prepare();

        using var clone = original.Clone();
        Assert.Equal(7, clone.ExecuteNonQuery());
    }
}
