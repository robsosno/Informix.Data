namespace Informix.Data.Sqli;

/// <summary>
/// The live-cursor implementation of <see cref="IIfxResultCursor"/>: a thin
/// adapter that lets <see cref="Informix.Data.IfxDataReader"/> drive the
/// forward-only fetch loop owned by <see cref="SqliSession"/>. Disposing it
/// closes the server cursor and releases the prepared statement.
/// </summary>
internal sealed class SqliCursor : IIfxResultCursor
{
    private readonly SqliSession _session;
    // Used by ExecuteScalar/SingleRow so a second Read never asks the server for more.
    private readonly bool _singleRow;
    // Statement to release or cache when the cursor closes; null when the command owns it (explicit Prepare).
    private readonly (SqliStatementHandle Handle, string Sql, bool Hold)? _endStatement;
    private bool _disposed;
    private bool _readOne;

    /// <param name="endStatement">When non-null, Dispose ends this statement's lifetime via
    /// <see cref="SqliSession.ReleaseOrCache"/> — a real SQ_RELEASE, or a prepared-statement
    /// cache return, depending on whether the session's cache is on. Null when the owning
    /// <see cref="Informix.Data.IfxCommand"/> explicitly <c>Prepare()</c>d this statement and
    /// is holding onto it for reuse — closing the cursor still ends the result set, but the
    /// prepared statement itself stays alive on the server either way.</param>
    internal SqliCursor(
        SqliSession session, bool singleRow = false,
        (SqliStatementHandle Handle, string Sql, bool Hold)? endStatement = null)
    {
        _session = session;
        _singleRow = singleRow;
        _endStatement = endStatement;
    }

    public IReadOnlyList<IfxColumnMetadata> Columns => _session.Columns;

    public int RecordsAffected => -1; // SELECT semantics per DbDataReader contract

    public bool HasRows => _session.HasRows;

    public bool Read()
    {
        // CommandBehavior.SingleRow: surface at most the first row.
        if (_singleRow && _readOne)
            return false;
        bool more = _session.NextRow();
        _readOne |= more;
        return more;
    }

    public object GetValue(int ordinal) => _session.DecodeColumn(ordinal);

    public byte[]? GetRawBytes(int ordinal) => _session.GetRawColumnBytes(ordinal);

    public double? GetDecimalAsDouble(int ordinal) => _session.DecodeColumnAsDouble(ordinal);

    public byte[]? GetSmartLobLocator(int ordinal) => _session.GetSmartLobLocator(ordinal);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.CloseCursor();
        if (_endStatement is { } e)
            _session.ReleaseOrCache(e.Handle, e.Sql, scroll: false, e.Hold);
    }
}

/// <summary>
/// A columns-only cursor for <see cref="System.Data.CommandBehavior.SchemaOnly"/>: the
/// statement is prepared/described but never opened, so there are no rows. The statement
/// is always already released (or cached, or left held) by the caller before this is
/// constructed — describing never opens a cursor, so there is nothing left to end here.
/// </summary>
internal sealed class SqliSchemaCursor(IReadOnlyList<IfxColumnMetadata> columns) : IIfxResultCursor
{
    public IReadOnlyList<IfxColumnMetadata> Columns { get; } = columns;

    public int RecordsAffected => -1;

    public bool HasRows => false;

    public bool Read() => false;

    public object GetValue(int ordinal) => throw new InvalidOperationException("SchemaOnly reader exposes no rows.");

    public byte[]? GetRawBytes(int ordinal) => null;

    public double? GetDecimalAsDouble(int ordinal) => null;

    public byte[]? GetSmartLobLocator(int ordinal) => null;

    public void Dispose() { }
}

/// <summary>
/// The no-rows cursor an <c>ExecuteReader</c> over a non-SELECT statement
/// returns: no columns, no rows, only the affected-row count.
/// </summary>
internal sealed class SqliEmptyCursor(int recordsAffected) : IIfxResultCursor
{
    public IReadOnlyList<IfxColumnMetadata> Columns { get; } = [];

    public int RecordsAffected => recordsAffected;

