namespace Informix.Data.Sqli;

/// <summary>
/// Implemented by a construct that pins <see cref="SqliSession.StatementOwner"/> and needs
/// to react to a commit/rollback issued while it is mid-load — currently only
/// <see cref="Informix.Data.IfxBulkLoader"/>. <see cref="SqliSession.ExecuteCommit"/> and
/// <see cref="SqliSession.ExecuteRollback"/> call these around the wire SQ_CMMTWORK/SQ_RBWORK
/// exchange whenever <see cref="SqliSession.StatementOwner"/> implements this interface.
/// </summary>
internal interface ISqliTransactionAwareStatement
{
    /// <summary>Called before SQ_CMMTWORK is sent. Implementations should make buffered
    /// work durable in the closing transaction (e.g. flush). An exception here propagates
    /// out of <see cref="SqliSession.ExecuteCommit"/> before SQ_CMMTWORK is sent, so the
    /// commit does not happen.</summary>
    void OnBeforeCommit();

    /// <summary>Called after SQ_CMMTWORK has completed successfully.</summary>
    void OnAfterCommit();

    /// <summary>Called before SQ_RBWORK is sent. Implementations should abandon
    /// buffered-but-unsent work rather than send it — a rollback means it was never meant
    /// to land.</summary>
    void OnBeforeRollback();

    /// <summary>Called after SQ_RBWORK has completed successfully.</summary>
    void OnAfterRollback();
}
