// Schema: catalog metadata through GetSchema, and the classic disconnected pattern -
// IfxDataAdapter + IfxCommandBuilder + DataSet.
//
// Shows: the metadata collections the driver offers (ODBC-shaped: Tables, Columns, PrimaryKeys,
// ForeignKeys, Indexes, Procedures, ProcedureColumns, ...), restrictions to narrow them, and a
// DataSet with two related tables edited and written back through generated commands.

using System.Data;
using Demo;
using Informix.Data;

Ui.Title("Schema");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

void Show(string title, DataTable table, int maxRows = 8, params string[] columns)
{
    var cols = columns.Length > 0
        ? columns.Where(c => table.Columns.Contains(c)).ToArray()
        : table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Take(6).ToArray();
    Ui.Line($"   {title}: {table.Rows.Count} row(s)");
    Ui.Table(cols, table.Rows.Cast<DataRow>().Take(maxRows).Select(r => cols.Select(c => r[c]).ToArray()));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Which collections are there");
{
    DataTable collections = cn.GetSchema();
    Ui.Kv("GetSchema()", string.Join(", ", collections.Rows.Cast<DataRow>().Select(r => r["CollectionName"])));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Tables and columns; restrictions are [catalog, schema, table, column], matched exactly");
{
    var tables = cn.GetSchema("Tables");
    Show("Tables (no restriction)", tables, 8, "TABLE_NAME", "TABLE_TYPE");
    Ui.Line();
    Show("Tables, restricted to 'stock'", cn.GetSchema("Tables", [null, null, "stock"]), 3, "TABLE_NAME", "TABLE_TYPE");

    Ui.Line();
    var columns = cn.GetSchema("Columns", [null, null, "stock"]);
    Show("columns of stock", columns, 20, "COLUMN_NAME", "TYPE_NAME", "COLUMN_SIZE", "DECIMAL_DIGITS", "NULLABLE");
}

Ui.Section("3. Keys and indexes of items");
{
    Show("PrimaryKeys", cn.GetSchema("PrimaryKeys", [null, null, "items"]), 5);
    Ui.Line();
    Show("ForeignKeys", cn.GetSchema("ForeignKeys", [null, null, "items"]), 5);
}

Ui.Section("4. Routines and their parameters");
{
    Show("Procedures named best_month (unrestricted there are 500+ system routines)", cn.GetSchema("Procedures", [null, null, "best_month"]), 3, "PROCEDURE_NAME", "PROCEDURE_TYPE");
    Ui.Line();
    Show("ProcedureColumns of stock_by_manufacturer", cn.GetSchema("ProcedureColumns", [null, null, "stock_by_manufacturer"]), 10,
        "PARAMETER_NAME", "COLUMN_TYPE", "TYPE_NAME", "ORDINAL_POSITION");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. DataAdapter + CommandBuilder + DataSet: manufacturers and their stock");
{
    using var tx = cn.BeginTransaction();
    var ds = new DataSet("superstore");

    var manufacturers = new IfxDataAdapter(new IfxCommand("SELECT manu_code, manu_name, terms_net FROM manufact ORDER BY manu_code", cn) { Transaction = tx });
    manufacturers.Fill(ds, "manufact");
    var stock = new IfxDataAdapter(new IfxCommand("SELECT stock_num, manu_code, unit, description, qty_on_hand FROM stock", cn) { Transaction = tx });
    stock.Fill(ds, "stock");

    ds.Relations.Add("manufact_stock", ds.Tables["manufact"]!.Columns["manu_code"]!, ds.Tables["stock"]!.Columns["manu_code"]!, createConstraints: false);

    var rows = new List<object?[]>();
    foreach (DataRow m in ds.Tables["manufact"]!.Rows.Cast<DataRow>().Take(4))
        rows.Add([m["manu_code"], m["manu_name"], m.GetChildRows("manufact_stock").Length]);
    Ui.Table(["manu_code", "manu_name", "stock rows"], rows);

    // Edit one row in memory and send it back.
    DataRow row = ds.Tables["manufact"]!.Select("manu_code = 'SMT'")[0];
    row["terms_net"] = (short)45;

    // IfxCommandBuilder derives INSERT / UPDATE / DELETE from the SELECT: the base table is read from the
    // statement and the primary key from the catalog (the reader's schema table says IsKey for manu_code).
    var builder = new IfxCommandBuilder(manufacturers);
    Ui.Line();
    Ui.Line("   generated UPDATE: " + builder.GetUpdateCommand().CommandText);
    manufacturers.UpdateCommand = builder.GetUpdateCommand();
    manufacturers.UpdateCommand.Transaction = tx;
    Ui.Kv("rows written back", manufacturers.Update(ds, "manufact"));

    using (var check = new IfxCommand("SELECT terms_net FROM manufact WHERE manu_code = 'SMT'", cn) { Transaction = tx })
        Ui.Kv("terms_net in the database", check.ExecuteScalar());

    tx.Rollback();
    using var after = new IfxCommand("SELECT terms_net FROM manufact WHERE manu_code = 'SMT'", cn);
    Ui.Kv("after the rollback", after.ExecuteScalar());
}

Ui.Line();
Ui.Line("Done.");
