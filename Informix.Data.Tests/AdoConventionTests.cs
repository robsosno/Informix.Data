using System.Data.Common;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Conventions surfaced by AdoNet.Specification.Tests: how a SQL NULL reads through GetString /
/// GetFieldValue, which factory a connection reports, and what a command does with a transaction
/// that belongs to another connection.
/// </summary>
public class AdoConventionTests
{
    // One-column, one-row cursor; the getters under test never look at the column metadata.
    private sealed class SingleValueCursor(object value) : IIfxResultCursor
    {
        public IReadOnlyList<IfxColumnMetadata> Columns { get; } =
            [new IfxColumnMetadata { Name = "c", SqliTypeCode = SqliTypeCode.VarChar }];
        public int RecordsAffected => -1;
        public bool HasRows => true;
        public bool Read() => true;
        public object GetValue(int ordinal) => value;
        public byte[]? GetRawBytes(int ordinal) => null;
        public double? GetDecimalAsDouble(int ordinal) => null;
        public byte[]? GetSmartLobLocator(int ordinal) => null;
        public void Dispose() { }
    }

    private static IfxDataReader ReaderOver(object value) => new(new SingleValueCursor(value));

    [Fact]
    public void GetString_throws_InvalidCastException_on_NULL()
    {
        using var reader = ReaderOver(DBNull.Value);
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
    }

    [Fact]
    public void GetString_still_returns_the_empty_string_for_an_empty_VARCHAR()
    {
        using var reader = ReaderOver("");
        Assert.Equal("", reader.GetString(0));
    }

    [Fact]
    public void GetFieldValue_DBNull_and_object_return_DBNull_Value_on_NULL()
    {
        using var reader = ReaderOver(DBNull.Value);
        Assert.Same(DBNull.Value, reader.GetFieldValue<DBNull>(0));
        Assert.Same(DBNull.Value, reader.GetFieldValue<object>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<int>(0));
    }

    [Fact]
    public void Connection_reports_the_Informix_factory()
    {
        using var connection = new IfxConnection();
        Assert.Same(InformixClientFactory.Instance, DbProviderFactories.GetFactory(connection));
    }

    private sealed class FakeConnectionTransaction
    {
        // IfxTransaction has an internal constructor taking the owning connection; the check only
        // compares IfxTransaction.Connection with the command's connection, which needs no server.
        public static IfxTransaction Of(IfxConnection owner) =>
            (IfxTransaction)Activator.CreateInstance(typeof(IfxTransaction),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new object[] { owner, System.Data.IsolationLevel.ReadCommitted, false }, null)!;
    }

    [Fact]
    public void Command_with_a_transaction_of_another_connection_throws_InvalidOperationException()
    {
        using var a = new IfxConnection();
        using var b = new IfxConnection();
        using var command = a.CreateCommand();
        command.CommandText = "select 1 from sysmaster:sysdual";
        command.Transaction = FakeConnectionTransaction.Of(b);
        var ex = Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
        Assert.Contains("different connection", ex.Message);
    }

    // ---- a completed transaction -------------------------------------------------------------

    private static byte[] BeginCommitScript() => SqliStatementKit.Join(
        SqliStatementKit.EotFrame(),   // SQ_BEGIN reply
        SqliStatementKit.EotFrame());  // SQ_CMMTWORK / SQ_RBWORK reply

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_completed_transaction_throws_InvalidOperationException_not_ObjectDisposedException(bool firstCommit, bool secondCommit)
    {
        using var cn = SqliStatementKit.OpenConnection(BeginCommitScript(), out _);
        using var tx = cn.BeginTransaction();
        if (firstCommit) tx.Commit(); else tx.Rollback();

        var ex = Assert.Throws<InvalidOperationException>(() => { if (secondCommit) tx.Commit(); else tx.Rollback(); });
        Assert.Contains("completed", ex.Message);
        Assert.IsNotType<ObjectDisposedException>(ex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Connection_is_null_once_the_transaction_has_completed(bool commit)
    {
        using var cn = SqliStatementKit.OpenConnection(BeginCommitScript(), out _);
        using var tx = cn.BeginTransaction();
        Assert.Same(cn, tx.Connection);
        if (commit) tx.Commit(); else tx.Rollback();
        Assert.Null(tx.Connection);
    }

    [Fact]
    public void A_disposed_transaction_still_throws_ObjectDisposedException()
    {
        using var cn = SqliStatementKit.OpenConnection(BeginCommitScript(), out _);
        var tx = cn.BeginTransaction();
        tx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => tx.Commit());
        Assert.Throws<ObjectDisposedException>(() => tx.Rollback());
    }

    [Fact]
    public void A_command_may_still_carry_a_completed_transaction_it_is_not_a_connection_mismatch()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.EotFrame(),   // begin
            SqliStatementKit.EotFrame(),   // commit
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = cn.BeginTransaction();
        using var command = cn.CreateCommand();
        command.CommandText = "insert into t values (1)";
        command.Transaction = tx;
        tx.Commit();
        // SqlClient treats a completed transaction as none, and so does this: no mismatch error.
        Assert.Null(Record.Exception(() => command.ExecuteNonQuery()));
    }

