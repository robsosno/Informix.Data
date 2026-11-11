using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// A set of statements executed together against an Informix database
/// (<see cref="DbBatch"/>) — the standard ADO.NET counterpart to
/// <see cref="IfxRowBatch"/> (one statement over many value rows). Unlike
/// <see cref="IfxRowBatch"/>, an <see cref="IfxBatch"/>'s commands may be
/// entirely different statements.
///
/// <para><see cref="ExecuteNonQuery"/> pipelines the batch over the wire: every
/// already-prepared or cached statement's bind+execute, and every parameterless
/// statement's immediate command, chain into as few PDUs as the ~32 KB guard
/// allows — one flush can carry many statements' worth of execution instead of
/// one round trip each (verified live against IDS 9.59; see the design notes on
/// <see cref="SqliSession.ExecuteChain"/>). A statement this batch has not seen
/// before still costs its own <see cref="Prepare"/> round trip the first time
/// (or every time, with prepared-statement caching off) — only its execute
/// chains with the rest.</para>
///
/// <para><see cref="ExecuteReader()"/> is NOT pipelined: each command opens (or
/// runs) in its own round trip, in order, as <see cref="System.Data.Common.DbDataReader.NextResult"/>
/// advances past the previous one — the single-open-cursor-per-session rule
/// forces this regardless. A non-query command contributes no rows but still
/// stamps its <see cref="IfxBatchCommand.RecordsAffected"/>.</para>
///
/// <para><see cref="ExecuteNonQuery"/> does not run a command whose statement
/// describes a result set (a SELECT, or a RETURNING procedure) — that needs
/// <see cref="ExecuteReader()"/> instead, and throws identifying the offending
/// <see cref="IfxBatchCommand"/> via <see cref="DbException.BatchCommand"/>.</para>
/// </summary>
public sealed class IfxBatch : DbBatch
{
    // A batch command whose statement describes a result set, passed to ExecuteNonQuery (which
    // only runs non-query statements). The server answers a parameterless one itself, so the
    // client-side check on the prepared path reports the same codes the server would, and the
    // two paths agree (live-verified against IDS 14.10): -260 for a SELECT,
    // -684 for a routine that RETURNs values.
    private const int SelectInNonQuerySqlCode = -260;
    private const int RoutineReturnsInNonQuerySqlCode = -684;

    private readonly IfxBatchCommandCollection _commands = new();
    private IfxConnection? _connection;
    private IfxTransaction? _transaction;

    public IfxBatch() { }

    internal IfxBatch(IfxConnection? connection) => _connection = connection;

    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    /// <summary>The statements to run, in order.</summary>
    public new IfxBatchCommandCollection BatchCommands => _commands;

    /// <summary>
    /// Seconds before the whole batch is cancelled (the ASF out-of-band cancel, the same
    /// mechanism <see cref="IfxCommand.CommandTimeout"/> uses) — one deadline for the
    /// entire <see cref="ExecuteNonQuery"/> call, not per statement. Default 30; 0 means
    /// no timeout.
    /// </summary>
    public override int Timeout { get; set; } = 30;

