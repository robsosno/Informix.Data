using System.Data;
using System.Text;
using Informix.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Reply frames for the SPL OUT/INOUT parameter exchange: the server's -9752 rejection of
/// the prepare, the SQ_GETROUTINE + SQ_FPARAM routine description, and the SQ_FPROUTINE
/// value reply. Layouts are ports of <c>IfxSqli.receiveFParam</c>/<c>receiveFastPath</c>,
/// cross-checked against a live IDS 9.59.
/// </summary>
internal static class OutParamKit
{
    /// <summary>One argument of an SQ_FPARAM descriptor.</summary>
    /// <param name="Flags">The argument flag byte (bit 0 = declares a DEFAULT); only sent, like the
    /// name, when the frame carries parameter names, which a non-zero flag switches on.</param>
    public sealed record Arg(SqliTypeCode Type, short Mode, string Name = "", byte Flags = 0);

    /// <summary>
    /// An SQ_FPARAM reply (no EOT). Return-value entries come first and carry no mode word;
    /// argument entries follow and each end with theirs (1 == written back to the caller).
    /// </summary>
    public static byte[] FParamFrame(
        IReadOnlyList<SqliTypeCode> returns, IReadOnlyList<Arg> args,
        long funcState = 0x1234, int request = 7, bool hasParameterNames = false)
    {
        hasParameterNames |= args.Any(a => a.Flags != 0);
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.FParam);
        w.WriteInt8(funcState);
        w.WriteInt32(request);
        w.Write((byte)0);   // isDone
        w.WriteInt32(11);   // id
        w.WriteInt8(0);     // row
        w.WriteInt32(-1);   // colid

        w.WriteInt16((short)returns.Count);
        foreach (var t in returns)
            WriteTypeInfo(w, t, mode: null, name: null, flags: 0);

