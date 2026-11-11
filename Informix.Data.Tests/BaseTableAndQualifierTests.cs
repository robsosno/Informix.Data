using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The pieces that let <see cref="IfxCommandBuilder"/> work and that expose a DATETIME / INTERVAL
/// column's qualifier: reading the base table out of a one-table SELECT, and
/// <see cref="IfxDbColumn.StartTimeUnit"/> / <see cref="IfxDbColumn.EndTimeUnit"/>.
/// </summary>
public class BaseTableAndQualifierTests
{
    // ---- the base table of a SELECT ----

    [Theory]
    [InlineData("SELECT * FROM manufact", null, "manufact")]
    [InlineData("select manu_code, manu_name from MANUFACT where manu_code = 'SMT'", null, "manufact")]
    [InlineData("SELECT a.x FROM stock a WHERE a.y = 1", null, "stock")]
    [InlineData("SELECT x FROM stock AS s ORDER BY x", null, "stock")]
    [InlineData("SELECT x FROM root.items", "root", "items")]
    [InlineData("SELECT x FROM superstore_demo:root.items", "root", "items")]
    [InlineData("SELECT x FROM superstore_demo:items", null, "items")]
    [InlineData("SELECT x FROM \"MixedCase\"", null, "MixedCase")]
    [InlineData("SELECT FIRST 10 x FROM items", null, "items")]
    [InlineData("SELECT x FROM ONLY(customer)", null, "customer")]
    [InlineData("SELECT x FROM items -- a comment\n WHERE 1 = 1", null, "items")]
    [InlineData("SELECT COUNT(*), (SELECT MAX(q) FROM other) FROM items", null, "items")]
    public void A_one_table_select_has_a_base_table(string sql, string? owner, string table)
    {
        var found = IfxBaseTable.Find(sql);
        Assert.NotNull(found);
        Assert.Equal(table, found!.Name);
        Assert.Equal(owner, found.Owner);
    }

    [Theory]
    [InlineData("SELECT * FROM a, b")]
    [InlineData("SELECT * FROM a JOIN b ON a.x = b.x")]
    [InlineData("SELECT * FROM a INNER JOIN b ON a.x = b.x")]
    [InlineData("SELECT * FROM a LEFT OUTER JOIN b ON a.x = b.x")]
    [InlineData("SELECT * FROM a UNION SELECT * FROM b")]
    [InlineData("SELECT * FROM TABLE(x)")]
    [InlineData("SELECT * FROM (SELECT * FROM a) t")]
    [InlineData("SELECT 1 FROM systables WHERE 1 = 1 UNION SELECT 2 FROM systables")]
    [InlineData("SELECT 1")]
    [InlineData("UPDATE a SET x = 1")]
    [InlineData("EXECUTE FUNCTION f()")]
    [InlineData("")]
    public void Anything_but_a_plain_one_table_select_has_none(string sql)
    {
        Assert.Null(IfxBaseTable.Find(sql));
    }

    [Fact]
    public void A_string_that_looks_like_FROM_does_not_start_the_table_name()
    {
        var found = IfxBaseTable.Find("SELECT 'from nowhere', x FROM items");
        Assert.Equal("items", found!.Name);
    }

    // ---- the qualifier of a DATETIME / INTERVAL column ----

    private static IfxDbColumn Column(SqliTypeCode type, short qualifier) =>
        new(new IfxColumnMetadata { Name = "c", SqliTypeCode = type, PrecisionScale = qualifier }, 0);

    [Theory]
    [InlineData(IfxTimeUnit.Year, IfxTimeUnit.Minute)]
    [InlineData(IfxTimeUnit.Year, IfxTimeUnit.Fraction5)]
    [InlineData(IfxTimeUnit.Month, IfxTimeUnit.Day)]
    [InlineData(IfxTimeUnit.Hour, IfxTimeUnit.Fraction3)]
    public void A_DATETIME_column_reports_its_qualifier(IfxTimeUnit start, IfxTimeUnit end)
    {
        var column = Column(SqliTypeCode.DateTime, IfxQualifier.ForDateTime(start, end));
        Assert.Equal(start, column.StartTimeUnit);
        Assert.Equal(end, column.EndTimeUnit);
        Assert.Equal(start, column["StartTimeUnit"]);
        Assert.Equal(end, column["EndTimeUnit"]);
    }

    [Fact]
    public void An_INTERVAL_column_reports_its_qualifier()
    {
        var column = Column(SqliTypeCode.Interval, IfxQualifier.ForInterval(3, IfxTimeUnit.Day, IfxTimeUnit.Day));
        Assert.Equal(IfxTimeUnit.Day, column.StartTimeUnit);
        Assert.Equal(IfxTimeUnit.Day, column.EndTimeUnit);
    }

    [Fact]
    public void Other_columns_have_no_qualifier()
    {
        var column = Column(SqliTypeCode.Int, 0);
        Assert.Null(column.StartTimeUnit);
        Assert.Null(column.EndTimeUnit);
    }
}
