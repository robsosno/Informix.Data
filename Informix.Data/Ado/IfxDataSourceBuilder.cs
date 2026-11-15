using Informix.Data.Asf;
using Microsoft.Extensions.Logging;

namespace Informix.Data;

/// <summary>
/// Builds an <see cref="IfxDataSource"/>: start from a connection string, layer on
/// logging, a PAM handler and per-connection initialization SQL, then <see cref="Build"/>.
/// </summary>
public sealed class IfxDataSourceBuilder
{
    /// <summary>The typed connection-string builder backing this data source; mutate it
    /// directly to set keywords (Host/Service/Server/Database/Pooling/...).</summary>
    public IfxConnectionStringBuilder ConnectionStringBuilder { get; }

    /// <summary>An optional name for the data source (defaults to the password-stripped
    /// connection string). Surfaces as <see cref="IfxDataSource.Name"/>.</summary>
    public string? Name { get; set; }

    internal ILoggerFactory? LoggerFactory { get; private set; }
    internal bool IsParameterLoggingEnabled { get; private set; }
    internal IIfxPamHandler? PamHandler { get; private set; }
    internal IReadOnlyList<string> InitializationSql { get; private set; } = [];
    internal IfxTracingOptions TracingOptions { get; private set; } = IfxTracingOptions.Default;

    /// <summary>Test seam: substitutes the ASF transport for the data source's connections.</summary>
    internal Func<AsfConnectionSettings, AsfConnection> AsfConnectionFactory { get; set; } =
        IfxConnection.DefaultAsfConnectionFactory;

    /// <summary>Starts a builder from an optional connection string.</summary>
    public IfxDataSourceBuilder(string? connectionString = null)
    {
        ConnectionStringBuilder = new IfxConnectionStringBuilder(connectionString);
    }

    /// <summary>The current connection string being built.</summary>
    public string ConnectionString => ConnectionStringBuilder.ConnectionString;

    /// <summary>
    /// Routes this data source's logging through <paramref name="loggerFactory"/>.
    /// When null (the default), the data source does not log.
    /// </summary>
    public IfxDataSourceBuilder UseLoggerFactory(ILoggerFactory? loggerFactory)
    {
        LoggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// Enables logging of bound parameter values alongside SQL statements (only takes
    /// effect at <see cref="Microsoft.Extensions.Logging.LogLevel.Trace"/>). This can
    /// reveal sensitive data — off by default.
    /// </summary>
    public IfxDataSourceBuilder EnableParameterLogging(bool enable = true)
    {
        IsParameterLoggingEnabled = enable;
        return this;
    }

    /// <summary>
    /// Sets a data-source-level PAM handler for <see cref="IfxAuthentication.Pam"/>
    /// ports. A bound connection's own <see cref="IfxConnection.PamHandler"/> takes
    /// precedence over this one when set.
    /// </summary>
    public IfxDataSourceBuilder UsePamHandler(IIfxPamHandler pamHandler)
    {
        ArgumentNullException.ThrowIfNull(pamHandler);
        PamHandler = pamHandler;
        return this;
    }

    /// <summary>
    /// Registers SQL statements run once on <em>each new physical connection</em>, right
    /// after bring-up and before first use. Typical use is session <c>SET</c>s
    /// (<c>SET LOCK MODE TO WAIT n</c>, <c>SET ISOLATION ...</c>, <c>SET PDQPRIORITY ...</c>).
    /// </summary>
    public IfxDataSourceBuilder UseConnectionInitializationSql(params string[] statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        var list = new List<string>(statements.Length);
        foreach (string statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement))
                throw new ArgumentException("Initialization SQL statements must be non-empty.", nameof(statements));
            list.Add(statement);
        }
        InitializationSql = list;
        return this;
    }

    /// <summary>
    /// Configures the OpenTelemetry tracing emitted by this data source's connections
    /// and commands (the <c>Informix.Data</c> <see cref="System.Diagnostics.ActivitySource"/>) —
    /// command filters, span-name and enrichment callbacks, and the optional events. To
    /// receive the spans, subscribe an <c>ActivityListener</c> (or the OpenTelemetry SDK)
    /// to the <c>"Informix.Data"</c> source.
    /// </summary>
    public IfxDataSourceBuilder ConfigureTracing(Action<IfxTracingOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var optionsBuilder = new IfxTracingOptionsBuilder();
        configure(optionsBuilder);
        TracingOptions = optionsBuilder.Build();
        return this;
    }

    /// <summary>Builds the configured <see cref="IfxDataSource"/>.</summary>
    public IfxDataSource Build() => new(this);
}
