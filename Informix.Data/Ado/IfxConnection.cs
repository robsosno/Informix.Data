using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Informix.Data.Asf;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// A connection to an Informix database (<see cref="DbConnection"/>).
/// <see cref="Open"/> performs the login handshake (socket + authentication +
/// database open) and brings up the SQLI session that commands execute over.
/// </summary>
public sealed class IfxConnection : DbConnection, ICloneable
{
    private IfxConnectionStringBuilder _settings = new();
    private ConnectionState _state = ConnectionState.Closed;
    // At most one of _activeTransaction (local transaction) and _enlistment (System.Transactions) is set.
    private IfxTransaction? _activeTransaction;
    // Non-null while enlisted in an ambient System.Transactions transaction (TransactionScope).
    // Mutually exclusive with _activeTransaction: a connection has at most one kind of transaction.
    private IfxEnlistment? _enlistment;
    private AsfConnection? _asf;
    private SqliSession? _sqli;
    private string _serverVersion = string.Empty;
    // Set while open: the physical connection backing this facade, and the pool it came
    // from (null when Pooling=false — the physical connection is torn down on Close instead).
    private PooledSession? _physical;
    private IfxConnectionPool? _pool;
    // Non-null when this facade was handed out by an IfxDataSource (CreateDbConnection):
    // Open() rents from the data source's own pool rather than the process-wide registry.
    private readonly IfxDataSource? _boundDataSource;
    // The logging configuration in force: the data source's when bound, else the global
    // default resolved at Open(). Null-object until then.
    private IfxLoggingConfiguration _logging = IfxLoggingConfiguration.NullConfiguration;

    public IfxConnection() { }

    public IfxConnection(string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
            ConnectionString = connectionString;
    }

    // Bound constructor used by IfxDataSource.CreateDbConnection: the facade carries the
    // data source's (password-stripped-or-not) connection string and logging, and rents
    // its physical connection from the data source's pool.
    internal IfxConnection(IfxDataSource dataSource)
    {
        _boundDataSource = dataSource;
        ConnectionString = dataSource.ConnectionString;
        _logging = dataSource.LoggingConfiguration;
    }

    /// <summary>
    /// Creates a new <see cref="IfxConnection"/> that copies this connection's configuration
    /// and is always returned <see cref="ConnectionState.Closed"/> — no live session state
    /// (<see cref="AsfSession"/>, the SQLI session, a rented physical connection, an active
    /// transaction or enlistment) is copied, matching <c>SqlConnection</c>/<c>OdbcConnection</c>/
    /// <c>NpgsqlConnection</c>. Cloning an open connection is legal; the result is a closed
    /// twin that can be opened independently.
    ///
    /// <para>A connection bound to an <see cref="IfxDataSource"/> (returned by
    /// <see cref="IfxDataSource.CreateConnection"/>) clones through the same internal bound
    /// constructor, so the clone rents from the same data source's pool and shares its
    /// logging/tracing/metrics — cloning it via the public <see cref="ConnectionString"/>
    /// would silently drop the password when the data source's string is password-stripped.
    /// Either way, <see cref="PamHandler"/> and the <see cref="AsfConnectionFactory"/> test
    /// seam are copied onto the clone.</para>
    /// </summary>
    public IfxConnection Clone()
    {
        IfxConnection clone = _boundDataSource is not null
            ? new IfxConnection(_boundDataSource)
            : new IfxConnection(_settings.ConnectionString);
        clone.PamHandler = PamHandler;
        clone.AsfConnectionFactory = AsfConnectionFactory;
        return clone;
    }

    object ICloneable.Clone() => Clone();

    /// <summary>The logging configuration this connection (and its commands) log through.</summary>
    internal IfxLoggingConfiguration LoggingConfiguration => _logging;

    /// <summary>The tracing configuration this connection's commands trace through: the
    /// bound data source's, else the defaults (standalone connections trace with defaults).</summary>
    internal IfxTracingOptions TracingOptions => _boundDataSource?.TracingOptions ?? IfxTracingOptions.Default;

    /// <summary>The metrics reporter for this connection's commands, or null when the
    /// connection is standalone (no data source) — standalone connections emit no metrics.</summary>
    internal IfxMetricsReporter? MetricsReporter => _boundDataSource?.MetricsReporter;

    /// <summary>The database + endpoint attributes for the trace tags of this connection's commands.</summary>
    internal IfxActivitySource.TraceTarget TraceTarget =>
        new(Database, _asf?.Host ?? DataSource, _asf?.Port ?? 0);

    // Assigning a new string resets the settings and the cached code set.
    [AllowNull]
    public override string ConnectionString
    {
        get => _settings.ConnectionString;
        set { _settings = new IfxConnectionStringBuilder(value); _dbEncoding = null; _dbLocaleWasDiscovered = false; }
    }

    /// <summary>Typed view over the parsed connection string.</summary>
    public IfxConnectionStringBuilder Settings => _settings;

    /// <summary>
    /// The text-conversion rules for this connection (<see cref="IfxConnectionStringBuilder.TextFormat"/>):
    /// pass it to <see cref="IfxTextConvert"/> to format and parse DATE/DATETIME text the way the
    /// server does for this session. Taken from the connection string only; the process environment
    /// is never consulted.
    /// </summary>
    public IfxTextFormat TextFormat => _settings.TextFormat;

    /// <summary>Test seam: lets tests substitute an in-memory ASF transport.</summary>
    internal Func<AsfConnectionSettings, AsfConnection> AsfConnectionFactory { get; set; } =
        DefaultAsfConnectionFactory;

    internal static readonly Func<AsfConnectionSettings, AsfConnection> DefaultAsfConnectionFactory =
        static s => new AsfConnection(s);

    // The test seam replaced the transport: the connection does not talk to Host/Service at all.
    internal bool HasInjectedTransport =>
        !ReferenceEquals(AsfConnectionFactory, DefaultAsfConnectionFactory)
        || _boundDataSource?.HasInjectedTransport == true;

    /// <summary>The established ASF session; the SQLI layer builds on this.</summary>
    internal AsfConnection? AsfSession => _asf;

    /// <summary>The live SQLI session (message engine) once the connection is open — always the
    /// connection's default/primary session, the one it opened with. Reads through this stay the
    /// same session across a connection's lifetime; a caller starting a brand new
    /// statement should call <see cref="AcquireSession"/> instead.</summary>
    internal SqliSession? SqliSession => _sqli;

    /// <summary>
    /// Hands back a session this connection can run a fresh statement on right now: the default
    /// session when it is free, an already-idle sibling session when it is not, or a freshly
    /// minted sibling (registered on the same <see cref="SqliLink"/>, sharing its socket and
    /// connection-wide state) when every existing session is busy.
    ///
    /// <para>"Busy" means holding an open cursor (<see cref="Sqli.SqliSession.CursorOpen"/>),
    /// pinned by a bulk loader (<see cref="Sqli.SqliSession.StatementOwner"/>), or already
    /// reserved by a concurrent <see cref="AcquireSession"/> call that hasn't finished with it
    /// yet — a session with a merely-held prepared statement and no open cursor is otherwise
    /// still available for something else, exactly as the single-session driver already allowed.
    /// </para>
    ///
    /// <para>The scan and the reservation happen under the same lock as every wire exchange
    /// (<see cref="Sqli.SqliLink.Gate"/>) so they are atomic with each other — live multithreaded
    /// testing (2026-08-03) caught the race an unlocked version of this method has: two threads
    /// both observing the same session as free before either had reserved it, both getting
    /// handed it, and the second one's <c>Prepare()</c> clobbering the first's mid-flight. The
    /// caller must call <see cref="Sqli.SqliSession.ClearReservedForAcquire"/> once its own
    /// top-level call finishes (success or failure) — see <c>IfxCommand</c>'s entry points for
    /// the <c>try</c>/<c>finally</c> that does this.</para>
    /// </summary>
    internal SqliSession AcquireSession()
    {
        if (_sqli is not { } primary)
            throw new InvalidOperationException("The connection is not open.");
        using var gate = primary.Link.Gate.EnterScope();
        if (IsFreeForNewStatement(primary))
        {
            primary.MarkReservedForAcquire();
            return primary;
        }
        foreach (var sibling in primary.Link.Sessions)
        {
            if (!ReferenceEquals(sibling, primary) && IsFreeForNewStatement(sibling))
            {
                sibling.MarkReservedForAcquire();
                return sibling;
            }
        }
        var fresh = new SqliSession(primary.Link);
        fresh.MarkReservedForAcquire();
        return fresh;
    }

