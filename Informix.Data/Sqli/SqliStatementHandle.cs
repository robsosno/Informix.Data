namespace Informix.Data.Sqli;

/// <summary>
/// An immutable snapshot of one prepared statement's identity on a
/// <see cref="SqliSession"/>: the server-side statement id plus everything the
/// wire protocol needs restored into the session's "current statement" bookkeeping
/// before a SQ_ID-driven operation (execute, open cursor, release) can target it.
///
/// <para>Produced by <see cref="SqliSession.Prepare"/>. The server tracks every
/// prepared statement id independently of whichever one the client last touched, so
/// several handles can stay valid at once (e.g. one per <see cref="Informix.Data.IfxCommand"/>
/// that has called <c>Prepare()</c>, or a session-level cache's entries) — only one can be
/// the session's "current" statement at a time, and <see cref="SqliSession.Activate"/>
/// switches which one that is, at no wire cost.</para>
/// </summary>
internal sealed class SqliStatementHandle
{
    public required short StatementId { get; init; }

    public required short StatementType { get; init; }

    /// <summary>The described result columns, snapshotted at prepare time.</summary>
    public required IReadOnlyList<IfxColumnMetadata> Columns { get; init; }

    public required int TupleSize { get; init; }

    /// <summary>Any var-length VARCHAR column present (see <c>SqliSession._rowNeedsWalk</c>).</summary>
    public required bool RowNeedsWalk { get; init; }

    /// <summary>LVARCHAR/UDT/BLOB or DISTINCT column present (see <c>SqliSession._rowHasUnsupported</c>).</summary>
    public required bool RowHasUnsupported { get; init; }

    /// <summary>The parameter count this statement was prepared with (SQ_PREPARE's count word).</summary>
    public required int ParameterCount { get; init; }

    /// <summary>
    /// True when this statement produces a result set: a SELECT, or an EXECUTE
    /// PROCEDURE that describes output columns (port of <c>isResultSet</c>; mirrors
    /// <see cref="SqliSession.DescribesResultSet"/> for a handle that may not be
    /// the session's current statement).
    /// </summary>
    public bool DescribesResultSet =>
        StatementType == (short)SqliStatementType.Select
        || (StatementType == (short)SqliStatementType.ExecProc && Columns.Count > 0);
}
