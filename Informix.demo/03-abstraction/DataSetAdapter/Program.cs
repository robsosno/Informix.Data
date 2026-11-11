// DataSetAdapter: the disconnected side of ADO.NET, and schema discovery, via System.Data.Common.
//
// Shows: DbProviderFactory.CreateDataAdapter / CreateCommandBuilder, DataSet + DataRelation,
// a DataView, writing changes back through DbDataAdapter.Update with the commands a
// DbCommandBuilder derives, and DbConnection.GetSchema() used to print the layout of the whole
// database without a line that is specific to the provider.

using System.Data;
using System.Data.Common;
using Demo;

Ui.Title("DataSetAdapter");

DbProviderFactory factory = Bootstrap.Factory();
using DbConnection cn = Bootstrap.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. A DataSet with a parent / child relation");
var ds = new DataSet("superstore");
{
    DbDataAdapter manufacturers = factory.CreateDataAdapter()!;
    manufacturers.SelectCommand = cn.Command("SELECT manu_code, manu_name, terms_net FROM manufact ORDER BY manu_code");
    manufacturers.Fill(ds, "manufact");

    DbDataAdapter stock = factory.CreateDataAdapter()!;
    stock.SelectCommand = cn.Command("SELECT stock_num, manu_code, unit, description, unit_price, qty_on_hand FROM stock");
    stock.Fill(ds, "stock");

    ds.Relations.Add("manufact_stock", ds.Tables["manufact"]!.Columns["manu_code"]!, ds.Tables["stock"]!.Columns["manu_code"]!, createConstraints: false);

    Ui.Table(["manu_code", "manu_name", "stock rows", "value on hand"],
        ds.Tables["manufact"]!.Rows.Cast<DataRow>().Take(5).Select(m => new object?[]
        {
            m["manu_code"], m["manu_name"], m.GetChildRows("manufact_stock").Length,
            m.GetChildRows("manufact_stock").Sum(s => Convert.ToDecimal(s["unit_price"]) * Convert.ToInt32(s["qty_on_hand"])),
        }));

    var view = new DataView(ds.Tables["stock"]!, "unit_price > 400", "unit_price DESC", DataViewRowState.CurrentRows);
    Ui.Line();
    Ui.Line($"   DataView 'unit_price > 400': {view.Count} rows, dearest first: " +
        string.Join(", ", view.Cast<DataRowView>().Take(3).Select(v => $"{v["description"]} ({v["unit_price"]})")));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Writing changes back");
{
    using DbTransaction tx = cn.BeginTransaction();
    DbDataAdapter adapter = factory.CreateDataAdapter()!;
    adapter.SelectCommand = cn.Command("SELECT manu_code, manu_name, terms_net FROM manufact", tx);

    // A DbCommandBuilder derives INSERT / UPDATE / DELETE from the SELECT: the base table and the
    // primary key come from the reader's schema table.
    DbCommandBuilder builder = factory.CreateCommandBuilder()!;
    builder.DataAdapter = adapter;
    DbCommand update = builder.GetUpdateCommand();
    update.Transaction = tx;
    adapter.UpdateCommand = update;
    Ui.Kv("generated UPDATE", Ui.FirstLine(update.CommandText));

    DataRow row = ds.Tables["manufact"]!.Select("manu_code = 'SMT'")[0];
    Ui.Kv("terms_net of SMT in the DataSet", $"{row["terms_net"]} -> 45");
    row["terms_net"] = (short)45;
    Ui.Kv("rows written", adapter.Update(ds.Tables["manufact"]!));
    Ui.Kv("terms_net in the database (in the tx)", cn.Scalar("SELECT terms_net FROM manufact WHERE manu_code = 'SMT'", tx));
    tx.Rollback();
    Ui.Kv("after the rollback", cn.Scalar("SELECT terms_net FROM manufact WHERE manu_code = 'SMT'"));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. The layout of the database, from GetSchema() alone");
{
    DataTable collections = cn.GetSchema();
    Ui.Kv("collections", string.Join(", ", collections.Rows.Cast<DataRow>().Select(r => r["CollectionName"]).Take(8)) + ", ...");

    DataTable tables = cn.GetSchema("Tables");
    var rows = new List<object?[]>();
    foreach (DataRow t in tables.Rows.Cast<DataRow>().OrderBy(r => r["TABLE_NAME"]))
    {
        string name = (string)t["TABLE_NAME"];
        int columns = cn.GetSchema("Columns", [null, null, name]).Rows.Count;
        string pk = string.Join(", ", cn.GetSchema("PrimaryKeys", [null, null, name]).Rows.Cast<DataRow>().OrderBy(r => r["KEY_SEQ"]).Select(r => r["COLUMN_NAME"]));
        string parents = string.Join(", ", cn.GetSchema("ForeignKeys", [null, null, name]).Rows.Cast<DataRow>().Select(r => (string)r["PKTABLE_NAME"]).Distinct());
        rows.Add([name, columns, pk, parents]);
    }
    Ui.Table(["table", "columns", "primary key", "refers to"], rows);
}

Ui.Line();
Ui.Line("Done.");
