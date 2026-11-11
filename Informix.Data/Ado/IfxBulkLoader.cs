using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Wire;

namespace Informix.Data;

/// <summary>How <see cref="IfxBulkLoader"/> delivers queued rows to the server.</summary>
public enum IfxBulkLoaderMode
{
    // Rows collect client-side and are sent in large chunks: through the native SQ_PUT insert cursor when every column is fixed width,
    // otherwise as a bind/execute chain (the same engine as IfxRowBatch).
    Buffered = 0,

    /// <summary>
    /// Every <see cref="IfxBulkLoader.Put(object?[])"/> executes immediately as its own
    /// round trip — for pinpointing a bad row in a failing batch and for benchmarking
    /// buffered versus plain inserts.
    /// </summary>
    PlainInserts = 1,
}

/// <summary>
/// Fast loading of many rows into one INSERT (or a row-less EXECUTE PROCEDURE). Declare the parameter types with
/// <see cref="DefineParameters"/>, then <see cref="Put(object?[])"/> rows and finish with <see cref="Complete"/> (or use <see cref="Execute"/>, which does all of it).
/// While a loader is active it pins the connection's session: no other command runs on that connection.
///
/// Commit and rollback interact with the loader: buffered rows are flushed before a commit and dropped before a rollback; the server also
/// closes a non-HOLD insert cursor on either, which is why <see cref="HoldCursor"/> must be set to keep loading across commits. On a logged
/// database a non-HOLD insert cursor cannot even be opened outside a transaction (-255). See docs/msg_flow.md section 5.
/// </summary>
public sealed class IfxBulkLoader : IDisposable, ISqliTransactionAwareStatement
{
    // The loader flushes a buffered batch once its estimated encoded size reaches
    // _flushThreshold: a little below the engine's PDU guard so a size-triggered flush
    // travels as a single round trip rather than tripping a mid-batch split. For the
    // SQ_BIND/SQ_EXECUTE batch engine the guard is the fixed ~32 KB PDU limit of
    // SqliSession.ExecuteBatch; for the SQ_PUT insert-cursor engine it is the session's
    // insert buffer (FET_BUF_SIZE sizes insert-cursor buffering
    // too, not just fetches), so Open() rescales the threshold to match.
    private const int BatchFlushThreshold = 30000;
    private const int FlushHeadroom = 2000;
    private int _flushThreshold = BatchFlushThreshold;

    // Pending rows of the bind/execute path.
    private readonly List<IReadOnlyList<SqliBindValue>> _buffer = [];
    private IfxType[]? _declaredTypes;
    // One converter per declared parameter: strict, range-checked conversion from a .NET value to a bind value.
    private Func<object?, SqliBindValue>[]? _converters;
    private SqliSession? _session;

    // True when the native SQ_PUT engine is in use (Buffered mode and all columns fixed width). Then _tupleBuffer holds finished
    // row images of _rowSize bytes laid out by _fields (the describe columns).
    private bool _useInsertCursor;
    private List<byte[]>? _tupleBuffer;
    private IfxColumnMetadata[]? _fields;
    private int _rowSize;

    private bool _holdCursor;
    private IfxBulkLoaderMode _mode;

    private long _rowsPut;
    private long _rowsInserted;
    private long _estimatedBytes;

    private bool _opened;
    private bool _closed;
    private bool _discarded;
    // Lifecycle: opened -> (closed | discarded | faulted). A faulted loader (server error, failed flush) cannot be reused.
    private bool _faulted;
    private bool _disposed;

    // The server closed the non-HOLD insert cursor at a commit/rollback; further rows are refused with an explanatory message.
    private bool _cursorClosedByTransaction;

    private const string CursorClosedMessage =
        "A commit or rollback closed the insert cursor because HoldCursor is false. " +
        "Set HoldCursor = true to load across commits/rollbacks, or call Complete() to end this load.";

