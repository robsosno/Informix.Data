// Transactions: commit and rollback, isolation, savepoints and TransactionScope.
//
// Shows: IfxTransaction (Commit / Rollback), a trigger's work being rolled back with the
// statement that fired it, what two connections see of each other under READ UNCOMMITTED and
// READ COMMITTED, savepoints (Save / Rollback(name) / Release) and System.Transactions
// (a connection opened inside a TransactionScope joins it; no MSDTC, one connection only).
//
// Changes are made in temporary or scratch tables, or rolled back, so the sample data is not
// touched.

using System.Data;
using System.Transactions;
using IsolationLevel = System.Data.IsolationLevel;
using Demo;
using Informix.Data;

Ui.Title("Transactions");

string cs = DemoEnv.ConnectionString();
using var cn = new IfxConnection(cs);
cn.Open();

long Scalar(IfxConnection c, string sql, IfxTransaction? tx = null)
{
    using var cmd = new IfxCommand(sql, c) { Transaction = tx };
    return Convert.ToInt64(cmd.ExecuteScalar());
}

void Exec(IfxConnection c, string sql, IfxTransaction? tx = null)
{
    using var cmd = new IfxCommand(sql, c) { Transaction = tx };
    cmd.ExecuteNonQuery();
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Commit and rollback: a transfer between two accounts");
{
    Exec(cn, "CREATE TEMP TABLE t_account (id INT, balance DECIMAL(10,2))");
    Exec(cn, "INSERT INTO t_account VALUES (1, 100.00)");
    Exec(cn, "INSERT INTO t_account VALUES (2, 50.00)");

    string Balances() =>
        string.Join(", ", Enumerable.Range(1, 2).Select(id => $"#{id}: {Scalar(cn, $"SELECT balance FROM t_account WHERE id = {id}")}"));

    void Transfer(decimal amount, bool failHalfway)
    {
        using var tx = cn.BeginTransaction();
        try
        {
            Exec(cn, $"UPDATE t_account SET balance = balance - {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} WHERE id = 1", tx);
            if (failHalfway) throw new InvalidOperationException("the second update never happens");
            Exec(cn, $"UPDATE t_account SET balance = balance + {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} WHERE id = 2", tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    Ui.Kv("start", Balances());
    Transfer(30m, failHalfway: false);
    Ui.Kv("after a transfer of 30", Balances());
    try { Transfer(20m, failHalfway: true); }
    catch (InvalidOperationException e) { Ui.Kv("a transfer that fails", e.Message); }
    Ui.Kv("after the failed one", Balances());
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. A trigger's work is part of the transaction");
{
    long Log() => Scalar(cn, "SELECT COUNT(*) FROM log_record");

    Ui.Kv("log_record rows", Log());
    using var tx = cn.BeginTransaction();
    Exec(cn, "UPDATE items SET quantity = quantity + 1 WHERE order_num = 1001 AND item_num = 1", tx);
    Ui.Kv("after UPDATE items.quantity", $"{Scalar(cn, "SELECT COUNT(*) FROM log_record", tx)} (trigger upqty_i logged it)");
    tx.Rollback();
    Ui.Kv("after Rollback()", Log());
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. Two connections: what one sees of the other's uncommitted change");
{
    const string Read = "SELECT quantity FROM items WHERE order_num = 1001 AND item_num = 1";
    using var writer = new IfxConnection(cs);
    writer.Open();
    using var wtx = writer.BeginTransaction();
    Exec(writer, "UPDATE items SET quantity = quantity + 100 WHERE order_num = 1001 AND item_num = 1", wtx);
    Ui.Kv("writer, inside its transaction", Scalar(writer, Read, wtx));

    foreach (var level in new[] { IsolationLevel.ReadUncommitted, IsolationLevel.ReadCommitted })
    {
        using var reader = new IfxConnection(cs);
        reader.Open();
        using var rtx = reader.BeginTransaction(level);
        string outcome;
        try { outcome = Scalar(reader, Read, rtx).ToString(); }
        catch (IfxException e) { outcome = $"error {e.IfxSqlCode} (the row is locked)"; }
        Ui.Kv($"reader, {level}", outcome);
        rtx.Rollback();
    }

    wtx.Rollback();
    using var after = new IfxConnection(cs);
    after.Open();
    Ui.Kv("after the writer rolls back", Scalar(after, Read));
    Ui.Note("READ UNCOMMITTED sees the change that never happened; READ COMMITTED refuses to wait (SET LOCK MODE TO WAIT would)");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Savepoints");
{
    Exec(cn, "CREATE TEMP TABLE t_steps (n INT)");
    using var tx = cn.BeginTransaction();
    Exec(cn, "INSERT INTO t_steps VALUES (1)", tx);
    tx.Save("after_one");
    Exec(cn, "INSERT INTO t_steps VALUES (2)", tx);
    Exec(cn, "INSERT INTO t_steps VALUES (3)", tx);
    Ui.Kv("rows before the partial rollback", Scalar(cn, "SELECT COUNT(*) FROM t_steps", tx));
    tx.Rollback("after_one");
    Ui.Kv("after Rollback(\"after_one\")", Scalar(cn, "SELECT COUNT(*) FROM t_steps", tx));
    Exec(cn, "INSERT INTO t_steps VALUES (4)", tx);
    tx.Release("after_one");
    tx.Commit();

    using var cmd = new IfxCommand("SELECT n FROM t_steps ORDER BY n", cn);
    using var r = cmd.ExecuteReader();
    var kept = new List<object?>();
    while (r.Read()) kept.Add(r.GetInt32(0));
    Ui.Kv("rows kept", kept);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. TransactionScope");
{
    try { Exec(cn, "DROP TABLE demo_tx"); } catch (IfxException) { }
    Exec(cn, "CREATE TABLE demo_tx (id INT, note VARCHAR(30))");

    using (var scope = new TransactionScope())
    {
        using var inner = new IfxConnection(cs);      // opened inside the scope: it joins the transaction
        inner.Open();
        Exec(inner, "INSERT INTO demo_tx VALUES (1, 'scope completed')");
        scope.Complete();
    }
    using (var scope = new TransactionScope())
    {
        using var inner = new IfxConnection(cs);
        inner.Open();
        Exec(inner, "INSERT INTO demo_tx VALUES (2, 'scope abandoned')");
        // no scope.Complete(): leaving the block rolls it back
    }

    using var cmd = new IfxCommand("SELECT id, note FROM demo_tx ORDER BY id", cn);
    using var r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read()) rows.Add([r.GetInt32(0), r.GetString(1)]);
    Ui.Table(["id", "note"], rows);
    Ui.Note("only the completed scope's row is there");
    r.Close();
    Exec(cn, "DROP TABLE demo_tx");
}

Ui.Line();
Ui.Line("Done.");
