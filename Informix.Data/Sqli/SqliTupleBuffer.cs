using Informix.Data.Types;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// The fetch buffer and row cursor extracted out of <see cref="SqliSession"/> (mirrors
/// <c>IfxRowColumn</c>'s <c>tuple</c>/<c>offsetVector</c> bookkeeping): the raw bytes of
/// every tuple in the current fetch, their per-row start offsets, the current row
/// position, and — once a variable-width row has been walked — the per-column offsets
/// within that one row.
///
/// <para>Deliberately narrow: this type knows nothing about the link, the gate, the wire
/// writer, or fetching. It does not decide when to fetch more, whether the current
/// statement's row layout needs walking, or what a column's fixed width is — those stay
/// on <see cref="SqliSession"/> (<c>_rowNeedsWalk</c>/<c>_rowHasUnsupported</c> are
/// properties of the described statement, carried across a prepared-statement cache
/// checkout by <see cref="SqliStatementHandle"/>, not of the buffer; <c>_first</c>/
/// <c>_closeReceived</c> are cursor lifecycle, driving <c>NextRow</c>'s refill decision).
/// Everything this type's callers must supply (a column list, capabilities, whether the
/// row needs walking) comes in as a parameter rather than being reached for.</para>
/// </summary>
internal sealed class SqliTupleBuffer
{
    private byte[] _tuple = [];
    private int _totalTupleSize;
    private readonly List<int> _rowOffsets = [0]; // _rowOffsets[i] = start of row i; last entry = end
    private int _totalTuples;
    private int _curRow = -1;
    private int _tupleBufferSize = -1;
    // SQ_TUPID: the absolute (1-based) result-set row id of the first tuple in the
    // current buffer, sent by the server ahead of the tuples for a scroll cursor.
    private int _tupid;
    private int[] _walkOffsets = [];
    private int[] _walkLengths = [];

    /// <summary>Rows currently held in the buffer.</summary>
    internal int Count => _totalTuples;

    /// <summary>The 0-based position of the current row, or -1 before the first <see cref="Advance"/>.</summary>
    internal int CurrentRow => _curRow;

    /// <summary>
    /// The absolute (1-based) result-set position of the current row (port of
    /// <c>getTupid</c>: <c>Tupid + curRow</c>). Meaningful after a scroll fetch positions
    /// on a row.
    /// </summary>
    internal int CurrentTupleId => _tupid + Math.Max(_curRow, 0);

    /// <summary>
    /// Port of <c>getTupleBufferSize(fetchBufferSize)</c>. A positive per-statement
    /// override wins (capped at <paramref name="maxFetchBufferSize"/>), otherwise the
    /// classic size derived from the tuple width (4 KB / 8 KB / tupleSize), bumped up to
    /// the connection-level FET_BUF_SIZE when that is larger. The result is cached for
    /// the statement (cleared by <see cref="Reset"/>'s caller — see
    /// <c>SqliSession.ClearStatementState</c>).
    /// </summary>
    internal int Capacity(int tupleSize, int connectionFetchBufferSize, int fetchBufferOverride, int maxFetchBufferSize)
    {
        if (fetchBufferOverride > 0)
            return Math.Min(fetchBufferOverride, maxFetchBufferSize);
        if (_tupleBufferSize != -1)
            return _tupleBufferSize;
        _tupleBufferSize = tupleSize <= 2048 ? 4096 : tupleSize <= 4096 ? 8192 : tupleSize;
        if (connectionFetchBufferSize > _tupleBufferSize)
            _tupleBufferSize = Math.Min(maxFetchBufferSize, connectionFetchBufferSize);
        return _tupleBufferSize;
    }

    /// <summary>Clears the cached <see cref="Capacity"/> result — call whenever the
    /// per-statement override or the described tuple width may have changed.</summary>
    internal void ResetCapacity() => _tupleBufferSize = -1;

    /// <summary>
    /// Reads one tuple's bytes (port of the buffer half of <c>receiveTuple</c>: the PDU's
    /// warnings word and size — extended vs. classic width — are read by the caller, since
    /// that framing choice is <see cref="SqliSession.Capabilities"/>, not buffer state) and
    /// records its cumulative end offset. <paramref name="growTo"/> is what an empty buffer
    /// grows to on its first tuple — <see cref="Capacity"/>'s result, computed by the caller
    /// since it needs session-level inputs this type does not have.
    /// </summary>
    internal void Append(IfxWireReader reader, int size, int growTo)
    {
        if (_totalTupleSize + size > _tuple.Length)
        {
            int newSize = Math.Max(_tuple.Length == 0 ? growTo : _tuple.Length * 2, _totalTupleSize + size);
            Array.Resize(ref _tuple, newSize);
        }
        reader.ReadFully(_tuple.AsSpan(_totalTupleSize, size));
        if ((size & 1) != 0)
            _ = reader.Read(); // even-length pad byte

        _totalTupleSize += size;
        _totalTuples++;
        _rowOffsets.Add(_totalTupleSize);
    }

