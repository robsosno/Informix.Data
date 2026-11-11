// ReadOnlyAccess: two independent ways to keep a connection from writing.
//
//   * A read-only TRANSACTION (SET TRANSACTION READ ONLY): the server refuses every write inside
//     it with SQLCODE -878. Asked per transaction - BeginTransaction(isolation, isReadOnly) - or
//     for every transaction of a connection with the connection-string keyword Read Only=true.
//     Statements outside a transaction (autocommit) are not covered.
//
//   * A SELECT-ONLY connection (Select Only=true): the driver itself accepts only statements the
//     server describes as SELECT and rejects the rest with -878 - after the statement has been
//     prepared and described, before it is executed. It covers autocommit too, but is a guard
//     against mistakes, not a security boundary.
//
// None of the statements here changes data: the writes match no row.

using System.Data;
using Demo;
using Informix.Data;

Ui.Title("ReadOnlyAccess");

const string Select = "SELECT COUNT(*) FROM items";
const string Write = "UPDATE items SET quantity = quantity WHERE order_num = -1";     // matches nothing

string Run(IfxConnection cn, string sql, IfxTransaction? tx)
{
    try
    {
        using var cmd = new IfxCommand(sql, cn) { Transaction = tx };
        if (sql.StartsWith("SELECT", StringComparison.Ordinal))
            cmd.ExecuteScalar();
        else
            cmd.ExecuteNonQuery();
        return "ok";
    }
    catch (IfxException e)
    {
        return $"error {e.IfxSqlCode}";
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Which statements get through");

var modes = new (string Name, string Keywords, Func<IfxConnection, IfxTransaction> Begin)[]
{
    ("plain connection", "", c => c.BeginTransaction()),
    ("BeginTransaction(isReadOnly: true)", "", c => c.BeginTransaction(IsolationLevel.ReadCommitted, true)),
    ("Read Only=true, BeginTransaction()", "Read Only=true", c => c.BeginTransaction()),
    ("Read Only=true, isReadOnly: false", "Read Only=true", c => c.BeginTransaction(IsolationLevel.ReadCommitted, false)),
    ("Select Only=true", "Select Only=true", c => c.BeginTransaction()),
};

var rows = new List<object?[]>();
foreach (var (name, keyword, begin) in modes)
{
    string cs = keyword.Length == 0
        ? DemoEnv.ConnectionString()
        : DemoEnv.ConnectionString(null, (keyword.Split('=')[0], keyword.Split('=')[1]));
    using var cn = new IfxConnection(cs);
    cn.Open();

    string selectAuto = Run(cn, Select, null);
    string writeAuto = Run(cn, Write, null);
    using var tx = begin(cn);
    string selectTx = Run(cn, Select, tx);
    string writeTx = Run(cn, Write, tx);
    tx.Rollback();
    rows.Add([name, selectAuto, writeAuto, selectTx, writeTx]);
}
Ui.Table(["connection", "SELECT", "write", "SELECT in tx", "write in tx"], rows);
Ui.Note("-878 = the write was refused. A read-only transaction stops writes inside it; Select Only stops them everywhere");

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Where the refusal happens");
{
    using var selectOnly = new IfxConnection(DemoEnv.ConnectionString(null, ("Select Only", "true")));
    selectOnly.Open();
    selectOnly.ResetFetchStats();
    Run(selectOnly, Write, null);
    Ui.Kv("Select Only: rejected write", $"{selectOnly.RequestRoundTrips} round trips (prepare + describe; never executed)");

    using var plain = new IfxConnection(DemoEnv.ConnectionString());
    plain.Open();
    using var tx = plain.BeginTransaction(IsolationLevel.ReadCommitted, true);
    plain.ResetFetchStats();
    Run(plain, Write, tx);
    Ui.Kv("read-only tx: rejected write", $"{plain.RequestRoundTrips} round trip (the server refused the statement)");
    tx.Rollback();
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. What a Select-Only connection accepts (the server describes each statement)");
{
    using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("Select Only", "true")));
    cn.Open();
    var statements = new (string What, string Sql)[]
    {
        ("SELECT", "SELECT COUNT(*) FROM state"),
        ("SELECT with a parameter-free join", "SELECT COUNT(*) FROM orders o, items i WHERE o.order_num = i.order_num"),
        ("EXECUTE FUNCTION (a reading routine)", "EXECUTE FUNCTION stock_by_manufacturer('HRO')"),
        ("UPDATE", "UPDATE items SET quantity = quantity WHERE order_num = -1"),
        ("DELETE", "DELETE FROM items WHERE order_num = -1"),
        ("INSERT", "INSERT INTO log_record SELECT * FROM log_record WHERE 1 = 0"),
        ("CREATE TEMP TABLE", "CREATE TEMP TABLE t_x (a INT) WITH NO LOG"),
    };
    var result = new List<object?[]>();
    foreach (var (what, sql) in statements)
    {
        string outcome, detail = "";
        try
        {
            using var cmd = new IfxCommand(sql, cn);
            if (sql.StartsWith("SELECT", StringComparison.Ordinal) || sql.StartsWith("EXECUTE FUNCTION", StringComparison.Ordinal))
            {
                using var r = cmd.ExecuteReader();
                while (r.Read()) { }
            }
            else cmd.ExecuteNonQuery();
            outcome = "ok";
        }
        catch (IfxException e)
        {
            outcome = $"error {e.IfxSqlCode}";
            if (e.IfxSqlCode != -878) detail = Ui.FirstLine(e.Message);
        }
        catch (Exception e) { outcome = e.GetType().Name; detail = Ui.FirstLine(e.Message); }
        result.Add([what, outcome, detail]);
    }
    Ui.Table(["statement", "result", "detail"], result);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. A read-only transaction with SNAPSHOT isolation");
{
    using var cn = new IfxConnection(DemoEnv.ConnectionString());
    cn.Open();
    using var tx = cn.BeginTransaction(IsolationLevel.Snapshot, true);
    Ui.Kv("IsReadOnly / IsolationLevel", $"{tx.IsReadOnly} / {tx.IsolationLevel}");
    Ui.Kv("SELECT", Run(cn, Select, tx));
    Ui.Kv("write", Run(cn, Write, tx));
    tx.Rollback();
    Ui.Note("the isolation level is set first, then SET TRANSACTION READ ONLY");
}

Ui.Line();
Ui.Line("Done.");
