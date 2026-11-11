// GenericSerial: reading the key an INSERT just generated, without provider-specific members.
//
// System.Data.Common has no "last generated key" property, so each database has its own way.
// For Informix it is SELECT DBINFO('sqlca.sqlerrd1') (SERIAL) or DBINFO('serial8') (SERIAL8) on
// the same connection straight after the INSERT. IfxCommand.LastSerial (02-native/GeneratedKeys)
// gets the value from the INSERT's own reply and saves that second statement.
//
// Shows: the DBINFO approach as two commands, as one DbBatch, and the repeated-insert cost.

using System.Data;
using System.Data.Common;
using Demo;

Ui.Title("GenericSerial");

using DbConnection cn = Bootstrap.Open();
cn.Exec("CREATE TEMP TABLE t_serial  (id SERIAL,         name VARCHAR(20)) WITH NO LOG");
cn.Exec("CREATE TEMP TABLE t_serial8 (id SERIAL8(1000),  name VARCHAR(20)) WITH NO LOG");

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Two commands: INSERT, then ask for the key");
{
    long Insert(string table, string name, string dbinfo)
    {
        using (DbCommand ins = cn.Command($"INSERT INTO {table} VALUES (0, @name)"))
        {
            ins.Add("@name", name);
            ins.ExecuteNonQuery();
        }
        return cn.Count($"SELECT DBINFO('{dbinfo}') FROM systables WHERE tabid = 1");
    }

    Ui.Table(["table", "row", "key"],
    [
        ["t_serial", "first", Insert("t_serial", "first", "sqlca.sqlerrd1")],
        ["t_serial", "second", Insert("t_serial", "second", "sqlca.sqlerrd1")],
        ["t_serial8", "first", Insert("t_serial8", "first", "serial8")],
        ["t_serial8", "second", Insert("t_serial8", "second", "serial8")],
    ]);
}

// ---------------------------------------------------------------------------------------------
Ui.Section("2. The same in one DbBatch");
{
    using DbBatch batch = cn.CreateBatch();
    DbBatchCommand ins = batch.CreateBatchCommand();
    ins.CommandText = "INSERT INTO t_serial VALUES (0, @name)";
    DbParameter p = ins.CreateParameter();
    p.ParameterName = "@name"; p.Value = "from a batch";
    ins.Parameters.Add(p);
    batch.BatchCommands.Add(ins);

    DbBatchCommand key = batch.CreateBatchCommand();
    key.CommandText = "SELECT DBINFO('sqlca.sqlerrd1') FROM systables WHERE tabid = 1";
    batch.BatchCommands.Add(key);

    try
    {
        using DbDataReader r = batch.ExecuteReader();
        object? value = null;
        do { while (r.Read()) value = r.GetValue(0); } while (r.NextResult());
        Ui.Kv("key read from the batch", value);
        Ui.Note("the INSERT has no rows to return; the key is the one result set the batch produces");
    }
    catch (Exception e)
    {
        Ui.Kv("batch", $"{e.GetType().Name}: {Ui.FirstLine(e.Message)}");
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. What it costs: 200 inserts, each followed by reading its key");
{
    var time = Ui.Time(() =>
    {
        for (int i = 0; i < 200; i++)
        {
            cn.Exec("INSERT INTO t_serial VALUES (0, 'x')");
            cn.Scalar("SELECT DBINFO('sqlca.sqlerrd1') FROM systables WHERE tabid = 1");
        }
    });
    Ui.Kv("INSERT + SELECT DBINFO", Ui.Ms(time));
    var plain = Ui.Time(() =>
    {
        for (int i = 0; i < 200; i++) cn.Exec("INSERT INTO t_serial VALUES (0, 'x')");
    });
    Ui.Kv("INSERT alone", Ui.Ms(plain));
    Ui.Note("the native LastSerial costs the second figure, not the first - see 02-native/GeneratedKeys");
}

Ui.Line();
Ui.Line("Done.");
