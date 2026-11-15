using System.Data.Common;
using Informix.Data.Asf;

namespace Informix.Data;

/// <summary>
/// A <see cref="DbDataSource"/> connection factory for Informix: owns a connection pool and the
/// configuration (logging, PAM handler, initialization SQL) for the connections it
/// hands out. This is the modern .NET 7+ idiom — build one per logical database
/// with <see cref="IfxDataSourceBuilder"/> (or <see cref="Create(string)"/>) and
/// draw connections and commands from it.
///
/// <para>Unlike a bare <see cref="IfxConnection"/>, a data source owns its pool
/// <em>directly</em>: two data sources built from the same connection string keep
/// two independent pools, and disposing a data source clears its pool without
/// touching the process-wide registry used by standalone connections.</para>
/// </summary>
/// <remarks>
/// Follows the standard ADO.NET data-source pattern. <see cref="DbDataSource.CreateBatch"/>
/// works (the base implementation opens a connection-owning <see cref="IfxBatch"/> per
/// call, exactly like <see cref="DbDataSource.CreateCommand"/> does for <see cref="IfxCommand"/>)
/// — for many value rows over one statement, <see cref="IfxRowBatch"/> (created from an
/// opened <see cref="IfxConnection"/>) remains the more direct, provider-specific choice.
/// </remarks>
public sealed class IfxDataSource : DbDataSource
{
    private readonly IfxConnectionStringBuilder _settings; // full settings incl. password
    private readonly string _connectionString;             // password-stripped unless Persist Security Info
    private readonly string _name;
    private readonly IfxLoggingConfiguration _logging;
    private readonly IfxTracingOptions _tracingOptions;
    private readonly IfxMetricsReporter _metrics;
    private readonly IReadOnlyList<string> _initSql;
    // Opens one physical connection (handshake, PAM, initialization SQL); the pool calls it when it needs a new one.
    private readonly IfxConnectionPool.PhysicalFactory _factory;
    private readonly IfxConnectionPool? _pool;             // null when Pooling=false
    private int _disposed;

    // The PAM handler a bound connection passes when it rents, made visible to the
    // physical factory (which resolves it per new physical connection) via a
    // thread-static — the driver is synchronous, so rent + create run on one thread.
    [ThreadStatic] private static IIfxPamHandler? _currentRentPamOverride;
    internal static IIfxPamHandler? CurrentRentPamOverride => _currentRentPamOverride;

    // The connection string exposed by the data source has the password removed unless Persist Security Info is set; the real settings stay private.
    internal IfxDataSource(IfxDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _settings = new IfxConnectionStringBuilder(builder.ConnectionStringBuilder.ConnectionString);
        _logging = builder.LoggerFactory is { } factory
            ? new IfxLoggingConfiguration(factory, builder.IsParameterLoggingEnabled)
            : IfxLoggingConfiguration.NullConfiguration;
        _tracingOptions = builder.TracingOptions;
        _initSql = builder.InitializationSql;

        // ConnectionString hides the password by default (the ADO.NET norm); the actual
        // credentials still flow to the server through the physical factory below.
        _connectionString = _settings.PersistSecurityInfo
            ? _settings.ConnectionString
            : _settings.ToStringWithoutPassword();
        _name = string.IsNullOrEmpty(builder.Name) ? _connectionString : builder.Name!;

        // Build the physical-connection factory from a settings-only template connection
        // (never opened). The PAM handler is resolved per physical connection so a bound
        // IfxConnection's own handler overrides this data source's.
        var template = new IfxConnection(_settings.ConnectionString)
        {
            AsfConnectionFactory = builder.AsfConnectionFactory,
        };
        HasInjectedTransport = template.HasInjectedTransport;

        // One metrics reporter per data source (disposed with it). It tags with this
        // data source's Name and reads its pool Statistics for the observable gauges;
        // Max Pool Size is only meaningful when pooling.
        string serverAddress = _settings.Host.Length > 0 ? _settings.Host : _settings.Server;
        int serverPort = int.TryParse(_settings.Service, out int p) ? p : 0;
        // Metrics (OpenTelemetry instruments) are tagged with the server address, port and pool name.
        _metrics = new IfxMetricsReporter(this, serverAddress, serverPort,
            _settings.Pooling ? _settings.MaxPoolSize : null);

        var dataSourcePam = builder.PamHandler;
        _factory = template.BuildPhysicalFactory(
            () => _currentRentPamOverride ?? dataSourcePam,
            _logging,
            _initSql.Count > 0 ? _initSql : null,
            _tracingOptions,
            _metrics);

        if (_settings.Pooling)
            _pool = new IfxConnectionPool(
                _settings.MinPoolSize, _settings.MaxPoolSize, _settings.ConnectionLifetime, _factory, _logging, _metrics);
    }

