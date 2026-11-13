using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Microsoft.Extensions.Logging;

namespace Informix.Data;

/// <summary>
/// Represents a SQL statement executed against an Informix database
/// (<see cref="DbCommand"/>). ADO.NET unifies the prepared and immediate paths
/// behind one command object.
///
/// <para>Parameter placeholders use the Informix <c>?</c> positional syntax;
/// values are bound in collection order. Statements without parameters take
/// the single-round-trip SQ_COMMAND path, parameterized ones the
/// prepare/bind/execute path.</para>
/// </summary>
/// <remarks>Replaces <c>IfxStatement</c>/<c>IfxPreparedStatement</c> pair.</remarks>
public sealed class IfxCommand : DbCommand, ICloneable
{
    private readonly IfxParameterCollection _parameters = new();
    private IfxConnection? _connection;
    private IfxTransaction? _transaction;
    private string _commandText = string.Empty;

    // The cached parse of _commandText (named-placeholder rewrite + placeholder names),
    // invalidated whenever CommandText is reassigned so repeat executions of the same
    // command never re-tokenize. Only ever consulted for CommandType.Text.
    private IfxParsedCommand? _parsedCommand;

    // The statement this command explicitly Prepare()d and is holding onto across
    // executions, plus the shape it was prepared with (compared against the current
    // shape on every execute to decide whether it's still valid to reuse). Null when
    // this command has never called Prepare(), or the held statement was dropped
    // because CommandText/Connection changed, the command was disposed, or the
    // connection closed.
    private SqliStatementHandle? _preparedHandle;
    private string? _preparedSql;
    private int _preparedParamCount;
    private bool _preparedHoldCursor;

    // The exact SqliSession instance the held handle was prepared on. A pooled
    // Connection can go through Close()+Open() without this command's CommandText or
    // Connection ever changing, and come back with a *different* physical session
    // (e.g. a different pooled physical, or a fresh one after ClearPool) -- reusing
    // the old statement id against that session would activate/release a foreign
    // statement. Compared by reference everywhere the held handle is reused.
    private SqliSession? _preparedSession;

    public IfxCommand() { }

    // True for the catalog queries and lookups the driver itself runs on the caller's connection
    // (GetSchema, the command builder). They are not the caller's statements, so they are exempt from
    // "a command must carry the connection's pending transaction" (see RequireOpenConnection).
    internal bool IsInternal { get; init; }

    public IfxCommand(string? commandText) => CommandText = commandText ?? string.Empty;

    public IfxCommand(string? commandText, IfxConnection? connection) : this(commandText) => _connection = connection;