        w.WriteInt16((short)args.Count);
        w.WriteInt16(hasParameterNames ? (short)1 : (short)0); // named flags
        foreach (var a in args)
            WriteTypeInfo(w, a.Type, a.Mode, hasParameterNames ? a.Name : null, a.Flags);
        return ms.ToArray();
    }

    private static void WriteTypeInfo(IfxWireWriter w, SqliTypeCode type, short? mode, string? name, byte flags)
    {
        if (name is not null)
        {
            w.Write(flags);     // argument flags
            w.WriteChar(name);
        }
        w.Write((byte)0);        // argNull
        w.WriteInt16((short)type);
        w.WriteInt32(0);         // extended type id
        w.WriteInt32(4);         // length
        w.WriteInt16(0);         // precision
        w.WriteInt16(0);         // scale
        if (mode is { } m)
            w.WriteInt16(m);
    }

    /// <summary>An SQ_FPROUTINE reply (no EOT) carrying the routine's produced values.</summary>
    public static byte[] FpValuesFrame(params object?[] values)
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.FpRoutine);
        w.WriteInt16((short)values.Length);
        foreach (object? v in values)
        {
            switch (v)
            {
                case null:
                    w.WriteInt16((short)SqliTypeCode.Int);
                    w.WriteInt16(-1); // NULL indicator
                    w.WriteInt16(0);
                    break;
                case int i:
                    w.WriteInt16((short)SqliTypeCode.Int);
                    w.WriteInt16(0);
                    w.WriteInt16(0);
                    w.WriteInt32(i);
                    break;
                case short s:
                    w.WriteInt16((short)SqliTypeCode.SmallInt);
                    w.WriteInt16(0);
                    w.WriteInt16(0);
                    w.WriteInt16(s);
                    break;
                case long l:
                    w.WriteInt16((short)SqliTypeCode.Int8);
                    w.WriteInt16(0);
                    w.WriteInt16(0);
                    w.WriteInt8(l);
                    break;
                case double d:
                    w.WriteInt16((short)SqliTypeCode.Float);
                    w.WriteInt16(0);
                    w.WriteInt16(0);
                    w.WriteDouble(d);
                    break;
                case string str:
                    w.WriteInt16((short)SqliTypeCode.Char);
                    w.WriteInt16(0);
                    w.WriteInt16(0);
                    w.WriteChar(str);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported test value {v.GetType()}");
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// The whole server side of one OUT-parameter call: the prepare rejection, then the
    /// routine description, then the values.
    /// </summary>
    public static byte[] Script(
        string db, int handle, IReadOnlyList<SqliTypeCode> returns, IReadOnlyList<Arg> args,
        params object?[] values) =>
        SqliStatementKit.Join(
            SqliStatementKit.ErrorReply(
                SqliSession.RoutineHasOutParametersSqlCode, $"{db}:{handle}:1"),
            SqliStatementKit.RoutineHandleFrame(db, handle),
            FParamFrame(returns, args),
            SqliStatementKit.EotFrame(),
            FpValuesFrame(values),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());
}

public class OutParameterCallTests
{
    private static readonly OutParamKit.Arg In = new(SqliTypeCode.Int, 0);
    private static readonly OutParamKit.Arg Out = new(SqliTypeCode.Int, SqliFParam.ModeOut);

    // The IBM doc routine: test_proc(INOUT arg1 int, OUT arg2 int, INOUT arg3 int) returning int.
    private static byte[] TestProcScript() =>
        OutParamKit.Script("testlog", 571, [SqliTypeCode.Int], [Out, Out, Out], 1, 2, 3, 15);

    [Fact]
    public void Odbc_call_escape_with_a_return_value_runs_and_fills_every_parameter()
    {
        using var cn = SqliStatementKit.OpenConnection(TestProcScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "{? = call test_proc(?,?,?)};";
        var p1 = cmd.Parameters.Add("ID", IfxType.Integer);
        var p2 = cmd.Parameters.Add("ID1", IfxType.Integer);
        var p3 = cmd.Parameters.Add("ID2", IfxType.Integer);
        var p4 = cmd.Parameters.Add("ID3", IfxType.Integer);
        p1.Value = 0; p2.Value = 5; p3.Value = 4; p4.Value = 10;
        p1.Direction = ParameterDirection.Output;
        p2.Direction = ParameterDirection.InputOutput;
        p3.Direction = ParameterDirection.Output;
        p4.Direction = ParameterDirection.InputOutput;

        int rows = cmd.ExecuteNonQuery();

        Assert.Equal(1, rows);
        // The escape's leading '?' is the RETURN value, whatever direction it was given.
        Assert.Equal(15, p1.Value);
        Assert.Equal(1, p2.Value);
        Assert.Equal(2, p3.Value);
        Assert.Equal(3, p4.Value);
    }

    [Fact]
    public void Call_escape_is_sent_as_execute_procedure()
    {
        using var cn = SqliStatementKit.OpenConnection(TestProcScript(), out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "{? = call test_proc(?,?,?)};";
        for (int i = 0; i < 4; i++)
            cmd.Parameters.Add(new IfxParameter($"p{i}", IfxType.Integer) { Value = i });
        cmd.Parameters[0].Direction = ParameterDirection.Output;
        cmd.ExecuteNonQuery();

        string sent = Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("execute procedure test_proc(?,?,?)", sent);
        Assert.DoesNotContain("call", sent);
    }

    [Fact]
    public void Plain_execute_procedure_text_fills_out_and_return_parameters()
    {
        using var cn = SqliStatementKit.OpenConnection(TestProcScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure test_proc(?,?,?)";
        var a1 = new IfxParameter("arg1", IfxType.Integer) { Value = 5, Direction = ParameterDirection.InputOutput };
        var a2 = new IfxParameter("arg2", IfxType.Integer) { Direction = ParameterDirection.Output };
        var a3 = new IfxParameter("arg3", IfxType.Integer) { Value = 10, Direction = ParameterDirection.InputOutput };
        var ret = new IfxParameter("ret", IfxType.Integer) { Direction = ParameterDirection.ReturnValue };
        cmd.Parameters.AddRange(new[] { a1, a2, a3, ret });

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(1, a1.Value);
        Assert.Equal(2, a2.Value);
        Assert.Equal(3, a3.Value);
        Assert.Equal(15, ret.Value);
    }

    [Fact]
    public void Stored_procedure_command_reprobes_with_the_output_arguments_counted()
    {
        // First prepare (inputs only -> "out_only(?)") cannot resolve the routine; the
        // re-probe with every argument counted draws the -9752 that identifies it.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.ErrorReply(-674, "out_only"),
            OutParamKit.Script(
                "testlog", 42, [],
                [In, Out, new OutParamKit.Arg(SqliTypeCode.Char, SqliFParam.ModeOut)],
                42, "hello"));

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "out_only";
        var i = new IfxParameter("inparam", IfxType.Integer) { Value = 21 };
        var o1 = new IfxParameter("o1", IfxType.Integer) { Direction = ParameterDirection.Output };
        var o2 = new IfxParameter("o2", IfxType.Char) { Direction = ParameterDirection.Output };
        cmd.Parameters.AddRange(new[] { i, o1, o2 });

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(42, o1.Value);
        Assert.Equal("hello", o2.Value);

        string sent = Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("execute procedure out_only(?)", sent);       // the first attempt
        Assert.Contains("execute procedure out_only(?, ?, ?)", sent); // the re-probe
    }

    [Fact]
    public void A_returning_procedure_still_fills_output_parameters_from_the_returned_row()
    {
        // Unchanged behaviour: no -9752, so Output parameters keep meaning "a value the
        // routine RETURNs" and take no argument slot.
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), 19);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), 21);
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.ExecProc, stmtId: 4, tupleSize: 8,
                new SqliStatementKit.Col("lo", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("hi", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "minmax";
        cmd.Parameters.Add("x", 20);
        var lo = new IfxParameter("lo", 0) { Direction = ParameterDirection.Output };
        var hi = new IfxParameter("hi", 0) { Direction = ParameterDirection.Output };
        cmd.Parameters.AddRange(new[] { lo, hi });

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(19, lo.Value);
        Assert.Equal(21, hi.Value);
        string sent = Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains("execute procedure minmax(?)", sent);
    }

    private static readonly OutParamKit.Arg Defaulted =
        new(SqliTypeCode.Int, 0, Name: "c", Flags: SqliFParam.FlagHasDefault);

    [Fact]
    public void An_omitted_argument_with_a_DEFAULT_is_sent_as_a_default_marker()
    {
        // proc(a IN, OUT b, c DEFAULT 7) called with two arguments: IBM JDBC declares all three
        // to the server and binds the missing one as [type][-2][0] (live-verified, IDS 14.10).
        byte[] script = OutParamKit.Script("testlog", 9, [], [In, Out, Defaulted], 2);
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure p(?,?)";
        var a = new IfxParameter("a", IfxType.Integer) { Value = 1 };
        var b = new IfxParameter("b", IfxType.Integer) { Direction = ParameterDirection.Output };
        cmd.Parameters.AddRange(new[] { a, b });

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(2, b.Value);

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        byte[] marker = [0x00, (byte)SqliTypeCode.Int, 0xFF, 0xFE, 0x00, 0x00];   // type Int, indicator -2, 0
        Assert.True(sent.AsSpan().IndexOf(marker) >= 0, "the default marker for the omitted argument was not sent");
    }

    [Fact]
    public void An_omitted_argument_without_a_DEFAULT_is_reported_like_JDBC()
    {
        using var cn = SqliStatementKit.OpenConnection(TestProcScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure test_proc(?,?)";
        cmd.Parameters.Add(new IfxParameter("a", IfxType.Integer) { Value = 1, Direction = ParameterDirection.InputOutput });
        cmd.Parameters.Add(new IfxParameter("b", IfxType.Integer) { Direction = ParameterDirection.Output });

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(-79823, ex.IfxSqlCode);   // IBM JDBC: "IN parameter has not been set"
        Assert.Contains("Argument 3", ex.Message);
    }

    [Fact]
    public void An_omitted_OUT_argument_with_a_DEFAULT_has_nowhere_to_land_and_is_dropped()
    {
        var outDefaulted = new OutParamKit.Arg(SqliTypeCode.Int, SqliFParam.ModeOut, Name: "c", Flags: SqliFParam.FlagHasDefault);
        byte[] script = OutParamKit.Script("testlog", 9, [], [Out, outDefaulted], 11, 22);
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure p(?)";
        var a = new IfxParameter("a", IfxType.Integer) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(a);

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(11, a.Value);
    }

    [Fact]
    public void More_arguments_than_the_routine_declares_are_rejected_as_an_unresolvable_routine()
    {
        byte[] script = OutParamKit.Script("testlog", 42, [], [In, Out], 84);
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure f(?,?,?)";
        cmd.Parameters.Add(new IfxParameter("a", IfxType.Integer) { Value = 1 });
        cmd.Parameters.Add(new IfxParameter("b", IfxType.Integer) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new IfxParameter("c", IfxType.Integer) { Value = 3 });

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(-674, ex.IfxSqlCode);   // the code the server itself gives for a call it cannot resolve
        Assert.Contains("declares 2 argument(s)", ex.Message);
        Assert.Contains("supplies 3", ex.Message);
    }

    [Fact]
    public void A_returned_value_needs_no_ReturnValue_parameter()
    {
        // The routine RETURNs an int but the command declares no ReturnValue parameter; IBM
        // JDBC ignores the return value in that case (live-verified), so must we.
        using var cn = SqliStatementKit.OpenConnection(TestProcScript(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure test_proc(?,?,?)";
        var a1 = new IfxParameter("arg1", IfxType.Integer) { Value = 5, Direction = ParameterDirection.InputOutput };
        var a2 = new IfxParameter("arg2", IfxType.Integer) { Direction = ParameterDirection.Output };
        var a3 = new IfxParameter("arg3", IfxType.Integer) { Value = 10, Direction = ParameterDirection.InputOutput };
        cmd.Parameters.AddRange(new[] { a1, a2, a3 });

        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(1, a1.Value);
        Assert.Equal(2, a2.Value);
        Assert.Equal(3, a3.Value);
    }

    [Fact]
    public void An_output_parameter_with_no_type_and_no_value_is_rejected_with_advice()
    {
        byte[] script = OutParamKit.Script("testlog", 42, [], [In, Out], 84);
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure f(?,?)";
        cmd.Parameters.Add(new IfxParameter("a", IfxType.Integer) { Value = 42 });
        cmd.Parameters.Add(new IfxParameter("o", 0) { Direction = ParameterDirection.Output });

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(-79826, ex.IfxSqlCode);   // IBM JDBC: "Ambiguous java.sql.Type" for an untyped OUT parameter
        Assert.Contains("Set IfxType", ex.Message);
    }

    [Fact]
    public void A_reader_over_an_out_parameter_routine_explains_what_to_do_instead()
    {
        byte[] script = SqliStatementKit.ErrorReply(
            SqliSession.RoutineHasOutParametersSqlCode, "testlog:571:1");
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure test_proc(?,?,?)";
        for (int i = 0; i < 3; i++)
            cmd.Parameters.Add(new IfxParameter($"p{i}", IfxType.Integer) { Value = i });

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteReader());
        Assert.Equal(SqliSession.RoutineHasOutParametersSqlCode, ex.IfxSqlCode);
        Assert.Contains("ExecuteNonQuery", ex.Message);
    }

    [Fact]
    public void A_second_execution_reuses_the_resolved_routine()
    {
        // Only ONE -9752/SQ_GETROUTINE/SQ_FPARAM exchange is scripted; the second execution
        // must go straight to SQ_EXFPROUTINE or it will run off the end of the script.
        byte[] script = SqliStatementKit.Join(
            OutParamKit.Script("testlog", 42, [], [In, Out], 84),
            OutParamKit.FpValuesFrame(200),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure f(?,?)";
        var a = new IfxParameter("a", IfxType.Integer) { Value = 42 };
        var o = new IfxParameter("o", IfxType.Integer) { Direction = ParameterDirection.Output };
        cmd.Parameters.AddRange(new[] { a, o });

        cmd.ExecuteNonQuery();
        Assert.Equal(84, o.Value);

        a.Value = 100;
        cmd.ExecuteNonQuery();
        Assert.Equal(200, o.Value);
    }

    [Fact]
    public void Changing_the_command_text_drops_the_resolved_routine()
    {
        // Two full exchanges scripted: reusing the first routine for the second text
        // would consume the wrong frames and fail.
        byte[] script = SqliStatementKit.Join(
            OutParamKit.Script("testlog", 42, [], [In, Out], 84),
            OutParamKit.Script("testlog", 77, [], [In, Out], 9));

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure f(?,?)";
        var a = new IfxParameter("a", IfxType.Integer) { Value = 42 };
        var o = new IfxParameter("o", IfxType.Integer) { Direction = ParameterDirection.Output };
        cmd.Parameters.AddRange(new[] { a, o });
        cmd.ExecuteNonQuery();
        Assert.Equal(84, o.Value);

        cmd.CommandText = "execute procedure g(?,?)";
        cmd.ExecuteNonQuery();
        Assert.Equal(9, o.Value);
    }

    [Fact]
    public void An_unassigned_out_argument_comes_back_null()
    {
        byte[] script = OutParamKit.Script("testlog", 42, [], [In, Out], [null]);
        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "execute procedure f(?,?)";
        cmd.Parameters.Add(new IfxParameter("a", IfxType.Integer) { Value = 42 });
        var o = new IfxParameter("o", IfxType.Integer) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(o);

        cmd.ExecuteNonQuery();
        Assert.Null(o.Value);
    }
}

public class ParameterCollectionAddOverloadTests
{
    [Fact]
    public void Add_with_an_IfxType_declares_the_type_rather_than_storing_it_as_the_value()
    {
        // Without the IfxType overload this call binds to Add(string, object?) and the enum
        // silently becomes the parameter's *value* -- which is how the IBM documentation's
        // own example is written.
        var c = new IfxParameterCollection();
        IfxParameter p = c.Add("ID", IfxType.Integer);

        Assert.Equal(IfxType.Integer, p.IfxType);
        Assert.Null(p.Value);
        Assert.Same(p, c[0]);
    }

    [Fact]
    public void Add_carries_size_and_source_column()
    {
        var c = new IfxParameterCollection();
        IfxParameter sized = c.Add("name", IfxType.VarChar, 40);
        IfxParameter mapped = c.Add("city", IfxType.Char, 20, "CITY");

        Assert.Equal(40, sized.Size);
        Assert.Equal(20, mapped.Size);
        Assert.Equal("CITY", mapped.SourceColumn);
    }

    [Fact]
    public void AddWithValue_stores_an_IfxType_as_a_value()
    {
        var c = new IfxParameterCollection();
        IfxParameter p = c.AddWithValue("x", IfxType.Integer);

        Assert.Equal(IfxType.Integer, p.Value);
        Assert.Equal(default, p.IfxType);
    }

    [Fact]
    public void Add_and_AddRange_of_parameters_return_and_append_in_order()
    {
        var c = new IfxParameterCollection();
        var first = new IfxParameter("a", IfxType.Integer);
        Assert.Same(first, c.Add(first));

        c.AddRange(new[] { new IfxParameter("b", IfxType.Char), new IfxParameter("c", IfxType.Float) });

        Assert.Equal(3, c.Count);
        Assert.Equal("a", c[0].ParameterName);
        Assert.Equal("b", c[1].ParameterName);
        Assert.Equal("c", c[2].ParameterName);
    }
}

public class CallEscapeParsingTests
{
    private static IfxParsedCommand Parse(string sql) => IfxSqlParser.Parse(sql);

    [Theory]
    [InlineData("{call proc()}", "execute procedure proc()")]
    [InlineData("{ call proc(?, ?) }", "execute procedure proc(?, ?)")]
    [InlineData("{CALL proc(?)}", "execute procedure proc(?)")]
    [InlineData("{call proc}", "execute procedure proc")]
    [InlineData("{call proc(?)};", "execute procedure proc(?)")]
    public void Call_escape_becomes_execute_procedure(string sql, string expected)
    {
        var parsed = Parse(sql);
        Assert.Equal(expected, parsed.Text);
        Assert.True(parsed.IsCallEscape);
        Assert.False(parsed.HasReturnValuePlaceholder);
    }

    [Theory]
    [InlineData("{? = call f(?,?)}", "execute procedure f(?,?)")]
    [InlineData("{?=call f(?)}", "execute procedure f(?)")]
    [InlineData("  { ? = CALL f() } ; ", "execute procedure f()")]
    public void Return_value_escape_drops_its_leading_placeholder(string sql, string expected)
    {
        var parsed = Parse(sql);
        Assert.Equal(expected, parsed.Text);
        Assert.True(parsed.HasReturnValuePlaceholder);
    }

    [Theory]
    [InlineData("select 1 from t")]
    [InlineData("{ this is an informix brace comment } select 1 from t")]
    [InlineData("select ?, { comment } 2 from t")]
    [InlineData("{ callsign }")]        // "call" must be a whole token
    [InlineData("{ x = call f() }")]    // only '?' may precede the '='
    public void Everything_else_is_left_alone(string sql)
    {
        var parsed = Parse(sql);
        Assert.Same(sql, parsed.Text);
        Assert.False(parsed.IsCallEscape);
    }

    [Fact]
    public void A_brace_comment_still_hides_its_placeholders()
    {
        var parsed = Parse("select 1 from t where a = ? { and b = ? }");
        Assert.Equal(IfxPlaceholderKind.Positional, parsed.Kind);
    }
}
