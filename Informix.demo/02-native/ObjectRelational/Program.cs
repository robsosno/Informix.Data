// ObjectRelational: named ROW types, inherited tables, collections and a distinct type.
//
// The superstore_demo2 customer is a ROW-typed table with sub-tables (retail_customer,
// whlsale_customer); a customer has a ROW name (name_t), a LIST of contact dates and a
// DISTINCT TYPE discount (percent). sales_rep has a SET(ROW(...)) of monthly sales and
// location_us a LIST of street lines and an unnamed ROW zip.
//
// Shows: IfxRow (type name, fields by name or position), collections as arrays, the column
// metadata that tells a ROW or a distinct type from a plain one, querying through the type
// hierarchy (and ONLY), and writing ROW / collection values - which, today, means SQL
// constructors in the statement text rather than parameters.

using Demo;
using Informix.Data;

Ui.Title("ObjectRelational");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

long Scalar(string sql)
{
    using var c = new IfxCommand(sql, cn);
    return Convert.ToInt64(c.ExecuteScalar());
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Querying through the type hierarchy");
Ui.Table(["query", "rows"],
[
    ["SELECT FROM customer (the whole hierarchy)", Scalar("SELECT COUNT(*) FROM customer")],
    ["SELECT FROM ONLY(customer)", Scalar("SELECT COUNT(*) FROM ONLY(customer)")],
    ["SELECT FROM retail_customer", Scalar("SELECT COUNT(*) FROM retail_customer")],
    ["SELECT FROM whlsale_customer", Scalar("SELECT COUNT(*) FROM whlsale_customer")],
]);
Ui.Note("a query on the super-table sees its sub-tables' rows, with the columns they all have");

// ---------------------------------------------------------------------------------------------
Ui.Section("2. A ROW column arrives as an IfxRow");
{
    using var cmd = new IfxCommand(
        "SELECT customer_num, customer_name, cust_discount, credit_status FROM retail_customer WHERE customer_num IN (101, 102)", cn);
    using var r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read())
    {
        var name = (IfxRow)r.GetValue(1);
        rows.Add([r.GetInt32(0), name.TypeName, name.GetValue("first"), name.GetString(name.GetOrdinal("last")), r.GetDecimal(2)]);
    }
    Ui.Table(["customer_num", "ROW type", "first", "last", "discount"], rows);
    Ui.Kv("IfxRow as text", ((IfxRow)new IfxCommand("SELECT customer_name FROM retail_customer WHERE customer_num = 101", cn).ExecuteScalar()!).ToString());
    Ui.Note("the same fields are reachable in SQL: SELECT customer_name.first FROM retail_customer");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. Collections arrive as arrays; their elements may be ROWs");
{
    using (var cmd = new IfxCommand(
        "SELECT customer_num, contact_dates, CARDINALITY(contact_dates) FROM retail_customer WHERE customer_num IN (101, 102)", cn))
    using (var r = cmd.ExecuteReader())
    {
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var dates = (object[])r.GetValue(1);
            rows.Add([r.GetInt32(0), dates.GetType().Name, r.GetInt32(2), string.Join(", ", dates.Select(Ui.Show))]);
        }
        Ui.Table(["customer_num", ".NET type", "elements", "LIST(DATETIME YEAR TO DAY)"], rows);
    }

    Ui.Line();
    using (var cmd = new IfxCommand("SELECT rep_num, name, sales FROM sales_rep ORDER BY rep_num", cn))
    using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            Ui.Line($"   rep {r.GetInt32(0)}  {r.GetValue(1)}");
            foreach (IfxRow sale in (object[])r.GetValue(2))
                Ui.Line($"      SET element: month {Ui.Show(sale.GetValue("month"))}  amount {Ui.Show(sale.GetValue("amount"))}  ({sale.GetFieldType(0).Name}, {sale.GetFieldType(1).Name})");
        }

    Ui.Line();
    using (var cmd = new IfxCommand("SELECT location_id, street_addr, zip FROM location_us WHERE location_id = 3", cn))
    using (var r = cmd.ExecuteReader())
    {
        r.Read();
        var zip = (IfxRow)r.GetValue(2);
        Ui.Kv("street_addr (LIST of VARCHAR)", r.GetValue(1));
        Ui.Kv("zip (unnamed ROW)", $"code {zip.GetValue("code")}, suffix {zip.GetValue("suffix")}  [type name '{zip.TypeName}']");
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. What the column metadata says");
{
    using var cmd = new IfxCommand(
        "SELECT customer_num, customer_name, contact_dates, cust_discount, credit_status FROM retail_customer", cn);
    using var r = (IfxDataReader)cmd.ExecuteReader(System.Data.CommandBehavior.SchemaOnly);
    var rows = new List<object?[]>();
    foreach (var c in r.GetColumnSchema().Cast<IfxDbColumn>())
        rows.Add([c.ColumnName, c.DataTypeName, c.DataType?.Name, c.IsDistinct, c.IsOpaqueComplex]);
    Ui.Table(["column", "type name", ".NET type", "distinct", "complex"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. Searching inside collections and calling the routines that work on them");
{
    Ui.Kv("customers contacted on 2008-03-14", Scalar("SELECT COUNT(*) FROM retail_customer WHERE DATETIME (2008-03-14) YEAR TO DAY IN contact_dates"));
    using var cmd = new IfxCommand("SELECT elem_position(contact_dates, 2), last_contact(customer_num) FROM retail_customer WHERE customer_num = 102", cn);
    using var r = cmd.ExecuteReader();
    r.Read();
    Ui.Kv("2nd contact / last contact", $"{Ui.Show(r.GetValue(0))} / {Ui.Show(r.GetValue(1))}   (SPL functions from the sample)");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("6. Writing: ROW and collection values are SQL constructors; scalars are parameters");
{
    using var tx = cn.BeginTransaction();

    using (var cmd = new IfxCommand(
        "INSERT INTO retail_customer (customer_type, customer_name, customer_loc, contact_dates, cust_discount, credit_status, credit_num, expiration) " +
        "VALUES ('R', ROW('Ada', 'Lovelace')::name_t, ?, LIST{'2024-01-15', '2024-02-20'}, ?, 'N', ?, ?)", cn) { Transaction = tx })
    {
        cmd.Parameters.Add(new IfxParameter("loc", 2));
        cmd.Parameters.Add(new IfxParameter("discount", 0.05m));
        cmd.Parameters.Add(new IfxParameter("card", "1234 5678 9012 3456"));
        cmd.Parameters.Add(new IfxParameter("exp", new DateOnly(2027, 12, 1)));
        cmd.ExecuteNonQuery();
        Ui.Kv("inserted customer_num", cmd.LastSerial);
    }

    using (var cmd = new IfxCommand(
        "SELECT customer_num, customer_name, contact_dates FROM retail_customer WHERE customer_name.last = 'Lovelace'", cn) { Transaction = tx })
    using (var r = cmd.ExecuteReader())
    {
        r.Read();
        Ui.Kv("read back", $"{r.GetInt32(0)}  {r.GetValue(1)}  {Ui.Show(r.GetValue(2))}");
    }

    using (var cmd = new IfxCommand(
        "UPDATE retail_customer SET contact_dates = LIST{'2024-03-01'} WHERE customer_name.last = 'Lovelace'", cn) { Transaction = tx })
        Ui.Kv("UPDATE of the LIST", $"{cmd.ExecuteNonQuery()} row");

    using (var cmd = new IfxCommand("SELECT COUNT(*) FROM customer", cn) { Transaction = tx })
        Ui.Kv("customers while the tx is open", cmd.ExecuteScalar());

    tx.Rollback();
    Ui.Kv("customers after the rollback", Scalar("SELECT COUNT(*) FROM customer"));
}

Ui.Line();
Ui.Line("Done.");
