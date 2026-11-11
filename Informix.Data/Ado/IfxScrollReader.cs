using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// A random-access reader over an Informix scroll cursor. ADO.NET's
/// <see cref="System.Data.Common.DbDataReader"/> is forward-only, so scrolling is
/// exposed through this provider-specific reader instead.
///
/// <para>Each <c>Move*</c> call issues one SQ_SFETCH round trip and positions on the
/// requested row; the client tracks the current and last row ids. Column values are
/// decoded through the same tuple path the forward-only <see cref="IfxDataReader"/>
/// uses.</para>
///
/// <para>A thin facade over <see cref="SqliScrollCursor"/>, which owns the actual
/// positioning state and the statement-lifetime <c>Dispose</c> logic — this class adds
/// only what a facade should: the <see cref="Activity"/> span lifetime, and sharing its
/// weakly-typed accessors (<see cref="GetBoolean"/>..<see cref="GetDateTime"/>) with
/// <see cref="IfxDataReader"/> via <see cref="IfxConvert"/> rather than each reader
/// carrying its own copy.</para>
/// </summary>
/// <c>IfxResultSet</c> (<c>first/last/absolute/relative/previous/next</c>,
/// <c>TYPE_SCROLL_INSENSITIVE</c>).</remarks>
public sealed class IfxScrollReader : IDisposable
{
    private readonly SqliScrollCursor _cursor;
    private readonly Activity? _activity; // the command's client span; ended on dispose
    private bool _disposed;

    /// <param name="endStatement">When non-null, Dispose ends this statement's lifetime via
    /// <see cref="SqliSession.ReleaseOrCache"/> — a real SQ_RELEASE, or a prepared-statement
    /// cache return, depending on whether the session's cache is on. Null when the owning
    /// <see cref="IfxCommand"/> explicitly <c>Prepare()</c>d this statement and is holding
    /// onto it for reuse — closing the cursor still ends the result set, but the prepared
    /// statement itself stays alive on the server either way.</param>
    internal IfxScrollReader(
        SqliSession session, Activity? activity = null,
        (SqliStatementHandle Handle, string Sql, bool Hold)? endStatement = null)
    {
        _cursor = new SqliScrollCursor(session, endStatement);
        _activity = activity;
    }

    /// <summary>The 1-based position of the current row, or 0 when not on a row.</summary>
    public int RowNumber => _cursor.RowNumber;

    public int FieldCount => _cursor.Columns.Count;

    public string GetName(int ordinal) => _cursor.Columns[ordinal].Name;

    public Type GetFieldType(int ordinal) => _cursor.Columns[ordinal].ClrType;

    // A DISTINCT type reports its own name (from the descriptor), not the base type it
    // decodes as; a named ROW/opaque UDT likewise reports its type name — matching
    // IfxDataReader.GetDataTypeName (IfxColumnMetadata.DataTypeName covers both).
    public string GetDataTypeName(int ordinal) => _cursor.Columns[ordinal].DataTypeName;

    public int GetOrdinal(string name) => IfxConvert.GetOrdinal(_cursor.Columns, name);

    /// <summary>
    /// Returns the typed column schema for this scroll cursor's result set as a collection
    /// of <see cref="IfxDbColumn"/> — the same schema <see cref="IfxDataReader.GetColumnSchema"/>
    /// produces, over the shared column metadata.
    /// </summary>
    public ReadOnlyCollection<DbColumn> GetColumnSchema() => IfxDbColumn.BuildSchema(_cursor.Columns);

    // ---- value accessors (shared with IfxDataReader via IfxConvert) ----

    public object GetValue(int ordinal) => _cursor.GetValue(ordinal);

    public bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public object this[int ordinal] => GetValue(ordinal);

    public object this[string name] => GetValue(GetOrdinal(name));

    public bool GetBoolean(int ordinal) => IfxConvert.GetBoolean(_cursor, ordinal);
    public byte GetByte(int ordinal) => IfxConvert.GetByte(_cursor, ordinal);
    public short GetInt16(int ordinal) => IfxConvert.GetInt16(_cursor, ordinal);
    public int GetInt32(int ordinal) => IfxConvert.GetInt32(_cursor, ordinal);
    public long GetInt64(int ordinal) => IfxConvert.GetInt64(_cursor, ordinal);
    public float GetFloat(int ordinal) => IfxConvert.GetFloat(_cursor, ordinal);
    // A DECIMAL/MONEY column converts straight from its packed digits, so GetDouble also
    // reads a magnitude outside the decimal range (see IfxDataReader.GetDouble).
    public double GetDouble(int ordinal) => IfxConvert.GetDouble(_cursor, ordinal);
    public decimal GetDecimal(int ordinal) => IfxConvert.GetDecimal(_cursor, ordinal);
    public string GetString(int ordinal) => IfxConvert.GetString(_cursor, ordinal);

    // Convert.ToDateTime doesn't accept DateOnly/TimeOnly (the new DATE/time-only
    // DATETIME defaults) — IfxConvert.GetDateTime bridges them (LegacyDateTimeFromTimeOnly).
    public DateTime GetDateTime(int ordinal) => IfxConvert.GetDateTime(_cursor, ordinal);

    // ---- navigation (ports of IfxResultSet.next/previous/first/last/absolute/relative,
    // now living on SqliScrollCursor; MoveNext is the interface's Read()) ----

    /// <summary>Moves to the next row. Returns false past the last row.</summary>
    public bool MoveNext() => _cursor.Read();

    /// <summary>Moves to the previous row. Returns false before the first row.</summary>
    public bool MovePrevious() => _cursor.MovePrevious();

    /// <summary>Moves to the first row. Returns false when the result set is empty.</summary>
    public bool MoveFirst() => _cursor.MoveFirst();

    /// <summary>Moves to the last row (and learns the row count). Returns false when empty.</summary>
    public bool MoveLast() => _cursor.MoveLast();

    /// <summary>
    /// Moves to absolute row <paramref name="row"/> (1-based). A negative value
    /// counts back from the end (-1 is the last row). Returns false when the
    /// position is outside the result set.
    /// </summary>
    public bool MoveAbsolute(int row) => _cursor.MoveAbsolute(row);

    /// <summary>
    /// Moves <paramref name="rows"/> from the current position (negative moves back).
    /// Returns false when the resulting position is outside the result set.
    /// </summary>
    public bool MoveRelative(int rows) => _cursor.MoveRelative(rows);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cursor.Dispose();
        // The command span covers the whole scroll session — end it when the reader closes.
        if (_activity is not null)
            IfxActivitySource.CommandStop(_activity);
    }
}
