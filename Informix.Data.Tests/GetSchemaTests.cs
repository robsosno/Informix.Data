using System.Data;
using System.Linq;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;
using static Informix.Data.Tests.SqliStatementKit;

namespace Informix.Data.Tests;

public class GetSchemaTests
{
    [Fact]
    public void GetSchema_lists_the_metadata_collections()
    {
        using var cn = new IfxConnection();
        var t = cn.GetSchema();
        Assert.Equal("MetaDataCollections", t.TableName);
        var names = t.Rows.Cast<DataRow>().Select(r => (string)r["CollectionName"]).ToArray();
        Assert.Contains("Tables", names);
        Assert.Contains("Columns", names);
        Assert.Contains("DataTypes", names);
        Assert.Contains("Procedures", names);
    }

    [Fact]
    public void GetSchema_DataTypes_reports_the_public_IfxType_values()
    {
        using var cn = new IfxConnection();
        var byName = cn.GetSchema("DataTypes").Rows.Cast<DataRow>()
            .ToDictionary(r => (string)r["TypeName"], r => (int)r["ProviderDbType"]);
        Assert.Equal((int)IfxType.Integer, byName["INTEGER"]);
        Assert.Equal((int)IfxType.Clob, byName["CLOB"]);
        Assert.Equal((int)IfxType.Boolean, byName["BOOLEAN"]);
        Assert.Equal((int)IfxType.LVarChar, byName["LVARCHAR"]);
    }

    [Fact]
    public void GetSchema_DataSourceInformation_uses_the_positional_parameter_marker()
    {
        using var cn = new IfxConnection();
        var row = cn.GetSchema("DataSourceInformation").Rows[0];
        Assert.Equal("?", (string)row["ParameterMarkerFormat"]);
        Assert.Equal("Informix", (string)row["DataSourceProductName"]);
    }

    [Fact]
    public void GetSchema_ReservedWords_and_Restrictions_are_populated()
    {
        using var cn = new IfxConnection();
        Assert.Contains("SELECT", cn.GetSchema("ReservedWords").Rows.Cast<DataRow>().Select(r => (string)r["ReservedWord"]));
        Assert.NotEmpty(cn.GetSchema("Restrictions").Rows);
    }

    [Fact]
    public void GetSchema_rejects_an_unknown_collection()
    {
        using var cn = new IfxConnection();
        Assert.Throws<ArgumentException>(() => cn.GetSchema("Nonexistent"));
    }

    [Fact]
    public void GetSchema_lists_ProcedureColumns_with_its_restrictions_registered()
    {
        using var cn = new IfxConnection();
        var names = cn.GetSchema().Rows.Cast<DataRow>().Select(r => (string)r["CollectionName"]).ToArray();
        Assert.Contains("ProcedureColumns", names);

        var restrictions = cn.GetSchema("Restrictions").Rows.Cast<DataRow>()
            .Where(r => (string)r["CollectionName"] == "ProcedureColumns")
            .Select(r => (string)r["RestrictionName"])
            .ToArray();
        Assert.Equal(["Catalog", "Owner", "Procedure", "Column"], restrictions);
    }

    // ---- ProcedureColumns (sysprocedures/sysproccolumns) ----

    private static byte[] ProcColumnRow(string owner, string procname, string paramname,
        int paramid, int paramattr, int paramtype, int paramxid, int paramlen)
    {
        var row = new byte[44];
        IfxWireConverter.EncodeChar(owner.PadRight(8)).CopyTo(row.AsSpan(0));
        IfxWireConverter.EncodeChar(procname.PadRight(8)).CopyTo(row.AsSpan(8));
        IfxWireConverter.EncodeChar(paramname.PadRight(8)).CopyTo(row.AsSpan(16));
        IfxWireConverter.WriteInt32(row.AsSpan(24, 4), paramid);
        IfxWireConverter.WriteInt32(row.AsSpan(28, 4), paramattr);
        IfxWireConverter.WriteInt32(row.AsSpan(32, 4), paramtype);
        IfxWireConverter.WriteInt32(row.AsSpan(36, 4), paramxid);
        IfxWireConverter.WriteInt32(row.AsSpan(40, 4), paramlen);
        return row;
    }

