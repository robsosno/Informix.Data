using System.Data;
using Informix.Data;
using ModelContextProtocol;

namespace Demos.InformixMcp;

/// <summary>
/// One connection per call, always inside a read-only transaction that is rolled back at the end.
/// Together with the forced Select Only keyword that is two independent guards: the driver refuses
/// anything but SELECT, and the server refuses writes inside the transaction.
/// </summary>
internal sealed class Database(Lazy<ServerSettings> settings)
{
    public const int CommandTimeoutSeconds = 30;

    public Database() : this(new Lazy<ServerSettings>(ServerSettings.Load)) { }

    public T Run<T>(Func<IfxConnection, IfxTransaction, T> work)
    {
        try
        {
            using var cn = new IfxConnection(settings.Value.ConnectionString);
            cn.Open();
            using IfxTransaction tx = cn.BeginTransaction(IsolationLevel.ReadCommitted, isReadOnly: true);
            try { return work(cn, tx); }
            finally { tx.Rollback(); }
        }
        catch (Exception e) when (e is not McpException)
        {
            string code = e is IfxException { IfxSqlCode: not 0 } ix ? $" (SQLCODE {ix.IfxSqlCode})" : "";
            throw new McpException(Scrub($"{e.GetType().Name}{code}: {e.Message}"), e);
        }
    }

    /// <summary>Removes the connection string and its credentials from a text before it is shown to the assistant.</summary>
    public string Scrub(string text) => settings.IsValueCreated ? settings.Value.Scrub(text) : text;
}
