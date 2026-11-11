// GenericBatch: DbBatch - several commands in one call.
//
// DbBatch (System.Data.Common, .NET 6+) sends a list of commands together; whether that is one
// round trip depends on the provider. This is the provider-independent counterpart of the
// native bulk APIs (IfxRowBatch, IfxBulkLoader - see 02-native/Load), which are faster but
// specific to Informix.Data.
//
// Shows: a DbBatch of inserts with parameters in a transaction, the same rows inserted one
// command at a time, a batch of SELECTs read as consecutive result sets, and the factory's
// CanCreateBatch flag.

using System.Data;
using System.Data.Common;
using Demo;

Ui.Title("GenericBatch");

DbProviderFactory factory = Bootstrap.Factory();
Ui.Kv("factory.CanCreateBatch", factory.CanCreateBatch);

using DbConnection cn = Bootstrap.Open();
cn.Exec("CREATE TEMP TABLE t_items (id INTEGER, name VARCHAR(20), price DECIMAL(10,2))");

const int N = 2000;

// ---------------------------------------------------------------------------------------------
Ui.Section($"1. {N:N0} INSERTs, one command each");
TimeSpan single;
{
    using DbTransaction tx = cn.BeginTransaction();
    using DbCommand cmd = cn.Command("INSERT INTO t_items VALUES (@id, @name, @price)", tx);
    DbParameter id = cmd.Add("@id", 0, DbType.Int32);
    DbParameter name = cmd.Add("@name", "", DbType.String);
    DbParameter price = cmd.Add("@price", 0m, DbType.Decimal);
    cmd.Prepare();
    single = Ui.Time(() =>
    {
        for (int i = 1; i <= N; i++)
        {
            id.Value = i; name.Value = "item " + i; price.Value = i * 0.5m;
            cmd.ExecuteNonQuery();
        }
    });
    tx.Commit();
    Ui.Kv("time", $"{Ui.Ms(single)}  ({N / single.TotalSeconds:N0} rows/s)");
}

// ---------------------------------------------------------------------------------------------
Ui.Section($"2. The same, as DbBatch commands (200 per batch)");
{
    cn.Exec("DELETE FROM t_items");
    using DbTransaction tx = cn.BeginTransaction();
    var time = Ui.Time(() =>
    {
        for (int first = 1; first <= N; first += 200)
        {
            using DbBatch batch = cn.CreateBatch();
            batch.Transaction = tx;
            for (int i = first; i < first + 200 && i <= N; i++)
            {
                DbBatchCommand bc = batch.CreateBatchCommand();
                bc.CommandText = "INSERT INTO t_items VALUES (@id, @name, @price)";
                foreach (var (n, v, t) in new (string, object, DbType)[] { ("@id", i, DbType.Int32), ("@name", "item " + i, DbType.String), ("@price", i * 0.5m, DbType.Decimal) })
                {
                    DbParameter p = bc.CreateParameter();
                    p.ParameterName = n; p.Value = v; p.DbType = t;
                    bc.Parameters.Add(p);
                }
                batch.BatchCommands.Add(bc);
            }
            batch.ExecuteNonQuery();
        }
    });
    tx.Commit();
    Ui.Kv("time", $"{Ui.Ms(time)}  ({N / time.TotalSeconds:N0} rows/s, {single.TotalMilliseconds / time.TotalMilliseconds:N1}x the speed of single commands)");
    Ui.Kv("rows in the table", cn.Count("SELECT COUNT(*) FROM t_items"));
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. A batch of SELECTs: one result set per command");
{
    using DbBatch batch = cn.CreateBatch();
    foreach (string sql in new[]
    {
        "SELECT COUNT(*) FROM state",
        "SELECT COUNT(*) FROM manufact",
        "SELECT MAX(price) FROM t_items",
    })
    {
        DbBatchCommand bc = batch.CreateBatchCommand();
        bc.CommandText = sql;
        batch.BatchCommands.Add(bc);
    }
    try
    {
        using DbDataReader r = batch.ExecuteReader();
        var results = new List<object?>();
        do
        {
            while (r.Read()) results.Add(r.GetValue(0));
        } while (r.NextResult());
        Ui.Kv("results (state, manufact, max price)", results);
    }
    catch (Exception e)
    {
        Ui.Kv("batch ExecuteReader", $"{e.GetType().Name}: {Ui.FirstLine(e.Message)}");
    }
}

Ui.Line();
Ui.Line("Done.");
