using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Informix.Data;

/// <summary>
/// Reports OpenTelemetry database-client metrics for one <see cref="IfxDataSource"/>
/// through the shared <c>Informix.Data</c> <see cref="Meter"/>. Created with the data
/// source and disposed with it. Standalone connections (no data source) get no
/// reporter and therefore emit no metrics.
/// </summary>
/// <remarks>
/// The connection-count / max instruments
/// are observable (pull) gauges that read every live reporter's pool statistics; the
/// operation-duration and connection-create-time histograms use
/// <see cref="Stopwatch.GetTimestamp"/> with no per-call allocation, and only measure
/// when a listener is attached (<see cref="Instrument.Enabled"/>).
/// See <see href="https://opentelemetry.io/docs/specs/semconv/database/database-metrics/"/>.
/// </remarks>
internal sealed class IfxMetricsReporter : IDisposable
{
    private static readonly Meter Meter = new("Informix.Data", GetLibraryVersion());

    // Bucket boundaries tuned for fast database calls (1 ms to 10 s).
    private static readonly InstrumentAdvice<double> ShortHistogramAdvice = new()
    {
        HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10],
    };

    private static readonly Histogram<double> OperationDuration;
    private static readonly Counter<int> OperationsFailed;
    private static readonly UpDownCounter<int> PendingConnectionRequests;
    private static readonly Counter<int> ConnectionTimeouts;
    private static readonly Histogram<double> ConnectionCreateTime;

    // All live reporters; the observable gauges below sum over them under a lock.
    private static readonly List<IfxMetricsReporter> Reporters = [];

    static IfxMetricsReporter()
    {
        // Stable in the OpenTelemetry spec.
        OperationDuration = Meter.CreateHistogram<double>(
            "db.client.operation.duration",
            unit: "s",
            description: "Duration of database client operations.",
            advice: ShortHistogramAdvice);

        Meter.CreateObservableUpDownCounter(
            "db.client.connection.count",
            GetConnectionCount,
            unit: "{connection}",
            description: "The number of connections currently in the state described by the state attribute.");

        Meter.CreateObservableUpDownCounter(
            "db.client.connection.max",
            GetConnectionMax,
            unit: "{connection}",
            description: "The maximum number of open connections allowed.");

        // Development-status / driver-specific metrics.
        PendingConnectionRequests = Meter.CreateUpDownCounter<int>(
            "db.client.connection.pending_requests",
            unit: "{request}",
            description: "The number of pending requests for a pooled connection, cumulative for the pool.");

        ConnectionTimeouts = Meter.CreateCounter<int>(
            "db.client.connection.timeouts",
            unit: "{timeout}",
            description: "The number of connection timeouts trying to obtain a connection from the pool.");

        ConnectionCreateTime = Meter.CreateHistogram<double>(
            "db.client.connection.create_time",
            unit: "s",
            description: "The time it took to create a new physical connection.",
            advice: ShortHistogramAdvice);

        OperationsFailed = Meter.CreateCounter<int>(
            "db.client.operation.failed",
            unit: "{command}",
            description: "The number of database commands which have failed.");
    }

    private readonly IfxDataSource _dataSource;
    private readonly int? _maxConnections; // null when the data source is not pooled
    private readonly KeyValuePair<string, object?> _poolNameTag;
    private readonly TagList _durationMetricTags;

    public IfxMetricsReporter(IfxDataSource dataSource, string serverAddress, int serverPort, int? maxConnections)
    {
        _dataSource = dataSource;
        _maxConnections = maxConnections;
        _poolNameTag = new KeyValuePair<string, object?>("db.client.connection.pool.name", dataSource.Name);
        _durationMetricTags = new TagList
        {
            { "db.system.name", "informix" },
            { "db.client.connection.pool.name", dataSource.Name },
            { "server.address", serverAddress },
            { "server.port", serverPort },
        };

        lock (Reporters)
            Reporters.Add(this);
    }

    /// <summary>Begins measuring a command's duration; returns a timestamp to pass to
    /// <see cref="ReportCommandStop"/>, or 0 when the histogram has no listener.</summary>
    public long ReportCommandStart() => OperationDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Records a command's duration when it was measured.</summary>
    public void ReportCommandStop(long startTimestamp)
    {
        if (OperationDuration.Enabled && startTimestamp > 0)
            OperationDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, _durationMetricTags);
    }

    /// <summary>Counts a failed command, tagged with the failure's <c>error.type</c>.</summary>
    public void ReportCommandFailed(string errorType) =>
        OperationsFailed.Add(1, _poolNameTag, new KeyValuePair<string, object?>("error.type", errorType));

    public void ReportPendingConnectionRequestStart() => PendingConnectionRequests.Add(1, _poolNameTag);
    public void ReportPendingConnectionRequestStop() => PendingConnectionRequests.Add(-1, _poolNameTag);

    public void ReportConnectionPoolTimeout() => ConnectionTimeouts.Add(1, _poolNameTag);

    /// <summary>Records how long a new physical connection took to build.</summary>
    public void ReportConnectionCreateTime(TimeSpan duration)
    {
        if (ConnectionCreateTime.Enabled)
            ConnectionCreateTime.Record(duration.TotalSeconds, _poolNameTag);
    }

    // Observable callback: idle and in-use connections for every data source's pool.
    private static IEnumerable<Measurement<int>> GetConnectionCount()
    {
        lock (Reporters)
        {
            var measurements = new List<Measurement<int>>(Reporters.Count * 2);
            foreach (var reporter in Reporters)
            {
                var (_, idle, busy) = reporter._dataSource.Statistics;
                measurements.Add(new Measurement<int>(idle, reporter._poolNameTag,
                    new KeyValuePair<string, object?>("db.client.connection.state", "idle")));
                measurements.Add(new Measurement<int>(busy, reporter._poolNameTag,
                    new KeyValuePair<string, object?>("db.client.connection.state", "used")));
            }
            return measurements;
        }
    }

    private static IEnumerable<Measurement<int>> GetConnectionMax()
    {
        lock (Reporters)
        {
            var measurements = new List<Measurement<int>>(Reporters.Count);
            foreach (var reporter in Reporters)
                if (reporter._maxConnections is { } max)
                    measurements.Add(new Measurement<int>(max, reporter._poolNameTag));
            return measurements;
        }
    }

    public void Dispose()
    {
        lock (Reporters)
            Reporters.Remove(this);
    }

    private static string GetLibraryVersion() =>
        typeof(IfxMetricsReporter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
}
