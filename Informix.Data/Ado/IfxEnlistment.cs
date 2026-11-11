using System.Transactions;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// Bridges an <see cref="IfxConnection"/> to a <see cref="System.Transactions.Transaction"/>
/// via <b>Promotable Single-Phase Enlistment</b> (PSPE). While this connection is the sole
/// resource manager in the ambient transaction, the transaction manager drives the local
/// SQLI transaction directly (<c>SQ_BEGIN</c> / <c>SQ_CMMTWORK</c> / <c>SQ_RBWORK</c>) with
/// no MSDTC and no XA — the same lightweight path SqlClient and Npgsql take.
/// </summary>
/// <remarks>
/// <para>If a second resource manager tries to join the same transaction, the transaction
/// manager calls <see cref="Promote"/> to obtain a distributed-transaction propagation token.
/// This driver has no XA support, so <see cref="Promote"/> throws and the promoting operation
/// fails — a documented limitation: one connection per <c>TransactionScope</c>.</para>
///
/// <para>The completion callbacks (<see cref="SinglePhaseCommit"/> / <see cref="Rollback"/>)
/// run on a transaction-manager thread, which may differ from the thread that used the
/// connection. As with SqlClient, a connection must not be used concurrently on another
/// thread while its ambient transaction is being resolved.</para>
/// </remarks>
internal sealed class IfxEnlistment : IPromotableSinglePhaseNotification
{
    // Active: connection still open. FacadeClosed: the application closed the connection before the transaction ended, so the physical
    private enum State
    {
        /// <summary>Enlisted; the facade is open and still owns the physical connection.</summary>
        Active,
        /// <summary>The facade was closed while the transaction was still live; this
        /// enlistment now owns the physical connection and returns it on completion.</summary>
        FacadeClosed,
        /// <summary>The transaction manager has resolved the transaction (terminal).</summary>
        Resolved,
    }

    private readonly IfxConnection _connection;
    private readonly Transaction _transaction;
    private readonly SqliSession _session;
    private readonly PooledSession _physical;
    private readonly IfxConnectionPool? _pool;
    private readonly IfxLoggingConfiguration _logging;
    private readonly bool _isReadOnly;
    private readonly object _gate = new();
    private State _state = State.Active;

    internal IfxEnlistment(
        IfxConnection connection, Transaction transaction, SqliSession session,
        PooledSession physical, IfxConnectionPool? pool, IfxLoggingConfiguration logging, bool isReadOnly)
    {
        _connection = connection;
        _transaction = transaction;
        _session = session;
        _physical = physical;
        _pool = pool;
        _logging = logging;
        _isReadOnly = isReadOnly;
    }

    /// <summary>The transaction this enlistment is bound to (used to detect a redundant enlist).</summary>
    internal Transaction Transaction => _transaction;

    // ---- IPromotableSinglePhaseNotification ----------------------------------------------

    /// <summary>
    /// Called synchronously by <c>Transaction.EnlistPromotableSinglePhase</c> on the enlisting
    /// thread: opens the local SQLI transaction (<c>SQ_BEGIN</c>) and applies the ambient
    /// transaction's isolation level.
    /// </summary>
    public void Initialize() =>
        _connection.BeginEnlistedTransaction(_session, _transaction.IsolationLevel, _isReadOnly);

    /// <summary>The transaction stayed single-resource: commit the local transaction.</summary>
    public void SinglePhaseCommit(SinglePhaseEnlistment singlePhaseEnlistment)
    {
        try
        {
            Complete(commit: true);
            singlePhaseEnlistment.Committed();
        }
        catch (Exception ex)
        {
            // The commit failed on the wire; report it to the transaction manager rather
            // than throwing back into it.
            singlePhaseEnlistment.Aborted(ex);
        }
    }

    /// <summary>The scope aborted (no <c>Complete()</c> or an exception): roll the local transaction back.</summary>
    public void Rollback(SinglePhaseEnlistment singlePhaseEnlistment)
    {
        try
        {
            Complete(commit: false);
            singlePhaseEnlistment.Aborted();
        }
        catch (Exception ex)
        {
            singlePhaseEnlistment.Aborted(ex);
        }
    }

    /// <summary>
    /// A second resource manager is joining the transaction, forcing promotion to a
    /// distributed transaction. This driver has no XA support, so promotion is refused.
    /// </summary>
    public byte[] Promote() =>
        throw new TransactionPromotionException(
            "The Informix driver does not support distributed (XA) transactions. A " +
            "System.Transactions transaction that involves more than one resource manager " +
            "cannot be promoted; use a single connection per TransactionScope.");

    // ---- ownership coordination ----------------------------------------------------------

    /// <summary>
    /// Called from <see cref="IfxConnection.Close"/> when the facade is closed while the
    /// ambient transaction is still live. Transfers ownership of the physical connection to
    /// this enlistment, which will run the commit/rollback and return it to the pool when the
    /// transaction manager resolves the transaction. Returns <c>false</c> if the transaction
    /// has already been resolved (a race), in which case the caller returns the physical
    /// connection to the pool itself.
    /// </summary>
    internal bool TryHandOffOnClose()
    {
        lock (_gate)
        {
            if (_state != State.Active)
                return false; // already resolved — the facade returns the physical the normal way
            _state = State.FacadeClosed;
            return true;
        }
    }

    // Runs the actual commit/rollback exactly once, then either returns the physical
    // connection to the pool (facade already closed) or hands control back to the still-open
    // facade so a later Close returns it. The State transition under the gate guarantees that
    // exactly one of {facade Close, this completion} owns the physical-connection return.
    private void Complete(bool commit)
    {
        State prior;
        lock (_gate)
        {
            if (_state == State.Resolved)
                return; // idempotent — the transaction manager double-fired
            prior = _state;
            _state = State.Resolved;
        }

        if (commit)
        {
            _session.ExecuteCommit();   // SQ_CMMTWORK
            LogMessages.TransactionCommitted(_logging.TransactionLogger);
        }
        else
        {
            _session.ExecuteRollback(); // SQ_RBWORK
            LogMessages.TransactionRolledBack(_logging.TransactionLogger);
        }

        if (prior == State.FacadeClosed)
            ReturnPhysical();
        else
            _connection.OnEnlistmentResolvedWhileOpen(this);
    }

    // Returns the physical connection to its pool now that its ambient transaction is done
    // (mirrors IfxConnection.Close's pooled-return path). Only reached when the facade was
    // closed before the transaction resolved, so this enlistment is the last owner.
    private void ReturnPhysical()
    {
        if (_pool is null)
        {
            _physical.Dispose(); // non-pooled: orderly SQ_EXIT then close the socket
            return;
        }
        try
        {
            // Link.IsIdle, not _session.IsIdle.
            if (_session.Link.IsIdle)
                _pool.Return(_physical);
            else
                _pool.Discard(_physical); // a leftover cursor makes reuse unsafe
        }
        catch
        {
            _pool.Discard(_physical);
        }
    }
}
