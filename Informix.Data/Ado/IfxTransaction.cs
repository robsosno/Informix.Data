using System.Data;
using System.Data.Common;

namespace Informix.Data;

/// <summary>
/// An Informix local transaction (<see cref="DbTransaction"/>), driving the SQLI
/// <c>SQ_BEGIN</c>/<c>SQ_CMMTWORK</c>/<c>SQ_RBWORK</c> messages underneath.
/// </summary>
public sealed class IfxTransaction : DbTransaction
{
    private IfxConnection? _connection;
    private bool _completed;
    private bool _disposed;

    internal IfxTransaction(IfxConnection connection, IsolationLevel isolationLevel, bool isReadOnly)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
        IsReadOnly = isReadOnly;
    }

    public override IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Whether this transaction started with <c>SET TRANSACTION READ ONLY</c> — from an
    /// explicit <c>isReadOnly</c> argument to
    /// <see cref="IfxConnection.BeginTransaction(IsolationLevel, bool)"/>, or from the
    /// <see cref="IfxConnectionStringBuilder.ReadOnly"/> keyword otherwise.
    /// </summary>
    public bool IsReadOnly { get; }

    protected override DbConnection? DbConnection => _connection;

    public new IfxConnection? Connection => _connection;

    public override void Commit()
    {
        EnsureActive();
        _connection!.CompleteTransaction(this, commit: true);
        Complete();
    }

    public override void Rollback()
    {
        EnsureActive();
        _connection!.CompleteTransaction(this, commit: false);
        Complete();
    }

    // Savepoints map to the SQLI savepoint messages (set, rollback to, release).
    public override bool SupportsSavepoints => true;

    /// <summary>Sets a named savepoint within this transaction (SQ_SQLISETSVPT).</summary>
    public override void Save(string savepointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(savepointName);
        EnsureActive();
        _connection!.SqliSession!.ExecuteSavepoint(savepointName);
    }

    /// <summary>Rolls the transaction back to a named savepoint (SQ_SQLIRBACKSVPT).</summary>
    public override void Rollback(string savepointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(savepointName);
        EnsureActive();
        _connection!.SqliSession!.ExecuteRollbackSavepoint(savepointName);
    }

    /// <summary>Releases a named savepoint (SQ_SQLIRELSVPT).</summary>
    public override void Release(string savepointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(savepointName);
        EnsureActive();
        _connection!.SqliSession!.ExecuteReleaseSavepoint(savepointName);
    }

    // Commit and Rollback end the transaction: it no longer belongs to a connection (Connection reads null,
    // as after Dispose) and every further use fails.
    private void Complete()
    {
        _completed = true;
        _connection = null;
    }

    // A disposed transaction is an ObjectDisposedException; one that was committed or rolled back, or
    // whose connection was closed, is an InvalidOperationException (the ADO.NET convention for a
    // transaction that has completed).
    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("This IfxTransaction has completed; it is no longer usable.");
        if (_connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The transaction is no longer usable; its connection is not open.");
    }

    // Rolls back if neither Commit nor Rollback was called; errors during this cleanup are swallowed.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection is { State: ConnectionState.Open })
        {
            // Roll back an uncommitted transaction on dispose, matching ADO.NET semantics.
            try { _connection.CompleteTransaction(this, commit: false); }
            catch { /* swallow on dispose */ }
        }
        _connection = null;
        _completed = true;
        _disposed = true;
        base.Dispose(disposing);
    }
}