    internal void SetTupleId(int tupid) => _tupid = tupid;

    /// <summary>The absolute row id of the first tuple in the buffer (SQ_TUPID); meaningful while <see cref="Count"/> is above 0.</summary>
    internal int FirstTupleId => _tupid;

    /// <summary>True when the buffer holds absolute row <paramref name="rowId"/>.</summary>
    internal bool Contains(int rowId) => _totalTuples > 0 && rowId >= _tupid && rowId < _tupid + _totalTuples;

    /// <summary>Makes absolute row <paramref name="rowId"/> (which must be <see cref="Contains"/>-ed) the current row.</summary>
    internal void MoveTo(int rowId) => _curRow = rowId - _tupid;

    /// <summary>Discards every buffered tuple and rewinds to before the first row. Does
    /// NOT touch the cached <see cref="Capacity"/> result (a statement-lifetime value,
    /// not a per-fetch one) — <see cref="ResetCapacity"/> is separate.</summary>
    internal void Reset()
    {
        _rowOffsets.Clear();
        _rowOffsets.Add(0);
        _totalTupleSize = 0;
        _totalTuples = 0;
        _curRow = -1;
    }

    /// <summary>Moves to the next buffered row. The caller (<c>SqliSession.NextRow</c>) is
    /// the one that knows whether a next row exists (it may need a wire round trip via
    /// <c>FetchMore</c> to find out) — this only ever does the <c>_curRow++</c> itself.</summary>
    internal void Advance() => _curRow++;

    /// <summary>
    // Computes offset and length of every field of the current row. Widths: VARCHAR is a length byte + data (or its
    // declared maximum when the server does not send actual lengths); LVARCHAR/LOB/JSON/BSON/UDT/BOOL are
    // [flag:1][len:int32][data]; ROW/collections are [len:int32][data] without a flag byte; everything else is fixed.
    /// </summary>
    internal void Walk(IReadOnlyList<IfxColumnMetadata> columns, SqliCapabilities caps)
    {
        if (_walkOffsets.Length < columns.Count)
        {
            _walkOffsets = new int[columns.Count];
            _walkLengths = new int[columns.Count];
        }
        int pos = _rowOffsets[_curRow];
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            int len;
            if (col.SqliTypeCode is SqliTypeCode.VarChar or SqliTypeCode.NVarChar)
                len = caps.IsVarcharVarLenSupported ? 1 + _tuple[pos] : col.ByteLength + 1;
            else if (col.SqliTypeCode is SqliTypeCode.LVarChar or SqliTypeCode.Clob or SqliTypeCode.Blob
                or SqliTypeCode.Json or SqliTypeCode.Bson or SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
                or SqliTypeCode.Bool)
                // [flag:1][length:int32][data] — the field footprint is 5 + length.
                // (A smart large object's data is its 72-byte locator; JSON/BSON and the
                // opaque UDTs carry their actual inline payload; NULL is flag 1 + len 0
                // in every case.)
                len = 5 + IfxWireConverter.ReadInt32(_tuple.AsSpan(pos + 1, 4));
            else if (col.SqliTypeCode is SqliTypeCode.Row or SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List
                or SqliTypeCode.Collection)
                // ROW/collection fields are framed [length:int32][data] (no flag byte);
                // the data is a self-describing complex value. Footprint = 4 + length.
                // (IfxTypes.isComplexType — ROW and the collections only.)
                len = 4 + IfxWireConverter.ReadInt32(_tuple.AsSpan(pos, 4));
            else
                len = col.ByteLength;
            _walkOffsets[i] = pos;
            _walkLengths[i] = len;
            pos += len;
        }
    }

    /// <summary>The byte offset of column <paramref name="ordinal"/> within the current
    /// row — the walked layout for a variable-width row, otherwise the described fixed
    /// position. Shared by <see cref="Extent"/> and callers that only need the offset
    /// (<c>GetRawColumnBytes</c>, <c>GetSmartLobLocator</c> compute their own length).</summary>
    internal int Offset(int ordinal, IfxColumnMetadata col, bool rowNeedsWalk) =>
        rowNeedsWalk ? _walkOffsets[ordinal] : _rowOffsets[_curRow] + col.StartPosition;

    /// <summary>Where a column's bytes sit in the current row: the walked layout for a
    /// variable-width row, otherwise the described fixed position/width.</summary>
    internal (int Offset, int Length) Extent(int ordinal, IfxColumnMetadata col, bool rowNeedsWalk)
    {
        if (rowNeedsWalk)
            return (_walkOffsets[ordinal], _walkLengths[ordinal]);
        return (
            Offset(ordinal, col, rowNeedsWalk),
            col.SqliTypeCode is SqliTypeCode.VarChar or SqliTypeCode.NVarChar ? col.ByteLength + 1 : col.ByteLength);
    }

    /// <summary>The raw bytes at <paramref name="offset"/>..+<paramref name="length"/> in the buffer.</summary>
    internal ReadOnlySpan<byte> Slice(int offset, int length) => _tuple.AsSpan(offset, length);
}