    /// <summary>Creates a data source for <paramref name="connectionString"/>.</summary>
    public static IfxDataSource Create(string connectionString) =>
        new IfxDataSourceBuilder(connectionString).Build();

    /// <summary>Creates a data source from a typed connection-string builder.</summary>
    public static IfxDataSource Create(IfxConnectionStringBuilder connectionStringBuilder)
    {
        ArgumentNullException.ThrowIfNull(connectionStringBuilder);
        return Create(connectionStringBuilder.ConnectionString);
    }

    /// <summary>
    /// The connection string this data source was built from, with the password
    /// removed unless <c>Persist Security Info=true</c> was set.
    /// </summary>
    public override string ConnectionString => _connectionString;

    /// <summary>
    /// A name for this data source (from <see cref="IfxDataSourceBuilder.Name"/>,
    /// else the password-stripped connection string). Used to tag metrics and traces.
    /// </summary>
    public string Name => _name;

    internal IfxLoggingConfiguration LoggingConfiguration => _logging;

    /// <summary>The tracing configuration this data source's commands trace through.</summary>
    internal IfxTracingOptions TracingOptions => _tracingOptions;

    /// <summary>The metrics reporter this data source's commands and pool report to.</summary>
    internal IfxMetricsReporter MetricsReporter => _metrics;

    /// <summary>This data source's pool (null when <c>Pooling=false</c>) — the same instance
    /// <see cref="Rent"/> draws from, exposed read-only so a bound <see cref="IfxConnection"/>
    /// can report <see cref="IfxConnection.GetIdleConnectionsCount"/>/
    /// <see cref="IfxConnection.GetActiveConnectionsCount"/> without renting.</summary>
    internal IfxConnectionPool? Pool => _pool;

    /// <summary>True when the test seam replaced the ASF transport (see <see cref="IfxDataSourceBuilder.AsfConnectionFactory"/>).</summary>
    internal bool HasInjectedTransport { get; }

    /// <summary>
    /// A snapshot of the pool: total physical connections, how many are idle, and how
    /// many are checked out (busy). All zero when <c>Pooling=false</c>.
    /// </summary>
    public (int Total, int Idle, int Busy) Statistics
    {
        get
        {
            if (_pool is null)
                return (0, 0, 0);
            int total = _pool.TotalCount, idle = _pool.IdleCount;
            return (total, idle, total - idle);
        }
    }

    /// <summary>
    /// Clears this data source's pool: idle connections are closed and any that are
    /// currently checked out are retired when they are next closed (ADO.NET
    /// <c>ClearPool</c> semantics). No-op when <c>Pooling=false</c>.
    /// </summary>
    public void Clear() => _pool?.Clear();

    /// <inheritdoc cref="DbDataSource.CreateConnection"/>
    public new IfxConnection CreateConnection() => (IfxConnection)CreateDbConnection();

    /// <inheritdoc cref="DbDataSource.OpenConnection"/>
    public new IfxConnection OpenConnection()
    {
        var connection = CreateConnection();
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override DbConnection CreateDbConnection()
    {
        CheckDisposed();
        return new IfxConnection(this);
    }

    /// <inheritdoc/>
    protected override DbConnection OpenDbConnection() => OpenConnection();

    // Rents a physical connection for a bound IfxConnection. connectionPamHandler (the
    // facade's own handler, or null) takes precedence over the data source handler when
    // a fresh physical connection has to authenticate.
    internal (PooledSession Physical, IfxConnectionPool? Pool) Rent(int timeoutSeconds, IIfxPamHandler? connectionPamHandler)
    {
        CheckDisposed();
        var previous = _currentRentPamOverride;
        _currentRentPamOverride = connectionPamHandler;
        try
        {
            if (_pool is not null)
                return (_pool.Rent(timeoutSeconds), _pool);

            var (asf, sqli, version) = _factory();
            return (new PooledSession(asf, sqli, version, generation: 0), null);
        }
        finally
        {
            _currentRentPamOverride = previous;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Clear on the first disposal, whether sync (Dispose(true)) or async
        // (DisposeAsync -> Dispose(false)); the pool only holds managed resources.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool?.Shutdown();
            _metrics.Dispose(); // unregister from the observable-gauge reporter list
        }
        base.Dispose(disposing);
    }

    private void CheckDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}
