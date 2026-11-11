// GenericTransactions: DbTransaction, savepoints, TransactionScope, timeouts and errors -
// all through System.Data.Common.
//
// Shows: DbConnection.BeginTransaction(IsolationLevel), DbTransaction.Save / Rollback(name) /
// Release (SupportsSavepoints), System.Transactions, retrying on DbException.IsTransient,
// reading the error through DbException (ErrorCode, SqlState), DbCommand.CommandTimeout and
// DbCommand.Cancel.

using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using Demo;
using IsolationLevel = System.Data.IsolationLevel;

Ui.Title("GenericTransactions");

using DbConnection cn = Bootstrap.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Commit, rollback and the isolation level");
{
    cn.Exec("CREATE TEMP TABLE t_account (id INT, balance DECIMAL(10,2))");
    cn.Exec("INSERT INTO t_account VALUES (1, 100)");

    foreach (var level in new[] { IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
    {
        using DbTransaction tx = cn.BeginTransaction(level);
        cn.Exec("UPDATE t_account SET balance = balance + 10", tx);
        Ui.Kv($"{level}", $"balance in the transaction {cn.Scalar("SELECT balance FROM t_account", tx)}, IsolationLevel = {tx.IsolationLevel}");
        tx.Rollback();
    }
    Ui.Kv("after three rollbacks", cn.Scalar("SELECT balance FROM t_account"));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Savepoints");
{
    cn.Exec("CREATE TEMP TABLE t_steps (n INT)");
    using DbTransaction tx = cn.BeginTransaction();
    Ui.Kv("SupportsSavepoints", tx.SupportsSavepoints);
    cn.Exec("INSERT INTO t_steps VALUES (1)", tx);
    tx.Save("one");
    cn.Exec("INSERT INTO t_steps VALUES (2)", tx);
    cn.Exec("INSERT INTO t_steps VALUES (3)", tx);
    tx.Rollback("one");
    cn.Exec("INSERT INTO t_steps VALUES (4)", tx);
    tx.Commit();
    Ui.Kv("rows kept", string.Join(", ", cn.Rows("SELECT n FROM t_steps ORDER BY n").Select(r => r[0])));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. TransactionScope: the connection joins it when it is opened inside");
{
    try { cn.Exec("DROP TABLE demo_tx"); } catch (DbException) { }
    cn.Exec("CREATE TABLE demo_tx (id INT)");
    using (var scope = new TransactionScope())
    {
        using DbConnection inner = Bootstrap.Open();
        inner.Exec("INSERT INTO demo_tx VALUES (1)");
        scope.Complete();
    }
    using (var scope = new TransactionScope())
    {
        using DbConnection inner = Bootstrap.Open();
        inner.Exec("INSERT INTO demo_tx VALUES (2)");
    }
    Ui.Kv("rows after a completed and an abandoned scope", string.Join(", ", cn.Rows("SELECT id FROM demo_tx").Select(r => r[0])));
    cn.Exec("DROP TABLE demo_tx");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Errors through DbException, and a retry on IsTransient");
{
    try { cn.Exec("SELECT * FROM no_such_table"); }
    catch (DbException e)
    {
        Ui.Kv("ErrorCode / SqlState / IsTransient", $"{e.ErrorCode} / {e.SqlState ?? "NULL"} / {e.IsTransient}");
    }

    const string Touch = "UPDATE items SET quantity = quantity WHERE order_num = 1001 AND item_num = 1";
    using DbConnection holder = Bootstrap.Open();
    DbTransaction hold = holder.BeginTransaction();
    holder.Exec(Touch, hold);
    var release = Task.Run(async () => { await Task.Delay(600); hold.Rollback(); });

    int attempt = 0;
    var sw = Stopwatch.StartNew();
    while (true)
    {
        attempt++;
        try
        {
            using DbConnection other = Bootstrap.Open();
            using DbTransaction otx = other.BeginTransaction();
            other.Exec(Touch, otx);
            otx.Rollback();                                    // the demo changes nothing for good
            Ui.Kv("update", $"went through on attempt {attempt} after {Ui.Ms(sw.Elapsed)}");
            break;
        }
        catch (DbException e) when (e.IsTransient && attempt < 10)
        {
            Thread.Sleep(200);
        }
    }
    release.Wait();
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. CommandTimeout and Cancel");
{
    // A cross join of the catalog with itself keeps the server busy for a long time.
    const string Slow = "SELECT COUNT(*) FROM systables a, systables b, systables c, systables d";

    using (DbCommand cmd = cn.Command(Slow))
    {
        cmd.CommandTimeout = 2;
        var sw = Stopwatch.StartNew();
        try { cmd.ExecuteScalar(); Ui.Kv("CommandTimeout = 2", "finished"); }
        catch (DbException e) { Ui.Kv("CommandTimeout = 2", $"stopped after {Ui.Ms(sw.Elapsed)}: error {e.ErrorCode}"); }
    }

    using (DbCommand cmd = cn.Command(Slow))
    {
        cmd.CommandTimeout = 0;
        var canceller = Task.Run(async () => { await Task.Delay(1000); cmd.Cancel(); });
        var sw = Stopwatch.StartNew();
        try { cmd.ExecuteScalar(); Ui.Kv("Cancel() after 1 s", "finished"); }
        catch (DbException e) { Ui.Kv("Cancel() after 1 s", $"stopped after {Ui.Ms(sw.Elapsed)}: error {e.ErrorCode}"); }
        canceller.Wait();
    }
    Ui.Kv("the connection afterwards", cn.Scalar("SELECT COUNT(*) FROM state"));
}

Ui.Line();
Ui.Line("Done.");
