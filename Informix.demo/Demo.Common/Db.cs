using System.Data;
using System.Data.Common;

namespace Demo;

/// <summary>
/// Small helpers written against System.Data.Common only, so the programs in 03-abstraction read
/// as ordinary provider-independent ADO.NET code.
/// </summary>
public static class Db
{
    /// <summary>A command on <paramref name="cn"/>, optionally in <paramref name="tx"/>.</summary>
    public static DbCommand Command(this DbConnection cn, string sql, DbTransaction? tx = null)
    {
        DbCommand cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>Adds a named parameter through the command's own factory method.</summary>
    public static DbParameter Add(this DbCommand cmd, string name, object? value, DbType? type = null)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        if (type is { } t) p.DbType = t;
        cmd.Parameters.Add(p);
        return p;
    }

    public static int Exec(this DbConnection cn, string sql, DbTransaction? tx = null)
    {
        using DbCommand cmd = cn.Command(sql, tx);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this DbConnection cn, string sql, DbTransaction? tx = null)
    {
        using DbCommand cmd = cn.Command(sql, tx);
        return cmd.ExecuteScalar();
    }

    public static long Count(this DbConnection cn, string sql, DbTransaction? tx = null) =>
        Convert.ToInt64(cn.Scalar(sql, tx), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>All rows of a query as object arrays (column values as the reader returns them).</summary>
    public static List<object?[]> Rows(this DbConnection cn, string sql, DbTransaction? tx = null)
    {
        using DbCommand cmd = cn.Command(sql, tx);
        using DbDataReader r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            r.GetValues(row!);
            rows.Add(row);
        }
        return rows;
    }
}