    /// <summary>The connection this batch runs against.</summary>
    public new IfxConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (IfxConnection?)value;
    }

    /// <summary>The transaction this batch runs within, if any.</summary>
    public new IfxTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = (IfxTransaction?)value;
    }

    protected override DbBatchCommand CreateDbBatchCommand() => CreateBatchCommand();

    /// <summary>Creates a new, unattached <see cref="IfxBatchCommand"/> — add it to
    /// <see cref="BatchCommands"/> to include it in this batch.</summary>
    public new IfxBatchCommand CreateBatchCommand() => new();

    /// <summary>
    /// Interrupts whatever this batch is currently running with the ASF out-of-band
    /// cancel byte (the same mechanism <see cref="IfxCommand.Cancel"/> uses). No-op when
    /// the connection is not open or nothing is executing on it.
    /// </summary>
    public override void Cancel() => _connection?.SqliSession?.Link.TryInterrupt();

    /// <summary>
    /// Prepares every parameterized command through the session's prepared-statement
    /// cache (<see cref="IfxConnectionStringBuilder.PreparedStatementCacheSize"/>), so
    /// the next <see cref="ExecuteNonQuery"/>/<see cref="ExecuteReader()"/> finds a cache
    /// hit for each instead of preparing it cold. With caching off this prepares and
    /// immediately releases each statement — a wasted round trip, not a mistake to make
    /// unless caching is also enabled. Parameterless, non-<see cref="CommandType.StoredProcedure"/>
    /// commands are skipped: they never go through a prepared statement (see the class docs).
    /// </summary>
    public override void Prepare()
    {
        var session = RequireOpenConnection();
        try
        {
            var warmed = new HashSet<(string Sql, int ParamCount)>();
            foreach (IfxBatchCommand cmd in _commands)
            {
                int paramCount = cmd.CountInputParameters();
                if (paramCount == 0 && cmd.CommandType != CommandType.StoredProcedure)
                    continue;
                string sql = cmd.EffectiveCommandText();
                if (!warmed.Add((sql, paramCount)))
                    continue; // an identical command earlier in this batch already warmed it
                var handle = session.PrepareCached(sql, paramCount, scroll: false, hold: false);
                session.ReleaseOrCache(handle, sql, scroll: false, hold: false);
            }
        }
        finally
        {
            session.ClearReservedForAcquire();
        }
    }

    /// <summary>
    /// Runs every command in <see cref="BatchCommands"/> and returns the sum of their
    /// affected-row counts (0 for an empty batch), stamping each command's own
    /// <see cref="IfxBatchCommand.RecordsAffected"/> along the way. Pipelined — see the
    /// class docs. Throws (without stamping anything past the failure) if a command
    /// describes a result set; use <see cref="ExecuteReader()"/> for those.
    /// </summary>
    public override int ExecuteNonQuery()
    {
        var session = RequireOpenConnection();
        if (_commands.Count == 0)
            return 0;

        var reporter = _connection!.MetricsReporter;
        Activity? activity = IfxActivitySource.IsEnabled
            ? IfxActivitySource.BatchStart(BuildTraceDescription(), _connection.TraceTarget)
            : null;
        using var _ = session.BeginCommandTimeout(Timeout);
        // Declared after the timeout scope so it disposes first (matches the original
        // finally-then-EndCommandTimeout order). logger: null -- a batch never logs (its
        // "one SQL code, one message" failure log does not map onto a multi-command run).
        // See IfxCommand.Prepare() for why ClearReservedForAcquire must always run.
        using var scope = new IfxExecutionScope(session, reporter, activity, logger: null);
        try
        {
            int rows = ExecuteNonQueryCore(session);
            if (activity is not null)
            {
                IfxActivitySource.ReceivedFirstResponse(activity, _connection.TracingOptions);
                IfxActivitySource.CommandStop(activity);
            }
            return rows;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    // Phase 1 (one prepare round trip per distinct, uncached parameterized statement): build the chain. A statement that turns
    // out to return rows is rejected here, before anything has executed, because the describe reply is the only reliable way to know.
    // Phase 2: execute the whole chain with one flush. On the first server error the remaining steps do not run; counts of the
    // completed steps are still recorded and the error is thrown with the failing command attached.
    private int ExecuteNonQueryCore(SqliSession session)
    {
        int n = _commands.Count;
        var steps = new List<SqliChainStep>(n);
        var handles = new SqliStatementHandle?[n]; // null for an Immediate (parameterless) step
        var resolved = new Dictionary<(string Sql, int ParamCount), SqliStatementHandle>();

        for (int idx = 0; idx < n; idx++)
        {
            IfxBatchCommand cmd = _commands[idx];
            string sql = cmd.EffectiveCommandText();
            List<SqliBindValue> binds = cmd.BuildBinds(session);

            if (binds.Count == 0 && cmd.CommandType != CommandType.StoredProcedure)
            {
                steps.Add(new SqliChainStep { Kind = SqliChainStepKind.Immediate, Sql = sql });
                continue;
            }

            var key = (sql, binds.Count);
            if (!resolved.TryGetValue(key, out SqliStatementHandle? handle))
            {
                handle = session.PrepareCached(sql, binds.Count, scroll: false, hold: false);
                resolved[key] = handle;
            }
            if (handle.DescribesResultSet)
            {
                foreach (SqliStatementHandle h in resolved.Values)
                    session.ReleaseStatementHandle(h);
                int describesCode = handle.StatementType == (short)SqliStatementType.Select
                    ? SelectInNonQuerySqlCode
                    : RoutineReturnsInNonQuerySqlCode;
                var ex = new IfxException(
                    $"Batch command {idx} (\"{sql}\") describes a result set; IfxBatch.ExecuteNonQuery does not " +
                    $"run SELECTs or RETURNING procedures ({describesCode}) -- use ExecuteReader instead.",
                    describesCode);
                ex.SetBatchCommand(cmd);
                throw ex;
            }
            handles[idx] = handle;
            steps.Add(new SqliChainStep { Kind = SqliChainStepKind.Execute, StatementId = handle.StatementId, Binds = binds });
        }

        SqliChainOutcome outcome;
        try
        {
            outcome = session.ExecuteChain(steps);
        }
        catch
        {
            foreach (SqliStatementHandle h in resolved.Values)
                session.ReleaseStatementHandle(h);
            throw;
        }

        // Return every distinct statement this run prepared: to the cache (or a real
        // release when caching is off) on a clean outcome; a stale query plan (-710) is
        // released for real instead, matching IfxCommand's own rule -- caching a statement
        // whose plan just proved stale would only hand the same error to the next checkout.
        bool stalePlan = outcome.Error is { IfxSqlCode: -710 };
        SqliStatementHandle? failedHandle = outcome.FailedIndex >= 0 ? handles[outcome.FailedIndex] : null;
        foreach ((string Sql, int ParamCount) key in resolved.Keys)
        {
            SqliStatementHandle h = resolved[key];
            if (stalePlan && ReferenceEquals(h, failedHandle))
                session.ReleaseStatementHandle(h);
            else
                session.ReleaseOrCache(h, key.Sql, scroll: false, hold: false);
        }

        int sum = 0;
        for (int idx = 0; idx < n; idx++)
        {
            if (outcome.Results[idx] is { } r)
            {
                _commands[idx].SetRecordsAffected(r.RowsProcessed);
                sum += r.RowsProcessed;
            }
        }

        if (outcome.Error is not null)
        {
            outcome.Error.SetBatchCommand(_commands[outcome.FailedIndex]);
            throw outcome.Error;
        }
        return sum;
    }

    /// <summary>
    /// Runs every command in <see cref="BatchCommands"/> in order and returns the first
    /// column of the first row of the first one that produces a result set (null if that
    /// result set is empty, or if none of them do).
    /// </summary>
    public override object? ExecuteScalar()
    {
        using IfxDataReader reader = ExecuteReader();
        do
        {
            if (reader.FieldCount > 0)
                return reader.Read() ? reader.GetValue(0) : null;
        }
        while (reader.NextResult());
        return null;
    }

    /// <summary>
    /// Runs the commands in <see cref="BatchCommands"/> one at a time, in order, as a
    /// single spanning reader: <see cref="System.Data.Common.DbDataReader.NextResult"/>
    /// closes the current command and runs the next. Not pipelined (see the class docs)
    /// — each command opens or executes in its own round trip. A non-query command
    /// contributes no rows to the reader but still stamps its own
    /// <see cref="IfxBatchCommand.RecordsAffected"/>.
    /// </summary>
    public new IfxDataReader ExecuteReader(CommandBehavior behavior = CommandBehavior.Default) =>
        (IfxDataReader)ExecuteDbDataReader(behavior);

    // Opens the first command's cursor now and the following ones lazily in NextResult.
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var session = RequireOpenConnection();
        if (_commands.Count == 0)
            return new IfxDataReader(new SqliEmptyCursor(0), null, _connection);

        var reporter = _connection!.MetricsReporter;
        Activity? activity = IfxActivitySource.IsEnabled
            ? IfxActivitySource.BatchStart(BuildTraceDescription(), _connection.TraceTarget)
            : null;
        // logger: null -- a batch never logs.
        using var scope = new IfxExecutionScope(session, reporter, activity, logger: null);
        try
        {
            int nextIndex = 0;
            IIfxResultCursor first = RunNextReaderCommand(session, nextIndex++, behavior)!;
            if (activity is not null)
                IfxActivitySource.ReceivedFirstResponse(activity, _connection.TracingOptions);

            Action? onClose = behavior.HasFlag(CommandBehavior.CloseConnection)
                ? () => _connection?.Close()
                : null;
            return new IfxDataReader(first, onClose, _connection, activity,
                nextCursor: () => RunNextReaderCommand(session, nextIndex++, behavior));
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    // Runs BatchCommands[index] (SchemaOnly/SkipRows semantics are not offered -- callers
    // needing them should use IfxCommand directly) and returns its cursor, or null once
    // every command has run. A non-query command executes immediately and stamps
    // RecordsAffected; a result-set command opens a real (unpipelined) cursor. Each
    // command gets its own CommandTimeout-bounded window (through the open/execute, not
    // the caller's own row-by-row iteration afterward), matching IfxCommand.ExecuteReader.
    private IIfxResultCursor? RunNextReaderCommand(SqliSession session, int index, CommandBehavior behavior)
    {
        if (index >= _commands.Count)
            return null;

        IfxBatchCommand cmd = _commands[index];
        string sql = cmd.EffectiveCommandText();
        List<SqliBindValue> binds = cmd.BuildBinds(session);

        using var _ = session.BeginCommandTimeout(Timeout);
        SqliStatementHandle handle = session.PrepareCached(sql, binds.Count, scroll: false, hold: false);
        try
        {
            if (!session.DescribesResultSet)
            {
                session.ExecuteStatement(binds.Count > 0 ? binds : null);
                cmd.SetRecordsAffected(session.RowsProcessed);
                session.ReleaseOrCache(handle, sql, scroll: false, hold: false);
                return new SqliEmptyCursor(session.RowsProcessed);
            }

            session.OpenCursor(binds.Count > 0 ? binds : null, hold: false, fetchBufferSize: 0);
            return new SqliCursor(session, behavior.HasFlag(CommandBehavior.SingleRow), (handle, sql, false));
        }
        catch
        {
            session.ReleaseStatementHandle(handle);
            throw;
        }
    }

    // ---- async members: the driver core stays synchronous; these are the same
    // check-token/run-sync/return-completed-task shape DbCommand's own default async
    // members give IfxCommand -- no real async I/O. ----

    /// <summary>Sync-delegating: runs <see cref="ExecuteNonQuery"/> synchronously and
    /// returns a completed (or canceled/faulted) <see cref="Task{TResult}"/>.</summary>
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<int>(cancellationToken);
        try
        {
            return Task.FromResult(ExecuteNonQuery());
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    /// <summary>Sync-delegating: runs <see cref="ExecuteScalar"/> synchronously and
    /// returns a completed (or canceled/faulted) <see cref="Task{TResult}"/>.</summary>
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<object?>(cancellationToken);
        try
        {
            return Task.FromResult(ExecuteScalar());
        }
        catch (Exception ex)
        {
            return Task.FromException<object?>(ex);
        }
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<DbDataReader>(cancellationToken);
        try
        {
            return Task.FromResult(ExecuteDbDataReader(behavior));
        }
        catch (Exception ex)
        {
            return Task.FromException<DbDataReader>(ex);
        }
    }

    /// <summary>Sync-delegating: runs <see cref="Prepare"/> synchronously and returns a
    /// completed (or canceled/faulted) <see cref="Task"/>.</summary>
    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        try
        {
            Prepare();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private SqliSession RequireOpenConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("IfxBatch.Connection has not been set.");
        // Same rule as IfxCommand.RequireOpenConnection: a transaction of another connection cannot be joined.
        if (_transaction is { Connection: { } owner } && !ReferenceEquals(owner, _connection))
            throw new InvalidOperationException(
                "The batch's Transaction was started on a different connection than the batch's Connection.");
        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
        // Same rule as IfxCommand.RequireOpenConnection: inside a local transaction the batch must carry it.
        if (_connection.ActiveTransaction is not null && _transaction is not { Connection: not null })
            throw new InvalidOperationException(
                "The batch requires its Transaction property to be set to the connection's pending local transaction.");
        // AcquireSession (not the connection's default session directly) so a batch is never
        // blocked by an unrelated open reader elsewhere on the same connection -- see
        // IfxCommand.RequireOpenConnection for the identical reasoning.
        SqliSession session = _connection.AcquireSession();
        session.ThrowIfStatementOwned();
        return session;
    }

    private string BuildTraceDescription() => string.Join("; ", _commands.Select(c => c.CommandText));
}
