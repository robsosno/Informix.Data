using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <c>GetSchema("PrimaryKeys"/"ForeignKeys")</c> (port of JDBC's
/// <c>getPrimaryKeys</c>/<c>getImportedKeys</c>): catalog-backed collections, so the
/// underlying <c>systables</c>/<c>syscolumns</c>/<c>sysindexes</c>/<c>sysconstraints</c>/
/// <c>sysreferences</c> query is exercised the same way <see cref="GetSchemaTests"/>
/// exercises a plain <c>SELECT</c> — full describe + fixed-layout tuple frames through
/// <see cref="SqliStatementKit"/>. Text columns are scripted CHAR(12): wide enough for
/// every identifier used below, matching the fixed-length CHAR the real catalog columns
/// (<c>tabname</c>/<c>colname</c>/<c>constrname</c>/<c>owner</c>) actually are.
/// </summary>
public class PrimaryForeignKeySchemaTests
{
    private const int TextWidth = 12;

    private static void PutChar(Span<byte> row, int offset, string value) =>
        IfxWireConverter.EncodeChar(value.PadRight(TextWidth)).CopyTo(row[offset..]);

    private static byte[] PkRow(string owner, string tabname, string colname, string constrname, int keySeq)
    {
        var row = new byte[4 * TextWidth + 4];
        PutChar(row, 0 * TextWidth, owner);
        PutChar(row, 1 * TextWidth, tabname);
        PutChar(row, 2 * TextWidth, colname);
        PutChar(row, 3 * TextWidth, constrname);
        IfxWireConverter.WriteInt32(row.AsSpan(4 * TextWidth, 4), keySeq);
        return row;
    }

    private static readonly SqliStatementKit.Col[] PkDescribeColumns =
    [
        new("owner", (short)SqliTypeCode.Char, TextWidth, 0 * TextWidth),
        new("tabname", (short)SqliTypeCode.Char, TextWidth, 1 * TextWidth),
        new("colname", (short)SqliTypeCode.Char, TextWidth, 2 * TextWidth),
        new("constrname", (short)SqliTypeCode.Char, TextWidth, 3 * TextWidth),
        new("key_seq", (short)SqliTypeCode.Int, 4, 4 * TextWidth),
    ];

    private const int PkTupleSize = 4 * TextWidth + 4;

    [Fact]
    public void GetSchema_PrimaryKeys_reports_composite_key_columns_in_key_seq_order()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, PkTupleSize, PkDescribeColumns),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            // open + fetch reply (fixed layout -> single round trip)
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(PkRow("informix", "child", "col_b", "pk_child", 2)),
            SqliStatementKit.TupleFrame(PkRow("informix", "child", "col_a", "pk_child", 1)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            // release reply
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var t = cn.GetSchema(IfxSchema.PrimaryKeys, ["mydb", "informix", "child"]);

        Assert.Equal(
            new[] { "TABLE_CATALOG", "TABLE_OWNER", "TABLE_NAME", "COLUMN_NAME", "KEY_SEQ", "PK_NAME" },
            t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());

        // The server can return composite-key rows in any order; ORDER BY key_seq in the
        // query is what the driver relies on -- proven here by the two rows arriving
        // reversed on the wire but the client not re-sorting them (the SQL already did).
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("col_b", t.Rows[0]["COLUMN_NAME"]);
        Assert.Equal(2, t.Rows[0]["KEY_SEQ"]);
        Assert.Equal("col_a", t.Rows[1]["COLUMN_NAME"]);
        Assert.Equal(1, t.Rows[1]["KEY_SEQ"]);
        Assert.Equal("pk_child", t.Rows[0]["PK_NAME"]);
        Assert.Equal("informix", t.Rows[0]["TABLE_OWNER"]);
        Assert.Equal("child", t.Rows[0]["TABLE_NAME"]);

        // Restriction values (owner, table) reached the wire as bound parameters.
        string hex = System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray());
        Assert.Contains("informix", hex);
        Assert.Contains("child", hex);
    }

    [Fact]
    public void GetSchema_PrimaryKeys_registers_in_MetaDataCollections_and_Restrictions()
    {
        using var cn = new IfxConnection();
        var collections = cn.GetSchema().Rows.Cast<DataRow>().Select(r => (string)r["CollectionName"]).ToArray();
        Assert.Contains(IfxSchema.PrimaryKeys, collections);
        Assert.Contains(IfxSchema.ForeignKeys, collections);

        var restrictions = cn.GetSchema("Restrictions").Rows.Cast<DataRow>()
            .Where(r => (string)r["CollectionName"] == IfxSchema.PrimaryKeys)
            .Select(r => (string)r["RestrictionName"])
            .ToArray();
        Assert.Equal(["Catalog", "Owner", "Table"], restrictions);
    }

    private static byte[] FkRow(
        string pOwner, string pTab, string pCol, string fOwner, string fTab, string fCol,
        string fkName, string pkName, string delRule, int keySeq)
    {
        var row = new byte[8 * TextWidth + 1 + 4];
        PutChar(row, 0 * TextWidth, pOwner);
        PutChar(row, 1 * TextWidth, pTab);
        PutChar(row, 2 * TextWidth, pCol);
        PutChar(row, 3 * TextWidth, fOwner);
        PutChar(row, 4 * TextWidth, fTab);
        PutChar(row, 5 * TextWidth, fCol);
        PutChar(row, 6 * TextWidth, fkName);
        PutChar(row, 7 * TextWidth, pkName);
        IfxWireConverter.EncodeChar(delRule).CopyTo(row.AsSpan(8 * TextWidth, 1));
        IfxWireConverter.WriteInt32(row.AsSpan(8 * TextWidth + 1, 4), keySeq);
        return row;
    }

    private static readonly SqliStatementKit.Col[] FkDescribeColumns =
    [
        new("pt_owner", (short)SqliTypeCode.Char, TextWidth, 0 * TextWidth),
        new("pt_tabname", (short)SqliTypeCode.Char, TextWidth, 1 * TextWidth),
        new("pc_colname", (short)SqliTypeCode.Char, TextWidth, 2 * TextWidth),
        new("ft_owner", (short)SqliTypeCode.Char, TextWidth, 3 * TextWidth),
        new("ft_tabname", (short)SqliTypeCode.Char, TextWidth, 4 * TextWidth),
        new("fc_colname", (short)SqliTypeCode.Char, TextWidth, 5 * TextWidth),
        new("fk_constrname", (short)SqliTypeCode.Char, TextWidth, 6 * TextWidth),
        new("pk_constrname", (short)SqliTypeCode.Char, TextWidth, 7 * TextWidth),
        new("delrule", (short)SqliTypeCode.Char, 1, 8 * TextWidth),
        new("key_seq", (short)SqliTypeCode.Int, 4, 8 * TextWidth + 1),
    ];

    private const int FkTupleSize = 8 * TextWidth + 1 + 4;

    [Fact]
    public void GetSchema_ForeignKeys_reports_composite_key_columns_with_cascade_delete_rule()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, FkTupleSize, FkDescribeColumns),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(FkRow(
                "informix", "parent", "col_a", "informix", "child", "fk_a",
                "fk_child", "pk_parent", "C", 1)),
            SqliStatementKit.TupleFrame(FkRow(
                "informix", "parent", "col_b", "informix", "child", "fk_b",
                "fk_child", "pk_parent", "C", 2)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame()); // release reply

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var t = cn.GetSchema(IfxSchema.ForeignKeys, ["mydb", "informix", "child"]);

        Assert.Equal(
            new[]
            {
                "PKTABLE_CATALOG", "PKTABLE_OWNER", "PKTABLE_NAME", "PKCOLUMN_NAME",
                "FKTABLE_CATALOG", "FKTABLE_OWNER", "FKTABLE_NAME", "FKCOLUMN_NAME",
                "KEY_SEQ", "UPDATE_RULE", "DELETE_RULE", "FK_NAME", "PK_NAME",
            },
            t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("col_a", t.Rows[0]["PKCOLUMN_NAME"]);
        Assert.Equal("fk_a", t.Rows[0]["FKCOLUMN_NAME"]);
        Assert.Equal(1, t.Rows[0]["KEY_SEQ"]);
        Assert.Equal("col_b", t.Rows[1]["PKCOLUMN_NAME"]);
        Assert.Equal(2, t.Rows[1]["KEY_SEQ"]);
        Assert.Equal("RESTRICT", t.Rows[0]["UPDATE_RULE"]); // Informix has no update rule
        Assert.Equal("CASCADE", t.Rows[0]["DELETE_RULE"]);
        Assert.Equal("fk_child", t.Rows[0]["FK_NAME"]);
        Assert.Equal("pk_parent", t.Rows[0]["PK_NAME"]);
        Assert.Equal("parent", t.Rows[0]["PKTABLE_NAME"]);
        Assert.Equal("child", t.Rows[0]["FKTABLE_NAME"]);
    }

    [Fact]
    public void GetSchema_ForeignKeys_maps_a_non_cascade_delete_rule_to_restrict()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 1, FkTupleSize, FkDescribeColumns),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(FkRow(
                "informix", "parent", "id", "informix", "child", "parent_id",
                "fk_child", "pk_parent", "R", 1)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var t = cn.GetSchema(IfxSchema.ForeignKeys, ["mydb", "informix", "child"]);

        Assert.Equal("RESTRICT", t.Rows[0]["DELETE_RULE"]);
    }
}
