using System.Diagnostics;
using Informix.Data.Sqli;
using Microsoft.Extensions.Logging;

namespace Informix.Data;

/// <summary>
/// The activity/metric/failure-log/cleanup bracket shared by
/// <see cref="IfxCommand.ExecuteNonQuery"/>, <c>ExecuteDbDataReader</c>,
/// <c>ExecuteScrollReader</c>, and <see cref="IfxBatch.ExecuteNonQuery"/>/
/// <c>ExecuteDbDataReader</c> — five of the six sites that used to hand-roll this
/// sequence (design doc §5). <see cref="IfxRowBatch"/>'s own <c>Execute</c> stays
/// hand-rolled: it bypasses <c>RequireOpenConnection</c>/<c>AcquireSession</c> by
/// construction, has no <c>CommandTimeout</c> at this level, uses
/// <c>session.ReleaseStatement()</c> instead of <c>ClearReservedForAcquire()</c>, and
/// hardcodes <see cref="IfxTracingOptions.Default"/> instead of the connection's own
/// (a deliberate choice — see the comment at its call site). Covering it here would mean
/// giving it behavior it does not have today; that is a decision for the maintainer, not
/// this refactor.
///
/// <para>Deliberately narrow — what the five sites do NOT share stays at the call site
/// and is not parameterized here: which activity-start function to call
/// (<c>CommandStart</c> vs <c>BatchStart</c> take different arguments), where
/// <c>BeginCommandTimeout</c>'s scope begins and ends (the whole method, a narrower
/// nested block, or delegated to a per-command helper — three different shapes across
/// the five sites), whether a successful call ends its own span (<c>CommandStop</c>) or
/// hands the span to a reader (<c>FirstResponse</c> only, the reader ends it later), and
/// whether success is logged at all (only <c>IfxCommand.ExecuteNonQuery</c> logs a
/// completion line with an elapsed time — nothing here owns a <see cref="Stopwatch"/>).
/// Folding any of those into a constructor flag would have pushed this well past the
/// "about three knobs" point where a shared abstraction stops paying for itself (§5.1's
/// gate) — <see cref="_logger"/> being null or not is the only one left.</para>
/// </summary>
internal readonly struct IfxExecutionScope : IDisposable
{
    private readonly SqliSession _session;
    private readonly IfxMetricsReporter? _reporter;
    private readonly Activity? _activity;
    private readonly ILogger? _logger;
    private readonly long _metricStart;

    /// <param name="logger">The command logger to report a failure to (every IfxCommand
    /// site passes its <c>Logging.CommandLogger</c>), or <c>null</c> to skip failure
    /// logging entirely — both IfxBatch sites, which never log at all (a batch's "one SQL
    /// code, one message" failure log does not map cleanly onto a multi-command run).</param>
    internal IfxExecutionScope(SqliSession session, IfxMetricsReporter? reporter, Activity? activity, ILogger? logger)
    {
        _session = session;
        _reporter = reporter;
        _activity = activity;
        _logger = logger;
        _metricStart = reporter?.ReportCommandStart() ?? 0;
    }

    /// <summary>Call from the catch block, before rethrowing: logs (when a logger was
    /// given and this is an <see cref="IfxException"/>), reports the metric failure, and
    /// marks the activity's exception.</summary>
    internal void Fail(Exception ex)
    {
        if (_logger is not null && ex is IfxException ifx)
            LogMessages.CommandFailed(_logger, ifx.IfxSqlCode, ifx.Message);
        _reporter?.ReportCommandFailed(IfxActivitySource.ErrorType(ex));
        if (_activity is not null)
            IfxActivitySource.SetException(_activity, ex);
    }

    /// <summary>Reports the metric's stop timestamp and clears the session's
    /// AcquireSession reservation — runs unconditionally, success or failure, matching
    /// every covered site's own <c>finally</c> block.</summary>
    public void Dispose()
    {
        _reporter?.ReportCommandStop(_metricStart);
        _session.ClearReservedForAcquire();
    }
}