    // Free = no open cursor, no reader still attached to a cursor a commit/rollback closed (it
    // keeps reading the rows buffered on that session), not already handed out, and not pinned
    // by a bulk loader.
    private static bool IsFreeForNewStatement(SqliSession session) =>
        !session.CursorOpen && !session.HasAttachedReader
        && !session.IsReservedForAcquire && session.StatementOwner is null;

    // Runs one parameterless statement the driver itself issues (SET ISOLATION / SET TRANSACTION)
    // on whichever session is free. These are connection-wide settings, so any session will do --
    // and the default one may be busy serving an open reader, where ExecuteImmediate is refused.
    private void ExecuteOnFreeSession(string sql)
    {
        var session = AcquireSession();
        try
        {
            session.ExecuteImmediate(sql);
        }
        finally
        {
            session.ClearReservedForAcquire();
        }
    }

    /// <summary>
    /// The database character encoding for CHAR/NCHAR/VARCHAR/LVARCHAR/TEXT/CLOB text —
    /// the code set of <c>DB Locale</c> (e.g. <c>pl_pl.8859-2</c> → ISO-8859-2), or the
    /// ISO-8859-1 "NOENCODING" default when <c>DB Locale</c> is absent.
    ///
    /// <para>This is the single conversion the driver performs: .NET UTF-16 ⇆ the database
    /// code set. <c>CLIENT_LOCALE</c> is a legacy concept for non-Unicode clients
    /// and plays no part — a managed client stores text as Unicode and can render it
    /// to any encoding it wants on its own.</para>
    /// </summary>
    internal System.Text.Encoding DbEncoding => _dbEncoding ??= Wire.IfxCodeSet.Resolve(_settings.DbLocale);

    private System.Text.Encoding? _dbEncoding;

    /// <summary>The server identity from the handshake, when open.</summary>
    public IfxServerInfo? ServerInfo => _asf?.ServerInfo;

    /// <summary>The negotiated SQLI capability set (server version + feature bits), when open.</summary>
    public IfxServerCapabilities? ServerCapabilities =>
        _sqli?.Capabilities is { } caps ? new IfxServerCapabilities(caps) : null;


    /// <summary>
    /// Idle (not checked out) physical connections in the pool this connection would use —
    /// resolved the same way <see cref="Open"/> resolves it (the bound data source's pool, or
    /// else the process-wide pool for this connection's string), <b>not</b> a live handle on
    /// this instance. <c>0</c> when <c>Pooling=false</c>, and also <c>0</c> — not a throw — for
    /// a connection that has never been opened and whose string has no pool yet: the count
    /// describes the pool, not this connection, so a closed connection is a legitimate caller
    /// (the reference throws only from members that need a live ODBC handle; this one does
    /// not need one, so it does not throw). Sampled independently of
    /// <see cref="GetActiveConnectionsCount"/> and therefore advisory under concurrency — the
    /// two are not read atomically and no lock is taken to make them consistent with each other.
    /// </summary>
    public int GetIdleConnectionsCount => ResolveCountsPool()?.IdleCount ?? 0;

    /// <summary>Checked-out physical connections in the pool this connection would use
    /// (<c>Total - Idle</c>). Same resolution, zero cases and no-throw-when-closed behavior as
    /// <see cref="GetIdleConnectionsCount"/>; see there for details.</summary>
    public int GetActiveConnectionsCount
    {
        get
        {
            var pool = ResolveCountsPool();
            return pool is null ? 0 : pool.TotalCount - pool.IdleCount;
        }
    }

    // Resolves the pool backing GetIdleConnectionsCount/GetActiveConnectionsCount without
    // renting from it — the same resolution Open() performs (bound data source's pool, else
    // the string-keyed process-wide pool), independent of whether this instance is open.
    // Null covers both Pooling=false and "not created yet"; both report as 0 above.
    private IfxConnectionPool? ResolveCountsPool() =>
        _boundDataSource is not null
            ? _boundDataSource.Pool
            : _settings.Pooling && IfxConnectionPools.TryGet(PoolKey(_settings), out var pool)
                ? pool
                : null;

    // ---- fetch diagnostics (for tuning: fewer fetches/round trips == faster scans) ----

    /// <summary>Number of SQ_NFETCH messages sent on this connection since the last
    /// <see cref="ResetFetchStats"/> (a bigger fetch buffer means fewer of them).</summary>
    public long FetchMessagesSent => _sqli?.FetchMessagesSent ?? 0;

    /// <summary>Number of request PDUs flushed to the server since the last
    /// <see cref="ResetFetchStats"/> (each is one client→server→client round trip).</summary>
    public long RequestRoundTrips => _sqli?.RequestRoundTrips ?? 0;

    /// <summary>Zeroes <see cref="FetchMessagesSent"/> and <see cref="RequestRoundTrips"/>
    /// (and <see cref="PreparedStatementCacheHits"/>/<see cref="PreparedStatementCacheMisses"/>).</summary>
    public void ResetFetchStats() => _sqli?.ResetFetchStats();

    /// <summary>Prepared-statement cache hits since the last <see cref="ResetFetchStats"/> —
    /// see <see cref="IfxConnectionStringBuilder.PreparedStatementCacheSize"/>.</summary>
    public long PreparedStatementCacheHits => _sqli?.PreparedStatementCacheHits ?? 0;

    /// <summary>Prepared-statement cache misses since the last <see cref="ResetFetchStats"/> —
    /// see <see cref="IfxConnectionStringBuilder.PreparedStatementCacheSize"/>.</summary>
    public long PreparedStatementCacheMisses => _sqli?.PreparedStatementCacheMisses ?? 0;

    /// <summary>
    /// Raised when the server returns low-severity diagnostics (warnings) that do
    /// not abort the statement — the modern <see cref="EventHandler{TEventArgs}"/>
    /// counterpart of ADO.NET's <c>InfoMessage</c> event.
    /// </summary>
    public event EventHandler<IfxInfoMessageEventArgs>? InfoMessage;

    // Raises InfoMessage for a non-zero SQLI warning word (sqlca.sqlwarn) and for any
    // server-supplied diagnostics collected during the round trip. Called after a
    // statement completes so warnings surface instead of being swallowed. The
    // diagnostics come first: they carry the server's own text and SQLSTATE, whereas the
    // SQLWARN record is only a bit word this driver renders into a sentence.
    internal void RaiseInfoMessage(short warnings, IReadOnlyList<IfxError>? diagnostics = null)
    {
        var handler = InfoMessage;
        int diagnosticCount = diagnostics?.Count ?? 0;
        if (handler is null || (warnings == 0 && diagnosticCount == 0))
            return;

        var errors = new IfxErrorCollection();
        for (int i = 0; i < diagnosticCount; i++)
            errors.Add(diagnostics![i]);
        if (warnings != 0)
            errors.Add(new IfxError(
                $"The statement completed with warnings (SQLWARN=0x{(ushort)warnings:X4}).",
                nativeError: 0, source: "Informix.Data", sqlState: "01000"));

        LogMessages.InfoMessageReceived(_logging.ConnectionLogger, errors[0].Message);
        handler(this, new IfxInfoMessageEventArgs(errors));
    }

