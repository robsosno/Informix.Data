using System.Data;
using System.Diagnostics;
using Informix.Data.Sqli;
using Informix.Data.Wire;

namespace Informix.Data;

/// <summary>
/// Executes one parameterized statement over many value rows in a single server
/// round trip. The statement is prepared once and each row's <c>SQ_BIND +
/// SQ_EXECUTE</c> is chained into one PDU, so a bulk insert/update costs a single
/// flush instead of one per row.
///
/// <para>The statement text may use the Informix-native positional <c>?</c> or named
/// placeholders (<c>@name</c>/<c>:name</c>), which are rewritten to <c>?</c> once for the
/// whole batch. Because a batch's rows carry only positional values (no names), a named
/// statement's row values must be supplied in placeholder-occurrence order — the same
/// order the <c>?</c>s appear in after the rewrite, with a repeated name's value repeated.</para>
/// </summary>
public sealed class IfxRowBatch
{
    private readonly List<object?[]> _rows = [];

    public IfxRowBatch() { }

    public IfxRowBatch(string commandText, IfxConnection? connection = null)
    {
        CommandText = commandText ?? string.Empty;
        Connection = connection;
    }

    /// <summary>The single parameterized statement run once per queued row.</summary>
    public string CommandText { get; set; } = string.Empty;

    public IfxConnection? Connection { get; set; }

    public IfxTransaction? Transaction { get; set; }

    /// <summary>The number of value rows queued for execution.</summary>
    public int RowCount => _rows.Count;

    /// <summary>Queues one execution with the given positional parameter values.</summary>
    public void AddRow(params object?[] values) => _rows.Add(values ?? []);

    /// <summary>Discards all queued rows.</summary>
    public void Clear() => _rows.Clear();

    /// <summary>
    /// Prepares the statement once and executes every queued row, returning each
    /// row's affected-row count in order. An empty batch returns an empty array.
    /// </summary>
    public int[] Execute()
    {
        if (Connection is null)
            throw new InvalidOperationException("IfxRowBatch.Connection has not been set.");
        if (Connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
        if (string.IsNullOrEmpty(CommandText))
            throw new InvalidOperationException("IfxRowBatch.CommandText has not been set.");
        if (_rows.Count == 0)
            return [];

        var session = Connection.SqliSession
            ?? throw new InvalidOperationException("The connection has no live SQLI session.");
        session.ThrowIfStatementOwned();

        // Rewrite named placeholders to positional '?' once (throws on mixed named + '?').
        // Row values are already positional and map onto the rewritten '?'s in order.
        string sql = IfxSqlParser.Parse(CommandText).Text;

        var binds = new List<IReadOnlyList<SqliBindValue>>(_rows.Count);
        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            var rowBinds = new List<SqliBindValue>(row.Length);
            for (int c = 0; c < row.Length; c++)
                rowBinds.Add(SqliBindValue.From(
                    row[c], session.DbEncoding, longRowId: session.Capabilities.IsLongRowId));
            binds.Add(rowBinds);
        }

        // One client span + duration/failed metrics for the whole batch. A batch is a
        // provider-specific one-statement construct, so it always traces with defaults
        // (the command filter/enrichment/span-name callbacks take an IfxCommand).
        var reporter = Connection.MetricsReporter;
        Activity? activity = IfxActivitySource.IsEnabled
            ? IfxActivitySource.BatchStart(sql, Connection.TraceTarget)
            : null;
        long metricStart = reporter?.ReportCommandStart() ?? 0;

        // The parameter count comes from the first row.
        session.Prepare(sql, binds[0].Count);
        try
        {
            int[] counts = session.ExecuteBatch(binds);
            if (activity is not null)
            {
                IfxActivitySource.ReceivedFirstResponse(activity, IfxTracingOptions.Default);
                IfxActivitySource.CommandStop(activity);
            }
            return counts;
        }
        catch (Exception ex)
        {
            reporter?.ReportCommandFailed(IfxActivitySource.ErrorType(ex));
            if (activity is not null)
                IfxActivitySource.SetException(activity, ex);
            throw;
        }
        finally
        {
            session.ReleaseStatement();
            reporter?.ReportCommandStop(metricStart);
        }
    }
}
