// ConnectionPooling: what the connection-string pool keywords do, and what IfxDataSource adds.
//
// Shows: Pooling on/off, the physical session behind a connection (DBINFO('sessionid')),
// Min/Max Pool Size, Connection Lifetime, Login Timeout when the pool is exhausted,
// IfxConnection.ClearPool, the idle/active counters, and IfxDataSource with its own pool,
// Statistics and a connection-initialization statement.
//
// (The same pool seen through DbProviderFactory is in 03-abstraction/ProviderFactory.)

using System.Data;
using System.Diagnostics;
using Demo;
using Informix.Data;

Ui.Title("ConnectionPooling");

// Connections whose strings differ share nothing, so each scenario varies Max Pool Size a little
// to get a pool of its own.
string Cs(int poolId, params (string Key, string Value)[] extra) =>
    DemoEnv.ConnectionString(null, [("Max Pool Size", (20 + poolId).ToString()), .. extra]);

long SessionId(IfxConnection cn)
{
    using var cmd = new IfxCommand("SELECT DBINFO('sessionid') FROM systables WHERE tabid = 1", cn);
    return Convert.ToInt64(cmd.ExecuteScalar());
}

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Pooling=false against Pooling=true: open + close, 15 times");
{
    var rows = new List<object?[]>();
    foreach (bool pooling in new[] { false, true })
    {
        string cs = Cs(pooling ? 1 : 2, ("Pooling", pooling.ToString().ToLowerInvariant()));
        var sessions = new HashSet<long>();
        var time = Ui.Time(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                using var cn = new IfxConnection(cs);
                cn.Open();
                sessions.Add(SessionId(cn));
            }
        });
        rows.Add([pooling, Ui.Ms(time), $"{time.TotalMilliseconds / 15:N1} ms", sessions.Count]);
    }
    Ui.Table(["Pooling", "15 x open+close", "per connection", "physical sessions used"], rows);
    Ui.Note("with the pool, Close() gives the session back and the next Open() gets the same one");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Counters and Min Pool Size");
{
    string cs = Cs(3, ("Min Pool Size", "2"));
    var a = new IfxConnection(cs);
    string Counts() => $"idle {a.GetIdleConnectionsCount}, active {a.GetActiveConnectionsCount}";

    a.Open();
    Ui.Kv("one open", Counts());
    var b = new IfxConnection(cs); b.Open();
    var c = new IfxConnection(cs); c.Open();
    Ui.Kv("three open", Counts());
    b.Dispose(); c.Dispose();
    Ui.Kv("two closed", Counts());
    a.Dispose();
    Ui.Kv("all closed", Counts());
    using var again = new IfxConnection(cs);
    again.Open();
    IfxConnection.ClearPool(again);
    Ui.Kv("after ClearPool", $"idle {again.GetIdleConnectionsCount}, active {again.GetActiveConnectionsCount}");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. Max Pool Size=2, Login Timeout=3: the third connection has to wait");
{
    string cs = Cs(4, ("Max Pool Size", "2"), ("Login Timeout", "3"));
    using var a = new IfxConnection(cs); a.Open();
    var b = new IfxConnection(cs); b.Open();

    var sw = Stopwatch.StartNew();
    try
    {
        using var c = new IfxConnection(cs);
        c.Open();
        Ui.Kv("third Open()", "succeeded");
    }
    catch (Exception e)
    {
        Ui.Kv("third Open()", $"{e.GetType().Name} after {Ui.Ms(sw.Elapsed)}");
        Ui.Kv("   message", Ui.FirstLine(e.Message));
    }

    b.Dispose();
    sw.Restart();
    using (var c = new IfxConnection(cs))
    {
        c.Open();
        Ui.Kv("after one Close()", $"third Open() took {Ui.Ms(sw.Elapsed)}");
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Connection Lifetime=1: a pooled session is retired after a second");
{
    string cs = Cs(5, ("Connection Lifetime", "1"));
    long first, second, third;
    using (var cn = new IfxConnection(cs)) { cn.Open(); first = SessionId(cn); }
    using (var cn = new IfxConnection(cs)) { cn.Open(); second = SessionId(cn); }
    Thread.Sleep(2000);
    using (var cn = new IfxConnection(cs)) { cn.Open(); third = SessionId(cn); }
    Ui.Kv("session ids", $"{first}, {second} (at once), {third} (after 2 s)");
    Ui.Kv("reused / retired", $"{(first == second ? "reused" : "NEW")} / {(second == third ? "reused" : "retired, new session")}");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. ClearPool throws the idle sessions away");
{
    string cs = Cs(6);
    long before, after;
    using (var cn = new IfxConnection(cs)) { cn.Open(); before = SessionId(cn); }
    using (var cn = new IfxConnection(cs)) { cn.Open(); IfxConnection.ClearPool(cn); }
    using (var cn = new IfxConnection(cs)) { cn.Open(); after = SessionId(cn); }
    Ui.Kv("session before / after", $"{before} / {after}  ({(before == after ? "same" : "new session")})");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("6. IfxDataSource: a pool of its own");
{
    string cs = Cs(7);
    using var ds1 = IfxDataSource.Create(cs);
    using var ds2 = IfxDataSource.Create(cs);

    long s1, s2;
    using (var cn = ds1.OpenConnection()) s1 = SessionId(cn);
    using (var cn = ds2.OpenConnection()) s2 = SessionId(cn);
    Ui.Kv("same string, two data sources", $"sessions {s1} and {s2} - separate pools");

    using (var a = ds1.OpenConnection())
    using (var b = ds1.OpenConnection())
    {
        var (total, idle, busy) = ds1.Statistics;
        Ui.Kv("ds1 with two open", $"total {total}, idle {idle}, busy {busy}");
    }
    var (t, i, bsy) = ds1.Statistics;
    Ui.Kv("ds1 after Close", $"total {t}, idle {i}, busy {bsy}");
    ds1.Clear();
    Ui.Kv("ds1 after Clear()", $"total {ds1.Statistics.Total}");
}

Ui.Section("7. A data source can run a statement on every new physical connection");
{
    // The statement sets how long a lock request waits. Measure it: one connection holds a row
    // lock, a second one asks for the same row.
    string cs = Cs(8);
    const string Touch = "UPDATE items SET quantity = quantity WHERE order_num = 1001 AND item_num = 1";

    TimeSpan Contend(IfxConnection waiter)
    {
        using var holder = new IfxConnection(cs);
        holder.Open();
        using var tx = holder.BeginTransaction();
        new IfxCommand(Touch, holder) { Transaction = tx }.ExecuteNonQuery();

        var sw = Stopwatch.StartNew();
        try { new IfxCommand(Touch, waiter).ExecuteNonQuery(); }
        catch (IfxException e) { Ui.Note($"waiter got SQLCODE {e.IfxSqlCode}"); }
        sw.Stop();
        tx.Rollback();
        return sw.Elapsed;
    }

    using (var plain = new IfxConnection(cs))
    {
        plain.Open();
        Ui.Kv("plain connection", $"gave up after {Ui.Ms(Contend(plain))}");
    }

    using var ds = new IfxDataSourceBuilder(cs).UseConnectionInitializationSql("SET LOCK MODE TO WAIT 2").Build();
    using (var waiting = ds.OpenConnection())
        Ui.Kv("with SET LOCK MODE TO WAIT 2", $"gave up after {Ui.Ms(Contend(waiting))}");
}

IfxConnection.ClearAllPools();
Ui.Line();
Ui.Line("Done.");
