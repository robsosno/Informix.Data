using System.Data;
using System.Linq;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

public class AdoComponentsTests
{
    private static byte[] IntRow(int a, int b)
    {
        var row = new byte[8];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), a);
        IfxWireConverter.WriteInt32(row.AsSpan(4, 4), b);
        return row;
    }

    private static byte[] TwoIntSelectScript(params byte[][] rows)
    {
        byte[] tuples = SqliStatementKit.Join(rows.Select(SqliStatementKit.TupleFrame).ToArray());
        return SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),          // open reply
            tuples,
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());           // release reply
    }

    [Fact]
    public void DataAdapter_fills_a_datatable_from_a_scripted_select()
    {
        using var cn = SqliStatementKit.OpenConnection(TwoIntSelectScript(IntRow(1, 2), IntRow(3, 4)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";

        var adapter = new IfxDataAdapter(cmd);
        var table = new DataTable();
        int n = adapter.Fill(table);

        Assert.Equal(2, n);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(1, table.Rows[0][0]);
        Assert.Equal(4, table.Rows[1][1]);
    }

    [Fact]
    public void CommandBuilder_quotes_and_unquotes_delimited_identifiers()
    {
        var b = new IfxCommandBuilder();
        Assert.Equal("\"order\"", b.QuoteIdentifier("order"));
        Assert.Equal("\"a\"\"b\"", b.QuoteIdentifier("a\"b"));      // embedded quote doubled
        Assert.Equal("order", b.UnquoteIdentifier("\"order\""));
        Assert.Equal("a\"b", b.UnquoteIdentifier("\"a\"\"b\""));
    }

    [Fact]
    public void DataSourceEnumerator_returns_the_shaped_table()
    {
        var table = IfxDataSourceEnumerator.Instance.GetDataSources();
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        Assert.Contains("ServerName", columns);
        Assert.Contains("Version", columns);
    }
}
