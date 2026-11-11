using System.Data;
using System.Text;
using Informix.Data.Sqli;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Covers the named-parameter tokenizer (<see cref="IfxSqlParser"/>) and its
/// integration into <see cref="IfxCommand"/>/<see cref="IfxRowBatch"/> binding: the
/// rewrite of <c>@name</c>/<c>:name</c> to the Informix-native positional <c>?</c>,
/// the by-name resolution of bind values, and the caching/fast-path guarantees.
/// </summary>
public class IfxSqlParserTests
{
    [Theory]
    // plain rewrite, multiples, duplicates
    [InlineData("@a", "?", "a")]
    [InlineData(":a", "?", "a")]
    [InlineData("select :a, :b, :a from t", "select ?, ?, ? from t", "a,b,a")]
    [InlineData("select @a, @b from t where @a > 0", "select ?, ? from t where ? > 0", "a,b,a")]
    // a single-quoted literal (with @/:/? inside) is left untouched; '' escapes a quote
    [InlineData("insert into t values ('@x :y ?', :real)", "insert into t values ('@x :y ?', ?)", "real")]
    [InlineData("select 'it''s @x :y' , :a", "select 'it''s @x :y' , ?", "a")]
    // a double-quoted identifier is left untouched ("" escapes a quote)
    [InlineData("select \"weird:col\" from t where id = :id", "select \"weird:col\" from t where id = ?", "id")]
    // comments: line, block (no nesting; stray * and / must not confuse), curly
    [InlineData("select :a -- :b @c ?\n from t", "select ? -- :b @c ?\n from t", "a")]
    [InlineData("update t set c = :a /* skip :b @c ? and * / edge */ where 1=1",
                "update t set c = ? /* skip :b @c ? and * / edge */ where 1=1", "a")]
    [InlineData("select :a { :b @c ? } from t", "select ? { :b @c ? } from t", "a")]
    // Informix-specific literal cases that must NOT be parameters
    [InlineData("select '5'::INTEGER, :a", "select '5'::INTEGER, ?", "a")]              // ::type cast
    [InlineData("update t set n = n where x := :a", "update t set n = n where x := ?", "a")] // := is literal
    [InlineData("delete from t where a = :col,:c2", "delete from t where a = ?,?", "col,c2")] // name stops at non-word char
    [InlineData("select :a.b from t", "select ?.b from t", "a")]                        // '.' not part of a name
    public void Rewrites_named_placeholders(string input, string expectedText, string names)
    {
        var parsed = IfxSqlParser.Parse(input);

        Assert.Equal(IfxPlaceholderKind.Named, parsed.Kind);
        Assert.True(parsed.IsNamed);
        Assert.Equal(expectedText, parsed.Text);
        Assert.Equal(names.Split(','), parsed.ParameterNames);
    }

    [Theory]
    // Informix cross-database / cross-server qualified object names: dbname:object,
    // dbname@server:object, dbname:owner.object. The ':' / '@' is glued to the end of an
    // identifier, so it is part of the name, NOT a placeholder.
    [InlineData("select * from stores_demo:customer")]
    [InlineData("select * from stores_demo@ol_darkstar:customer")]
    [InlineData("select * from remotedb:informix.orders")]
    [InlineData("update accounts@prod:public.ledger set n = n")]
    public void Cross_database_qualifier_is_left_literal(string sql)
    {
        var parsed = IfxSqlParser.Parse(sql);

        Assert.Same(sql, parsed.Text);
        Assert.Equal(IfxPlaceholderKind.None, parsed.Kind);
        Assert.Empty(parsed.ParameterNames);
    }

    [Theory]
    [InlineData("select c from stores_demo:customer where id = :id",
                "select c from stores_demo:customer where id = ?", "id")]
    [InlineData("execute procedure otherdb:myproc(:x)",
                "execute procedure otherdb:myproc(?)", "x")]
    [InlineData("execute procedure sales@prod:daily_close(:d, :region)",
                "execute procedure sales@prod:daily_close(?, ?)", "d,region")]
    [InlineData("select * from db@srv:tab t where t.k = @k",
                "select * from db@srv:tab t where t.k = ?", "k")]
    public void Cross_database_qualifier_coexists_with_named_parameters(
        string input, string expectedText, string names)
    {
        var parsed = IfxSqlParser.Parse(input);

        Assert.Equal(IfxPlaceholderKind.Named, parsed.Kind);
        Assert.Equal(expectedText, parsed.Text);
        Assert.Equal(names.Split(','), parsed.ParameterNames);
    }

