// GenericReadOnly: the same provider-independent code, run with different connection strings.
//
// "Read Only" and "Select Only" are connection-string keywords, so keeping a connection from
// writing needs no provider-specific code at all: the code below only uses DbConnection,
// DbCommand and DbTransaction, and the difference between the rows of the table is the string
// it is opened with. (What the keywords mean is explained in 02-native/ReadOnlyAccess.)

using System.Data;
using System.Data.Common;
using Demo;

Ui.Title("GenericReadOnly");

const string Select = "SELECT COUNT(*) FROM items";
const string Write = "UPDATE items SET quantity = quantity WHERE order_num = -1";     // matches no row

string Run(DbConnection cn, string sql, DbTransaction? tx)
{
    try
    {
        using DbCommand cmd = cn.Command(sql, tx);
        if (sql.StartsWith("SELECT", StringComparison.Ordinal)) cmd.ExecuteScalar(); else cmd.ExecuteNonQuery();
        return "ok";
    }
    catch (DbException e)
    {
        return $"error {e.ErrorCode}";                       // the provider's error number, through DbException
    }
}

var modes = new (string Name, (string Key, string Value)[] Keywords)[]
{
    ("(nothing added)", []),
    ("Read Only=true", [("Read Only", "true")]),
    ("Select Only=true", [("Select Only", "true")]),
};

var rows = new List<object?[]>();
foreach (var (name, keywords) in modes)
{
    using DbConnection cn = Bootstrap.Open(keywords);
    string selectAuto = Run(cn, Select, null);
    string writeAuto = Run(cn, Write, null);
    using DbTransaction tx = cn.BeginTransaction();          // the same call whichever string was used
    string selectTx = Run(cn, Select, tx);
    string writeTx = Run(cn, Write, tx);
    tx.Rollback();
    rows.Add([name, selectAuto, writeAuto, selectTx, writeTx]);
}
Ui.Table(["connection string adds", "SELECT", "write", "SELECT in tx", "write in tx"], rows);
Ui.Note("Read Only=true turns each transaction the driver begins into a read-only one (-878 on a write inside it)");
Ui.Note("Select Only=true refuses writes everywhere, autocommit included");

Ui.Line();
Ui.Line("Done.");