    public bool HasRows => false;

    public bool Read() => false;

    public object GetValue(int ordinal) => throw new InvalidOperationException("The statement returned no result set.");

    public byte[]? GetRawBytes(int ordinal) => null;

    public double? GetDecimalAsDouble(int ordinal) => null;

    public byte[]? GetSmartLobLocator(int ordinal) => null;

    public void Dispose() { }
}

/// <summary>
/// The random-access implementation of <see cref="IIfxResultCursor"/> backing
/// <see cref="Informix.Data.IfxScrollReader"/> — a scroll cursor's positioning state
/// (<c>MoveFirst</c>/<c>MoveLast</c>/<c>MovePrevious</c>/<c>MoveAbsolute</c>/
/// <c>MoveRelative</c>, on top of the interface's own <see cref="Read"/> for "next") plus
/// the statement-lifetime <c>Dispose</c> logic <see cref="IfxScrollReader"/> used to own
/// directly. Each <c>Move*</c>/<see cref="Read"/> call issues one SQ_SFETCH round trip
/// through <see cref="SqliSession.ScrollFetch"/> (ports of <c>IfxResultSet.next</c>/
/// <c>previous</c>/<c>first</c>/<c>last</c>/<c>absolute</c>/<c>relative</c>).
/// </summary>
internal sealed class SqliScrollCursor : IIfxResultCursor
{
    private readonly SqliSession _session;
    private readonly (SqliStatementHandle Handle, string Sql, bool Hold)? _endStatement;
    private bool _disposed;
    private int _curRowId;   // 1-based position of the current row; 0 = before-first / no row
    private int _lastRowId;  // >0 known last row id; 0 unknown; -1 the result set is empty
    // The row the SERVER's cursor stands on after the last fetch. A NEXT fetch returns a buffer of rows and
    // leaves the server on the last of them, so "next" is the client's next row only once it has used up
    // that buffer. int.MinValue = unknown (then only absolute fetches are safe).
    private int _serverRow;

    /// <param name="endStatement">See <see cref="SqliCursor(SqliSession, bool,
    /// ValueTuple{SqliStatementHandle, string, bool}?)"/>'s parameter of the same name —
    /// identical contract, just for a scroll cursor's <c>SQ_HOLD</c> instead.</param>
    internal SqliScrollCursor(SqliSession session, (SqliStatementHandle Handle, string Sql, bool Hold)? endStatement = null)
    {
        _session = session;
        _endStatement = endStatement;
    }

    public IReadOnlyList<IfxColumnMetadata> Columns => _session.Columns;

    public int RecordsAffected => -1; // SELECT semantics per DbDataReader contract

    public bool HasRows => _session.HasRows;

    /// <summary>The 1-based position of the current row, or 0 when not on a row.</summary>
    public int RowNumber => _curRowId;

    public object GetValue(int ordinal) => _session.DecodeColumn(ordinal);

    public byte[]? GetRawBytes(int ordinal) => _session.GetRawColumnBytes(ordinal);

    public double? GetDecimalAsDouble(int ordinal) => _session.DecodeColumnAsDouble(ordinal);

    public byte[]? GetSmartLobLocator(int ordinal) => _session.GetSmartLobLocator(ordinal);

    /// <summary>Moves to the next row (the interface's "next tuple" — for a scroll cursor,
    /// the same operation <see cref="Informix.Data.IfxScrollReader.MoveNext"/> exposes as
    /// <c>MoveNext</c>). Returns false past the last row.</summary>
    public bool Read()
    {
        int target = _curRowId + 1;
        if (_lastRowId > 0 && target > _lastRowId)
        {
            _curRowId = _lastRowId + 1; // after-last
            return false;
        }
        if (_session.TryPositionInBuffer(target))
        {
            _curRowId = target;
            return true;
        }
        // NEXT is the server's next row, which is ours only if the server stands on the row before it.
        short direction = target == _serverRow + 1 ? SqliSession.ScrollNext : SqliSession.ScrollAbsolute;
        if (!Fetch(direction, target))
        {
            _lastRowId = _curRowId;     // the previous row was the last one
            _curRowId += 1;             // after-last
            return false;
        }
        _curRowId = _session.CurrentTupleId;
        return true;
    }

