// UnloadFromProcedure: the rows a stored routine returns go to an UNLOAD file exactly like the
// rows of a table, and a routine with OUT parameters hands its values back through the parameters.
//
// Needs sql/c_stock_proc.sql (the loader installs it): stock_by_manufacturer returns several rows
// (RETURNING ... WITH RESUME); stock_summary has two OUT parameters.
//
// Shows: EXECUTE FUNCTION with a named parameter, CommandType.StoredProcedure, one command reused
// for many calls, IfxCommandBuilder.DeriveParameters, GetSchema("ProcedureColumns") and
// ParameterDirection.Output.

using System.Data;
using Demo;
using Informix.Data;

Ui.Title("UnloadFromProcedure - routine results to UNLOAD files");
string dir = DemoEnv.WorkDir("procedure");

using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", "MDY4/")));
cn.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. EXECUTE FUNCTION with a named parameter");
{
    using var cmd = new IfxCommand("EXECUTE FUNCTION stock_by_manufacturer(@manu)", cn);
    cmd.Parameters.Add(new IfxParameter("@manu", "HRO"));
    using var reader = cmd.ExecuteReader();

    var columns = reader.GetColumnSchema();
    Ui.Line("   the routine's RETURNING list, as the reader sees it:");
    Ui.Table(["name", "type", "CLR type"], columns.Select(c => new object?[] { c.ColumnName, c.DataTypeName, c.DataType?.Name }));

    string path = Path.Combine(dir, "stock_HRO.unl");
    using (var writer = Unl.Create(path))
        Ui.Kv("rows unloaded", Unl.Write(reader, writer, cn.TextFormat));
    Ui.Head(path, 3);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. CommandType.StoredProcedure - just the routine name");
{
    using var cmd = new IfxCommand("stock_by_manufacturer", cn) { CommandType = CommandType.StoredProcedure };
    cmd.Parameters.Add(new IfxParameter("p_manu", "SMT"));
    using var reader = cmd.ExecuteReader();
    string path = Path.Combine(dir, "stock_SMT.unl");
    using var writer = Unl.Create(path);
    Ui.Kv("rows unloaded", Unl.Write(reader, writer, cn.TextFormat));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. One command, every manufacturer, one file");
{
    var manufacturers = new List<string>();
    using (var q = new IfxCommand("SELECT manu_code FROM manufact ORDER BY manu_code", cn))
    using (var r = q.ExecuteReader())
        while (r.Read()) manufacturers.Add(r.GetString(0).Trim());

    string path = Path.Combine(dir, "stock_all.unl");
    using var writer = Unl.Create(path);
    using var cmd = new IfxCommand("EXECUTE FUNCTION stock_by_manufacturer(@manu)", cn);
    var manu = cmd.Parameters.Add(new IfxParameter("@manu", ""));

    var counts = new List<object?[]>();
    foreach (string code in manufacturers)
    {
        manu.Value = code;
        using var reader = cmd.ExecuteReader();
        counts.Add([code, Unl.Write(reader, writer, cn.TextFormat)]);
    }
    Ui.Table(["manufacturer", "rows"], counts);
    Ui.Kv("total", counts.Sum(c => (long)c[1]!));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Discovering a routine's parameters");
{
    using var cmd = new IfxCommand("stock_summary", cn) { CommandType = CommandType.StoredProcedure };
    new IfxCommandBuilder().DeriveParameters(cmd);
    Ui.Line("   IfxCommandBuilder.DeriveParameters(stock_summary):");
    Ui.Table(["name", "direction"],
        cmd.Parameters.Cast<IfxParameter>().Select(p => new object?[] { p.ParameterName, p.Direction }));

    var schema = cn.GetSchema("ProcedureColumns", [null, null, "stock_summary"]);
    Ui.Line();
    Ui.Line("   GetSchema(\"ProcedureColumns\") - COLUMN_TYPE is ODBC's numbering (1 IN, 2 INOUT, 3 RETURNING, 4 OUT):");
    Ui.Table(["PARAMETER_NAME", "COLUMN_TYPE", "TYPE_NAME"],
        schema.Rows.Cast<DataRow>().Select(r => new object?[] { r["PARAMETER_NAME"], r["COLUMN_TYPE"], r["TYPE_NAME"] }));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. OUT parameters: ExecuteNonQuery, values come back through the parameters");
{
    using var cmd = new IfxCommand("stock_summary", cn) { CommandType = CommandType.StoredProcedure };
    var manu = cmd.Parameters.Add(new IfxParameter("p_manu", "HRO"));
    var items = cmd.Parameters.Add(new IfxParameter("p_items", IfxType.Integer) { Direction = ParameterDirection.Output });
    var value = cmd.Parameters.Add(new IfxParameter("p_value", IfxType.Decimal) { Direction = ParameterDirection.Output });

    var rows = new List<object?[]>();
    foreach (string code in new[] { "HRO", "SMT", "ANZ" })
    {
        manu.Value = code;
        cmd.ExecuteNonQuery();
        rows.Add([code, items.Value, value.Value]);
    }
    Ui.Table(["manufacturer", "items in stock list", "stock value"], rows);

    Ui.Line();
    Ui.Line("   the ODBC call escape works too:");
    using var esc = new IfxCommand("{call stock_summary(?, ?, ?)}", cn);
    esc.Parameters.Add(new IfxParameter("a", "SHM"));
    esc.Parameters.Add(new IfxParameter("b", IfxType.Integer) { Direction = ParameterDirection.Output });
    esc.Parameters.Add(new IfxParameter("c", IfxType.Decimal) { Direction = ParameterDirection.Output });
    esc.ExecuteNonQuery();
    Ui.Kv("SHM", $"{esc.Parameters[1].Value} items, value {esc.Parameters[2].Value}");
}

Ui.Line();
Ui.Line("Done.");