    /// <summary>
    /// Optional custom PAM handler for <see cref="IfxAuthentication.Pam"/> ports
    /// (multi-factor / one-time-token schemes). When null, the connection
    /// password answers password prompts.
    /// </summary>
    public IIfxPamHandler? PamHandler { get; set; }

    public override string Database => _settings.Database;

    /// <summary>True when the connection string asked for DELIMIDENT (double-quoted identifiers).</summary>
    internal bool DelimitedIdentifiersEnabled => _settings.DelimitedIdentifiers;

    public override string DataSource => _settings.Server.Length > 0 ? _settings.Server : _settings.Host;

    // Only an open connection has a server to ask; a closed or connecting one used to answer with an empty string.
    public override string ServerVersion => _state == ConnectionState.Open
        ? _serverVersion
        : throw new InvalidOperationException($"ServerVersion requires an open connection (state = {_state}).");

    /// <summary>Product name taken from the handshake's version string (for example "Informix Dynamic Server").</summary>
    public string ServerType
    {
        get
        {
            if (_state != ConnectionState.Open)
                throw new InvalidOperationException("The connection must be open to read ServerType.");
            string raw = _asf?.ServerInfo?.VersionNumber ?? "";
            int split = raw.IndexOf(" Version ", StringComparison.Ordinal);
            return split >= 0 ? raw[..split] : "Informix Dynamic Server";
        }
    }

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => _settings.LoginTimeout;

    /// <summary>Begins a local transaction (SQ_BEGIN). The overloads accept an isolation level and/or an explicit read-only flag; read-only defaults to the <c>Read Only</c> keyword.</summary>
    public new IfxTransaction BeginTransaction() =>
        BeginTransactionCore(IsolationLevel.Unspecified, _settings.ReadOnly);

    public new IfxTransaction BeginTransaction(IsolationLevel isolationLevel) =>
        BeginTransactionCore(isolationLevel, _settings.ReadOnly);

    /// <summary>
    /// Begins a transaction with an explicit read-only flag that overrides the
    /// <see cref="IfxConnectionStringBuilder.ReadOnly"/> keyword for this one transaction
    /// (MySqlConnector precedent). <c>isReadOnly: true</c> sends <c>SET TRANSACTION READ
    /// ONLY</c> right after <c>SQ_BEGIN</c> — server-enforced, SQLCODE -878 on a write
    /// attempt. See <see cref="IfxConnectionStringBuilder.ReadOnly"/> for exactly what
    /// that forbids and how it compares to <see cref="IfxConnectionStringBuilder.SelectOnly"/>.
    ///
    /// <para><see cref="IsolationLevel.Snapshot"/> combined with <c>isReadOnly: true</c> is
    /// live-verified sound: it sends <c>SET ISOLATION TO COMMITTED READ LAST COMMITTED</c>
    /// followed by <c>SET TRANSACTION READ ONLY</c> (two round trips), and the transaction is
    /// genuinely read-only. LAST COMMITTED's own lock-avoidance benefit needs a server new
    /// enough to have it (introduced in IDS 11.10) with the target table using ROW-level
    /// locking; anywhere it doesn't apply — an older server, or a PAGE-locked table — the
    /// isolation half quietly behaves as plain <see cref="IsolationLevel.ReadCommitted"/>
    /// instead (still correct, just without that benefit). The <c>USELASTCOMMITTED</c>
    /// onconfig parameter only governs whether a *plain* <c>COMMITTED READ</c>/<c>DIRTY
    /// READ</c> — without this driver's explicit <c>LAST COMMITTED</c> clause — gets upgraded
    /// implicitly; it does not gate this explicit request.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="isolationLevel"/> is
    /// <see cref="IsolationLevel.Chaos"/>, which Informix has no equivalent for.</exception>
    public IfxTransaction BeginTransaction(IsolationLevel isolationLevel, bool isReadOnly) =>
        BeginTransactionCore(isolationLevel, isReadOnly);

    /// <summary>Shorthand for <c>BeginTransaction(IsolationLevel.Unspecified, isReadOnly)</c>.</summary>
    public IfxTransaction BeginTransaction(bool isReadOnly) =>
        BeginTransactionCore(IsolationLevel.Unspecified, isReadOnly);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginTransactionCore(isolationLevel, _settings.ReadOnly);

    // One transaction at a time, and not while enlisted in a System.Transactions transaction. The isolation level and
    // read-only mode are applied after SQ_BEGIN so that they last only until the transaction ends.
    private IfxTransaction BeginTransactionCore(IsolationLevel isolationLevel, bool isReadOnly)
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open to begin a transaction.");
        if (_activeTransaction is not null)
            throw new InvalidOperationException("An Informix connection supports a single active transaction at a time.");
        if (_enlistment is not null)
            throw new InvalidOperationException(
                "Cannot begin a local transaction while the connection is enlisted in a System.Transactions transaction.");
        // Checked before SQ_BEGIN is sent, so a rejected level leaves no transaction open
        // on the connection at all.
        ValidateIsolationLevel(isolationLevel);