    // ---- null into the parameter collection ----------------------------------------------------

    [Fact]
    public void Parameter_collection_rejects_null_with_ArgumentNullException()
    {
        using var cn = new IfxConnection();
        using var command = cn.CreateCommand();
        var parameters = command.Parameters;
        parameters.Add(new IfxParameter("a", 1));

        Assert.Throws<ArgumentNullException>(() => ((System.Collections.IList)parameters).Add(null));
        Assert.Throws<ArgumentNullException>(() => parameters.Insert(0, null!));
        Assert.Throws<ArgumentNullException>(() => parameters.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => ((System.Collections.IList)parameters)[0] = null);
        Assert.Throws<ArgumentNullException>(() => parameters[0] = null!);
        Assert.Throws<ArgumentNullException>(() => ((System.Data.Common.DbParameterCollection)parameters)["a"] = null!);
        Assert.Throws<ArgumentNullException>(() => parameters.AddRange((Array)null!));
        Assert.Equal(1, parameters.Count);
    }

    [Fact]
    public void Parameter_collection_still_rejects_a_foreign_object_with_InvalidCastException()
    {
        using var cn = new IfxConnection();
        using var command = cn.CreateCommand();
        Assert.Throws<InvalidCastException>(() => ((System.Collections.IList)command.Parameters).Add("not a parameter"));
    }

    // ---- ExecuteXxx / Open argument checks --------------------------------------------------------

