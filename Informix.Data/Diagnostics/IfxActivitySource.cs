using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Informix.Data;

/// <summary>
/// The driver-level <see cref="ActivitySource"/> (<c>Informix.Data</c>): one
/// <see cref="ActivityKind.Client"/> span per command execution, following the
/// OpenTelemetry database client semantic conventions
/// (<see href="https://opentelemetry.io/docs/specs/semconv/database/database-spans/"/>).
/// </summary>
/// <remarks>
/// Every call site checks <see cref="IsEnabled"/> first, so when no listener is subscribed
/// the driver allocates nothing and touches no <see cref="Activity"/>. The registered
/// well-known <c>db.system.name</c> value for Informix is <c>informix</c>.
/// </remarks>
internal static class IfxActivitySource
{
    private static readonly ActivitySource Source = new("Informix.Data", GetLibraryVersion());

    /// <summary>Whether a listener is subscribed. Guards every entry point so the
    /// no-listener path is allocation-free.</summary>
    internal static bool IsEnabled => Source.HasListeners();

    /// <summary>The target attributes shared by a command's tags (database + endpoint).</summary>
    internal readonly record struct TraceTarget(string Database, string ServerAddress, int ServerPort);

    /// <summary>
    /// Starts a client span for a command execution, or returns null when there is no
    /// listener or the command filter excludes it. The caller ends it with
    /// <see cref="CommandStop"/> / <see cref="SetException"/> (or by handing it to the
    /// reader, which ends it on close).
    /// </summary>
    internal static Activity? CommandStart(IfxCommand command, in TraceTarget target, IfxTracingOptions options)
    {
        if (options.CommandFilter is { } filter && !filter(command))
            return null;

        string commandText = command.CommandText;
        string? spanName = options.CommandSpanNameProvider?.Invoke(command);
        string? operationName = null;

        if (command.CommandType == CommandType.StoredProcedure)
        {
            // {db.operation.name} {target}: EXECUTE PROCEDURE {routine name}.
            operationName = "EXECUTE PROCEDURE";
            spanName ??= $"{operationName} {commandText}";
        }
        else
        {
            // No db.query.summary/db.operation.name without parsing SQL — fall back to
            // db.system.name.
            spanName ??= "informix";
        }

        var activity = Source.StartActivity(spanName, ActivityKind.Client);
        if (activity is not { IsAllDataRequested: true })
            return activity;

        activity.SetTag("db.system.name", "informix");
        activity.SetTag("db.namespace", target.Database);
        activity.SetTag("db.query.text", commandText);
        if (target.ServerAddress.Length > 0)
            activity.SetTag("server.address", target.ServerAddress);
        if (target.ServerPort > 0)
            activity.SetTag("server.port", target.ServerPort);

        if (operationName is not null)
        {
            activity.SetTag("db.operation.name", operationName);
            activity.SetTag("db.stored_procedure.name", commandText);
        }

        options.CommandEnrichmentCallback?.Invoke(activity, command);
        return activity;
    }

    /// <summary>
    /// Starts a client span for an <see cref="IfxRowBatch"/> execution. Batches always
    /// trace with default options (no filter / enrichment / span-name provider — those
    /// take an <see cref="IfxCommand"/>).
    /// </summary>
    internal static Activity? BatchStart(string commandText, in TraceTarget target)
    {
        var activity = Source.StartActivity("informix", ActivityKind.Client);
        if (activity is not { IsAllDataRequested: true })
            return activity;

        activity.SetTag("db.system.name", "informix");
        activity.SetTag("db.namespace", target.Database);
        activity.SetTag("db.query.text", commandText);
        if (target.ServerAddress.Length > 0)
            activity.SetTag("server.address", target.ServerAddress);
        if (target.ServerPort > 0)
            activity.SetTag("server.port", target.ServerPort);
        return activity;
    }

    /// <summary>
    /// Starts the optional physical-connection open span (<c>connect</c>), or null when
    /// disabled / no listener. Not part of the OpenTelemetry spec.
    /// </summary>
    internal static Activity? PhysicalOpenStart(in TraceTarget target, IfxTracingOptions options)
    {
        if (!options.EnablePhysicalOpenTracing)
            return null;

        var activity = Source.StartActivity("connect", ActivityKind.Client);
        if (activity is not { IsAllDataRequested: true })
            return activity;

        // Basic tags set up-front so they survive even if the open then fails.
        activity.SetTag("db.system.name", "informix");
        activity.SetTag("db.namespace", target.Database);
        if (target.ServerAddress.Length > 0)
            activity.SetTag("server.address", target.ServerAddress);
        if (target.ServerPort > 0)
            activity.SetTag("server.port", target.ServerPort);
        return activity;
    }

    /// <summary>Records the <c>received-first-response</c> event when enabled.</summary>
    internal static void ReceivedFirstResponse(Activity activity, IfxTracingOptions options)
    {
        if (!activity.IsAllDataRequested || !options.EnableFirstResponseEvent)
            return;
        activity.AddEvent(new ActivityEvent("received-first-response"));
    }

    /// <summary>Ends a span successfully (status Ok) and disposes it.</summary>
    internal static void CommandStop(Activity activity)
    {
        activity.SetStatus(ActivityStatusCode.Ok);
        activity.Dispose();
    }

    /// <summary>
    /// Marks a span failed: adds the exception event, tags <c>error.type</c> and (for an
    /// <see cref="IfxException"/> carrying a SQLSTATE) <c>db.response.status_code</c>,
    /// sets status Error, and disposes it.
    /// </summary>
    internal static void SetException(Activity activity, Exception exception)
    {
        activity.AddException(exception);

        // A server error's SQLSTATE is the db.response.status_code (the driver has no
        // sqlcode→description table, so the SQLSTATE is the best status code we can give).
        if (exception is IfxException { SqlState: { Length: > 0 } sqlState })
            activity.SetTag("db.response.status_code", sqlState);
        activity.SetTag("error.type", ErrorType(exception));

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.Dispose();
    }

    /// <summary>
    /// The <c>error.type</c> for a failure: an <see cref="IfxException"/>'s SQLSTATE when
    /// it carries one, else its sqlcode; any other exception's full type name. Shared by
    /// the trace <c>error.type</c> tag and the <c>db.client.operation.failed</c> metric.
    /// </summary>
    internal static string ErrorType(Exception exception) => exception switch
    {
        IfxException { SqlState: { Length: > 0 } sqlState } => sqlState,
        IfxException ifx => ifx.IfxSqlCode.ToString(CultureInfo.InvariantCulture),
        _ => exception.GetType().FullName ?? exception.GetType().Name,
    };

    private static string GetLibraryVersion() =>
        typeof(IfxActivitySource).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
}
