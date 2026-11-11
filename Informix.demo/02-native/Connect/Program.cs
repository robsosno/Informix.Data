// Connect: opening a connection and looking at what the driver learned about the server.
//
// Shows: IfxConnectionStringBuilder, what Open() reports (server version and type, capabilities),
// ChangeDatabase, the session-environment keywords (DB Date, Delimited
// Identifiers) and what they change on the server, and an SSL connection attempt.
// Authentication here is whatever the connection string says (PAM in the test setup); the
// protocol is spoken natively - no CSDK, ODBC driver or DSN is involved.

using System.Data;
using Demo;
using Informix.Data;

Ui.Title("Connect");

string cs = DemoEnv.ConnectionString();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. The connection string, through the builder");
{
    var b = new IfxConnectionStringBuilder(cs);
    Ui.Kv("Host / Service", $"{b.Host} / {b.Service}");
    Ui.Kv("Server (INFORMIXSERVER)", b.Server);
    Ui.Kv("Database", b.Database);
    Ui.Kv("User ID", b.UserId);
    Ui.Kv("Authentication", b["Authentication"]);
    Ui.Kv("Pooling", b.Pooling);
    Ui.Note("the password is not shown; the builder reads and writes every keyword, including the aliases of the ODBC ones");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. Opening it");
{
    using var cn = new IfxConnection(cs);
    Ui.Kv("State before Open()", cn.State);
    var time = Ui.Time(cn.Open);
    Ui.Kv("Open()", Ui.Ms(time));
    Ui.Kv("State", cn.State);
    Ui.Kv("ServerVersion", cn.ServerVersion);
    Ui.Kv("ServerType", cn.ServerType);
    Ui.Kv("DataSource", cn.DataSource);
    Ui.Kv("Database", cn.Database);
    Ui.Kv("named parameters supported", cn.ServerCapabilities?.IsNamedParametersSupported);

    using (var cmd = new IfxCommand(
        "SELECT DBINFO('sessionid'), DBINFO('dbname'), USER, CURRENT YEAR TO SECOND FROM systables WHERE tabid = 1", cn))
    using (var r = cmd.ExecuteReader())
    {
        r.Read();
        Ui.Kv("session id / database / user", $"{r.GetValue(0)} / {r.GetString(1).Trim()} / {r.GetString(2).Trim()}");
        Ui.Kv("server clock", Ui.Show(r.GetValue(3)));
    }

    cn.Close();
    Ui.Kv("State after Close()", cn.State);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. ChangeDatabase");
{
    using var cn = new IfxConnection(cs);
    cn.Open();
    string Name() => new IfxCommand("SELECT DBINFO('dbname') FROM systables WHERE tabid = 1", cn).ExecuteScalar()!.ToString()!.Trim();
    Ui.Kv("before", Name());
    cn.ChangeDatabase("sysmaster");
    Ui.Kv("after ChangeDatabase(\"sysmaster\")", Name());
    Ui.Note("the server will not switch databases on an open session, so the driver reconnects");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Session keywords change what the SERVER does with text");
{
    var rows = new List<object?[]>();
    foreach (string dbDate in new[] { "MDY4/", "DMY4.", "Y4MD-" })
    {
        using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("DB Date", dbDate)));
        cn.Open();
        using var cmd = new IfxCommand("SELECT order_date::LVARCHAR FROM orders WHERE order_num = 1001", cn);
        using var r = cmd.ExecuteReader();
        r.Read();
        rows.Add([dbDate, r.GetString(0), cn.TextFormat.DbDate]);
    }
    Ui.Table(["DB Date=", "order_date::LVARCHAR", "connection.TextFormat.DbDate"], rows);

    foreach (bool delimited in new[] { false, true })
    {
        using var cn = new IfxConnection(DemoEnv.ConnectionString(null, ("Delimited Identifiers", delimited.ToString().ToLowerInvariant())));
        cn.Open();
        try
        {
            using var cmd = new IfxCommand("SELECT \"state\".\"code\" FROM \"state\" WHERE \"code\" = 'CA'", cn);
            Ui.Kv($"Delimited Identifiers={delimited}", $"\"quoted\" names work: {cmd.ExecuteScalar()}");
        }
        catch (IfxException e)
        {
            Ui.Kv($"Delimited Identifiers={delimited}", $"quoted names rejected, SQLCODE {e.IfxSqlCode}");
        }
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. SSL");
{
    foreach (string server in new[] { new IfxConnectionStringBuilder(cs).Server, "ol_ssl" }.Distinct())
    {
        string ssl = DemoEnv.ConnectionString(null,
            ("Service", "9089"), ("Server", server), ("SSL Connection", "True"), ("Trust Server Certificate", "True"), ("Login Timeout", "5"), ("Pooling", "false"));
        try
        {
            using var cn = new IfxConnection(ssl);
            cn.Open();
            Ui.Kv($"port 9089, Server={server}", $"connected over SSL, {cn.ServerVersion}");
            return;
        }
        catch (Exception e)
        {
            Ui.Kv($"port 9089, Server={server}", $"{e.GetType().Name}: {Ui.FirstLine(e.Message)}");
        }
    }
    Ui.Note("no SSL listener on this server answered; the keywords are SSL Connection and Trust Server Certificate");
}

Ui.Line();
Ui.Line("Done.");