    private static byte[] ProcColumnsSelect(params byte[][] rows) => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 44,
            new Col("owner", (short)SqliTypeCode.Char, 8, 0),
            new Col("procname", (short)SqliTypeCode.Char, 8, 8),
            new Col("paramname", (short)SqliTypeCode.Char, 8, 16),
            new Col("paramid", (short)SqliTypeCode.Int, 4, 24),
            new Col("paramattr", (short)SqliTypeCode.Int, 4, 28),
            new Col("paramtype", (short)SqliTypeCode.Int, 4, 32),
            new Col("paramxid", (short)SqliTypeCode.Int, 4, 36),
            new Col("paramlen", (short)SqliTypeCode.Int, 4, 40)),
        DoneFrame(), EotFrame(),
        DoneFrame(), Join(rows.Select(TupleFrame).ToArray()), CloseFrame(), EotFrame(),
        EotFrame());

    [Fact]
    public void GetSchema_ProcedureColumns_reports_paramattr_as_COLUMN_TYPE_with_type_size_and_nullability()
    {
        const int notNull = (int)IfxTypeFlags.NotNullable;
        // Shapes and paramattr codes mirror a live capture against IDS (14.10 engine) of
        //   create procedure ptest(a int, out b int, inout c varchar(10))
        //   create function ftest2(a decimal(10,2)) returning datetime year to second
        // — RETURNING rows carry paramattr 3 with a NULL paramname and precede the parameters.
        byte[] script = ProcColumnsSelect(
            // the RETURNING value: datetime year to second, qualifier word 3594 (0x0E0A)
            ProcColumnRow("informix", "sp", "", 0, 3, (int)SqliTypeCode.DateTime, 0, 3594),
            // IN parameter, NOT NULL INTEGER
            ProcColumnRow("informix", "sp", "id", 1, 1, (int)SqliTypeCode.Int | notNull, 0, 4),
            // OUT parameter, DECIMAL(10,2)
            ProcColumnRow("informix", "sp", "amt", 2, 4, (int)SqliTypeCode.Decimal, 0, (10 << 8) | 2),
            // INOUT parameter, LVARCHAR (opaque UdtVar carrying extended id LVarChar)
            ProcColumnRow("informix", "sp", "note", 3, 2, (int)SqliTypeCode.UdtVar, (int)IfxExtendedType.LVarChar, 200));

        using var cn = OpenConnection(script, out _);
        var t = cn.GetSchema("ProcedureColumns", [null, null, "sp", null]);

        string[] expectedColumns =
        [
            "PROCEDURE_CATALOG", "PROCEDURE_SCHEMA", "PROCEDURE_NAME", "PARAMETER_NAME", "ORDINAL_POSITION",
            "COLUMN_TYPE", "DATA_TYPE", "TYPE_NAME", "COLUMN_SIZE", "DECIMAL_DIGITS", "NULLABLE",
        ];
        Assert.Equal(expectedColumns, t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());

        Assert.Equal(4, t.Rows.Count);
        var rows = t.Rows.Cast<DataRow>().ToArray();

        var ret = rows.Single(r => (int)r["ORDINAL_POSITION"] == 0);
        Assert.Equal("", (string)ret["PARAMETER_NAME"]);
        Assert.Equal(3, (int)ret["COLUMN_TYPE"]); // SQL_RESULT_COL — a RETURNING value
        Assert.Equal((int)IfxType.DateTime, (int)ret["DATA_TYPE"]);
        Assert.Equal("datetime year to second", (string)ret["TYPE_NAME"]);

        var id = rows.Single(r => (string)r["PARAMETER_NAME"] == "id");
        Assert.Equal(1, (int)id["COLUMN_TYPE"]); // SQL_PARAM_INPUT
        Assert.Equal((int)IfxType.Integer, (int)id["DATA_TYPE"]);
        Assert.Equal(4, (int)id["COLUMN_SIZE"]);
        Assert.Equal(0, (int)id["DECIMAL_DIGITS"]);
        Assert.False((bool)id["NULLABLE"]);

        var amt = rows.Single(r => (string)r["PARAMETER_NAME"] == "amt");
        Assert.Equal(4, (int)amt["COLUMN_TYPE"]); // SQL_PARAM_OUTPUT
        Assert.Equal((int)IfxType.Decimal, (int)amt["DATA_TYPE"]);
        Assert.Equal(10, (int)amt["COLUMN_SIZE"]);
        Assert.Equal(2, (int)amt["DECIMAL_DIGITS"]);
        Assert.True((bool)amt["NULLABLE"]);

        var note = rows.Single(r => (string)r["PARAMETER_NAME"] == "note");
        Assert.Equal(2, (int)note["COLUMN_TYPE"]); // SQL_PARAM_INPUT_OUTPUT
        Assert.Equal((int)IfxType.LVarChar, (int)note["DATA_TYPE"]);
        Assert.Equal("LVarChar", (string)note["TYPE_NAME"]);
        Assert.Equal(200, (int)note["COLUMN_SIZE"]);
        Assert.True((bool)note["NULLABLE"]);
    }

    // ---- IfxCommandBuilder.DeriveParameters (sysproccolumns.paramname / paramattr) ----

    private static byte[] ParamAttrRow(string paramname, int paramattr)
    {
        var row = new byte[12];
        IfxWireConverter.EncodeChar(paramname.PadRight(8)).CopyTo(row.AsSpan(0));
        IfxWireConverter.WriteInt32(row.AsSpan(8, 4), paramattr);
        return row;
    }

    private static byte[] ParamAttrSelect(params byte[][] rows) => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 12,
            new Col("paramname", (short)SqliTypeCode.Char, 8, 0),
            new Col("paramattr", (short)SqliTypeCode.Int, 4, 8)),
        DoneFrame(), EotFrame(),
        DoneFrame(), Join(rows.Select(TupleFrame).ToArray()), CloseFrame(), EotFrame(),
        EotFrame());

    // DeriveParameters first resolves the routine (sysprocedures: procid, owner, numargs),
    // then reads that procid's sysproccolumns rows.
    private static byte[] RoutineRow(int procid, string owner, int numargs)
    {
        var row = new byte[16];
        IfxWireConverter.WriteInt32(row.AsSpan(0, 4), procid);
        IfxWireConverter.EncodeChar(owner.PadRight(8)).CopyTo(row.AsSpan(4));
        IfxWireConverter.WriteInt32(row.AsSpan(12, 4), numargs);
        return row;
    }

    private static byte[] RoutineSelect(params byte[][] rows) => Join(
        DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 16,
            new Col("procid", (short)SqliTypeCode.Int, 4, 0),
            new Col("owner", (short)SqliTypeCode.Char, 8, 4),
            new Col("numargs", (short)SqliTypeCode.Int, 4, 12)),
        DoneFrame(), EotFrame(),
        DoneFrame(), Join(rows.Select(TupleFrame).ToArray()), CloseFrame(), EotFrame(),
        EotFrame());

    private static readonly byte[] SingleRoutine = RoutineSelect(RoutineRow(7, "informix", 1));

    // Runs DeriveParameters against a scripted server and returns the SQL text the driver sent.
    private static string RunDerive(byte[] script, string commandText, int? procId,
        out IfxParameterCollection parameters)
    {
        using var cn = OpenConnection(script, out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = commandText;
        if (procId is { } id)
            new IfxCommandBuilder().DeriveParameters(cmd, id);
        else
            new IfxCommandBuilder().DeriveParameters(cmd);
        parameters = cmd.Parameters;
        return System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray());
    }

    private static (string Name, ParameterDirection Direction)[] Derive(params byte[][] catalogRows)
    {
        RunDerive(Join(SingleRoutine, ParamAttrSelect(catalogRows)), "sp", null, out var parameters);
        return parameters.Cast<IfxParameter>().Select(p => (p.ParameterName, p.Direction)).ToArray();
    }

    [Fact]
    public void DeriveParameters_maps_each_paramattr_code_to_its_direction()
    {
        // paramattr is ODBC's COLUMN_TYPE enumeration (1 IN, 2 INOUT, 4 OUT), not a bit mask.
        var derived = Derive(
            ParamAttrRow("a", 1),
            ParamAttrRow("b", 2),
            ParamAttrRow("c", 4));

        Assert.Equal(
        [
            ("a", ParameterDirection.Input),
            ("b", ParameterDirection.InputOutput),
            ("c", ParameterDirection.Output),
        ], derived);
    }

    [Fact]
    public void DeriveParameters_maps_RETURNING_rows_to_ReturnValue_parameters()
    {
        // create function f(a int, out b int) returning int, int — the two RETURNING rows
        // (paramattr 3, NULL paramname) precede the parameters in paramid order. Under the old
        // "attr & 6" reading they became InputOutput parameters p1/p2 ahead of a and b.
        // IfxCommand hands RETURNed values to ReturnValue parameters, in order (as IBM's
        // Informix.Net.Core maps SQL_RESULT_COL too).
        var derived = Derive(
            ParamAttrRow("", 3),
            ParamAttrRow("", 3),
            ParamAttrRow("a", 1),
            ParamAttrRow("b", 4));

        Assert.Equal(
        [
            ("RETURN_VALUE", ParameterDirection.ReturnValue),
            ("RETURN_VALUE2", ParameterDirection.ReturnValue),
            ("a", ParameterDirection.Input),
            ("b", ParameterDirection.Output),
        ], derived);
    }

    [Fact]
    public void DeriveParameters_maps_a_return_value_row_to_ReturnValue()
    {
        // SQL_RETURN_VALUE (5) used to fall into the Output bit of the mask.
        var derived = Derive(
            ParamAttrRow("", 5),
            ParamAttrRow("a", 1));

        Assert.Equal(
        [
            ("RETURN_VALUE", ParameterDirection.ReturnValue),
            ("a", ParameterDirection.Input),
        ], derived);
    }

    [Fact]
    public void DeriveParameters_names_unnamed_arguments_by_their_position_in_the_collection()
    {
        var derived = Derive(
            ParamAttrRow("", 3),
            ParamAttrRow("", 1),
            ParamAttrRow("", 4));

        Assert.Equal(
        [
            ("RETURN_VALUE", ParameterDirection.ReturnValue),
            ("p2", ParameterDirection.Input),
            ("p3", ParameterDirection.Output),
        ], derived);
    }

    // ---- DeriveParameters: overloads are told apart by procid ----

    [Fact]
    public void DeriveParameters_reads_the_columns_of_the_resolved_procid_not_of_the_name()
    {
        string sent = RunDerive(Join(SingleRoutine, ParamAttrSelect(ParamAttrRow("a", 1))), "sp", null, out _);

        Assert.Contains("FROM sysprocedures WHERE procname = ?", sent);
        Assert.Contains("FROM sysproccolumns WHERE procid = ?", sent);
        Assert.DoesNotContain("owner = ?", sent); // an unqualified name is not narrowed by owner
        Assert.DoesNotContain("AND procid = ?", sent); // nor by a procid nobody asked for
    }

    [Fact]
    public void DeriveParameters_rejects_an_overloaded_name_and_lists_the_candidates()
    {
        byte[] script = RoutineSelect(
            RoutineRow(7, "informix", 1),
            RoutineRow(8, "informix", 2));

        using var cn = OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp";
        cmd.Parameters.Add(new IfxParameter("keep", 1));

        var ex = Assert.Throws<InvalidOperationException>(() => new IfxCommandBuilder().DeriveParameters(cmd));

        Assert.Contains("procid 7 = informix.sp (1 argument(s))", ex.Message);
        Assert.Contains("procid 8 = informix.sp (2 argument(s))", ex.Message);
        Assert.Single(cmd.Parameters); // the collection is left untouched
    }

    [Fact]
    public void DeriveParameters_picks_the_overload_with_the_given_procid()
    {
        // The server answers the procid filter with the one routine; its columns are then read.
        byte[] script = Join(
            RoutineSelect(RoutineRow(8, "informix", 2)),
            ParamAttrSelect(ParamAttrRow("s", 1), ParamAttrRow("t", 4)));

        string sent = RunDerive(script, "sp", 8, out var parameters);

        Assert.Contains("AND procid = ?", sent);
        Assert.Equal(["s", "t"], parameters.Cast<IfxParameter>().Select(p => p.ParameterName).ToArray());
    }

    [Fact]
    public void DeriveParameters_rejects_a_procid_that_names_no_such_routine()
    {
        using var cn = OpenConnection(RoutineSelect(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp";
        cmd.Parameters.Add(new IfxParameter("keep", 1));

        var ex = Assert.Throws<InvalidOperationException>(() => new IfxCommandBuilder().DeriveParameters(cmd, 99));

        Assert.Contains("procid 99", ex.Message);
        Assert.Single(cmd.Parameters);
    }

    [Theory]
    [InlineData("bob.sp")]
    [InlineData("\"bob\".\"sp\"")]
    public void DeriveParameters_narrows_by_owner_only_when_the_name_is_qualified(string commandText)
    {
        string sent = RunDerive(Join(SingleRoutine, ParamAttrSelect(ParamAttrRow("a", 1))), commandText, null, out _);

        Assert.Contains("AND owner = ?", sent);
        Assert.Contains("bob", sent);
        Assert.DoesNotContain("\"bob\"", sent); // the owner is bound unquoted
    }

    [Fact]
    public void DeriveParameters_leaves_an_empty_collection_for_an_unknown_routine()
    {
        using var cn = OpenConnection(RoutineSelect(), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "nosuch";
        cmd.Parameters.Add(new IfxParameter("stale", 1));

        new IfxCommandBuilder().DeriveParameters(cmd);

        Assert.Empty(cmd.Parameters);
    }

    [Fact]
    public void DeriveParameters_requires_a_StoredProcedure_command()
    {
        using var cn = new IfxConnection();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select 1 from systables";
        Assert.Throws<InvalidOperationException>(() => new IfxCommandBuilder().DeriveParameters(cmd));
    }

    [Fact]
    public void DeriveParameters_requires_an_open_connection()
    {
        using var cn = new IfxConnection();
        using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp";
        Assert.Throws<InvalidOperationException>(() => new IfxCommandBuilder().DeriveParameters(cmd));
    }


    // ---- Columns: decoded type words (syscolumns coltype / collength / extended_id) ----

    // Every row is a live capture of `select colname, coltype, collength, extended_id from
    // syscolumns` against the test server (IDS, ol_darkstar) for a table declared with the type
    // named in the first argument. BLOB/CLOB/BOOLEAN/LVARCHAR/JSON all share coltype 40 or 41 and
    // only extended_id tells them apart; VARCHAR(30,5) packs (min<<8)|max; DECIMAL(16) with no
    // scale is "floating" (scale byte 255).
    [Theory]
    [InlineData("SERIAL NOT NULL", 262, 4, 0, IfxType.Serial, "Serial", 4, 0, false)]
    [InlineData("DECIMAL(10,2)", 5, 2562, 0, IfxType.Decimal, "Decimal", 10, 2, true)]
    [InlineData("VARCHAR(30,5)", 13, 1310, 0, IfxType.VarChar, "VarChar", 30, 0, true)]
    [InlineData("DATETIME YEAR TO FRACTION(3)", 10, 4365, 0, IfxType.DateTime, "datetime year to fraction(3)", 17, 3, true)]
    [InlineData("INTERVAL DAY(4) TO FRACTION(3)", 14, 3405, 0, IfxType.IntervalDayFraction, "interval day to fraction(3)", 13, 3, true)]
    [InlineData("INTERVAL YEAR(4) TO MONTH", 14, 1538, 0, IfxType.IntervalYearMonth, "interval year to month", 6, 0, true)]
    [InlineData("BLOB", 41, 72, 10, IfxType.Blob, "Blob", null, 0, true)]
    [InlineData("CLOB", 41, 72, 11, IfxType.Clob, "Clob", null, 0, true)]
    [InlineData("BOOLEAN", 41, 1, 5, IfxType.Boolean, "Bool", 1, 0, true)]
    [InlineData("LVARCHAR(500)", 40, 500, 1, IfxType.LVarChar, "LVarChar", 500, 0, true)]
    [InlineData("CHAR(12)", 0, 12, 0, IfxType.Char, "Char", 12, 0, true)]
    [InlineData("MONEY(8,2)", 8, 2050, 0, IfxType.Money, "Money", 8, 2, true)]
    [InlineData("DECIMAL(16)", 5, 4351, 0, IfxType.Decimal, "Decimal", 16, null, true)]
    [InlineData("BIGINT", 52, 8, 0, IfxType.BigInt, "BigInt", 8, 0, true)]
    [InlineData("JSON", 40, 4096, 25, IfxType.Json, "Json", 4096, 0, true)]
    [InlineData("SMALLINT NOT NULL", 257, 2, 0, IfxType.SmallInt, "SmallInt", 2, 0, false)]
    public void DescribeColumnType_decodes_live_captured_syscolumns_words(
        string declared, int colType, int colLength, int extendedId,
        IfxType dataType, string typeName, int? size, int? digits, bool nullable)
    {
        var info = IfxSchema.DescribeColumnType(colType, extendedId, colLength);
        Assert.True(dataType == info.DataType, $"{declared}: DATA_TYPE");
        Assert.True(typeName == info.TypeName, $"{declared}: TYPE_NAME was {info.TypeName}");
        Assert.True(size == info.Size, $"{declared}: COLUMN_SIZE was {info.Size}");
        Assert.True(digits == info.Digits, $"{declared}: DECIMAL_DIGITS was {info.Digits}");
        Assert.True(nullable == info.Nullable, $"{declared}: NULLABLE");
    }

    private static byte[] ColumnRow(string owner, string tabname, string colname,
        int colno, int coltype, int collength, int extendedId)
    {
        var row = new byte[40];
        IfxWireConverter.EncodeChar(owner.PadRight(8)).CopyTo(row.AsSpan(0));
        IfxWireConverter.EncodeChar(tabname.PadRight(8)).CopyTo(row.AsSpan(8));
        IfxWireConverter.EncodeChar(colname.PadRight(8)).CopyTo(row.AsSpan(16));
        IfxWireConverter.WriteInt32(row.AsSpan(24, 4), colno);
        IfxWireConverter.WriteInt32(row.AsSpan(28, 4), coltype);
        IfxWireConverter.WriteInt32(row.AsSpan(32, 4), collength);
        IfxWireConverter.WriteInt32(row.AsSpan(36, 4), extendedId);
        return row;
    }

    [Fact]
    public void GetSchema_Columns_keeps_the_raw_words_and_adds_the_decoded_columns()
    {
        byte[] script = Join(
            DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 40,
                new Col("owner", (short)SqliTypeCode.Char, 8, 0),
                new Col("tabname", (short)SqliTypeCode.Char, 8, 8),
                new Col("colname", (short)SqliTypeCode.Char, 8, 16),
                new Col("colno", (short)SqliTypeCode.Int, 4, 24),
                new Col("coltype", (short)SqliTypeCode.Int, 4, 28),
                new Col("collength", (short)SqliTypeCode.Int, 4, 32),
                new Col("extended_id", (short)SqliTypeCode.Int, 4, 36)),
            DoneFrame(), EotFrame(),
            DoneFrame(),
            Join(TupleFrame(ColumnRow("informix", "t", "id", 1, 262, 4, 0)),
                 TupleFrame(ColumnRow("informix", "t", "body", 2, 41, 72, 11)),
                 TupleFrame(ColumnRow("informix", "t", "price", 3, 5, 2562, 0))),
            CloseFrame(), EotFrame(),
            EotFrame());

        using var cn = OpenConnection(script, out _);
        var t = cn.GetSchema("Columns", [null, null, "t", null]);

        Assert.Equal(
            ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION",
             "COLUMN_TYPE", "COLUMN_LENGTH", "DATA_TYPE", "TYPE_NAME", "COLUMN_SIZE", "DECIMAL_DIGITS", "NULLABLE"],
            t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());

        var rows = t.Rows.Cast<DataRow>().ToArray();
        Assert.Equal(3, rows.Length);

        Assert.Equal(262, (int)rows[0]["COLUMN_TYPE"]);      // raw word, NOT NULL flag still in it
        Assert.Equal((int)IfxType.Serial, (int)rows[0]["DATA_TYPE"]);
        Assert.False((bool)rows[0]["NULLABLE"]);

        Assert.Equal(41, (int)rows[1]["COLUMN_TYPE"]);       // the same raw 41 BOOLEAN has...
        Assert.Equal((int)IfxType.Clob, (int)rows[1]["DATA_TYPE"]); // ...but extended_id 11 says CLOB
        Assert.Equal(DBNull.Value, rows[1]["COLUMN_SIZE"]);  // collength 72 is the locator, not a size

        Assert.Equal(10, (int)rows[2]["COLUMN_SIZE"]);
        Assert.Equal(2, (int)rows[2]["DECIMAL_DIGITS"]);
    }

    [Fact]
    public void GetSchemaTable_carries_the_extended_type_columns()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 8,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Int, 4, 0),
                new SqliStatementKit.Col("b", (short)SqliTypeCode.Int, 4, 4)),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b from t";
        using var reader = cmd.ExecuteReader();
        var schema = reader.GetSchemaTable();

        Assert.True(schema.Columns.Contains("ExtendedTypeName"));
        Assert.True(schema.Columns.Contains("ExtendedOwner"));
        Assert.True(schema.Columns.Contains("IsDistinct"));
        Assert.True(schema.Columns.Contains("IsOpaqueComplex"));
        Assert.False((bool)schema.Rows[0]["IsOpaqueComplex"]);
    }
}
