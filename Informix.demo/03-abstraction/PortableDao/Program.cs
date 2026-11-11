// PortableDao: a small data-access layer written on top of ADO.NET abstractions only.
//
// The point is the shape of the code, not the features: the DAO classes know DbConnection,
// DbCommand and DbDataReader and nothing else, take their connections from a Func<DbConnection>,
// map rows with a delegate, and run several steps as one unit of work. Put another provider's
// factory behind the same delegate and the classes do not change.
//
// Shows: parameterized queries from a repository, row mappers, joins mapped to a parent with
// children, and a unit of work (one DbTransaction shared by two DAOs, rolled back on failure).

using System.Data;
using System.Data.Common;
using Demo;

Ui.Title("PortableDao");

Func<DbConnection> connections = () => Bootstrap.Open();        // the single place that picks the provider

var uow = new UnitOfWork(connections);
var stockDao = new StockDao(uow);
var orderDao = new OrderDao(uow);

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Repositories with parameters and a row mapper");
{
    var cheap = stockDao.ByManufacturer("HRO").Where(s => s.UnitPrice < 100).ToList();
    Ui.Table(["stock_num", "unit", "description", "unit_price", "qty"],
        cheap.Select(s => new object?[] { s.StockNum, s.Unit, s.Description, s.UnitPrice, s.QtyOnHand }));
    Ui.Kv("manufacturers with stock", string.Join(", ", stockDao.Manufacturers()));
}

Ui.Section("2. A parent with its children");
{
    foreach (Order o in orderDao.ForCustomer(104).Take(3))
    {
        Ui.Line($"   order {o.OrderNum}  placed {o.OrderDate:yyyy-MM-dd}  {(o.Backlog ? "(on backlog)" : "")}");
        foreach (OrderLine l in o.Lines)
            Ui.Line($"      {l.ItemNum}. {l.Quantity} x {l.Description,-18} {l.Subtotal,8}");
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. A unit of work: two DAOs, one transaction");
{
    decimal Total(string manu) => stockDao.ByManufacturer(manu).Sum(s => s.UnitPrice * s.QtyOnHand);
    Ui.Kv("stock value HRO / SMT before", $"{Total("HRO"):N2} / {Total("SMT"):N2}");

    try
    {
        uow.Run(() =>
        {
            stockDao.AdjustQuantity("HRO", -10);
            stockDao.AdjustQuantity("SMT", +10);
            Ui.Kv("inside the unit of work", $"{Total("HRO"):N2} / {Total("SMT"):N2}");
            throw new InvalidOperationException("something went wrong after both updates");
        });
    }
    catch (InvalidOperationException e)
    {
        Ui.Kv("the unit of work failed", e.Message);
    }
    Ui.Kv("stock value HRO / SMT after", $"{Total("HRO"):N2} / {Total("SMT"):N2}   (rolled back)");
}

Ui.Line();
Ui.Line("Done.");

// =============================================================================================

record Stock(short StockNum, string ManuCode, string Unit, string Description, decimal UnitPrice, short QtyOnHand);
record OrderLine(short ItemNum, int Quantity, string Description, decimal Subtotal);
record Order(long OrderNum, DateOnly OrderDate, bool Backlog, List<OrderLine> Lines);

/// <summary>Opens the connection lazily and keeps one transaction while a unit of work runs.</summary>
sealed class UnitOfWork(Func<DbConnection> open) : IDisposable
{
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public DbConnection Connection => _connection ??= open();

    public DbCommand Command(string sql)
    {
        DbCommand cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        return cmd;
    }

    public void Run(Action work)
    {
        _transaction = Connection.BeginTransaction();
        try
        {
            work();
            _transaction.Commit();
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public void Dispose() => _connection?.Dispose();
}

static class Sql
{
    public static IEnumerable<T> Query<T>(this UnitOfWork uow, string sql, Func<DbDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        using DbCommand cmd = uow.Command(sql);
        foreach (var (name, value) in parameters)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        using DbDataReader r = cmd.ExecuteReader();
        var rows = new List<T>();
        while (r.Read()) rows.Add(map(r));
        return rows;
    }
}

sealed class StockDao(UnitOfWork uow)
{
    private static Stock Map(DbDataReader r) =>
        new(r.GetInt16(0), r.GetString(1).Trim(), r.GetString(2).Trim(), r.GetString(3), r.GetDecimal(4), r.GetInt16(5));

    public IEnumerable<Stock> ByManufacturer(string manuCode) =>
        uow.Query("SELECT stock_num, manu_code, unit, description, unit_price, qty_on_hand FROM stock WHERE manu_code = @manu ORDER BY stock_num, unit",
            Map, ("@manu", manuCode));

    public IEnumerable<string> Manufacturers() =>
        uow.Query("SELECT DISTINCT manu_code FROM stock ORDER BY 1", r => r.GetString(0).Trim());

    public void AdjustQuantity(string manuCode, int delta)
    {
        using DbCommand cmd = uow.Command("UPDATE stock SET qty_on_hand = qty_on_hand + @delta WHERE manu_code = @manu");
        DbParameter d = cmd.CreateParameter(); d.ParameterName = "@delta"; d.Value = delta; cmd.Parameters.Add(d);
        DbParameter m = cmd.CreateParameter(); m.ParameterName = "@manu"; m.Value = manuCode; cmd.Parameters.Add(m);
        cmd.ExecuteNonQuery();
    }
}

sealed class OrderDao(UnitOfWork uow)
{
    public IEnumerable<Order> ForCustomer(int customerNum)
    {
        var byOrder = new Dictionary<long, Order>();
        foreach (var (order, line) in uow.Query(
            "SELECT o.order_num, o.order_date, o.backlog, i.item_num, i.quantity, s.description, i.item_subtotal " +
            "FROM orders o, items i, stock s " +
            "WHERE o.order_num = i.order_num AND i.stock_num = s.stock_num AND i.manu_code = s.manu_code AND i.unit = s.unit " +
            "AND o.customer_num = @customer ORDER BY o.order_num, i.item_num",
            r => (Order: new Order(r.GetInt64(0), r.GetFieldValue<DateOnly>(1), r.GetBoolean(2), []),
                  Line: new OrderLine(r.GetInt16(3), r.GetInt16(4), r.GetString(5), r.GetDecimal(6))),
            ("@customer", customerNum)))
        {
            if (!byOrder.TryGetValue(order.OrderNum, out Order? existing)) byOrder[order.OrderNum] = existing = order;
            existing.Lines.Add(line);
        }
        return byOrder.Values;
    }
}
