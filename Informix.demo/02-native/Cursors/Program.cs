// Cursors: several open readers on one connection, scrolling in both directions, and a cursor
// that survives a COMMIT.
//
// Shows: concurrent readers on a single IfxConnection (the driver gives each its own statement
// session on the shared socket), ExecuteScrollReader
// (MoveFirst / MoveLast / MoveAbsolute / MoveRelative / MovePrevious / MoveNext), and
// IfxCommand.HoldCursor (a WITH HOLD cursor).

using Demo;
using Informix.Data;

Ui.Title("Cursors");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Master / detail on ONE connection: the orders reader stays open while items are queried");
{
    cn.ResetFetchStats();
    long orders = 0, items = 0;
    decimal value = 0;
    var time = Ui.Time(() =>
    {
        using var outer = new IfxCommand("SELECT order_num, customer_num FROM orders ORDER BY order_num", cn);
        using var orderReader = outer.ExecuteReader();
        while (orderReader.Read())
        {
            orders++;
            using var inner = new IfxCommand("SELECT item_num, quantity, item_subtotal FROM items WHERE order_num = @o", cn);
            inner.Parameters.Add(new IfxParameter("@o", orderReader.GetInt64(0)));
            using var itemReader = inner.ExecuteReader();           // a second reader, while the first is open
            while (itemReader.Read()) { items++; value += itemReader.GetDecimal(2); }
        }
    });
    Ui.Kv("orders / items / value", $"{orders} / {items} / {value:N2}");
    Ui.Kv("time, round trips", $"{Ui.Ms(time)}, {cn.RequestRoundTrips}");
}

Ui.Section("2. Three readers interleaved, row by row");
{
    using var a = new IfxCommand("SELECT code FROM state ORDER BY code", cn);
    using var b = new IfxCommand("SELECT manu_code FROM manufact ORDER BY manu_code", cn);
    using var c = new IfxCommand("SELECT call_code FROM call_type ORDER BY call_code", cn);
    using var ra = a.ExecuteReader();
    using var rb = b.ExecuteReader();
    using var rc = c.ExecuteReader();
    var rows = new List<object?[]>();
    while (ra.Read() && rb.Read() && rc.Read() && rows.Count < 5)
        rows.Add([ra.GetString(0), rb.GetString(0), rc.GetString(0)]);
    Ui.Table(["state", "manufact", "call_type"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. A scroll cursor: forward, backward, absolute and relative");
{
    using var cmd = new IfxCommand(
        "SELECT stock_num, manu_code, description FROM stock ORDER BY stock_num, manu_code, unit", cn);
    using IfxScrollReader scroll = cmd.ExecuteScrollReader();


    string Row() => $"row {scroll.RowNumber,3}: {scroll.GetInt16(0),3} {scroll.GetString(1)} {scroll.GetString(2).Trim()}";

    scroll.MoveLast();       Ui.Kv("MoveLast()", Row());
    scroll.MoveFirst();      Ui.Kv("MoveFirst()", Row());
    scroll.MoveAbsolute(50); Ui.Kv("MoveAbsolute(50)", Row());
    scroll.MoveRelative(-3); Ui.Kv("MoveRelative(-3)", Row());
    scroll.MovePrevious();   Ui.Kv("MovePrevious()", Row());
    scroll.MoveNext();       Ui.Kv("MoveNext()", Row());

    Ui.Line();
    Ui.Line("   paging in any order, 4 rows a page (jump with MoveAbsolute, then walk with MoveNext):");
    foreach (int page in new[] { 3, 1, 28, 2 })
    {
        var names = new List<string>();
        bool more = scroll.MoveAbsolute((page - 1) * 4 + 1);
        for (int i = 0; i < 4 && more; i++)
        {
            names.Add(scroll.GetString(2).Trim());
            more = scroll.MoveNext();
        }
        Ui.Line($"      page {page,2}: {(names.Count == 0 ? "(no such page)" : string.Join(", ", names))}");
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. A cursor that survives COMMIT (WITH HOLD)");
{
    int ReadAcrossCommit(bool hold)
    {
        int read = 0;
        using var tx = cn.BeginTransaction();
        using var cmd = new IfxCommand("SELECT code FROM state ORDER BY code", cn) { Transaction = tx, HoldCursor = hold };
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            read++;
            if (read == 10) tx.Commit();              // in the middle of the result set
        }
        return read;
    }

    foreach (bool hold in new[] { true, false })
    {
        try
        {
            Ui.Kv($"HoldCursor = {hold}", $"read all {ReadAcrossCommit(hold)} rows across the commit");
        }
        catch (Exception e)
        {
            Ui.Kv($"HoldCursor = {hold}", $"{e.GetType().Name}: {Ui.FirstLine(e.Message)}");
        }
    }
}

Ui.Line();
Ui.Line("Done.");
