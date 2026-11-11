using System.Diagnostics;

namespace Informix.Data;

/// <summary>
/// Configures the OpenTelemetry tracing Informix.Data emits (the
/// <c>Informix.Data</c> <see cref="ActivitySource"/>). Obtain one from
/// <see cref="IfxDataSourceBuilder.ConfigureTracing"/> and set filters, span-name
/// and enrichment callbacks, and the optional events.
/// </summary>
/// <remarks>There is no batch/copy variant: <see cref="IfxRowBatch"/> is a provider-specific,
/// one-statement construct that always traces with default options.</remarks>
public sealed class IfxTracingOptionsBuilder
{
    private Func<IfxCommand, bool>? _commandFilter;
    private Action<Activity, IfxCommand>? _commandEnrichmentCallback;
    private Func<IfxCommand, string?>? _commandSpanNameProvider;
    private bool _enableFirstResponseEvent = true;
    private bool _enablePhysicalOpenTracing = true;

    internal IfxTracingOptionsBuilder() { }

    /// <summary>
    /// Filters which commands are traced: the callback runs before a span is started
    /// and, when it returns <see langword="false"/>, no activity is created for that
    /// command. By default every command is traced.
    /// </summary>
    public IfxTracingOptionsBuilder ConfigureCommandFilter(Func<IfxCommand, bool>? commandFilter)
    {
        _commandFilter = commandFilter;
        return this;
    }

    /// <summary>
    /// Enriches the <see cref="Activity"/> of each traced command — add custom tags,
    /// baggage or events. Runs after the standard tags are set, only when the activity
    /// is sampled (<see cref="Activity.IsAllDataRequested"/>).
    /// </summary>
    public IfxTracingOptionsBuilder ConfigureCommandEnrichmentCallback(Action<Activity, IfxCommand>? commandEnrichmentCallback)
    {
        _commandEnrichmentCallback = commandEnrichmentCallback;
        return this;
    }

    /// <summary>
    /// Overrides the span name for a command. When the callback is null or returns
    /// null, the default name is used (<c>EXECUTE PROCEDURE {name}</c> for a stored
    /// procedure, otherwise <c>informix</c> — a safe fallback, since a
    /// summary name would require parsing the SQL).
    /// </summary>
    public IfxTracingOptionsBuilder ConfigureCommandSpanNameProvider(Func<IfxCommand, string?>? commandSpanNameProvider)
    {
        _commandSpanNameProvider = commandSpanNameProvider;
        return this;
    }

    /// <summary>
    /// Whether to add the <c>received-first-response</c> event to the span when the
    /// first reply of an execution arrives (cursor open / execute return). Default
    /// <see langword="true"/>.
    /// </summary>
    public IfxTracingOptionsBuilder EnableFirstResponseEvent(bool enable = true)
    {
        _enableFirstResponseEvent = enable;
        return this;
    }

    /// <summary>
    /// Whether to emit a <c>connect</c> span around each physical-connection open
    /// (socket + handshake + SQLI bring-up). Not part of the OpenTelemetry spec.
    /// Default <see langword="true"/>.
    /// </summary>
    public IfxTracingOptionsBuilder EnablePhysicalOpenTracing(bool enable = true)
    {
        _enablePhysicalOpenTracing = enable;
        return this;
    }

    internal IfxTracingOptions Build() => new()
    {
        CommandFilter = _commandFilter,
        CommandEnrichmentCallback = _commandEnrichmentCallback,
        CommandSpanNameProvider = _commandSpanNameProvider,
        EnableFirstResponseEvent = _enableFirstResponseEvent,
        EnablePhysicalOpenTracing = _enablePhysicalOpenTracing,
    };
}

/// <summary>The immutable, resolved tracing configuration a data source (or the
/// default) hands to <see cref="IfxActivitySource"/>.</summary>
internal sealed class IfxTracingOptions
{
    /// <summary>Defaults used by standalone connections (no data source): trace every
    /// command, no enrichment, first-response and physical-open spans on.</summary>
    public static readonly IfxTracingOptions Default = new();

    public Func<IfxCommand, bool>? CommandFilter { get; init; }
    public Action<Activity, IfxCommand>? CommandEnrichmentCallback { get; init; }
    public Func<IfxCommand, string?>? CommandSpanNameProvider { get; init; }
    public bool EnableFirstResponseEvent { get; init; } = true;
    public bool EnablePhysicalOpenTracing { get; init; } = true;
}