    // Changing the text releases a held prepared statement and discards the parsed form.
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            string newText = value ?? string.Empty;
            if (_commandText == newText)
                return;
            ReleaseHeldHandle(); // drop any Prepare()d statement -- it was prepared against the old text
            _commandText = newText;
            _parsedCommand = null;       // invalidate the cached parse
            _outParameterRoutine = null; // ...and the resolved OUT-parameter routine
        }
    }

    // Parses (once, then cached) the current CommandText for named placeholders. The
    // ordinary positional-? / no-parameter cases return the original string instance
    // untouched, so those workloads see zero wire change and no re-parse.
    private IfxParsedCommand ParsedCommand => _parsedCommand ??= ParseCommandText();

    private IfxParsedCommand ParseCommandText()
    {
        ParseInvocationCount++;
        return IfxSqlParser.Parse(_commandText);
    }

    // Test seam: how many times this command has actually tokenized its text. Proves the
    // parse is cached across executions and re-runs only when CommandText changes.
    internal int ParseInvocationCount { get; private set; }

    // Test seam: forces the (cached) parse without needing an open connection.
    internal IfxParsedCommand ParseForTest() => ParsedCommand;

    /// <summary>Seconds before the running statement is cancelled out-of-band (the server then answers -213); default 30, 0 disables the timeout.</summary>
    public override int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// When set, cursors opened by this command are declared <c>WITH HOLD</c>: they
    /// stay open across a <see cref="IfxTransaction.Commit"/>/<see cref="IfxTransaction.Rollback"/>
    /// instead of being closed by it (emits SQ_HOLD at open; ignored for
    /// EXECUTE PROCEDURE). Applies to both <see cref="ExecuteReader()"/> and
    /// <see cref="ExecuteScrollReader"/>.
    /// </summary>
    public bool HoldCursor { get; set; }

    /// <summary>
    /// Fetch-buffer size in bytes for cursors opened by this command (Informix
    /// <c>Statement.fetchBufferSize</c>); 0 (the default) uses the connection's
    /// <see cref="IfxConnectionStringBuilder.FetchBufferSize"/> or the built-in
    /// tuple-size default. A larger buffer packs more rows per server reply, cutting
    /// the number of fetch round trips on a large result set. Overrides the
    /// connection-level default. Applies to <see cref="ExecuteReader()"/> and
    /// <see cref="ExecuteScrollReader"/>.
    /// </summary>
    public int FetchBufferSize { get; set; }

    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (_commandType == value)
                return;
            _commandType = value;
            // CommandText means something different per type (a routine name vs. a whole
            // statement), so anything resolved from it under the old type is now stale.
            _outParameterRoutine = null;
        }
    }

    private CommandType _commandType = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;

    public new IfxParameterCollection Parameters => _parameters;

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new IfxConnection? Connection
    {
        get => _connection;
        set => SetConnection(value);
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => SetConnection((IfxConnection?)value);
    }

    private void SetConnection(IfxConnection? value)
    {
        if (ReferenceEquals(_connection, value))
            return;
        ReleaseHeldHandle(); // a Prepare()d statement belongs to the old connection's session
        _connection = value;
    }

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

    protected override DbParameter CreateDbParameter() => new IfxParameter();

    public new IfxParameter CreateParameter() => new();

    // The logging configuration for this command (from its connection); the null
    // configuration when the command has no open connection yet.
    private IfxLoggingConfiguration Logging =>
        _connection?.LoggingConfiguration ?? IfxLoggingConfiguration.NullConfiguration;

    // Starts the OpenTelemetry client span for this execution, or null when no listener
    // is subscribed or the data source's command filter excludes it. Allocation-free
    // when tracing is off (guarded by HasListeners).
    private Activity? StartCommandActivity() =>
        IfxActivitySource.IsEnabled && _connection is not null
            ? IfxActivitySource.CommandStart(this, _connection.TraceTarget, _connection.TracingOptions)
            : null;

    public override void Cancel()
    {
        // Interrupts the running statement with the ASF out-of-band cancel byte
        // (port of IfxCancelQueryImpl -> Connection.sendOutOfBand). No-op when the
        // connection is not open or nothing is executing on it: a byte sent into an idle
        // connection would interrupt the next statement instead (SQLCODE -213).
        if (_connection?.SqliSession?.Link is { } link && link.TryInterrupt())
            LogMessages.SentOutOfBandCancel(Logging.ConnectionLogger);
    }

    // Logs the SQL about to execute, plus the bound parameter values when parameter
    // logging is enabled (and the logger is listening) — the values are built lazily
    // so a disabled logger pays nothing.
    private void LogExecuting(string sql)
    {
        var logger = Logging.CommandLogger;
        LogMessages.CommandExecuting(logger, sql);
        if (Logging.IsParameterLoggingEnabled && _parameters.Count > 0 && logger.IsEnabled(LogLevel.Trace))
            LogMessages.ParameterValues(logger, FormatParameters());
    }

    private string FormatParameters()
    {
        var parts = new List<string>(_parameters.Count);
        foreach (IfxParameter p in _parameters)
            parts.Add($"{p.ParameterName}={FormatValue(p.Value)}");
        return string.Join(", ", parts);

        static string FormatValue(object? value) => value switch
        {
            null or DBNull => "NULL",
            byte[] b => $"byte[{b.Length}]",
            string s => $"'{s}'",
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    /// Prepares the statement now and holds onto it: every subsequent
    /// <see cref="ExecuteNonQuery"/>/<see cref="ExecuteReader()"/>/<see cref="ExecuteScrollReader"/>
    /// call on this command reactivates the same server-side prepared statement (no
    /// re-prepare) as long as <see cref="CommandText"/>, the input parameter count and
    /// <see cref="HoldCursor"/> stay exactly as they were when <c>Prepare()</c> was
    /// called — a change to any of those (or reassigning <see cref="Connection"/>,
    /// disposing the command, or closing the connection) releases it. A prepared
    /// command always drives the prepare/bind/execute path, even with zero parameters
    /// (it bypasses the single-round-trip immediate-command shortcut, since that path
    /// has no statement to hold onto). Call <see cref="Unprepare"/> to drop the held
    /// statement early without waiting for one of those triggers; <see cref="IsPrepared"/>
    /// reports whether this command currently holds one.
    /// </summary>
    public override void Prepare()
    {
        var session = RequireOpenConnection();
        try
        {
            string sql = EffectiveCommandText();
            int paramCount = CountInputParameters();

            if (_preparedHandle is not null
                && _preparedSql == sql && _preparedParamCount == paramCount && _preparedHoldCursor == HoldCursor
                && ReferenceEquals(_preparedSession, session))
                return; // already holding a still-valid prepare of this exact shape, on this exact session

            ReleaseHeldHandle(); // drop whatever this command was holding before, if anything
            _preparedHandle = session.Prepare(sql, paramCount);
            _preparedSession = session;
            _preparedSql = sql;
            _preparedParamCount = paramCount;
            _preparedHoldCursor = HoldCursor;
        }
        finally
        {
            session.ClearReservedForAcquire();
        }
    }

    /// <summary>
    /// True while this command is holding a statement from an explicit <see cref="Prepare"/>
    /// call that it can reactivate on its next execution. False before <see cref="Prepare"/>
    /// has been called, after <see cref="Unprepare"/>, or once the held statement has been
    /// dropped by a <see cref="CommandText"/>/<see cref="Connection"/> change — it does not
    /// reflect a prepared-statement-cache hit on an otherwise unprepared command.
    /// </summary>
    public bool IsPrepared => _preparedHandle is not null;

    /// <summary>
    /// Releases the server-side prepared statement created by a previous <see cref="Prepare"/>
    /// call, without waiting for <see cref="CommandText"/>/<see cref="Connection"/> to change
    /// or the command to be disposed (Npgsql <c>NpgsqlCommand.Unprepare</c> parity — plain
    /// ADO.NET has no un-prepare). A no-op, not an error, when this command holds nothing:
    /// it was never <see cref="Prepare"/>d, was already unprepared, or the held statement was
    /// already dropped by a <see cref="CommandText"/>/<see cref="Connection"/> change. Only
    /// ever touches the handle this command itself explicitly prepared and held — the
    /// session's prepared-statement cache, which backs ordinary (non-<see cref="Prepare"/>d)
    /// executions, is never evicted or otherwise disturbed by this call.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> if a data reader is still open on
    /// this command's held statement (close it first) — a session has only one active cursor,
    /// and releasing the statement out from under an open cursor is not something the server
    /// supports.</para>
    /// </summary>
    public void Unprepare()
    {
        if (_preparedHandle is null)
            return; // never prepared, already unprepared, or dropped by a CommandText/Connection change

        if (LiveSessionOf(_preparedSession) is not { } session)
        {
            // Not open, or the held statement was prepared on a physical connection that is no
            // longer this connection's live one (see _preparedSession) -- nothing to release
            // remotely; just forget the handle locally.
            ClearHeldState();
            return;
        }

        session.ThrowIfStatementOwned();
        if (session.CursorOpen || session.HasAttachedReader)
            throw new InvalidOperationException(
                "A data reader is open on this command's prepared statement; close it before calling Unprepare().");

        var (handle, _) = ClearHeldState()!.Value;
        session.ReleaseStatementHandle(handle);
    }

    // The bind count Prepare() needs (SQ_PREPARE's parameter-count word) without
    // encoding any parameter values — unlike BuildBinds, which does real work per
    // parameter (e.g. uploading a smart-LOB parameter's value right away), something
    // Prepare() must not trigger before the caller has necessarily set real values.
    private int CountInputParameters()
    {
        if (CommandType == CommandType.Text)
        {
            if (ParsedCommand.IsNamed)
                return ParsedCommand.ParameterNames.Count;
            // Caller-written placeholders: every parameter backs one (bar the "{? = call}"
            // escape's leading '?', which is the return value) — see IfxParameterBinder.Build.
            return ParsedCommand.HasReturnValuePlaceholder ? _parameters.Count - 1 : _parameters.Count;
        }
        int count = 0;
        foreach (IfxParameter p in _parameters)
            if (p.Direction is not (ParameterDirection.Output or ParameterDirection.ReturnValue))
                count++;
        return count;
    }

    // Clears this command's locally-held prepare state (handle + sql + the session it
    // was prepared on) and hands back what was held, or null when nothing was. Shared by
    // ReleaseHeldHandle and Unprepare so there is exactly one place that resets the fields.
    private (SqliStatementHandle Handle, SqliSession? Session)? ClearHeldState()
    {
        if (_preparedHandle is not { } handle)
            return null;
        var session = _preparedSession;
        _preparedHandle = null;
        _preparedSql = null;
        _preparedSession = null;
        return (handle, session);
    }

    // Drops this command's persisted prepared-statement handle (from an explicit
    // Prepare() call), releasing it on the server when the owning connection is still
    // open *and* still backed by the exact session the statement was prepared on --
    // a stale handle from a session this connection has since moved past (see
    // _preparedSession) is simply forgotten, never sent to whatever session is live now.
    // Called whenever the handle would otherwise dangle: CommandText/Connection
    // reassignment, Dispose, or AcquireStatement finding it stale.
    private void ReleaseHeldHandle()
    {
        if (ClearHeldState() is not { } held)
            return;
        if (LiveSessionOf(held.Session) is not { } session)
            return;
        // A reader still open on this very statement owns the session's row buffer; releasing
        // the statement under it would wipe the rows it has not read yet. It is left to the
        // connection's close, which releases whatever a session still has prepared.
        if (session.HasAttachedReader && session.StatementId == held.Handle.StatementId)
            return;
        session.ReleaseStatementHandle(held.Handle);
    }

    // The session a held statement was prepared on, when it still belongs to the physical
    // connection behind this command's (open) Connection; null otherwise. With several sessions
    // on one connection the statement may sit on a sibling session rather than the default one,
    // so the test is "same link", not "is the default session".
    private SqliSession? LiveSessionOf(SqliSession? preparedOn) =>
        preparedOn is not null
        && _connection is { State: ConnectionState.Open } conn
        && conn.SqliSession is { } live
        && ReferenceEquals(live.Link, preparedOn.Link)
            ? preparedOn
            : null;

    /// <summary>
    /// Prepares (or reactivates this command's already-held statement, or a session
    /// prepared-statement cache hit) for execution. Returns the handle to drive plus
    /// whether the caller must end its lifetime when done — false only while an explicit
    /// <see cref="Prepare"/> is keeping it alive for this command's next execution; when
    /// true, the caller ends it with <see cref="EndStatementUse"/>, not a bare release,
    /// so a successful execution can be handed to the session's prepared-statement cache
    /// (a held <see cref="Prepare"/> always takes precedence over the cache for this
    /// command's own executions — it never checks the cache in or out).
    /// </summary>
    private (SqliStatementHandle Handle, bool MustRelease) AcquireStatement(
        SqliSession session, string sql, int paramCount, bool scroll = false)
    {
        if (_preparedHandle is { } held
            && _preparedSql == sql && _preparedParamCount == paramCount && _preparedHoldCursor == HoldCursor
            && ReferenceEquals(_preparedSession, session))
        {
            session.Activate(held);
            return (held, false);
        }
        if (_preparedHandle is not null)
            ReleaseHeldHandle(); // stale -- CommandText/params/HoldCursor drifted, or the session changed, since Prepare()
        return (session.PrepareCached(sql, paramCount, scroll, HoldCursor), true);
    }

    // The Informix stale-plan error: the prepared statement's query plan refers to a
    // table that has since been altered. Only meaningful for a statement AcquireStatement
    // says must be ended (a session cache entry, or a fresh one-shot prepare) -- an
    // explicitly Prepare()d command's own held statement is left as the caller's problem
    // (this driver does not retry any SQL error automatically).
    private const int StalePlanSqlCode = -710;

    // Ends use of a statement AcquireStatement said the caller must release: on a clean
    // finish, offers it to the prepared-statement cache (a no-op, real release when
    // caching is off); on a stale-plan error the checkout already pulled it out of the
    // cache (or it was never in there), so there is nothing to do but release it for real
    // -- caching a statement whose plan just proved stale would only hand the same error
    // to the next checkout.
    private void EndStatementUse(SqliSession session, SqliStatementHandle handle, string sql, bool scroll, bool stalePlan)
    {
        if (stalePlan)
            session.ReleaseStatementHandle(handle);
        else
            session.ReleaseOrCache(handle, sql, scroll, HoldCursor);
    }

    // The same, while an exception from the statement is already on its way out. Releasing
    // talks to the server, and after a broken reply or a dropped connection it fails too;
    // that second failure must not replace the first, which is the one that says what
    // went wrong. The next use of the connection reports the broken state on its own.
    private void EndStatementUseAfterFailure(SqliSession session, SqliStatementHandle handle, string sql, bool scroll, bool stalePlan)
    {
        try
        {
            EndStatementUse(session, handle, sql, scroll, stalePlan);
        }
        catch (Exception)
        {
        }
    }

    private int _lastSerial;
    private long _lastSerial8;
    private long _lastBigSerial;
    private IfxSerialKind _lastSerialKind;

    /// <summary>
    /// The <c>SERIAL</c> value generated by the most recently executed <see cref="ExecuteNonQuery"/>
    /// (the SQ_DONE reply's serial field — Informix <c>sqlca.sqlerrd[1]</c>, what
    /// <c>dbinfo('sqlca.sqlerrd1')</c> would otherwise cost a second round trip to read).
    /// 0 when that statement did not insert into a SERIAL column. Only <see cref="ExecuteNonQuery"/>
    /// updates this — <see cref="ExecuteReader()"/> and <see cref="ExecuteScrollReader"/> do not.
    /// </summary>
    public int LastSerial => _lastSerial;

    /// <summary>
    /// The <c>SERIAL8</c> value generated by the most recently executed <see cref="ExecuteNonQuery"/>
    /// (from the server's SQ_INSERTDONE reply — what <c>dbinfo('serial8')</c> would otherwise cost
    /// a second round trip to read). 0 when that statement did not insert into a SERIAL8 column.
    /// Only <see cref="ExecuteNonQuery"/> updates this.
    /// </summary>
    public long LastSerial8 => _lastSerial8;

    /// <summary>
    /// The <c>BIGSERIAL</c> value generated by the most recently executed <see cref="ExecuteNonQuery"/>
    /// (from the server's SQ_INSERTDONE reply — what <c>dbinfo('bigserial')</c> would otherwise cost
    /// a second round trip to read). 0 when that statement did not insert into a BIGSERIAL column,
    /// or the server has not granted the BIGSERIAL capability. Only <see cref="ExecuteNonQuery"/>
    /// updates this.
    /// </summary>
    public long LastBigSerial => _lastBigSerial;

    /// <summary>
    /// Which of <see cref="LastSerial"/>, <see cref="LastSerial8"/> and <see cref="LastBigSerial"/>
    /// the most recently executed <see cref="ExecuteNonQuery"/> actually filled in, so callers do
    /// not have to probe all three. <see cref="IfxSerialKind.None"/> when the statement generated
    /// no auto-increment value. Read-only: the driver sets it from the server's reply.
    /// </summary>
    public IfxSerialKind LastSerialKind => _lastSerialKind;

    // A single INSERT feeds at most one auto-increment column, so whichever value came back
    // non-zero names the flavour. SERIAL8 and BIGSERIAL both arrive in SQ_INSERTDONE and the
    // server fills only the one the table actually has; SERIAL arrives separately in SQ_DONE.
    private static IfxSerialKind ClassifySerial(int serial, long serial8, long bigSerial) =>
        bigSerial != 0 ? IfxSerialKind.BigSerial
        : serial8 != 0 ? IfxSerialKind.Serial8
        : serial != 0 ? IfxSerialKind.Serial
        : IfxSerialKind.None;

    /// <summary>
    /// Executes a statement that does not return a result set and returns the number of affected rows. A row-returning statement
    /// (SELECT or procedure) is still run: the first row populates Output parameters and 1 or 0 is returned.
    /// </summary>
    public override int ExecuteNonQuery()
    {
        var session = BeginExecution(out string sql, out var binds);

        Activity? activity = StartCommandActivity();
        // Arm the CommandTimeout cancel timer for the whole execution (prepare +
        // execute/open + any RETURNING fetch), mirroring the cq.startCancel bracket
        // around IfxStatement.executeUpdate/execute.
        using var _ = session.BeginCommandTimeout(CommandTimeout);
        // Declared after the timeout scope so it disposes first (matches the original
        // finally-then-EndCommandTimeout order): ReportCommandStop/ClearReservedForAcquire
        // before the receive-timeout restore. See Prepare() for why ClearReservedForAcquire
        // must always run.
        using var scope = new IfxExecutionScope(session, _connection!.MetricsReporter, activity, Logging.CommandLogger);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            int rows = ExecuteWithOutParameterFallback(session, sql, binds);
            _lastSerial = session.SerialInserted;
            _lastSerial8 = session.Serial8Inserted;
            _lastBigSerial = session.BigSerialInserted;
            _lastSerialKind = ClassifySerial(_lastSerial, _lastSerial8, _lastBigSerial);
            LogMessages.CommandCompleted(Logging.CommandLogger, rows, stopwatch.ElapsedMilliseconds);
            if (activity is not null)
            {
                // A non-query's first (and only) response is the execute reply.
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

    // Informix "routine not found": also what an EXECUTE PROCEDURE gets when the argument
    // count does not match any routine of that name.
    private const int RoutineNotFoundSqlCode = -674;

    /// <summary>
    /// Runs the statement, transparently re-driving it over the FastPath channel when it
    /// turns out to name a routine that declares <c>OUT</c>/<c>INOUT</c> parameters — a
    /// routine the prepare/execute path cannot run at all (see <see cref="IfxOutParameterCall"/>).
    ///
    /// <para>The server announces that with sqlcode -9752 at prepare time, but only once the
    /// statement carries a placeholder for every declared argument. A
    /// <see cref="CommandType.StoredProcedure"/> command builds its <c>EXECUTE PROCEDURE</c>
    /// text from the <em>input</em> parameters alone (an output parameter conventionally
    /// receives a value the routine RETURNs, and takes no argument slot), so for such a
    /// routine the first attempt instead fails to resolve the name at all (-674). That case —
    /// and only that case — is re-probed with the output parameters counted in as arguments;
    /// if the re-probe does not produce -9752 the original error stands.</para>
    /// </summary>
    private int ExecuteWithOutParameterFallback(SqliSession session, string sql, List<SqliBindValue> binds)
    {
        // Already known to be such a routine: go straight to the FastPath call, skipping
        // both the prepare that would only be rejected again and the routine description.
        if (_outParameterRoutine is { } known && ReferenceEquals(known.Session, session))
        {
            var cached = known.FParam;
            return IfxOutParameterCall.Execute(
                session, known.Routine, _parameters, known.ReturnValueIsFirst, ref cached);
        }

        try
        {
            return ExecuteNonQueryCore(session, sql, binds);
        }
        catch (IfxException ex) when (ex.IfxSqlCode == SqliSession.RoutineHasOutParametersSqlCode)
        {
            if (session.OutParameterRoutine is not { } routine)
                throw;
            return RunOutParameterCall(session, routine, ReturnValueIsFirstParameter);
        }
        catch (IfxException ex) when (ex.IfxSqlCode == RoutineNotFoundSqlCode
                                      && CommandType == CommandType.StoredProcedure)
        {
            if (ProbeForOutParameterRoutine(session) is not { } routine)
                throw;
            return RunOutParameterCall(session, routine, returnValueIsFirst: false);
        }
    }

    // Runs the call and, once it has succeeded, remembers the routine so this command's
    // next execution costs one round trip instead of three (prepare-and-be-rejected +
    // describe + call) -- the same reuse a Prepare()d statement gets, and dropped by the
    // same events: a CommandText change, or the connection coming back on a different
    // physical session (see _preparedSession).
    private int RunOutParameterCall(
        SqliSession session, SqliOutParameterRoutine routine, bool returnValueIsFirst)
    {
        SqliFParam? fparam = null;
        int rows = IfxOutParameterCall.Execute(
            session, routine, _parameters, returnValueIsFirst, ref fparam);
        if (fparam is not null)
            _outParameterRoutine = (routine, fparam, session, returnValueIsFirst);
        return rows;
    }

    // The OUT/INOUT routine this command's text resolved to, its descriptor, and the
    // session both were resolved on (routine handles are per-session, exactly like
    // statement ids).
    private (SqliOutParameterRoutine Routine, SqliFParam FParam, SqliSession Session, bool ReturnValueIsFirst)?
        _outParameterRoutine;

    // The ODBC "{? = call ...}" escape makes the first parameter the routine's return value
    // positionally (the escape's leading '?'), independent of its ParameterDirection.
    private bool ReturnValueIsFirstParameter =>
        CommandType == CommandType.Text && ParsedCommand.HasReturnValuePlaceholder;

    // Re-prepares the routine call with a placeholder per declared argument (output
    // parameters included) purely to see whether the server answers -9752. Returns the
    // routine identity when it does, null for every other outcome — including a prepare
    // that succeeds, whose statement is released again: this only ever diagnoses, it never
    // becomes the statement that runs.
    private SqliOutParameterRoutine? ProbeForOutParameterRoutine(SqliSession session)
    {
        int arguments = 0;
        bool anyOutput = false;
        foreach (IfxParameter p in _parameters)
        {
            if (p.Direction == ParameterDirection.ReturnValue)
                continue;
            arguments++;
            anyOutput |= p.Direction == ParameterDirection.Output;
        }
        if (!anyOutput)
            return null; // the probe text would be the text that just failed

        string probe = IfxProcedureText.Build(CommandText, arguments);

        try
        {
            var handle = session.Prepare(probe, arguments);
            session.ReleaseStatementHandle(handle);
            return null;
        }
        catch (IfxException ex) when (ex.IfxSqlCode == SqliSession.RoutineHasOutParametersSqlCode)
        {
            return session.OutParameterRoutine;
        }
        catch (IfxException)
        {
            return null; // the re-probe failed on its own terms; the original error stands
        }
    }

    // Fast path: no parameters, no prepare, no statement cache, no Select Only guard -> one fused round trip (SQ_COMMAND).
    // Otherwise prepare/activate, then open a cursor for row-returning statements or execute directly.
    private int ExecuteNonQueryCore(SqliSession session, string sql, List<SqliBindValue> binds)
    {
        if (binds.Count == 0 && CommandType != CommandType.StoredProcedure
            && _preparedHandle is null && session.PreparedStatementCacheSize == 0
            && !session.SelectOnly)
        {
            // Non-parameterized statement: the one-round-trip SQ_COMMAND path. Skipped
            // when this command is holding a Prepare()d statement (an explicitly prepared
            // command always drives the prepared path below, so it has a statement to
            // reactivate next time), the session caches prepared statements (SQ_COMMAND
            // has no separate statement id to cache -- prepare/execute/release are one
            // opcode sequence -- so caching needs the real prepared path too), or the
            // session is SelectOnly: SQ_COMMAND sends SQ_EXECUTE in the same PDU as
            // SQ_NDESCRIBE, before the client ever sees the reply describing the
            // statement's type, so there is no point in the round trip where a SelectOnly
            // session could still reject it -- SelectOnly must always go through the
            // prepared path below instead, where Prepare() checks the type before this
            // method gets a handle to execute.
            session.ExecuteImmediate(sql);
            _connection?.RaiseInfoMessage(session.Warnings, session.Diagnostics);
            return session.RowsProcessed;
        }

        var (handle, mustRelease) = AcquireStatement(session, sql, binds.Count);
        int affected;
        try
        {
            if (session.DescribesResultSet)
            {
                // A routine that RETURNs values (EXECUTE PROCEDURE ... RETURNING):
                // its outputs arrive as a one-row result set. Fetch that row so any
                // output/return parameters can be filled, and report one affected row.
                session.OpenCursor(binds.Count > 0 ? binds : null);
                try
                {
                    affected = session.NextRow() ? 1 : 0;
                    if (affected == 1)
                        PopulateOutputParameters(session);
                }
                catch
                {
                    // Keep the fetch error; a close that fails after it adds nothing.
                    try { session.CloseCursor(); } catch (Exception) { }
                    throw;
                }
                session.CloseCursor();
            }
            else
            {
                session.ExecuteStatement(binds.Count > 0 ? binds : null);
                _connection?.RaiseInfoMessage(session.Warnings, session.Diagnostics);
                affected = session.RowsProcessed;
            }
        }
        catch (IfxException ex) when (ex.IfxSqlCode == StalePlanSqlCode)
        {
            if (mustRelease)
                EndStatementUseAfterFailure(session, handle, sql, scroll: false, stalePlan: true);
            throw;
        }
        catch
        {
            if (mustRelease)
                EndStatementUseAfterFailure(session, handle, sql, scroll: false, stalePlan: false);
            throw;
        }
        if (mustRelease)
            EndStatementUse(session, handle, sql, scroll: false, stalePlan: false);
        return affected;
    }

    /// <summary>First column of the first row, or null when there are no rows. Rides the normal cursor path (see docs/msg_flow.md).</summary>
    public override object? ExecuteScalar()
    {
        using var reader = ExecuteReader();
        return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    public new IfxDataReader ExecuteReader() => (IfxDataReader)ExecuteDbDataReader(CommandBehavior.Default);

    // A routine with OUT/INOUT parameters returns values, not a result set; say so instead of surfacing a bare -9752.
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var session = BeginExecution(out string sql, out var binds);

        Activity? activity = StartCommandActivity();
        // The operation-duration metric measures the execute/open call itself; the span
        // additionally spans the reader's fetch phase.
        using var scope = new IfxExecutionScope(session, _connection!.MetricsReporter, activity, Logging.CommandLogger);
        try
        {
            // On success the activity is handed to the reader, which ends the span when
            // it closes (covering the fetch phase); only failures end it here.
            return ExecuteReaderCore(session, binds, sql, behavior, activity);
        }
        catch (IfxException ex) when (ex.IfxSqlCode == SqliSession.RoutineHasOutParametersSqlCode)
        {
            // Informix cannot stream a result set out of a routine that declares OUT/INOUT
            // parameters -- one execution produces exactly one set of values, delivered
            // through the parameters themselves. Replace the server's bare "db:handle:unique"
            // payload with something the caller can act on.
            var actionable = new IfxException(
                "This routine declares OUT/INOUT parameters, so it cannot be read through a data reader " +
                "(Informix returns one set of values per execution, not a result set). Call ExecuteNonQuery() " +
                "and read the values from the command's Output/InputOutput/ReturnValue parameters " +
                $"({SqliSession.RoutineHasOutParametersSqlCode}).",
                SqliSession.RoutineHasOutParametersSqlCode);
            scope.Fail(actionable);
            throw actionable;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    // Handles the CommandBehavior flags (SchemaOnly returns metadata without running the statement, SingleRow fuses the close
    // with the first fetch, CloseConnection closes the connection with the reader). Statements without a result set are executed
    // at once and wrapped in an empty cursor that only carries the affected-row count.
    private DbDataReader ExecuteReaderCore(SqliSession session, List<SqliBindValue> binds, string sql,
        CommandBehavior behavior, Activity? activity)
    {
        Action? onClose = behavior.HasFlag(CommandBehavior.CloseConnection)
            ? () => _connection?.Close()
            : null;

        // CommandTimeout covers preparing and opening the cursor (through the first
        // fetch that OpenCursor primes) — the query-timeout window. Row-by-row
        // iteration afterward is not bounded by it, matching executeQueryImpl.
        using var _ = session.BeginCommandTimeout(CommandTimeout);

        var (handle, mustRelease) = AcquireStatement(session, sql, binds.Count);

        if (behavior.HasFlag(CommandBehavior.SchemaOnly))
        {
            // Describe only: expose the columns, never open a server cursor -- no wire
            // operation here can fail with a stale plan. Snapshot the metadata before
            // ending the statement's lifetime (that clears the session's column list).
            var columns = session.Columns.ToList();
            if (mustRelease)
                EndStatementUse(session, handle, sql, scroll: false, stalePlan: false);
            FirstResponse(activity);
            return new IfxDataReader(new SqliSchemaCursor(columns), onClose, _connection, activity)
            {
                BaseTable = ResolveBaseTable(sql, behavior),
            };
        }

        if (!session.DescribesResultSet)
        {
            // ExecuteReader over a non-SELECT: run it and hand back an empty reader.
            int rowsProcessed;
            try
            {
                session.ExecuteStatement(binds.Count > 0 ? binds : null);
                FirstResponse(activity);
                rowsProcessed = session.RowsProcessed;
            }
            catch (IfxException ex) when (ex.IfxSqlCode == StalePlanSqlCode)
            {
                if (mustRelease)
                    EndStatementUseAfterFailure(session, handle, sql, scroll: false, stalePlan: true);
                throw;
            }
            catch
            {
                if (mustRelease)
                    EndStatementUseAfterFailure(session, handle, sql, scroll: false, stalePlan: false);
                throw;
            }
            if (mustRelease)
                EndStatementUse(session, handle, sql, scroll: false, stalePlan: false);
            return new IfxDataReader(new SqliEmptyCursor(rowsProcessed), onClose, _connection, activity);
        }

        bool singleRow = behavior.HasFlag(CommandBehavior.SingleRow);
        try
        {
            // singleRow lets the session close the server cursor in the same PDU as the
            // fetch, so disposing the reader sends nothing.
            session.OpenCursor(binds.Count > 0 ? binds : null, HoldCursor, FetchBufferSize, singleRow);
        }
        catch (IfxException ex) when (ex.IfxSqlCode == StalePlanSqlCode)
        {
            if (mustRelease)
                EndStatementUseAfterFailure(session, handle, sql, scroll: false, stalePlan: true);
            throw;
        }
        catch
        {
            if (mustRelease)
                EndStatementUseAfterFailure(session, handle, sql, scroll: false, stalePlan: false);
            throw;
        }
        FirstResponse(activity);
        return new IfxDataReader(
            new SqliCursor(
                session, singleRow,
                mustRelease ? (handle, sql, HoldCursor) : null),
            onClose, _connection, activity)
        {
            BaseTable = ResolveBaseTable(sql, behavior),
        };
    }

    // A reader asked for its schema (SchemaOnly) or its keys (KeyInfo) gets the base table of a one-table
    // SELECT, which is what DbCommandBuilder needs. The catalog lookup of columns and primary key runs for
    // KeyInfo only. Any failure to look it up just leaves the reader without base-table information.
    private IfxBaseTable? ResolveBaseTable(string sql, CommandBehavior behavior)
    {
        if ((behavior & (CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo)) == 0 || _connection is null)
            return null;
        var table = IfxBaseTable.Find(sql);
        if (table is not null && behavior.HasFlag(CommandBehavior.KeyInfo))
        {
            try
            {
                table.Load(_connection);
            }
            catch (IfxException)
            {
                return null;
            }
            if (!table.Loaded)
                return null;
        }
        return table;
    }

    // Records the received-first-response span event once the first reply of the
    // execution (describe / execute / cursor open) has arrived.
    private void FirstResponse(Activity? activity)
    {
        if (activity is not null && _connection is not null)
            IfxActivitySource.ReceivedFirstResponse(activity, _connection.TracingOptions);
    }

    /// <summary>
    /// Opens a scrollable cursor over a SELECT and returns an
    /// <see cref="IfxScrollReader"/> for random-access navigation
    /// (<c>MoveFirst/MoveLast/MoveAbsolute/MoveRelative/MovePrevious/MoveNext</c>).
    /// Honors <see cref="HoldCursor"/>. Provider-specific: ADO.NET's
    /// <see cref="DbDataReader"/> is forward-only, so this is separate from
    /// <see cref="ExecuteReader()"/>.
    /// </summary>
    public IfxScrollReader ExecuteScrollReader()
    {
        var session = BeginExecution(out string sql, out var binds);

        Activity? activity = StartCommandActivity();
        using var scope = new IfxExecutionScope(session, _connection!.MetricsReporter, activity, Logging.CommandLogger);
        SqliStatementHandle? handle = null;
        bool mustRelease = true;
        try
        {
            using (session.BeginCommandTimeout(CommandTimeout))
            {
                (handle, mustRelease) = AcquireStatement(session, sql, binds.Count, scroll: true);
                if (!session.DescribesResultSet)
                {
                    if (mustRelease)
                        EndStatementUse(session, handle, sql, scroll: true, stalePlan: false);
                    throw new IfxException(
                        "ExecuteScrollReader requires a statement that returns rows (-79750).", -79750);
                }
                try
                {
                    session.OpenScrollCursor(HoldCursor, binds.Count > 0 ? binds : null, FetchBufferSize);
                }
                catch (IfxException ex) when (ex.IfxSqlCode == StalePlanSqlCode)
                {
                    if (mustRelease)
                        EndStatementUseAfterFailure(session, handle, sql, scroll: true, stalePlan: true);
                    throw;
                }
                catch
                {
                    if (mustRelease)
                        EndStatementUseAfterFailure(session, handle, sql, scroll: true, stalePlan: false);
                    throw;
                }
            }
            // Success: the scroll reader owns the span and ends it when it is disposed.
            FirstResponse(activity);
            return new IfxScrollReader(session, activity, mustRelease ? (handle, sql, HoldCursor) : null);
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    // Encodes the *input* parameters for SQ_BIND (positional, or by name for a
    // CommandType.Text statement written with named placeholders) — see
    // IfxParameterBinder for the shared rules (smart-LOB upload, JSON/BSON framing).
    private List<SqliBindValue> BuildBinds(SqliSession session) =>
        IfxParameterBinder.Build(session, CommandType, CommandType == CommandType.Text ? ParsedCommand : null, _parameters);

    // Builds the SQL actually sent to the server. For CommandType.StoredProcedure the
    // ADO.NET convention is that CommandText names the routine; it is wrapped as
    // EXECUTE PROCEDURE name(?, ...) via IfxProcedureText (shared with IfxBatchCommand).
    // CommandType.Text is sent with any named placeholders rewritten to positional '?'
    // (positional-only text is returned verbatim).
    private string EffectiveCommandText()
    {
        if (CommandType == CommandType.Text)
            return ParsedCommand.Text;
        if (CommandType != CommandType.StoredProcedure)
            return CommandText;

        int inputs = 0;
        foreach (IfxParameter p in _parameters)
            if (p.Direction is ParameterDirection.Input or ParameterDirection.InputOutput)
                inputs++;
        return IfxProcedureText.Build(CommandText, inputs);
    }

    // Assigns the returned row's columns, in order, to the output/return parameters
    // (in collection order) — the "output values arrive as a one-row result set"
    // model Informix uses for routine RETURNs.
    private void PopulateOutputParameters(SqliSession session)
    {
        int col = 0;
        foreach (IfxParameter p in _parameters)
        {
            if (p.Direction is not (ParameterDirection.Output
                or ParameterDirection.InputOutput or ParameterDirection.ReturnValue))
                continue;
            if (col >= session.Columns.Count)
                break;
            object value = session.DecodeColumn(col++);
            p.Value = value is DBNull ? null : value;
        }
    }

    // The common opening of every Execute* method: reserve a session, then resolve the statement
    // text and encode the binds. The reservation is normally released by the caller's
    // IfxExecutionScope, which does not exist yet at this point -- so anything that throws here
    // (a placeholder/parameter count mismatch, an unsupported parameter value, a mixed-placeholder
    // statement, a failed smart-LOB upload) has to release it itself. Left reserved, the session
    // would never be handed out again: every later statement on the connection would run on a
    // freshly minted sibling session, one more for each such failure.
    private SqliSession BeginExecution(out string sql, out List<SqliBindValue> binds)
    {
        var session = RequireOpenConnection();
        try
        {
            sql = EffectiveCommandText();
            binds = BuildBinds(session);
            LogExecuting(sql);
            return session;
        }
        catch
        {
            session.ClearReservedForAcquire();
            throw;
        }
    }

    // Reserves a session for this execution (the primary one, or a sibling if a reader is open); IfxExecutionScope releases the reservation.
    private SqliSession RequireOpenConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("IfxCommand.Connection has not been set.");
        // The transaction is the connection's; one that belongs to another connection cannot be
        // joined, and running anyway would silently execute outside the transaction the caller
        // assigned (a rollback of it would not undo this command's work).
        if (_transaction is { Connection: { } owner } && !ReferenceEquals(owner, _connection))
            throw new InvalidOperationException(
                "The command's Transaction was started on a different connection than the command's Connection.");
        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
        if (string.IsNullOrWhiteSpace(_commandText))
            throw new InvalidOperationException("The CommandText property has not been initialized.");
        // While the connection has a local transaction, a command must say it belongs to it (as with
        // SqlClient and the IBM provider). Informix would run the statement inside the transaction
        // either way; requiring the assignment makes code that forgets it fail loudly instead of
        // working only until the transaction is removed. A completed transaction counts as none.
        if (!IsInternal && _connection.ActiveTransaction is not null && _transaction is not { Connection: not null })
            throw new InvalidOperationException(
                "The command requires its Transaction property to be set to the connection's pending local transaction.");
        // AcquireSession never hands out a session a bulk loader has pinned, so there is no
        // ownership check to fail (and leak the reservation) after it.
        return _connection.AcquireSession();
    }

    /// <summary>
    /// Creates a new <see cref="IfxCommand"/> that copies this command's public settings —
    /// <see cref="CommandText"/>, <see cref="CommandType"/>, <see cref="CommandTimeout"/>,
    /// <see cref="DesignTimeVisible"/>, <see cref="UpdatedRowSource"/>, <see cref="HoldCursor"/>,
    /// <see cref="FetchBufferSize"/>, <see cref="Connection"/> and <see cref="Transaction"/> —
    /// plus a deep clone of every parameter in <see cref="Parameters"/> via
    /// <see cref="IfxParameter.Clone"/> (mutating a cloned parameter never touches the
    /// original). <see cref="Connection"/> and <see cref="Transaction"/> are copied by
    /// reference, not cloned themselves.
    ///
    /// <para>This command's own execution state does not travel: the clone starts with no
    /// held <see cref="Prepare"/>d statement and no cached named-placeholder parse, even when
    /// this command holds one.</para>
    /// </summary>
    public IfxCommand Clone()
    {
        var clone = new IfxCommand(_commandText, _connection)
        {
            CommandType = CommandType,
            CommandTimeout = CommandTimeout,
            DesignTimeVisible = DesignTimeVisible,
            UpdatedRowSource = UpdatedRowSource,
            HoldCursor = HoldCursor,
            FetchBufferSize = FetchBufferSize,
            Transaction = _transaction,
        };
        foreach (IfxParameter p in _parameters)
            clone.Parameters.Add(p.Clone());
        return clone;
    }

    object ICloneable.Clone() => Clone();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ReleaseHeldHandle();
        base.Dispose(disposing);
    }
}
