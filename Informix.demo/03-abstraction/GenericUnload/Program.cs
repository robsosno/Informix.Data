// GenericUnload: writing the result of ANY query, or of a stored routine, as an UNLOAD-style
// file using System.Data.Common only.
//
// The writer asks GetColumnSchema() what each column is and formats it by its .NET type, so the
// same loop serves a table, a join and a routine. Formatting is plain .NET with fixed invariant
// formats (an application chooses its own; dates here follow DBDATE=MDY4/). The driver's exact
// Informix text conversion - qualifier-aware DATETIME and INTERVAL text, DBDATE variants - is the
// provider-specific IfxTextConvert, used in 02-native/Unload.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Demo;

Ui.Title("GenericUnload");
string dir = DemoEnv.WorkDir("generic-unload");

static string Field(DbDataReader r, int i)
{
    if (r.IsDBNull(i)) return "";
    object v = r.GetValue(i);
    return v switch
    {
        DateOnly d => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.Days.ToString(CultureInfo.InvariantCulture),            // day-only intervals
        bool b => b ? "t" : "f",
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

long Unload(DbDataReader reader, string path)
{
    using var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
    long rows = 0;
    var line = new StringBuilder();
    while (reader.Read())
    {
        line.Clear();
        for (int i = 0; i < reader.FieldCount; i++) line.Append(Field(reader, i)).Append('|');
        w.WriteLine(line);
        rows++;
    }
    return rows;
}

using DbConnection cn = Bootstrap.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. A table");
{
    string path = Path.Combine(dir, "orders.unl");
    using DbCommand cmd = cn.Command("SELECT order_num, order_date, customer_num, backlog, po_num, paid_date FROM orders ORDER BY order_num");
    using DbDataReader r = cmd.ExecuteReader();
    Ui.Kv("rows", Unload(r, path));
    Ui.Head(path, 2);
}

Ui.Section("2. A join, with a parameter");
{
    string path = Path.Combine(dir, "order_lines.unl");
    using DbCommand cmd = cn.Command(
        "SELECT o.order_num, i.item_num, s.description, i.quantity, i.item_subtotal " +
        "FROM orders o, items i, stock s " +
        "WHERE o.order_num = i.order_num AND i.stock_num = s.stock_num AND i.manu_code = s.manu_code AND i.unit = s.unit " +
        "AND o.customer_num = @customer ORDER BY 1, 2");
    cmd.Add("@customer", 104);
    using DbDataReader r = cmd.ExecuteReader();
    Ui.Kv("rows", Unload(r, path));
    Ui.Head(path, 3);
}

Ui.Section("3. A stored routine that returns many rows");
{
    string path = Path.Combine(dir, "stock_HRO.unl");
    using DbCommand cmd = cn.Command("stock_by_manufacturer");
    cmd.CommandType = CommandType.StoredProcedure;
    cmd.Add("p_manu", "HRO");
    using DbDataReader r = cmd.ExecuteReader();
    Ui.Line("   columns: " + string.Join(", ", r.GetColumnSchema().Select(c => $"{c.ColumnName} ({c.DataTypeName})")));
    Ui.Kv("rows", Unload(r, path));
    Ui.Head(path, 3);
}

Ui.Section("4. Where the generic version is less exact than the native one");
{
    using DbCommand cmd = cn.Command("SELECT customer_num, call_dtime, res_dtime FROM cust_calls ORDER BY call_dtime");
    using DbDataReader r = cmd.ExecuteReader();
    r.Read();
    Ui.Kv("this unload writes", $"{Field(r, 1)}   (a DATETIME YEAR TO MINUTE)");
    Ui.Kv("the Informix text would be", "2007-11-28 13:34   (see 02-native/Unload)");
    Ui.Note("a DateTime does not know its qualifier; the provider's own conversion routine does");
}

Ui.Line();
Ui.Line("Done.");
