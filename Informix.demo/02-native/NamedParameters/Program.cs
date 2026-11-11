// NamedParameters: @name and :name placeholders next to the classic positional '?'.
//
// Shows: both sigils, one value used several times, parameters in any order, matching without
// the sigil, placeholders inside string literals left alone, the errors for a missing value, and
// how a prepared statement is reused across executions (prepared-statement cache, round trips).

using Demo;
using Informix.Data;

Ui.Title("NamedParameters");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

long Count(IfxCommand cmd) => Convert.ToInt64(cmd.ExecuteScalar());

// ---------------------------------------------------------------------------------------------
Ui.Section("1. @name and :name");
{
    using var a = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = @manu AND unit_price > @min", cn);
    a.Parameters.Add(new IfxParameter("@manu", "HRO"));
    a.Parameters.Add(new IfxParameter("@min", 100m));
    Ui.Kv("@manu / @min", Count(a));

    using var b = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = :manu AND unit_price > :min", cn);
    b.Parameters.Add(new IfxParameter(":manu", "HRO"));
    b.Parameters.Add(new IfxParameter(":min", 100m));
    Ui.Kv(":manu / :min", Count(b));
}

Ui.Section("2. One name, several uses: the value is supplied once");
{
    using var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE unit_price >= @limit AND qty_on_hand >= @limit", cn);
    cmd.Parameters.Add(new IfxParameter("@limit", 150));
    Ui.Kv("price >= 150 AND qty >= 150", Count(cmd));
    Ui.Note("with '?' the same value would have to be added twice");
}

Ui.Section("3. The order of the parameters does not matter");
{
    using var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = @manu AND unit_price > @min", cn);
    cmd.Parameters.Add(new IfxParameter("@min", 100m));          // not the order they appear in the text
    cmd.Parameters.Add(new IfxParameter("@manu", "HRO"));
    Ui.Kv("@min added first", Count(cmd));
}

Ui.Section("4. Binding ignores the sigil and the case: Parameters has \"manu\", the text says @MANU");
{
    using var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = @MANU", cn);
    cmd.Parameters.Add(new IfxParameter("manu", "HRO"));
    Ui.Kv("Parameters[\"manu\"] for @MANU", Count(cmd));
    Ui.Kv("collection lookup", cmd.Parameters["manu"].ParameterName);
}

Ui.Section("5. Placeholders inside a string literal are text");
{
    using var cmd = new IfxCommand("SELECT '@manu is not a parameter here', manu_code FROM manufact WHERE manu_code = @manu", cn);
    cmd.Parameters.Add(new IfxParameter("@manu", "SMT"));
    using var r = cmd.ExecuteReader();
    r.Read();
    Ui.Kv("result", $"{r.GetString(0)} / {r.GetString(1)}");
}

Ui.Section("6. Mistakes");
{
    Ui.ExpectError("no value for @manu", () =>
    {
        using var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = @manu", cn);
        cmd.ExecuteScalar();
    });
    Ui.ExpectError("a value nothing refers to", () =>
    {
        using var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = 'HRO'", cn);
        cmd.Parameters.Add(new IfxParameter("@unused", 1));
        cmd.ExecuteScalar();
    });
    Ui.ExpectError("named and '?' in one statement", () =>
    {
        using var cmd = new IfxCommand("SELECT COUNT(*) FROM stock WHERE manu_code = @manu AND unit_price > ?", cn);
        cmd.Parameters.Add(new IfxParameter("@manu", "HRO"));
        cmd.Parameters.Add(new IfxParameter("p", 100m));
        cmd.ExecuteScalar();
    });
}

// ---------------------------------------------------------------------------------------------
Ui.Section("7. Re-executing with new values: round trips, Prepare() and the statement cache");
{
    var manufacturers = new[] { "ANZ", "HRO", "HSK", "KAR", "NKL", "PRC", "SHM", "SMT" };
    const string Sql = "SELECT COUNT(*) FROM stock WHERE manu_code = @manu";
    const int Rounds = 25;

    string Run(IfxConnection c, bool prepare)
    {
        c.ResetFetchStats();
        using var cmd = new IfxCommand(Sql, c);
        var manu = cmd.Parameters.Add(new IfxParameter("@manu", ""));
        if (prepare) cmd.Prepare();
        for (int round = 0; round < Rounds; round++)
            foreach (string code in manufacturers) { manu.Value = code; cmd.ExecuteScalar(); }
        return $"{c.RequestRoundTrips,4} round trips; cache {c.PreparedStatementCacheHits} hits / {c.PreparedStatementCacheMisses} misses";
    }

    Ui.Kv($"{Rounds * manufacturers.Length} runs, plain", Run(cn, prepare: false));
    Ui.Kv("same, after Prepare()", Run(cn, prepare: true));

    using (var cached = new IfxConnection(DemoEnv.ConnectionString(null, ("Prepared Statement Cache Size", "10"))))
    {
        cached.Open();
        Ui.Kv("plain, statement cache on", Run(cached, prepare: false));
        Ui.Note("Prepared Statement Cache Size=10 in the connection string; the default 0 keeps nothing");
    }

    using var held = new IfxCommand(Sql, cn);
    held.Parameters.Add(new IfxParameter("@manu", "HRO"));
    held.Prepare();
    Ui.Kv("IsPrepared after Prepare()", held.IsPrepared);
    held.Unprepare();
    Ui.Kv("IsPrepared after Unprepare()", held.IsPrepared);
}

Ui.Line();
Ui.Line("Done.");