    internal IfxBulkLoader(IfxConnection connection, string commandText)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
    }

    /// <summary>The INSERT / EXECUTE PROCEDURE statement, with positional <c>?</c> placeholders.</summary>
    public string CommandText { get; }

    /// <summary>The connection this loader streams into.</summary>
    public IfxConnection Connection { get; }

    /// <summary>The ambient transaction, if any. Reassign it after committing/rolling back
    /// mid-load (e.g. right after your own <see cref="Flush"/> + <see cref="IfxTransaction.Commit"/>
    /// cadence starts a new one); it is informational — the session already reflects the
    /// connection's transaction state.</summary>
    public IfxTransaction? Transaction { get; set; }

    /// <summary>Buffered (default) or plain inserts; cannot be changed after the loader has opened.</summary>
    public IfxBulkLoaderMode Mode
    {
        get => _mode;
        set
        {
            ThrowIfOpen(nameof(Mode));
            _mode = value;
        }
    }

    /// <summary>
    /// Declare the insert cursor WITH HOLD so it survives commit and rollback (needed to commit periodically while loading, and to open the
    /// cursor outside a transaction on a logged database). Default false. Cannot be changed after opening.
    /// </summary>
    public bool HoldCursor
    {
        get => _holdCursor;
        set
        {
            ThrowIfOpen(nameof(HoldCursor));
            _holdCursor = value;
        }
    }

    /// <summary>Rows accepted by <c>Put</c> so far.</summary>
    public long RowsPut => _rowsPut;

    /// <summary>Rows the server has confirmed; trails <see cref="RowsPut"/> by the buffered rows.</summary>
    public long RowsInserted => _rowsInserted;

    /// <summary>Rows buffered but not yet sent (always 0 in <see cref="IfxBulkLoaderMode.PlainInserts"/>).</summary>
    public int PendingRows => _useInsertCursor ? (_tupleBuffer?.Count ?? 0) : _buffer.Count;

    /// <summary>
    /// Declares the parameter types, in placeholder order. Obligatory before opening, with at
    /// least one parameter; locks the per-column converters <c>Put</c> uses. Throws
    /// <see cref="NotSupportedException"/> if a declared type cannot be bound.
    /// </summary>
    public void DefineParameters(params IfxType[] types)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfOpen(nameof(DefineParameters));
        ArgumentNullException.ThrowIfNull(types);
        if (types.Length == 0)
            throw new ArgumentException("A bulk loader needs at least one parameter.", nameof(types));

        var converters = new Func<object?, SqliBindValue>[types.Length];
        for (int i = 0; i < types.Length; i++)
            converters[i] = SqliBindValue.ConverterFor(types[i], i, Connection.DbEncoding);

        _declaredTypes = (IfxType[])types.Clone();
        _converters = converters;
    }

    /// <summary>
    /// Runs a whole load: opens if needed,
    /// invokes <paramref name="body"/>, then <see cref="Complete"/> on normal return — or
    /// <see cref="Discard"/> and rethrow when the body throws. Returns <see cref="RowsInserted"/>.
    /// </summary>
    public long Execute(Action<IfxBulkLoader> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
            Open();
        try
        {
            body(this);
        }
        catch
        {
            Discard();
            throw;
        }
        return CompleteCore();
    }

    /// <summary>
    /// Prepares the statement and freezes the configuration. Optional — the first
    /// <c>Put</c>/<see cref="Execute"/> opens implicitly. Validates that the statement is an
    /// INSERT (or a row-less EXECUTE PROCEDURE) whose <c>?</c> count matches the declared
    /// parameter count, and pins the connection.
    /// </summary>
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException("The bulk loader has faulted and cannot be reused.");
        if (_closed || _discarded)
            throw new InvalidOperationException("The bulk loader has already been completed or discarded.");
        if (_opened)
            return;

        if (_declaredTypes is null || _converters is null || _declaredTypes.Length == 0)
            throw new InvalidOperationException(
                "Call DefineParameters with at least one parameter before opening the bulk loader.");

        // Non-wire validation first (throws without pinning or touching the connection):
        // reject named placeholders and cross-check the '?' count against the declaration.
        int placeholders = IfxSqlParser.CountPositionalPlaceholders(CommandText);
        if (placeholders != _declaredTypes.Length)
            throw new InvalidOperationException(
                $"DefineParameters declared {_declaredTypes.Length} parameter(s) but the statement has " +
                $"{placeholders} '?' placeholder(s).");

        var session = RequireSession();

        // DefineParameters built the converters before a session existed, so it could not
        // know the negotiated blob-descriptor layout. Rebuild them now that it is known;
        // the types were already validated there, so this cannot start throwing.
        if (session.Capabilities.IsLongRowId)
            for (int i = 0; i < _declaredTypes.Length; i++)
                _converters[i] = SqliBindValue.ConverterFor(
                    _declaredTypes[i], i, Connection.DbEncoding, longRowId: true);

        session.PinStatement(this); // throws if another loader already owns the connection
        try
        {
            session.Prepare(CommandText, _declaredTypes.Length);

            short st = session.StatementType;
            bool accepted = st == (short)SqliStatementType.Insert
                || (st == (short)SqliStatementType.ExecProc && !session.DescribesResultSet);
            if (!accepted)
            {
                session.ReleaseStatement();
                session.UnpinStatement(this);
                throw new InvalidOperationException(
                    "IfxBulkLoader accepts only an INSERT or a row-less EXECUTE PROCEDURE statement.");
            }

            // Choose the streaming SQ_PUT insert-cursor engine when the statement is an
            // INSERT whose describe is all fixed-width columns and the load is Buffered;
            // otherwise fall through to the SQ_BIND/SQ_EXECUTE batch engine.
            if (_mode == IfxBulkLoaderMode.Buffered
                && st == (short)SqliStatementType.Insert
                && session.Columns.Count == _declaredTypes.Length
                && SqliInsertTuple.IsEligible(session.Columns))
            {
                _fields = [.. session.Columns];
                _rowSize = session.TupleSize;
                session.OpenInsertCursor(hold: _holdCursor);
                _tupleBuffer = [];
                _useInsertCursor = true;
                // Size the flush trigger from the insert buffer (FET_BUF_SIZE when set),
                // so a configured buffer actually packs more rows per SQ_PUT round trip.
                _flushThreshold = Math.Max(1024, session.InsertBufferSize - FlushHeadroom);
            }
            _opened = true;
        }
        catch (InvalidOperationException)
        {
            // Statement-shape rejection above already released + unpinned; a pin left set
            // means Prepare itself threw — treat that as a fault.
            if (ReferenceEquals(session.StatementOwner, this))
                Fault();
            throw;
        }
        catch
        {
            Fault();
            throw;
        }
    }

    /// <summary>Buffers one row from discrete values; may trigger an auto-flush.</summary>
    public void Put(params ReadOnlySpan<object?> values)
    {
        EnsureOpen();
        if (values.Length != _declaredTypes!.Length)
            throw Arity(values.Length);
        PutValues(values);
    }

    /// <summary>Buffers one row supplied as a plain array; may trigger an auto-flush.</summary>
    public void Put(object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Put(new ReadOnlySpan<object?>(values));
    }

    /// <summary>Buffers one row read from an <see cref="IDataRecord"/> (or a live reader):
    /// <see cref="IDataRecord.IsDBNull(int)"/> maps to SQL NULL and each field runs through
    /// the declared converters. The record's field count must equal the declared count.</summary>
    public void Put(IDataRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureOpen();
        int count = _declaredTypes!.Length;
        if (record.FieldCount != count)
            throw new ArgumentException(
                $"IfxBulkLoader.Put(IDataRecord) at row {_rowsPut + 1}: the record has {record.FieldCount} " +
                $"field(s) but {count} parameter(s) are declared.");
        var values = new object?[count];
        for (int i = 0; i < count; i++)
            values[i] = record.IsDBNull(i) ? null : record.GetValue(i);
        PutValues(values);
    }

    // Routes one already-arity-checked row to the active engine: a fixed-width tuple
    // image for the SQ_PUT insert cursor, or a length-prefixed SQ_BIND row otherwise.
    private void PutValues(ReadOnlySpan<object?> values)
    {
        if (_useInsertCursor)
        {
            AcceptTuple(EncodeTuple(values));
            return;
        }
        var row = new SqliBindValue[values.Length];
        for (int i = 0; i < row.Length; i++)
            row[i] = Convert(i, values[i]);
        AcceptBinds(row);
    }

    /// <summary>Sends the buffered rows now, returning how many the server inserted. A no-op
    /// returning 0 in <see cref="IfxBulkLoaderMode.PlainInserts"/> (rows travel as they arrive).</summary>
    public long Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException("The bulk loader has faulted; it cannot flush.");
        if (_cursorClosedByTransaction)
            throw new InvalidOperationException(CursorClosedMessage);
        if (!_opened || _closed || _discarded || _mode == IfxBulkLoaderMode.PlainInserts)
            return 0;
        return FlushCore();
    }

    /// <summary>Flushes any tail and releases the prepared statement; returns
    /// <see cref="RowsInserted"/>. The loader is closed afterwards.</summary>
    public long Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CompleteCore();
    }

    /// <summary>Drops any buffered rows and releases the statement without sending them.
    /// The manual-style counterpart of an <see cref="Execute"/> rollback.</summary>
    public void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closed || _discarded)
            return;
        _buffer.Clear();
        _tupleBuffer?.Clear();
        _estimatedBytes = 0;
        if (_opened && _session is not null)
        {
            if (!_faulted)
            {
                try
                {
                    if (_useInsertCursor)
                        _session.CloseInsertCursor();
                    _session.ReleaseStatement();
                }
                catch { _faulted = true; } // wire release failed; still unpin below
            }
            _session.UnpinStatement(this);
        }
        _discarded = true;
    }

    /// <summary>Completes the loader (flushing the tail) unless it faulted or was discarded,
    /// in which case it releases the connection without sending. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_closed || _discarded)
            return;
        if (_faulted)
        {
            _session?.UnpinStatement(this);
            return;
        }
        CompleteCore();
    }


    // Flush what is buffered, close the insert cursor, release the statement and unpin the session. Errors fault the loader.
    private long CompleteCore()
    {
        if (_faulted)
            throw new InvalidOperationException("The bulk loader has faulted; it cannot be completed.");
        if (_closed || _discarded)
            return _rowsInserted;

        if (_opened && _session is not null)
        {
            if (_mode == IfxBulkLoaderMode.Buffered)
                FlushCore();

            try
            {
                if (_useInsertCursor)
                    _session.CloseInsertCursor(); // no-op if a commit/rollback already closed it
                _session.ReleaseStatement();
            }
            catch { Fault(); throw; }
            _session.UnpinStatement(this);
        }
        _closed = true;
        return _rowsInserted;
    }

    // Plain mode runs the INSERT right away. Buffered mode queues the row and flushes when the estimated size passes the threshold.
    private void AcceptBinds(SqliBindValue[] row)
    {
        if (_mode == IfxBulkLoaderMode.PlainInserts)
        {
            try { _session!.ExecuteStatement(row); }
            catch { Fault(); throw; }
            _rowsPut++;
            _rowsInserted += _session!.RowsProcessed;
            return;
        }

        _buffer.Add(row);
        _rowsPut++;
        _estimatedBytes += EstimateBytes(row);
        if (_estimatedBytes >= _flushThreshold)
            FlushCore();
    }

    private void AcceptTuple(byte[] tuple)
    {
        _tupleBuffer!.Add(tuple);
        _rowsPut++;
        _estimatedBytes += (tuple.Length + 1) & ~1; // even-padded footprint on the wire
        if (_estimatedBytes >= _flushThreshold)
            FlushCore();
    }

    private byte[] EncodeTuple(ReadOnlySpan<object?> values)
    {
        try
        {
            return SqliInsertTuple.Encode(_fields!, _rowSize, values, Connection.DbEncoding);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            string message = $"IfxBulkLoader row {_rowsPut + 1}: {ex.Message}";
            throw ex is OverflowException
                ? new OverflowException(message, ex)
                : new InvalidCastException(message, ex);
        }
    }

    // Insert-cursor path: send the queued row images as SQ_PUT messages. Bind path: one SQ_ID + (SQ_BIND + SQ_EXECUTE) x N chain.
    // A server error closes the cursor and faults the loader.
    private long FlushCore()
    {
        if (_useInsertCursor)
        {
            if (_tupleBuffer!.Count == 0)
                return 0;
            int put;
            try
            {
                put = _session!.PutRows(_tupleBuffer, _rowSize);
            }
            catch (IfxException)
            {
                // A server-side SQ_PUTERR (e.g. a duplicate key) drained cleanly to EOT.
                // Close the insert cursor now (SQ_ID+SQ_CLOSE) — a WITH HOLD cursor left
                // open after a PUT error deadlocks the caller's commit/rollback (the server
                // won't resolve the transaction until the cursor is closed).
                try { _session!.CloseInsertCursor(); } catch { /* connection broken */ }
                Fault();
                throw;
            }
            catch { Fault(); throw; }
            _rowsInserted += put;
            _tupleBuffer.Clear();
            _estimatedBytes = 0;
            return put;
        }

        if (_buffer.Count == 0)
            return 0;

        int[] counts;
        try { counts = _session!.ExecuteBatch(_buffer); }
        catch { Fault(); throw; }

        long inserted = 0;
        foreach (int c in counts)
            inserted += c;
        _rowsInserted += inserted;
        _buffer.Clear();
        _estimatedBytes = 0;
        return inserted;
    }

    private SqliBindValue Convert(int column, object? value)
    {
        try
        {
            return _converters![column](value);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            string message = $"IfxBulkLoader row {_rowsPut + 1}: {ex.Message}";
            throw ex is OverflowException
                ? new OverflowException(message, ex)
                : new InvalidCastException(message, ex);
        }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException("The bulk loader has faulted and can no longer accept rows.");
        if (_cursorClosedByTransaction)
            throw new InvalidOperationException(CursorClosedMessage);
        if (_closed || _discarded)
            throw new InvalidOperationException("The bulk loader has already been completed or discarded.");
        if (!_opened)
            Open();
    }


    // The session calls these four hooks around COMMIT/ROLLBACK: flush before a commit (the rows join the committing transaction),
    // drop before a rollback (they were never sent, so there is nothing to undo), and afterwards note that the server closed a non-HOLD cursor.
    void ISqliTransactionAwareStatement.OnBeforeCommit()
    {
        if (!CanHandleTransactionEvent())
            return;
        FlushCore(); // whatever was Put so far becomes durable in the closing transaction
    }

    void ISqliTransactionAwareStatement.OnAfterCommit() => NoteCursorClosedIfNonHold();

    void ISqliTransactionAwareStatement.OnBeforeRollback()
    {
        if (!CanHandleTransactionEvent())
            return;
        // Abandon, don't flush: rows never sent were never part of the transaction being
        // rolled back, and sending them now would just leave them for a *different* one.
        _buffer.Clear();
        _tupleBuffer?.Clear();
        _estimatedBytes = 0;
    }

    void ISqliTransactionAwareStatement.OnAfterRollback() => NoteCursorClosedIfNonHold();

    private bool CanHandleTransactionEvent() => _opened && !_faulted && !_closed && !_discarded;

    private void NoteCursorClosedIfNonHold()
    {
        if (!CanHandleTransactionEvent() || !_useInsertCursor || _holdCursor)
            return;
        _session!.MarkInsertCursorClosed();
        _cursorClosedByTransaction = true;
    }

    private SqliSession RequireSession()
    {
        if (Connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
        return _session = Connection.SqliSession
            ?? throw new InvalidOperationException("The connection has no live SQLI session.");
    }

    private void Fault()
    {
        _faulted = true;
        _buffer.Clear();
        _tupleBuffer?.Clear();
        _estimatedBytes = 0;
        _session?.UnpinStatement(this); // no-op unless we own the pin
    }

    private void ThrowIfOpen(string member)
    {
        if (_opened)
            throw new InvalidOperationException($"{member} cannot be changed after the bulk loader has opened.");
    }

    private ArgumentException Arity(int given) => new(
        $"IfxBulkLoader.Put at row {_rowsPut + 1}: expected {_converters!.Length} value(s) but got {given}.");

    // Rough wire size of a bound row (type, indicator and precision words per value plus data), used only to decide when to flush.
    private static long EstimateBytes(SqliBindValue[] row)
    {
        long total = 4; // SQ_BIND header + SQ_EXECUTE opcode
        foreach (var bind in row)
            total += 6 + (bind.Data?.Length ?? 0) + (bind.BlobData?.Length ?? 0);
        return total;
    }
}
