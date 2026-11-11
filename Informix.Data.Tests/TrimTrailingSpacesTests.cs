using System;
using System.Text;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The opt-in <see cref="IfxConnectionStringBuilder.TrimTrailingSpaces"/> property
/// (JDBC <c>IFX_TRIMTRAILINGSPACES</c> parity). 
/// Only <c>IfxChar.fromIfx</c> (CHAR/NCHAR) trims a decoded value's trailing
/// ASCII spaces; the VARCHAR equivalent (<c>IfxVarChar.trimIfxVarChar</c>) is only ever
/// consulted by internal numeric coercion (<c>getInt</c> et al.), never by
/// <c>getString()</c>/<c>fromIfx</c>, so VARCHAR/NVARCHAR/LVARCHAR text is never trimmed.
/// </summary>
public class TrimTrailingSpacesTests
{
    // A one-column SELECT script for a single fixed-width CHAR(len) row, mirroring
    // DateTimeIntervalMappingTests.OneColumnScript.
    private static byte[] CharColumnScript(SqliTypeCode type, int len, byte[] row) =>
        SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, tupleSize: (short)row.Length,
                new SqliStatementKit.Col("v", (short)type, len, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

    // Reads a single CHAR/NCHAR value with the option on or off.
    private static string ReadOneChar(SqliTypeCode type, int len, byte[] row, bool trim)
    {
        using var cn = SqliStatementKit.OpenConnection(CharColumnScript(type, len, row), out _,
            extraSettings: trim ? "TrimTrailingSpaces=true" : null);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return Assert.IsType<string>(reader.GetValue(0));
    }

    [Fact]
    public void Trim_off_by_default_keeps_the_space_padding_char()
    {
        byte[] row = Encoding.ASCII.GetBytes("hello     "); // "hello" + 5 trailing spaces
        Assert.Equal("hello     ", ReadOneChar(SqliTypeCode.Char, 10, row, trim: false));
    }

    [Fact]
    public void Trim_off_by_default_keeps_the_space_padding_nchar()
    {
        byte[] row = Encoding.ASCII.GetBytes("hello     ");
        Assert.Equal("hello     ", ReadOneChar(SqliTypeCode.NChar, 10, row, trim: false));
    }

    [Fact]
    public void Trim_on_strips_trailing_spaces_char()
    {
        byte[] row = Encoding.ASCII.GetBytes("hello     ");
        Assert.Equal("hello", ReadOneChar(SqliTypeCode.Char, 10, row, trim: true));
    }

    [Fact]
    public void Trim_on_strips_trailing_spaces_nchar()
    {
        byte[] row = Encoding.ASCII.GetBytes("hello     ");
        Assert.Equal("hello", ReadOneChar(SqliTypeCode.NChar, 10, row, trim: true));
    }

    // An all-blank CHAR becomes "" (not NULL).
    [Fact]
    public void Trim_on_reduces_an_all_space_value_to_empty_string()
    {
        byte[] row = Encoding.ASCII.GetBytes("          "); // 10 spaces
        Assert.Equal("", ReadOneChar(SqliTypeCode.Char, 10, row, trim: true));
    }

    [Fact]
    public void Trim_on_preserves_interior_spaces()
    {
        byte[] row = Encoding.ASCII.GetBytes("a b       "); // "a b" + 7 trailing spaces
        Assert.Equal("a b", ReadOneChar(SqliTypeCode.Char, 10, row, trim: true));
    }

    // A CHAR whose first byte is 0 is NULL; trimming must not change that.
    [Fact]
    public void Trim_on_does_not_touch_a_null_char_value()
    {
        byte[] row = new byte[10]; // all-zero => NULL sentinel (b[0]==0)
        using var cn = SqliStatementKit.OpenConnection(CharColumnScript(SqliTypeCode.Char, 10, row), out _,
            extraSettings: "TrimTrailingSpaces=true");
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Varchar_is_never_trimmed(bool trim)
    {
        // VARCHAR is a walked (variable-length) column: describe -> a separate open
        // reply -> a typed fetch reply carrying [lenByte][data] rows (SqliStatementTests
        // .Varchar_rows_are_walked_per_row's shape), unlike the fixed-layout CHAR script.
        byte[] describe = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 2, tupleSize: 21,
                new SqliStatementKit.Col("v", (short)SqliTypeCode.VarChar, 20, 0)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame());

        byte[] data = Encoding.ASCII.GetBytes("hi  "); // "hi" + 2 trailing spaces
        byte[] row = new byte[1 + data.Length];
        row[0] = (byte)data.Length;
        data.CopyTo(row, 1);

        byte[] script = SqliStatementKit.Join(
            describe,
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(row),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _,
            extraSettings: trim ? "TrimTrailingSpaces=true" : null);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select v from t";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("hi  ", reader.GetString(0));
    }

    [Fact]
    public void Connection_string_builder_defaults_to_off()
    {
        var b = new IfxConnectionStringBuilder();
        Assert.False(b.TrimTrailingSpaces);
        b.TrimTrailingSpaces = true;
        Assert.Contains("TrimTrailingSpaces", b.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }
}
