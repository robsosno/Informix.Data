using Informix.Data;

namespace SuperstoreLoader;

/// <summary>A positional '?' parameter; <see cref="Type"/> is set only where the driver must be told.</summary>
internal sealed record Param(object? Value, IfxType? Type = null);

/// <summary>Where the loader sends its statements: a live server, or the console for --dry-run.</summary>
internal abstract class Target : IDisposable
{
    /// <summary>Connects to an existing database.</summary>
    public abstract void Use(string database);

    /// <summary>True when the current database has transaction logging.</summary>
    public abstract bool IsLogged { get; }

    public abstract void Begin();
    public abstract void Commit();
    public abstract void Rollback();
    public abstract void Execute(string sql, IReadOnlyList<Param>? parameters = null);

    /// <summary>A single numeric result, or -1 when nothing was really executed.</summary>
    public abstract long Scalar(string sql);

    public long Count(string table) => Scalar($"SELECT COUNT(*) FROM {table}");

    public virtual void Dispose() { }
}

internal sealed class LiveTarget : Target
{
    private readonly string _connectionString;
    private IfxConnection? _cn;
    private IfxTransaction? _tx;

    public LiveTarget(string connectionString) => _connectionString = connectionString;

    private bool _logged;

    public override bool IsLogged => _logged;

    public override void Use(string database)
    {
        Close();
        var b = new IfxConnectionStringBuilder(_connectionString) { Database = database };
        // The .unl files write dates as MM/DD/YYYY; the server converts the text parameters.
        b["DB Date"] = "MDY4/";
        b["Pooling"] = "false";
        _cn = new IfxConnection(b.ConnectionString);
        try
        {
            _cn.Open();
        }
        catch (IfxException e)
        {
            Close();
            throw new InvalidOperationException(
                $"Cannot open database '{database}' ({Describe(e)}). " +
                "It has to exist already: run sql/create_databases.sql on the server first (see README.md).", e);
        }
        _logged = Scalar("SELECT is_logging FROM sysmaster:sysdatabases WHERE name = DBINFO('dbname')") == 1;
    }

    public override void Begin()
    {
        if (_logged) _tx = Connection.BeginTransaction();
    }

    public override void Commit()
    {
        _tx?.Commit();
        _tx = null;
    }

    public override void Rollback()
    {
        _tx?.Rollback();
        _tx = null;
    }

    public override void Execute(string sql, IReadOnlyList<Param>? parameters = null)
    {
        using var cmd = Command(sql, parameters);
        cmd.ExecuteNonQuery();
    }

    public override long Scalar(string sql)
    {
        using var cmd = Command(sql, null);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private IfxCommand Command(string sql, IReadOnlyList<Param>? parameters)
    {
        var cmd = new IfxCommand(sql, Connection) { Transaction = _tx };
        if (parameters is not null)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = new IfxParameter($"p{i}", parameters[i].Value ?? DBNull.Value);
                if (parameters[i].Type is { } t) p.IfxType = t;
                cmd.Parameters.Add(p);
            }
        }
        return cmd;
    }

    private IfxConnection Connection => _cn ?? throw new InvalidOperationException("No connection is open.");

    internal static string Describe(IfxException e) => $"SQLCODE {e.IfxSqlCode}, ISAM {e.IsamCode}";

    private void Close()
    {
        _tx?.Dispose();
        _tx = null;
        _cn?.Dispose();
        _cn = null;
    }

    public override void Dispose() => Close();
}

internal sealed class DryTarget : Target
{
    private const int ShownPerTable = 3;
    private int _inTransaction = -1;

    public override bool IsLogged => true;

    public override void Use(string database) => Console.WriteLine($"-- connect to {database}");

    public override void Begin() => _inTransaction = 0;

    public override void Commit()
    {
        if (_inTransaction > ShownPerTable)
            Console.WriteLine($"-- ... and {_inTransaction - ShownPerTable} more statements");
        _inTransaction = -1;
    }

    public override void Rollback() => _inTransaction = -1;

    public override void Execute(string sql, IReadOnlyList<Param>? parameters = null)
    {
        if (_inTransaction >= 0 && ++_inTransaction > ShownPerTable) return;
        Console.WriteLine(sql + ";");
        if (parameters is { Count: > 0 })
        {
            Console.WriteLine("--   parameters: " + string.Join(", ", parameters.Select((p, i) =>
                $"p{i}=" + p.Type switch
                {
                    IfxType.Blob => p.Value is byte[] b ? $"<{b.Length} bytes BLOB>" : "NULL BLOB",
                    IfxType.Clob => p.Value is string s ? $"<{s.Length} chars CLOB>" : "NULL CLOB",
                    _ => p.Value is null ? "NULL" : Literal.Quote(p.Value.ToString()!),
                })));
        }
    }

    public override long Scalar(string sql) => -1;
}