    // One SQ_SFETCH, unless the rows buffered by an earlier fetch already contain the wanted one.
    private bool Fetch(short direction, int target)
    {
        if (direction != SqliSession.ScrollLast && _session.TryPositionInBuffer(target))
            return true;
        bool found = _session.ScrollFetch(direction, target);
        _serverRow = found ? _session.LastBufferedTupleId : int.MinValue;
        return found;
    }

    /// <summary>Moves to the previous row. Returns false before the first row.</summary>
    public bool MovePrevious()
    {
        if (_curRowId <= 1)
        {
            _curRowId = 0; // before-first
            return false;
        }
        if (!Fetch(SqliSession.ScrollAbsolute, _curRowId - 1))
        {
            _curRowId = 0;
            return false;
        }
        _curRowId = _session.CurrentTupleId;
        return true;
    }

    /// <summary>Moves to the first row. Returns false when the result set is empty.</summary>
    public bool MoveFirst()
    {
        if (!Fetch(SqliSession.ScrollAbsolute, 1))
        {
            _lastRowId = -1;
            _curRowId = 0;
            return false;
        }
        _curRowId = _session.CurrentTupleId;
        return true;
    }

    /// <summary>Moves to the last row (and learns the row count). Returns false when empty.</summary>
    public bool MoveLast()
    {
        if (!Fetch(SqliSession.ScrollLast, 0))
        {
            _lastRowId = -1;
            _curRowId = 0;
            return false;
        }
        _curRowId = _lastRowId = _session.CurrentTupleId;
        return true;
    }

    /// <summary>
    /// Moves to absolute row <paramref name="row"/> (1-based). A negative value
    /// counts back from the end (-1 is the last row). Returns false when the
    /// position is outside the result set.
    /// </summary>
    public bool MoveAbsolute(int row)
    {
        if (row == 0)
        {
            _curRowId = 0; // before-first
            return false;
        }
        if (row > 0)
        {
            if (_lastRowId > 0 && row > _lastRowId)
            {
                _curRowId = _lastRowId + 1;
                return false;
            }
            if (!Fetch(SqliSession.ScrollAbsolute, row))
            {
                if (_lastRowId <= 0)
                    _lastRowId = row - 1; // learned the end short of the request
                _curRowId = _lastRowId > 0 ? _lastRowId + 1 : 0;
                return false;
            }
            _curRowId = _session.CurrentTupleId;
            return true;
        }
        // row < 0: relative to the end, so the row count must be known.
        if (_lastRowId <= 0 && !MoveLast())
            return false;
        int target = _lastRowId + row + 1;
        if (target < 1)
        {
            _curRowId = 0;
            return false;
        }
        if (!Fetch(SqliSession.ScrollAbsolute, target))
        {
            _curRowId = 0;
            return false;
        }
        _curRowId = _session.CurrentTupleId;
        return true;
    }

    /// <summary>
    /// Moves <paramref name="rows"/> from the current position (negative moves back).
    /// Returns false when the resulting position is outside the result set.
    /// </summary>
    public bool MoveRelative(int rows)
    {
        if (rows == 0)
            return _curRowId > 0;
        int target = _curRowId + rows;
        if (target < 1)
        {
            _curRowId = 0;
            return false;
        }
        if (_lastRowId > 0 && target > _lastRowId)
        {
            _curRowId = _lastRowId + 1;
            return false;
        }
        if (!Fetch(SqliSession.ScrollAbsolute, target))
        {
            if (_lastRowId <= 0)
                _lastRowId = target - 1;
            _curRowId = _lastRowId > 0 ? _lastRowId + 1 : 0;
            return false;
        }
        _curRowId = _session.CurrentTupleId;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.CloseCursor();
        if (_endStatement is { } e)
            _session.ReleaseOrCache(e.Handle, e.Sql, scroll: true, e.Hold);
    }
}
