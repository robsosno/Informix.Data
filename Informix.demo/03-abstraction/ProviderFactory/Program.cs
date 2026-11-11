// ProviderFactory: getting everything from a DbProviderFactory, and the connection pool seen
// through it.
//
// The only line that names the database driver is Bootstrap.Factory() (it registers the factory
// under an invariant name, as a config file or DI setup would, and looks it up again). From
// there on this is System.Data.Common.
//
// Shows: DbProviderFactories, the Create* members and CanCreate* flags, a provider-independent
// connection-string builder, pooling turned on and off through the connection string, and a
// DbDataSource - which the factory makes with its own pool.

using System.Data.Common;
using Demo;

Ui.Title("ProviderFactory");

DbProviderFactory factory = Bootstrap.Factory();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. The factory");
Ui.Kv("factory type", factory.GetType().Name);
Ui.Table(["registered provider", "description", "factory type"],
    DbProviderFactories.GetFactoryClasses().Rows.Cast<System.Data.DataRow>()
        .Select(r => new object?[] { r["InvariantName"], r["Description"], r["AssemblyQualifiedName"]?.ToString()?.Split(',')[0] }));

Ui.Table(["can it create ...", "answer"],
[
    ["a DbDataAdapter", factory.CanCreateDataAdapter],
    ["a DbCommandBuilder", factory.CanCreateCommandBuilder],
    ["a DbBatch", factory.CanCreateBatch],
    ["a DbDataSourceEnumerator", factory.CanCreateDataSourceEnumerator],
]);

// ---------------------------------------------------------------------------------------------
Ui.Section("2. A connection string edited without knowing the driver");
{
    DbConnectionStringBuilder builder = factory.CreateConnectionStringBuilder()!;
    builder.ConnectionString = DemoEnv.ConnectionString();
    builder["Pooling"] = false;
    builder["Max Pool Size"] = 5;
    Ui.Kv("Pooling", builder["Pooling"]);
    Ui.Kv("keys the builder knows", builder.Keys.Count);
    Ui.Kv("Database", builder["Database"]);
}

// ---------------------------------------------------------------------------------------------
long SessionId(DbConnection cn) => cn.Count("SELECT DBINFO('sessionid') FROM systables WHERE tabid = 1");

Ui.Section("3. Pooling off and on, through DbProviderFactory.CreateConnection");
{
    var rows = new List<object?[]>();
    foreach (bool pooling in new[] { false, true })
    {
        string cs = DemoEnv.ConnectionString(null, ("Pooling", pooling.ToString().ToLowerInvariant()), ("Max Pool Size", pooling ? "31" : "32"));
        var sessions = new HashSet<long>();
        var time = Ui.Time(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                using DbConnection cn = factory.CreateConnection()!;
                cn.ConnectionString = cs;
                cn.Open();
                sessions.Add(SessionId(cn));
            }
        });
        rows.Add([pooling, Ui.Ms(time), $"{time.TotalMilliseconds / 15:N1} ms", sessions.Count]);
    }
    Ui.Table(["Pooling", "15 x open+close", "per connection", "physical sessions"], rows);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. DbDataSource: the factory builds one with a pool of its own");
{
    string cs = DemoEnv.ConnectionString(null, ("Max Pool Size", "33"));
    using DbDataSource dataSource = factory.CreateDataSource(cs);
    Ui.Kv("data source type", dataSource.GetType().Name);

    var ids = new List<long>();
    for (int i = 0; i < 3; i++)
    {
        using DbConnection cn = dataSource.OpenConnection();
        ids.Add(SessionId(cn));
    }
    Ui.Kv("session ids, 3 open/close", ids);

    // A command made by the data source takes its own connection from the pool when it runs.
    using DbCommand cmd = dataSource.CreateCommand("SELECT COUNT(*) FROM stock");
    Ui.Kv("a command made by the data source", cmd.ExecuteScalar());
}

// ---------------------------------------------------------------------------------------------
Ui.Section("5. Looking for servers");
{
    if (factory.CanCreateDataSourceEnumerator)
    {
        try
        {
            var servers = factory.CreateDataSourceEnumerator()!.GetDataSources();
            Ui.Kv("GetDataSources()", $"{servers.Rows.Count} row(s)");
        }
        catch (Exception e)
        {
            Ui.Kv("GetDataSources()", $"{e.GetType().Name}: {Ui.FirstLine(e.Message)}");
        }
    }
}

Ui.Line();
Ui.Line("Done.");