        _sqli!.ExecuteBegin(); // SQ_BEGIN (BEGIN WORK)
        ApplyCharacteristicsOrRollBack(_sqli, isolationLevel, isReadOnly);
        _activeTransaction = new IfxTransaction(this, isolationLevel, isReadOnly);
        LogMessages.TransactionBegan(_logging.TransactionLogger, DescribeCharacteristics(isolationLevel, isReadOnly));
        return _activeTransaction;
    }

    // Chaos has no Informix equivalent at all -- reject it before SQ_BEGIN is sent, rather
    // than let the server silently ignore what the caller asked for. Every other
    // level/readOnly combination is sendable (see ApplyTransactionCharacteristics), including
    // Snapshot+readOnly -- live-verified 2026-09-14 as sound, see that method's doc comment.
    private static void ValidateIsolationLevel(IsolationLevel level)
    {
        if (level == IsolationLevel.Chaos)
            throw new ArgumentOutOfRangeException(nameof(level), level,
                "IsolationLevel.Chaos has no Informix equivalent.");
    }

    private static string DescribeCharacteristics(IsolationLevel level, bool readOnly) =>
        readOnly ? $"{level}, ReadOnly" : level.ToString();

    // Maps an ADO.NET IsolationLevel onto the Informix SET ISOLATION statement, used only
    // by Open() for the "Isolation Level" keyword: it runs outside any transaction and must
    // last for the session, unlike ApplyTransactionCharacteristics below.
    // Informix has no separate SERIALIZABLE — REPEATABLE READ is its most restrictive level
    // — and no dedicated opcode: the level is a session SET. Unspecified leaves the server
    // default in force.
    private void ApplySessionIsolation(IsolationLevel level)
    {
        string? sql = level switch
        {
            IsolationLevel.ReadUncommitted => "SET ISOLATION TO DIRTY READ",
            IsolationLevel.ReadCommitted => "SET ISOLATION TO COMMITTED READ",
            IsolationLevel.RepeatableRead => "SET ISOLATION TO REPEATABLE READ",
            IsolationLevel.Serializable => "SET ISOLATION TO REPEATABLE READ",
            IsolationLevel.Snapshot => "SET ISOLATION TO COMMITTED READ LAST COMMITTED",
            _ => null, // Unspecified / Chaos / ReadCommitted-default -> server default
        };
        if (sql is not null)
            _sqli!.ExecuteImmediate(sql);
    }

    // Applies a transaction's isolation level and access mode right after SQ_BEGIN, via
    // SET TRANSACTION where Informix has an ANSI equivalent (everything but Snapshot) and
    // SET ISOLATION otherwise -- see InformixDataSetTransactionPrompt.md §2.1 for the full
    // table and the live-verified rationale. ValidateIsolationLevel has already ruled out
    // Chaos by the time this runs.
    //
    // SET TRANSACTION's characteristics last only until the transaction ends (live-verified:
    // §5 P3) -- unlike SET ISOLATION, which leaks onto the session -- so, Snapshot aside,
    // BeginTransaction no longer leaves any level behind after Commit/Rollback.
    // SQ_BEGIN has already opened the transaction when its characteristics are applied. If that
    // statement fails (the server rejects the level, the statement is interrupted, ...), nothing
    // on the client owns the transaction yet -- no IfxTransaction, no enlistment -- so it would
    // stay open on the server unseen: later statements would silently run inside it, nothing would
    // ever commit them, and Close() would return the connection to the pool still in it. Undo the
    // BEGIN before reporting the failure.
    private void ApplyCharacteristicsOrRollBack(SqliSession session, IsolationLevel level, bool readOnly)
    {
        try
        {
            ApplyTransactionCharacteristics(level, readOnly);
        }
        catch
        {
            try { session.ExecuteRollback(); }
            catch { /* the original failure is the one to report */ }
            throw;
        }
    }

    private void ApplyTransactionCharacteristics(IsolationLevel level, bool readOnly)
    {
        if (level == IsolationLevel.Snapshot)
        {
            // Snapshot's isolation clause never goes through SET TRANSACTION (Informix's
            // "Comparing SET TRANSACTION with SET ISOLATION" doc: Last Committed has no SET
            // TRANSACTION equivalent). This SET ISOLATION deliberately leaks onto the session
            // after the transaction ends, same as IBM's own drivers; no restore logic is
            // added for it (§2.1).
            //
            // Combined with a read-only transaction: live-tested 2026-09-14 as sound and
            // fully functional -- SELECT keeps working, every write is rejected with -878,
            // and the two statements never conflict (order doesn't trip the "SET TRANSACTION
            // must be the transaction's first statement" rule, since SET ISOLATION doesn't
            // count as a statement for that purpose either -- see §5 P2/P7). The one caveat
            // is LAST COMMITTED itself: it needs a server new enough to have the feature at
            // all (introduced in IDS 11.10) and the target table using ROW-level locking, or
            // the isolation half quietly behaves as plain COMMITTED READ instead of skipping
            // locked rows -- still correct, just not the special lock-avoidance behavior LAST
            // COMMITTED promises (confirmed against the live test server, IDS 9.59, which
            // predates the feature -- NOT a USELASTCOMMITTED onconfig effect: that parameter
            // only upgrades a *plain* COMMITTED READ/DIRTY READ implicitly and has no bearing
            // on this driver's explicit LAST COMMITTED clause).
            ExecuteOnFreeSession("SET ISOLATION TO COMMITTED READ LAST COMMITTED");
            if (readOnly)
                ExecuteOnFreeSession("SET TRANSACTION READ ONLY");
            return;
        }

        string? isolationClause = level switch
        {
            IsolationLevel.Unspecified => null,
            IsolationLevel.ReadUncommitted => "READ UNCOMMITTED",
            IsolationLevel.ReadCommitted => "READ COMMITTED",
            IsolationLevel.RepeatableRead => "REPEATABLE READ",
            IsolationLevel.Serializable => "SERIALIZABLE",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unrecognized isolation level."),
        };

        string? sql = (isolationClause, readOnly) switch
        {
            (null, false) => null, // server default, read-write: nothing to send
            (null, true) => "SET TRANSACTION READ ONLY",
            (not null, false) => $"SET TRANSACTION ISOLATION LEVEL {isolationClause}",
            (not null, true) => $"SET TRANSACTION READ ONLY, ISOLATION LEVEL {isolationClause}",
        };
        if (sql is not null)
            ExecuteOnFreeSession(sql);
    }

    // Called by IfxTransaction.Commit/Rollback.
    //
    // The transaction stops being the connection's active one only once the server has confirmed
    // the commit/rollback. When the exchange fails (a constraint checked at commit, a bulk
    // loader's pre-commit flush, an interrupt) the server-side transaction may well still be
    // open; keeping it registered here is what lets the IfxTransaction's Dispose -- or, failing
    // that, Close() -- roll it back. Forgetting it up front would leave the connection inside a
    // transaction nothing knows about: the next BeginTransaction would fail with "already in
    // transaction", and a pooled connection would be handed to its next user still holding the
    // old transaction's locks.
    internal void CompleteTransaction(IfxTransaction transaction, bool commit)
    {
        if (!ReferenceEquals(_activeTransaction, transaction))
            return;
        if (_sqli is null)
        {
            _activeTransaction = null;
            return;
        }
        if (commit)
        {
            _sqli.ExecuteCommit();   // SQ_CMMTWORK
            _activeTransaction = null;
            LogMessages.TransactionCommitted(_logging.TransactionLogger);
        }
        else
        {
            try
            {
                _sqli.ExecuteRollback(); // SQ_RBWORK
            }
            finally
            {
                // A rollback that fails is not retried: there is nothing more the client can
                // do with the transaction, and Close() discards a connection it cannot clean.
                _activeTransaction = null;
            }
            LogMessages.TransactionRolledBack(_logging.TransactionLogger);
        }
    }

    /// <summary>
    /// Enlists this open connection in a <see cref="System.Transactions.Transaction"/>
    /// (usually the ambient <c>Transaction.Current</c> of a <c>TransactionScope</c>). The
    /// connection participates via promotable single-phase enlistment: while it is the only
    /// resource manager in the transaction it stays a lightweight local transaction (no
    /// MSDTC). Passing <c>null</c> when not already enlisted is a no-op.
    /// </summary>
    public override void EnlistTransaction(System.Transactions.Transaction? transaction)
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open to enlist in a transaction.");
        if (_activeTransaction is not null)
            throw new InvalidOperationException(
                "Cannot enlist in a System.Transactions transaction while a local transaction is active.");

        if (_enlistment is not null)
        {
            // Re-enlisting in the same transaction is a harmless no-op; anything else conflicts.
            if (transaction is not null && _enlistment.Transaction.Equals(transaction))
                return;
            throw new InvalidOperationException(
                "The connection is already enlisted in a System.Transactions transaction.");
        }

        if (transaction is null)
            return; // "unenlist" with no active enlistment — nothing to do

        EnlistCore(transaction);
    }

    // Creates the PSPE enlistment and registers it with the transaction manager. The
    // registration calls IfxEnlistment.Initialize() synchronously, which runs SQ_BEGIN.
    // EnlistPromotableSinglePhase returns false when the transaction already has a
    // promotable delegate or has already promoted — i.e. a second resource manager is
    // present and the transaction would have to promote to XA, which this driver cannot do.
    private void EnlistCore(System.Transactions.Transaction transaction)
    {
        // System.Transactions has no read-only flag of its own; an enlisted transaction
        // takes it from the Read Only keyword instead (§2.2). Chaos is checked up front,
        // same as the local-transaction path, so a rejected level never sends SQ_BEGIN and
        // never registers the enlistment.
        if (transaction.IsolationLevel == System.Transactions.IsolationLevel.Chaos)
            throw new ArgumentOutOfRangeException(nameof(transaction), transaction.IsolationLevel,
                "System.Transactions.IsolationLevel.Chaos has no Informix equivalent.");
        bool isReadOnly = _settings.ReadOnly;

        var enlistment = new IfxEnlistment(this, transaction, _sqli!, _physical!, _pool, _logging, isReadOnly);
        if (!transaction.EnlistPromotableSinglePhase(enlistment))
            throw new NotSupportedException(
                "This System.Transactions transaction already involves another resource manager. " +
                "The Informix driver supports only single-connection (non-distributed) transactions; " +
                "distributed/XA promotion is not implemented.");
        _enlistment = enlistment;
    }

    // Called by IfxEnlistment.Initialize() on the enlisting thread: open the local
    // transaction and apply the ambient transaction's isolation level plus the Read Only
    // keyword's read-only flag. Runs on the enlistment's captured session (which is this
    // connection's live session). EnlistCore has already validated the combination.
    internal void BeginEnlistedTransaction(
        SqliSession session, System.Transactions.IsolationLevel isolation, bool isReadOnly)
    {
        var level = MapIsolationLevel(isolation);
        session.ExecuteBegin(); // SQ_BEGIN (BEGIN WORK)
        ApplyCharacteristicsOrRollBack(session, level, isReadOnly);
        LogMessages.TransactionBegan(_logging.TransactionLogger, DescribeCharacteristics(level, isReadOnly));
    }

    // Called by IfxEnlistment when the transaction resolves while the facade is still open:
    // the physical connection stays with the facade, so we just drop the enlistment state.
    internal void OnEnlistmentResolvedWhileOpen(IfxEnlistment enlistment)
    {
        if (ReferenceEquals(_enlistment, enlistment))
            _enlistment = null;
    }

    // Maps a System.Transactions isolation level onto the ADO.NET one that
    // ApplyTransactionCharacteristics understands.
    private static IsolationLevel MapIsolationLevel(System.Transactions.IsolationLevel isolation) => isolation switch
    {
        System.Transactions.IsolationLevel.ReadUncommitted => IsolationLevel.ReadUncommitted,
        System.Transactions.IsolationLevel.ReadCommitted => IsolationLevel.ReadCommitted,
        System.Transactions.IsolationLevel.RepeatableRead => IsolationLevel.RepeatableRead,
        System.Transactions.IsolationLevel.Serializable => IsolationLevel.Serializable,
        System.Transactions.IsolationLevel.Snapshot => IsolationLevel.Snapshot,
        _ => IsolationLevel.Unspecified, // Chaos / Unspecified -> server default
    };

    // SQ_DBOPEN is rejected on a connection that was opened with a database, so changing database means reconnecting.
    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (_state != ConnectionState.Open)
        {
            ForgetDiscoveredDbLocale();
            _settings.Database = databaseName;
            return;
        }
        if (_activeTransaction is not null || _enlistment is not null)
            throw new InvalidOperationException("Cannot change the database while a transaction is active.");

        // The bare SQLI SQ_DBOPEN re-opens a database on the live session, but
        // Informix rejects it with -759 on a connection established with an explicit
        // database — "you cannot select another database until you disconnect".
        // Every connection this provider makes carries its database in the login
        // PDU, so the portable way to switch is to reconnect; that also drops all
        // server-side statements.
        Close();
        // The code set Open() discovered was the old database's; the new one may use another,
        // and connecting with the stale locale would decode its text wrongly (or be refused by
        // the server's locale check). Let Open() look it up afresh.
        ForgetDiscoveredDbLocale();
        _settings.Database = databaseName;
        Open();
    }

    /// <summary>
    /// Opens the connection: resolves the settings, dials the server and runs
    /// the ASF handshake. On success <see cref="ServerVersion"/> carries the
    /// server's version string.
    /// </summary>
    public override void Open()
    {
        if (_state != ConnectionState.Closed)
            throw new InvalidOperationException($"The connection is not closed (state = {_state}).");

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
            throw new InvalidOperationException("The ConnectionString property has not been initialized.");
        ValidateSettings();
        // Resolve the logging configuration: the data source's when bound, else the
        // process-wide default set via IfxLoggingConfiguration.InitializeLogging.
        if (_boundDataSource is null)
            _logging = IfxLoggingConfiguration.GlobalDefault;

        // Auto-discover the database code set when the caller didn't specify DB Locale,
        // so the encoding is correct (and the server's per-database locale check passes)
        // without the caller knowing the DB_LOCALE. Resolved once per database, then cached.
        DiscoverDbLocaleIfNeeded();
        LogMessages.OpeningConnection(_logging.ConnectionLogger, DataSource, Database, _settings.ToStringWithoutPassword());

        _state = ConnectionState.Connecting;
        try
        {
            if (_boundDataSource is not null)
            {
                // Rent from the data source's own pool (or build an unpooled physical),
                // passing this connection's PAM handler so it beats the source's.
                (_physical, _pool) = _boundDataSource.Rent(_settings.LoginTimeout, PamHandler);
            }
            else if (_settings.Pooling)
            {
                // Draw a physical connection from the string-keyed pool (creating the pool,
                // and warming Min Pool Size, only on the first Open for this string).
                var capturedPam = PamHandler;
                var logging = _logging;
                var tracing = TracingOptions;
                _pool = IfxConnectionPools.GetOrCreate(
                    PoolKey(_settings),
                    () => new IfxConnectionPool(
                        _settings.MinPoolSize, _settings.MaxPoolSize, _settings.ConnectionLifetime,
                        BuildPhysicalFactory(() => capturedPam, logging, initSql: null, tracing), logging));
                _physical = _pool.Rent(_settings.LoginTimeout); // blocks up to ConnectionTimeout on saturation
            }
            else
            {
                var capturedPam = PamHandler;
                _pool = null;
                var (asf, sqli, ver) = BuildPhysicalFactory(() => capturedPam, _logging, initSql: null, TracingOptions)();
                _physical = new PooledSession(asf, sqli, ver, generation: 0);
            }

            _asf = _physical.Asf;
            _sqli = _physical.Sqli;
            _serverVersion = _physical.ServerVersion;
            _state = ConnectionState.Open;
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
            LogMessages.OpenedConnection(_logging.ConnectionLogger, DataSource, Database, _settings.ToStringWithoutPassword());
        }
        catch
        {
            // Rent / the factory already dispose whatever they created on failure.
            _physical = null;
            _pool = null;
            _sqli = null;
            _asf = null;
            _state = ConnectionState.Closed;
            throw;
        }

        // Pin the session isolation level when — and only when — the connection string asked
        // for one. Unspecified (the default) sends nothing at all: SET ISOLATION is rejected on
        // a database created without logging, so a driver that always issued it would break
        // every unlogged-database connection. Must precede the enlist below, which begins a
        // transaction and applies the ambient transaction's own level.
        if (_settings.IsolationLevel != IsolationLevel.Unspecified)
        {
            try
            {
                ApplySessionIsolation(_settings.IsolationLevel);
            }
            catch
            {
                Close(); // return the physical to the pool rather than leak it
                throw;
            }
        }

        // Auto-enlist in the ambient System.Transactions transaction, if any (the ADO.NET
        // 'Enlist' keyword, default true). Done after the connection is fully open; a failure
        // here (e.g. the scope already has another resource manager and would need XA) undoes
        // the open so the physical connection is returned to the pool rather than leaked.
        if (_settings.Enlist && System.Transactions.Transaction.Current is { } ambient)
        {
            try
            {
                EnlistCore(ambient);
            }
            catch
            {
                Close();
                throw;
            }
        }
    }

    /// <summary>
    /// Releases the physical connection. A connection that is enlisted in a still-live System.Transactions transaction is handed off
    /// to the enlistment and goes back to the pool only when the transaction ends. Otherwise: leftover statements are released; a connection
    /// that was interrupted or still has an open cursor is discarded; an open local transaction is rolled back; then it returns to the pool.
    /// </summary>
    public override void Close()
    {
        var previous = _state;
        if (previous == ConnectionState.Closed)
            return;

        try
        {
            CloseCore();
        }
        finally
        {
            // The state is Closed whichever way the clean-up went, so the event is raised either way.
            OnStateChange(new StateChangeEventArgs(previous, ConnectionState.Closed));
        }
    }

    private void CloseCore()
    {
        if (_state == ConnectionState.Closed)
            return;

        var connLogger = _logging.ConnectionLogger;
        LogMessages.ClosingConnection(connLogger, DataSource, Database);

        var physical = _physical;
        var pool = _pool;
        bool hadTransaction = _activeTransaction is not null;
        var enlistment = _enlistment;
        _activeTransaction = null;
        _enlistment = null;
        _physical = null;
        _pool = null;
        _asf = null;
        _sqli = null;
        _serverVersion = string.Empty;
        _state = ConnectionState.Closed;

        if (physical is null)
        {
            LogMessages.ClosedConnection(connLogger, DataSource, Database);
            return;
        }

        // Still enlisted in a live ambient (System.Transactions) transaction: the physical
        // connection stays alive under the enlistment, which will COMMIT/ROLLBACK WORK and
        // return it to the pool when the transaction manager resolves the transaction. Do not
        // roll back or return it here. (If the transaction already resolved in a race, the
        // hand-off is declined and we fall through to the normal return path.)
        if (enlistment is not null && enlistment.TryHandOffOnClose())
        {
            LogMessages.ClosedConnection(connLogger, DataSource, Database);
            return;
        }

        if (pool is null)
        {
            physical.Dispose(); // non-pooled: orderly SQ_EXIT then close the socket
            LogMessages.ClosedPhysicalConnection(connLogger);
            LogMessages.ClosedConnection(connLogger, DataSource, Database);
            return;
        }

        // Pooled: hand the physical connection back for reuse. A still-open cursor/statement
        // (an abandoned reader) makes it unsafe to reuse — discard it; otherwise roll back any
        // uncommitted transaction (SQ_RBWORK) and return it. Any error along the way discards.
        try
        {
            // A command that called Prepare() and never released it (no Dispose, no
            // CommandText/Connection change before Close()) leaves a statement prepared
            // but otherwise idle — release it here so it doesn't needlessly discard an
            // otherwise-healthy connection below. Never touches a statement backing a
            // still-open cursor (that's the abandoned-reader case IsIdle must still catch).
            // With MARS a connection can have sibling sessions beyond the default one (see
            // AcquireSession), so this cleanup and the idleness check below both cover every
            // session on the link, not just the default.
            foreach (var session in physical.Sqli.Link.Sessions)
                if (!session.CursorOpen && !session.HasAttachedReader && session.StatementId != -1)
                    session.ReleaseStatement();
            if (physical.Asf.OutOfBandSent || !physical.Sqli.Link.IsIdle)
            {
                // Interrupted (a cancel byte was sent — it may still be in flight, so skip the
                // rollback exchange below too) or an abandoned reader on any session: unsafe to reuse.
                pool.Discard(physical);
                LogMessages.ClosedConnection(connLogger, DataSource, Database);
                return;
            }
            if (hadTransaction)
                physical.Sqli.ExecuteRollback();
            pool.Return(physical);
        }
        catch
        {
            pool.Discard(physical);
        }
        LogMessages.ClosedConnection(connLogger, DataSource, Database);
    }

    /// <summary>Empties and rebuilds the pool that <paramref name="connection"/>'s string maps
    /// to; in-use connections are retired when they are next closed (ADO.NET <c>ClearPool</c>).</summary>
    public static void ClearPool(IfxConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IfxConnectionPools.Clear(PoolKey(connection._settings));
    }

    /// <summary>Empties every connection pool in the process (ADO.NET <c>ClearAllPools</c>).</summary>
    public static void ClearAllPools() => IfxConnectionPools.ClearAll();

    // Canonical, order-independent pool key: the exact connection string reduced to a sorted
    // list of key=value pairs so "a=1;b=2" and "b=2;a=1" share a pool.
    internal static string PoolKey(IfxConnectionStringBuilder settings) =>
        string.Join(";", settings.Keys.Cast<string>()
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => $"{k.ToLowerInvariant()}={settings[k]}"));

    // Builds a factory that produces a fresh physical connection (ASF transport + brought-up
    // SQLI session). It captures only immutable snapshots — never `this` — so the pool, which
    // outlives any single facade, does not pin a connection instance. The PAM handler is
    // resolved lazily (per physical connection) so a data source can let a per-connection
    // handler override its own; initialization SQL runs once per new physical connection,
    // after bring-up and before first use.
    internal IfxConnectionPool.PhysicalFactory BuildPhysicalFactory(
        Func<IIfxPamHandler?> pamResolver, IfxLoggingConfiguration logging, IReadOnlyList<string>? initSql,
        IfxTracingOptions? tracingOptions = null, IfxMetricsReporter? metrics = null)
    {
        var asfFactory = AsfConnectionFactory;
        // Resolve the endpoint once (a sqlhosts lookup may fill host/port/SSL/auth);
        // both the ASF settings and the authentication decision key off the result.
        var endpoint = ResolveEndpoint();
        var asfSettings = BuildAsfSettings(endpoint);
        int fetchBufferSize = _settings.FetchBufferSize;
        bool optofc = _settings.Optofc;
        bool trimTrailingSpaces = _settings.TrimTrailingSpaces;
        bool largeRowFormats = _settings.LargeRowFormats;
        int preparedStatementCacheSize = _settings.PreparedStatementCacheSize;
        bool selectOnly = _settings.SelectOnly;
        var authentication = endpoint.Authentication;
        string userId = _settings.UserId;
        string password = _settings.Password;
        string server = DataSource;
        string database = _settings.Database;
        var connLogger = logging.ConnectionLogger;
        var tracing = tracingOptions ?? IfxTracingOptions.Default;

        return () =>
        {
            // Optional "connect" span around the whole physical open (socket + handshake
            // + SQLI bring-up); it also captures a failed open. Guarded by HasListeners.
            Activity? openActivity = IfxActivitySource.IsEnabled
                ? IfxActivitySource.PhysicalOpenStart(
                    new IfxActivitySource.TraceTarget(database, endpoint.Host, endpoint.Port), tracing)
                : null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var asf = asfFactory(asfSettings);
            try
            {
                var info = asf.Connect();
                var sqli = new SqliSession(asf)
                {
                    ConnectionFetchBufferSize = fetchBufferSize,
                    Optofc = optofc,
                    TrimTrailingSpaces = trimTrailingSpaces,
                    RequestLargeRowFormats = largeRowFormats,
                    PreparedStatementCacheSize = preparedStatementCacheSize,
                    SelectOnly = selectOnly,
                };
                // Bring the SQLI session up in the server-required order: SQ_PROTOCOLS,
                // then PAM authentication on PAM ports, then SQ_VERSION.
                IIfxPamHandler? handler = authentication == IfxAuthentication.Pam
                    ? pamResolver() ?? new PasswordPamHandler(
                        userId.Length > 0 ? userId : null,
                        password.Length > 0 ? password : null)
                    : null;
                sqli.BringUp(handler);
                // Run any per-physical initialization SQL (SET LOCK MODE / ISOLATION / PDQ)
                // on the fresh, fully brought-up connection before it is handed out.
                if (initSql is { Count: > 0 })
                    foreach (string statement in initSql)
                        sqli.ExecuteImmediate(statement);
                string ver = sqli.ServerVersion.Length > 0 ? sqli.ServerVersion : info.VersionNumber;
                stopwatch.Stop();
                metrics?.ReportConnectionCreateTime(stopwatch.Elapsed);
                LogMessages.OpenedPhysicalConnection(connLogger, server, database, stopwatch.ElapsedMilliseconds);
                if (openActivity is not null)
                    IfxActivitySource.CommandStop(openActivity);
                return (asf, sqli, ver);
            }
            catch (Exception ex)
            {
                if (openActivity is not null)
                    IfxActivitySource.SetException(openActivity, ex);
                asf.Dispose();
                throw;
            }
        };
    }

    // Factory methods for the other execution APIs: row batch, DbBatch, bulk loader, smart LOBs, schema.
    protected override DbCommand CreateDbCommand() => new IfxCommand(null, this);

    // The local transaction BeginTransaction started and that has not completed yet, if any.
    internal IfxTransaction? ActiveTransaction => _activeTransaction;

    // A command the driver runs on the caller's own connection for its own purposes (catalog queries,
    // command-builder lookups): not subject to the "command must carry the pending transaction" rule.
    internal IfxCommand CreateInternalCommand() => new(null, this) { IsInternal = true };

    // Lets DbProviderFactories.GetFactory(connection) and provider-agnostic code find the factory.
    protected override DbProviderFactory? DbProviderFactory => InformixClientFactory.Instance;

    public new IfxCommand CreateCommand() => new(null, this);

    /// <summary>Creates a batch that runs one parameterized statement over many value rows.</summary>
    public IfxRowBatch CreateRowBatch(string commandText) => new(commandText, this);

    public override bool CanCreateBatch => true;

    protected override DbBatch CreateDbBatch() => CreateBatch();

    /// <summary>Creates a standard ADO.NET batch (<see cref="IfxBatch"/>) of possibly
    /// heterogeneous statements bound to this connection.</summary>
    public new IfxBatch CreateBatch() => new(this);

    /// <summary>
    /// Creates a streaming bulk loader for one parameterized INSERT (or a row-less
    /// EXECUTE PROCEDURE). Rows queued
    /// with <see cref="IfxBulkLoader.Put(object?[])"/> accumulate client-side and travel in
    /// batches. Declare the parameter types with
    /// <see cref="IfxBulkLoader.DefineParameters(IfxType[])"/> before the first row.
    /// </summary>
    public IfxBulkLoader CreateBulkLoader(string commandText) => new(this, commandText);

    /// <summary>Returns the list of supported schema collections (the MetaDataCollections table).</summary>
    public override DataTable GetSchema() => IfxSchema.Get(this, IfxSchema.MetaDataCollections, null);

    /// <summary>Returns the named schema collection.</summary>
    public override DataTable GetSchema(string collectionName) => IfxSchema.Get(this, collectionName, null);

    /// <summary>Returns the named schema collection filtered by the given restrictions.</summary>
    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues) =>
        IfxSchema.Get(this, collectionName, restrictionValues);

    /// <summary>Empties every connection pool in the process (ADO.NET alias of <see cref="ClearAllPools"/>).</summary>
    public void ReleaseObjectPool() => ClearAllPools();

    /// <summary>
    /// Prepares a new BLOB smart large object. Nothing is created on the server yet:
    /// <see cref="IfxSmartLOB.Open"/> creates it, using whatever create-time properties
    /// (<see cref="IfxSmartLOB.SBSpace"/>, <see cref="IfxSmartLOB.ExtentSize"/>, ...) were set
    /// in between -- the same lifecycle as Informix.Net.Core. Bind the opened object (or its
    /// <see cref="IfxSmartLOB.GetLocator"/>) into an INSERT/UPDATE to store it.
    /// </summary>
    public IfxBlob GetIfxBlob() => new(this);

    /// <summary>
    /// Prepares a new BLOB that <see cref="IfxSmartLOB.Open"/> creates with the storage settings of
    /// <paramref name="tabname"/>.<paramref name="colname"/> -- its sbspace from
    /// <c>PUT ... IN (...)</c> and its other column-level attributes.
    /// </summary>
    public IfxBlob GetIfxBlob(string tabname, string colname) => new(this, tabname, colname);

    /// <summary>Prepares a new CLOB; see <see cref="GetIfxBlob()"/>.</summary>
    public IfxClob GetIfxClob() => new(this);

    /// <summary>Prepares a new CLOB with a column's storage settings; see <see cref="GetIfxBlob(string, string)"/>.</summary>
    public IfxClob GetIfxClob(string tabname, string colname) => new(this, tabname, colname);

    // Required: Database, and either a Server (resolved through sqlhosts) or Host plus a Service/port.
    private void ValidateSettings()
    {
        if (_settings.Database.Length == 0)
            throw new IfxException("The connection string is missing the required 'Database'.", -79760);
        // A 'Server' (INFORMIXSERVER) lets sqlhosts supply the host/port, so Host and
        // Service are only required when no Server is given to resolve.
        if (_settings.Server.Length == 0)
        {
            if (_settings.Host.Length == 0)
                throw new IfxException("The connection string is missing the required 'Host'.", -79758);
            if (_settings.Service.Length == 0)
                throw new IfxException("The connection string must specify a 'Service'/port or a 'Server' (INFORMIXSERVER).", -25596);
        }
        // Surfaces an unparseable 'Isolation Level' here, before dialing the server, rather
        // than after the physical connection is already up (the property getter throws).
        // 'Read Only' is a plain bool (like Pooling/Enlist/Optofc) and never throws, so it
        // needs no similar check here.
        _ = _settings.IsolationLevel;
    }

    /// <summary>Test seam: builds the sqlhosts resolver (lets tests inject file/registry sources).</summary>
    internal Func<SqlhostsResolver> SqlhostsResolverFactory { get; set; } = static () => new SqlhostsResolver();

    // The resolved TCP endpoint (post sqlhosts lookup) plus the authentication mode
    // to use — which sqlhosts may imply (s=4 → PAM) unless the caller set it.
    private readonly record struct ResolvedEndpoint(string Host, int Port, bool UseSsl, bool IsGroup, IfxAuthentication Authentication);

    // Resolves Host/Port from the connection string, consulting sqlhosts when the
    // Service is not a numeric port or when only a Server (INFORMIXSERVER) is given.
    // Explicit Host + numeric Service always win and skip the lookup entirely.
    private ResolvedEndpoint ResolveEndpoint()
    {
        string host = _settings.Host;
        string service = _settings.Service;
        string server = _settings.Server;
        bool useSsl = _settings.SslConnection;
        bool authExplicit = _settings.ContainsKey("Authentication");
        var auth = _settings.Authentication;
        bool numeric = int.TryParse(service, out int port);

        if (numeric && host.Length > 0)
            return new ResolvedEndpoint(host, port, useSsl, IsGroup: false, auth);

        var resolver = SqlhostsResolverFactory();

        if (server.Length > 0)
        {
            var e = resolver.ResolveServer(server);
            if (e.UseSsl)
                useSsl = true;
            if (!authExplicit && e.Authentication is { } implied)
                auth = implied;
            return new ResolvedEndpoint(e.Host, e.Port, useSsl, e.IsGroup, auth);
        }

        if (host.Length > 0 && service.Length > 0 && !numeric)
            return new ResolvedEndpoint(host, resolver.ResolveServicePort(service), useSsl, IsGroup: false, auth);

        throw new IfxException(
            "Cannot resolve a network endpoint: give a numeric 'Service' with 'Host', or a 'Server' (INFORMIXSERVER) resolvable via sqlhosts.",
            -25563);
    }

    // Maps the ADO.NET connection string (with the resolved endpoint) onto the ASF handshake inputs.
    private AsfConnectionSettings BuildAsfSettings(ResolvedEndpoint endpoint)
    {
        var asf = new AsfConnectionSettings
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            ServerName = _settings.Server,
            DatabaseName = _settings.Database,
            UserName = _settings.UserId.Length > 0 ? _settings.UserId : null,
            Password = _settings.Password.Length > 0 ? _settings.Password : null,
            LoginTimeoutSeconds = _settings.LoginTimeout,
            // IFX_SOC_TIMEOUT is not a typed keyword: any unrecognized keyword is forwarded, and this one also sets the socket timeout.
            SocketTimeoutSeconds = _settings.TryGetValue("IFX_SOC_TIMEOUT", out var st) && int.TryParse(st?.ToString(), out var s) ? s : 0,
            UseSsl = endpoint.UseSsl,
            TrustServerCertificate = _settings.TrustServerCertificate,
            SqlhostsGroup = endpoint.IsGroup,
            // Wire text is encoded/decoded in the database code set (DB_LOCALE); the driver
            // itself does the only conversion (.NET UTF-16 ⇆ DB code set).
            DbEncoding = DbEncoding,
        };

        // Announce DB_LOCALE so the server sends/stores in that code set and does no
        // conversion of its own (the driver already speaks the DB code set). CLIENT_LOCALE
        // is deliberately NOT sent: it only matters for non-Unicode clients that need the
        // server to transcode, which a managed (UTF-16) client never does.
        if (_settings.DbLocale.Length > 0)
            asf.EnvironmentVariables["DB_LOCALE"] = _settings.DbLocale;

        // Standard env the Informix client driver always sends; CLNT_PAM_CAPABLE advertises
        // that the client can complete a PAM exchange (else PAM ports reply -1809).
        asf.EnvironmentVariables["DBPATH"] = ".";
        asf.EnvironmentVariables["IFX_UPDDESC"] = "1";
        asf.EnvironmentVariables["CLNT_PAM_CAPABLE"] = "1";

        // Session environment the caller asked for in the connection string. Only the handshake
        // (ASF) block is honoured for DBDATE, DELIMIDENT and the like -- verified against a live
        // server, where the same variable sent later in an SQ_INFO message had no effect -- so this
        // is the one place they can go. Unset keys send nothing and the server falls back to its
        // own process environment. DELIMIDENT is presence-only on the server (any value, even "n",
        // turns it on), hence it is sent for true and not at all for false.
        void Send(string name, string value)
        {
            if (value.Length > 0)
                asf.EnvironmentVariables[name] = value;
        }
        Send("DBDATE", _settings.DbDate);
        Send("GL_DATE", _settings.GlDate);
        Send("DBCENTURY", _settings.DbCentury);
        Send("GL_DATETIME", _settings.GlDateTime);
        if (_settings.StatementCache is { } stmtCache)
            Send("STMT_CACHE", stmtCache ? "1" : "0");
        if (_settings.LongIdentifiers is { } longId)
            Send("IFX_LONGID", longId ? "1" : "0");
        // The server tests only for the presence of DELIMIDENT, so it is sent only when enabled.
        if (_settings.DelimitedIdentifiers)
            Send("DELIMIDENT", "y");

        return asf;
    }

    // Cache of discovered database code sets, keyed by endpoint+database, so the
    // sysmaster lookup runs at most once per database per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> DbLocaleCache = new();

    // Whether Open has to ask sysmaster for the database's code set. A locale spelled out
    // in the connection string (DB Locale or its DB_LOCALE synonym) is authoritative, so
    // the lookup — and the extra connection it costs — is skipped; so is an explicit
    // "DB Locale Discovery=false", and a connection string with no database to look up.
    internal bool IsDbLocaleDiscoveryNeeded =>
        _settings.DbLocale.Length == 0 && _settings.DbLocaleDiscovery && _settings.Database.Length > 0;

    // When the caller left DB Locale unset, learn the database's code set from
    // sysmaster:sysdbslocale and adopt it as DB_LOCALE before the connection opens.
    // Best-effort: any failure (sysmaster unreachable, no such database, no permission)
    // leaves DB Locale unset and the open proceeds unchanged.
    private void DiscoverDbLocaleIfNeeded()
    {
        // With an injected (test) transport the helper below would dial the connection
        // string's real Host/Service instead of that transport -- a different "server".
        if (!IsDbLocaleDiscoveryNeeded || HasInjectedTransport)
            return;

        string database = _settings.Database;
        string key = $"{_settings.Host}|{_settings.Service}|{_settings.Server}|{database}";
        string? locale = DbLocaleCache.TryGetValue(key, out var cached)
            ? cached
            : QueryDbLocaleFromSysmaster(database);
        if (string.IsNullOrEmpty(locale))
            return;

        DbLocaleCache.TryAdd(key, locale);
        _settings.DbLocale = locale;
        _dbLocaleWasDiscovered = true;
        _dbEncoding = null;
    }

    // True when _settings.DbLocale was filled in by DiscoverDbLocaleIfNeeded rather than by the
    // caller. A discovered locale belongs to the database it was looked up for, so it is dropped
    // again when the database changes (see ChangeDatabase).
    private bool _dbLocaleWasDiscovered;

    private void ForgetDiscoveredDbLocale()
    {
        if (!_dbLocaleWasDiscovered)
            return;
        _settings.DbLocale = "";
        _dbLocaleWasDiscovered = false;
        _dbEncoding = null;
    }

    // Opens a short-lived unpooled connection to sysmaster (always reachable without a
    // locale) and reads the target database's collation code set from sysdbslocale.
    private string? QueryDbLocaleFromSysmaster(string database)
    {
        bool opened = false;
        try
        {
            var b = new IfxConnectionStringBuilder(_settings.ConnectionString)
            {
                Database = "sysmaster",
                DbLocale = "",
                DbLocaleDiscovery = false, // the helper must not recurse
                Pooling = false,
                // The helper must not join the caller's ambient transaction: only one
                // connection can hold a transaction's promotable-single-phase slot, so if
                // this locale lookup enlisted first the real connection's own Open() would
                // fail to enlist (EnlistPromotableSinglePhase returns false) and the whole
                // Open() would abort.
                Enlist = false,
            };
            using var helper = new IfxConnection(b.ConnectionString) { PamHandler = PamHandler };
            helper.Open();
            opened = true;
            using var cmd = helper.CreateCommand();
            cmd.CommandText = "SELECT TRIM(dbs_collate) FROM sysdbslocale WHERE TRIM(dbs_dbsname) = ?";
            var p = cmd.CreateParameter();
            p.Value = database.Trim();
            cmd.Parameters.Add(p);
            string? locale = (cmd.ExecuteScalar() as string)?.Trim();
            return string.IsNullOrEmpty(locale) ? null : locale;
        }
        // A helper that could not even reach the server (-908: refused, timed out, TLS or
        // handshake failure) is not swallowed: the real connection goes to the same host and
        // port and would only fail the same way after a second full Login Timeout.
        catch (Exception e) when (opened || e is not IfxException { IfxSqlCode: -908 })
        {
            // Best-effort: no permission / no such database / the lookup query failed.
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }
}