    [Fact]
    public void Positional_only_text_returns_the_same_string_instance()
    {
        string sql = "insert into t values (?, ?)";
        var parsed = IfxSqlParser.Parse(sql);

        Assert.Same(sql, parsed.Text); // no allocation, no rewrite
        Assert.Equal(IfxPlaceholderKind.Positional, parsed.Kind);
        Assert.False(parsed.IsNamed);
        Assert.Empty(parsed.ParameterNames);
    }

    [Fact]
    public void Text_without_placeholders_returns_the_same_string_instance()
    {
        string sql = "select count(*) from systables";
        var parsed = IfxSqlParser.Parse(sql);

        Assert.Same(sql, parsed.Text);
        Assert.Equal(IfxPlaceholderKind.None, parsed.Kind);
    }

    [Theory]
    [InlineData("select :5, :=, x from t")]          // :5 and := are literal
    [InlineData("select 'a:b @c' from t")]           // inside a string literal
    [InlineData("select a::integer from t")]         // bare :: cast with no leading string
    public void Colon_forms_that_are_not_parameters_leave_the_text_unchanged(string sql)
    {
        var parsed = IfxSqlParser.Parse(sql);

        Assert.Same(sql, parsed.Text);
        Assert.Equal(IfxPlaceholderKind.None, parsed.Kind);
        Assert.Empty(parsed.ParameterNames);
    }

    [Theory]
    [InlineData("insert into t values (?, :a)")]
    [InlineData("insert into t values (:a, ?)")]
    [InlineData("select ? from t where x = :a or y = :a")]
    public void Mixing_named_and_positional_placeholders_throws(string sql)
    {
        var ex = Assert.Throws<IfxException>(() => IfxSqlParser.Parse(sql));
        Assert.Equal(-79888, ex.IfxSqlCode);
    }
}