    [Fact]
    public void An_empty_or_missing_CommandText_throws_InvalidOperationException_before_anything_is_sent()
    {
        using var cn = SqliStatementKit.OpenConnection(Array.Empty<byte>(), out var duplex);
        long before = duplex.Output.Length;
        using var command = cn.CreateCommand();

        foreach (string? text in new string?[] { null, "", "   " })
        {
            command.CommandText = text;
            Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
            Assert.Throws<InvalidOperationException>(() => command.ExecuteReader());
            Assert.Throws<InvalidOperationException>(() => command.Prepare());
        }
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void Open_without_a_ConnectionString_throws_InvalidOperationException()
    {
        using var cn = new IfxConnection();
        var ex = Assert.Throws<InvalidOperationException>(() => cn.Open());
        Assert.Contains("ConnectionString", ex.Message);
    }

    // ---- the reader: closed, no result set, ordinal out of range -----------------------------------

    [Fact]
    public void A_closed_reader_throws_InvalidOperationException_from_every_member()
    {
        var reader = ReaderOver("x");
        reader.Dispose();

        Assert.Throws<InvalidOperationException>(() => reader.Read());
        Assert.Throws<InvalidOperationException>(() => reader.NextResult());
        Assert.Throws<InvalidOperationException>(() => reader.FieldCount);
        Assert.Throws<InvalidOperationException>(() => reader.HasRows);
        Assert.Throws<InvalidOperationException>(() => reader.GetName(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldType(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetDataTypeName(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetOrdinal("c"));
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.Throws<InvalidOperationException>(() => reader.IsDBNull(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetString(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetValues(new object[1]));
        Assert.Throws<InvalidOperationException>(() => reader.GetSchemaTable());
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<string>(0));
    }

    [Fact]
    public void An_ordinal_outside_the_result_set_throws_IndexOutOfRangeException()
    {
        using var reader = ReaderOver("x");

        Assert.Throws<IndexOutOfRangeException>(() => reader.GetName(1));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetName(-1));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetFieldType(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetDataTypeName(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetValue(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.IsDBNull(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetInt32(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetString(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("nope"));
        Assert.Equal("c", reader.GetName(0));   // the valid ordinal is untouched
    }

    [Fact]
    public void A_statement_without_a_result_set_throws_InvalidOperationException_for_column_access()
    {
        using var reader = new IfxDataReader(new SqliEmptyCursor(0));

        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.Read());
        Assert.False(reader.NextResult());
        Assert.Throws<InvalidOperationException>(() => reader.GetName(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldType(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetDataTypeName(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.Throws<InvalidOperationException>(() => reader.IsDBNull(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetValues(new object[1]));
        // GetSchemaTable keeps answering with an empty table rather than null (a caller dereferencing it never faults).
        Assert.Empty(reader.GetSchemaTable().Rows);
    }

    // ---- a command inside a local transaction carries it ----------------------------------------------

    [Fact]
    public void A_command_without_the_connections_pending_transaction_throws_InvalidOperationException()
    {
        using var cn = SqliStatementKit.OpenConnection(SqliStatementKit.EotFrame(), out var duplex);
        using var tx = cn.BeginTransaction();
        long before = duplex.Output.Length;
        using var command = cn.CreateCommand();
        command.CommandText = "select 1 from sysmaster:sysdual";

        var ex = Assert.Throws<InvalidOperationException>(() => command.ExecuteReader());
        Assert.Contains("Transaction", ex.Message);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void A_command_outside_any_transaction_needs_no_Transaction()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScriptForCounts(), out _);
        using var command = cn.CreateCommand();
        command.CommandText = "insert into t values (1)";
        Assert.Null(Record.Exception(() => command.ExecuteNonQuery()));   // no marker, no parameter: nothing to count or carry
    }

    // ---- positional placeholders and the parameter count ------------------------------------------------

    private static byte[] InsertScriptForCounts() => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    [Theory]
    [InlineData("insert into t values (1)", 1)]          // no marker, one parameter
    [InlineData("insert into t values (?)", 0)]          // one marker, no parameter
    [InlineData("insert into t values (?, ?)", 1)]       // too few
    [InlineData("insert into t values (?)", 2)]          // too many
    public void Positional_text_with_a_different_number_of_parameters_throws_before_sending(string sql, int parameters)
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScriptForCounts(), out var duplex);
        long before = duplex.Output.Length;
        using var command = cn.CreateCommand();
        command.CommandText = sql;
        for (int i = 0; i < parameters; i++)
            command.Parameters.Add(new IfxParameter("p" + i, i));

        var ex = Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
        Assert.Contains("placeholder", ex.Message);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void A_question_mark_inside_a_literal_or_comment_is_not_a_placeholder()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScriptForCounts(), out _);
        using var command = cn.CreateCommand();
        command.CommandText = "insert into t values ('?') -- why? ";
        Assert.Null(Record.Exception(() => command.ExecuteNonQuery()));
    }

    [Fact]
    public void Positional_text_with_matching_parameters_executes()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScriptForCounts(), out _);
        using var command = cn.CreateCommand();
        command.CommandText = "insert into t values (?)";
        command.Parameters.Add(new IfxParameter("p", 1));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public void Named_text_still_ignores_a_parameter_nothing_refers_to()
    {
        using var cn = SqliStatementKit.OpenConnection(InsertScriptForCounts(), out _);
        using var command = cn.CreateCommand();
        command.CommandText = "insert into t values (@a)";
        command.Parameters.Add(new IfxParameter("@a", 1));
        command.Parameters.Add(new IfxParameter("@extra", 2));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    // ---- IfxParameter.SourceVersion ---------------------------------------------------------------------

    [Fact]
    public void SourceVersion_defaults_to_Current()
    {
        Assert.Equal(System.Data.DataRowVersion.Current, new IfxParameter().SourceVersion);
        Assert.Equal(System.Data.DataRowVersion.Current, new IfxParameter("p", 1).SourceVersion);
    }

    [Theory]
    [InlineData(System.Data.DataRowVersion.Original)]
    [InlineData(System.Data.DataRowVersion.Current)]
    [InlineData(System.Data.DataRowVersion.Proposed)]
    [InlineData(System.Data.DataRowVersion.Default)]
    public void SourceVersion_keeps_the_value_it_was_given(System.Data.DataRowVersion version)
    {
        var parameter = new IfxParameter("p", 1) { SourceVersion = version };
        Assert.Equal(version, parameter.SourceVersion);
        // a DbDataAdapter reads it through the interface
        Assert.Equal(version, ((System.Data.IDataParameter)parameter).SourceVersion);
        Assert.Equal(version, ((System.Data.Common.DbParameter)parameter).SourceVersion);
    }

    [Fact]
    public void Clone_carries_SourceVersion()
    {
        var original = new IfxParameter("orig_id", 1) { SourceColumn = "id", SourceVersion = System.Data.DataRowVersion.Original };
        Assert.Equal(System.Data.DataRowVersion.Original, original.Clone().SourceVersion);
        Assert.Equal(System.Data.DataRowVersion.Original, ((System.Data.Common.DbParameter)((ICloneable)original).Clone()).SourceVersion);
    }

    // ---- ServerVersion -------------------------------------------------------------------------------------

    [Fact]
    public void ServerVersion_throws_InvalidOperationException_when_the_connection_is_not_open()
    {
        using var never = new IfxConnection();
        var ex = Assert.Throws<InvalidOperationException>(() => never.ServerVersion);
        Assert.Contains("open connection", ex.Message);

        var closed = SqliStatementKit.OpenConnection(Array.Empty<byte>(), out _);
        Assert.False(string.IsNullOrEmpty(closed.ServerVersion));   // open: answers
        closed.Dispose();
        Assert.Throws<InvalidOperationException>(() => closed.ServerVersion);   // closed again: throws
    }
}