public class NamedParameterBindingTests
{
    // A prepare(+describe)/execute/release reply for a parameterized non-query. The
    // statement type only affects DescribesResultSet, which is false for INSERT/UPDATE.
    private static byte[] NonQueryScript(short stmtType, short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(stmtType, stmtId, tupleSize: 0),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(rows: 1),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    // One SQ_BIND value block for an INT: type INT(2), indicator 0, precision 4, 4 bytes.
    private static byte[] IntBind(int v) =>
        [0, 2, 0, 0, 0, 4, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] SqId(short id) => [0, 4, (byte)(id >> 8), (byte)id];

    private static byte[] Sent(DuplexStream duplex, long from) => duplex.Output.ToArray()[(int)from..];

    [Fact]
    public void Named_insert_rewrites_the_prepare_text_and_binds_in_placeholder_order()
    {
        using var cn = SqliStatementKit.OpenConnection(
            NonQueryScript((short)SqliStatementType.Insert, stmtId: 9), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t (a, b) values (:a, :b)";
        // Deliberately add the parameters in the OPPOSITE order to the placeholders:
        // if binding were by collection order, 99 would precede 42 on the wire.
        cmd.Parameters.Add("b", 99);
        cmd.Parameters.Add("a", 42);

        Assert.Equal(1, cmd.ExecuteNonQuery());

        byte[] sent = Sent(duplex, before);
        string text = Encoding.Latin1.GetString(sent);
        // The SQ_PREPARE carries the positional-rewritten text; the named form is gone.
        Assert.Contains("insert into t (a, b) values (?, ?)", text);
        Assert.DoesNotContain(":a", text);

        // The execute PDU binds :a (42) before :b (99) — placeholder order, not add order.
        byte[] expectedBinds = SqliStatementKit.Join(
            SqId(9), [0, 5, 0, 2], IntBind(42), IntBind(99), [0, 7]);
        Assert.Contains(BitConverter.ToString(expectedBinds), BitConverter.ToString(sent));
    }

    [Fact]
    public void Duplicate_named_placeholder_binds_the_same_value_once_per_occurrence()
    {
        using var cn = SqliStatementKit.OpenConnection(
            NonQueryScript((short)SqliStatementType.Update, stmtId: 5), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set n = 1 where a = :x or b = :x";
        cmd.Parameters.Add("x", 7); // one parameter, two placeholder occurrences

        cmd.ExecuteNonQuery();

        byte[] sent = Sent(duplex, before);
        Assert.Contains("update t set n = 1 where a = ? or b = ?", Encoding.Latin1.GetString(sent));
        // Two INT(7) blocks: SQ_BIND count is 2 and the value is bound twice.
        byte[] expectedBinds = SqliStatementKit.Join(
            SqId(5), [0, 5, 0, 2], IntBind(7), IntBind(7), [0, 7]);
        Assert.Contains(BitConverter.ToString(expectedBinds), BitConverter.ToString(sent));
    }

    [Theory]
    [InlineData("@id")]
    [InlineData(":id")]
    [InlineData("id")]
    [InlineData("ID")]
    [InlineData("@ID")]
    public void Parameter_name_matches_the_placeholder_regardless_of_sigil_or_case(string paramName)
    {
        using var cn = SqliStatementKit.OpenConnection(
            NonQueryScript((short)SqliStatementType.Update, stmtId: 3), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set n = 1 where k = :id";
        cmd.Parameters.Add(paramName, 55);

        cmd.ExecuteNonQuery();

        byte[] sent = Sent(duplex, before);
        byte[] expectedBinds = SqliStatementKit.Join(SqId(3), [0, 5, 0, 1], IntBind(55), [0, 7]);
        Assert.Contains(BitConverter.ToString(expectedBinds), BitConverter.ToString(sent));
    }

    [Fact]
    public void Named_placeholder_without_a_matching_parameter_throws_before_anything_is_sent()
    {
        // No statement replies are scripted: the throw must happen during bind building,
        // before any prepare/execute frame reaches the wire.
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (:missing)";
        cmd.Parameters.Add("other", 1);

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(-79887, ex.IfxSqlCode);
        Assert.Equal(before, duplex.Output.Length); // nothing was sent
    }

    [Fact]
    public void Unreferenced_parameters_are_simply_not_bound()
    {
        using var cn = SqliStatementKit.OpenConnection(
            NonQueryScript((short)SqliStatementType.Update, stmtId: 8), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set n = 1 where k = :id";
        cmd.Parameters.Add("id", 12);
        cmd.Parameters.Add("unused", 34); // referenced by no placeholder

        cmd.ExecuteNonQuery();

        byte[] sent = Sent(duplex, before);
        // Exactly one bound value (count 1); the unused parameter's 34 never appears.
        byte[] expectedBinds = SqliStatementKit.Join(SqId(8), [0, 5, 0, 1], IntBind(12), [0, 7]);
        Assert.Contains(BitConverter.ToString(expectedBinds), BitConverter.ToString(sent));
        Assert.DoesNotContain(BitConverter.ToString(IntBind(34)), BitConverter.ToString(sent));
    }

    [Fact]
    public void Positional_command_is_byte_for_byte_unchanged_and_uses_collection_order()
    {
        using var cn = SqliStatementKit.OpenConnection(
            NonQueryScript((short)SqliStatementType.Insert, stmtId: 9), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?, ?)";
        cmd.Parameters.Add("p1", 42);
        cmd.Parameters.Add("p2", 99);

        cmd.ExecuteNonQuery();

        byte[] sent = Sent(duplex, before);
        Assert.Contains("insert into t values (?, ?)", Encoding.Latin1.GetString(sent));
        byte[] expectedBinds = SqliStatementKit.Join(
            SqId(9), [0, 5, 0, 2], IntBind(42), IntBind(99), [0, 7]);
        Assert.Contains(BitConverter.ToString(expectedBinds), BitConverter.ToString(sent));
    }
}

public class NamedParameterCacheTests
{
    [Fact]
    public void The_parse_is_cached_and_re_runs_only_when_CommandText_changes()
    {
        var cmd = new IfxCommand();
        Assert.Equal(0, cmd.ParseInvocationCount);

        cmd.CommandText = "select :a from t";
        Assert.Equal(0, cmd.ParseInvocationCount); // the setter defers, it does not parse

        var first = cmd.ParseForTest();
        var second = cmd.ParseForTest();
        Assert.Same(first, second);                // same cached result
        Assert.Equal(1, cmd.ParseInvocationCount); // parsed exactly once

        cmd.CommandText = "select :b from t";      // invalidates the cache
        cmd.ParseForTest();
        Assert.Equal(2, cmd.ParseInvocationCount);
    }
}

public class NamedParameterBatchTests
{
    [Fact]
    public void Batch_rewrites_named_placeholders_once_for_all_rows()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // batch execute: one SQ_DONE per row then a single EOT
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        var batch = cn.CreateRowBatch("insert into t (a, b) values (:a, :b)");
        batch.AddRow(1, 2); // row values are positional, in placeholder-occurrence order
        batch.AddRow(3, 4);

        int[] counts = batch.Execute();

        Assert.Equal(new[] { 1, 1 }, counts);
        Assert.Contains("insert into t (a, b) values (?, ?)",
            Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]));
    }

    [Fact]
    public void Batch_with_mixed_placeholders_throws()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);
        var batch = cn.CreateRowBatch("insert into t values (?, :a)");
        batch.AddRow(1, 2);

        var ex = Assert.Throws<IfxException>(() => batch.Execute());
        Assert.Equal(-79888, ex.IfxSqlCode);
    }
}
