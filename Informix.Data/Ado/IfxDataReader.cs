using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.Json;
using Informix.Data.Sqli;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Forward-only reader over a cursor (<see cref="IIfxResultCursor"/>): a live server cursor, a schema-only stub or an empty
/// result. Date/time columns read as modern types by default (DATE -> DateOnly, time-only DATETIME -> TimeOnly,
/// day-time INTERVAL -> TimeSpan); the legacy structs stay reachable through <c>GetFieldValue&lt;T&gt;</c> and the <c>GetIfx*</c> methods.
/// </summary>
/// <remarks>
/// <para><b>Conversions.</b> A <c>GetXxx</c> getter reads a column as its own type or a wider one (<c>GetInt64</c> on a
/// SMALLINT, <c>GetDouble</c> on a SMALLFLOAT or DECIMAL); the integer getters also narrow by value and throw
/// <see cref="OverflowException"/> outside the target range. Unrelated kinds throw <see cref="InvalidCastException"/>:
/// boolean &lt;-&gt; number, string &lt;-&gt; number, floating point -&gt; <c>decimal</c>, <c>double</c> -&gt; <c>float</c>,
/// and every getter on a SQL NULL.</para>
/// <para><see cref="GetString"/> returns text only where it cannot depend on the culture: a string column, or an integer
/// column (formatted with the invariant culture). A DECIMAL, FLOAT, BOOLEAN or date column throws; format it yourself, which
/// also keeps the code working if the database changes. <see cref="GetChar"/> needs exactly one character: CHAR/NCHAR
/// drop trailing blanks first (an all-blank value is <c>' '</c>), VARCHAR and the other string types are not trimmed.
/// <see cref="GetDateTime"/> additionally reads DATE and time-only DATETIME columns.</para>
/// <para><c>GetFieldValue&lt;T&gt;</c> applies the same rules for a primitive <c>T</c>, except that
/// <c>GetFieldValue&lt;string&gt;</c> is the exact-type read and does not format an integer.</para>
/// </remarks>
public sealed class IfxDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private IIfxResultCursor _cursor;
    // Runs when the reader is closed: used for CommandBehavior.CloseConnection.
    private readonly Action? _onClose;
    private readonly IfxConnection? _connection;
    private readonly Activity? _activity; // the command's client span; ended on reader close
    private bool _closed;
    // Smart-LOB streams handed out for the current row; the session they hold is
    // message-at-a-time, so the next row advance (or reader close) disposes them.
    private List<IDisposable>? _liveLobStreams;
    // Set only by IfxBatch.ExecuteReader: runs the next batch command and returns its
    // cursor, or null once every command has run. NextResult() is a no-op (false) for
    // every other caller — a single IfxCommand result set never spans.
    private Func<IIfxResultCursor?>? _nextCursor;

    internal IfxDataReader(IIfxResultCursor cursor, Action? onClose = null, IfxConnection? connection = null,
        Activity? activity = null, Func<IIfxResultCursor?>? nextCursor = null)
    {
        _cursor = cursor;
        _onClose = onClose;
        _connection = connection;
        _activity = activity;
        _nextCursor = nextCursor;
    }

    private IfxColumnMetadata Column(int ordinal, [CallerMemberName] string? member = null) => Cursor(ordinal, member).Columns[ordinal];

    // Every member that reads data or metadata goes through one of these two, so a closed reader, a
    // statement without a result set and a bad ordinal each fail the same way everywhere:
    //   closed reader / no result set -> InvalidOperationException, ordinal out of range -> IndexOutOfRangeException.
    private IIfxResultCursor Open([CallerMemberName] string? member = null) =>
        _closed ? throw new InvalidOperationException($"Invalid attempt to call {member} when reader is closed.") : _cursor;

    private IIfxResultCursor Cursor(int ordinal, [CallerMemberName] string? member = null)
    {
        var cursor = Open(member);
        int count = cursor.Columns.Count;
        if (count == 0)
            throw new InvalidOperationException($"Invalid attempt to call {member}: the statement returned no result set.");
        if ((uint)ordinal >= (uint)count)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the {count} column(s) of the result set.");
        return cursor;
    }

    /// <summary>
    /// The base table of the command's SELECT when it has exactly one, set for a reader opened with
    /// <see cref="CommandBehavior.SchemaOnly"/> or <see cref="CommandBehavior.KeyInfo"/>; it adds the
    /// BaseTableName / IsKey ... columns to <see cref="GetSchemaTable"/>.
    /// </summary>
    internal IfxBaseTable? BaseTable { get; set; }

    public override int FieldCount => Open().Columns.Count;

    public override bool HasRows => Open().HasRows;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => _cursor.RecordsAffected;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        Open();
        InvalidateLiveLobStreams(); // release any LO stream handed out for the current row
        bool more = _cursor.Read();
        return more;
    }

    /// <summary>
    /// For a reader spanning an <see cref="IfxBatch"/> (see <see cref="IfxBatch.ExecuteReader()"/>),
    /// closes the current command's cursor/statement and runs the next command, returning
    /// true when it produced a new current result set. False (and no further effect) for
    /// every other reader — a single <see cref="IfxCommand"/> execution never spans.
    /// </summary>
    public override bool NextResult()
    {
        Open();
        if (_nextCursor is null)
            return false;
        _cursor.Dispose();
        var next = _nextCursor();
        if (next is null)
        {
            _nextCursor = null;
            return false;
        }
        _cursor = next;
        return true;
    }

    public override object GetValue(int ordinal) => Cursor(ordinal).GetValue(ordinal);

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Open().Columns.Count == 0)
            throw new InvalidOperationException("Invalid attempt to call GetValues: the statement returned no result set.");
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++)
            values[i] = GetValue(i);
        return n;
    }

    public override bool IsDBNull(int ordinal) => Cursor(ordinal).GetValue(ordinal) is DBNull;

    public override string GetName(int ordinal) => Column(ordinal).Name;

    public override int GetOrdinal(string name) => IfxConvert.GetOrdinal(Open().Columns, name);

    public override Type GetFieldType(int ordinal) => Column(ordinal).ClrType;

    // A DISTINCT type reports its own name (from the descriptor), not the base type it
    // decodes as; a named ROW/opaque UDT likewise reports its type name. Other types
    // (LVARCHAR, smart blobs, unnamed collections) fall back to the base type name.
    public override string GetDataTypeName(int ordinal) => Column(ordinal).DataTypeName;

    // The typed getters share one conversion layer (IfxConvert) with IfxScrollReader.
    public override bool GetBoolean(int ordinal) => IfxConvert.GetBoolean(Cursor(ordinal), ordinal);
    public override byte GetByte(int ordinal) => IfxConvert.GetByte(Cursor(ordinal), ordinal);
    public override char GetChar(int ordinal) => IfxConvert.GetChar(Cursor(ordinal), ordinal);
    public override short GetInt16(int ordinal) => IfxConvert.GetInt16(Cursor(ordinal), ordinal);
    public override int GetInt32(int ordinal) => IfxConvert.GetInt32(Cursor(ordinal), ordinal);
    public override long GetInt64(int ordinal) => IfxConvert.GetInt64(Cursor(ordinal), ordinal);
    public override float GetFloat(int ordinal) => IfxConvert.GetFloat(Cursor(ordinal), ordinal);
    public override decimal GetDecimal(int ordinal) => IfxConvert.GetDecimal(Cursor(ordinal), ordinal);
    public override string GetString(int ordinal) => IfxConvert.GetString(Cursor(ordinal), ordinal);

    /// <summary>
    /// The column value as a <see cref="double"/>.
    /// </summary>
    /// <remarks>
    /// A DECIMAL/MONEY column converts directly from its packed digits, so this is the one
    /// numeric accessor that also reads a value whose magnitude is outside the
    /// <see cref="decimal"/> range (about ±7.92e28) — where <see cref="GetDecimal"/> and
    /// <see cref="GetValue"/> throw <see cref="OverflowException"/>. The usual double
    /// precision limit applies: ~15-17 significant digits against DECIMAL's 32. For the exact
    /// digits of such a value, cast the column in the query (<c>col::lvarchar</c>).
    /// </remarks>
    public override double GetDouble(int ordinal) => IfxConvert.GetDouble(Cursor(ordinal), ordinal);

    // Convert.ToDateTime doesn't accept DateOnly/TimeOnly (neither implements
    // IConvertible), so the new default DATE/time-only-DATETIME mappings need an
    // explicit bridge; a genuine DateTime value behaves exactly as before.
    public override DateTime GetDateTime(int ordinal) => IfxConvert.GetDateTime(Cursor(ordinal), ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    // The old ToDateTime() 1700/current-month default for a time-only DATETIME value,
    // reproduced from a TimeOnly for the DateTime escape hatch (GetFieldValue<DateTime>,
    // GetIfxDateTime) — faithful to PackedValue.ToDateTime's missing-year/month/day quirk
    // documented there.
    // WARNING: The returned value depends on DateTime.Now.Month at call time; two reads
    // of the same row around midnight on the 1st may yield different results.
    private static DateTime LegacyDateTimeFromTimeOnly(TimeOnly t) => IfxConvert.LegacyDateTimeFromTimeOnly(t);

    /// <summary>
    /// Returns the value of the column as type <typeparamref name="T"/>, avoiding the
    /// box-then-<c>Convert</c> round trip of the weakly typed getters when the decoded
    /// value is already a <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// <para>Conventions:</para>
    /// <list type="bullet">
    /// <item><description><c>GetFieldValue&lt;Stream&gt;</c> is <see cref="GetStream"/>;
    /// <c>GetFieldValue&lt;TextReader&gt;</c> is <see cref="GetTextReader"/>.</description></item>
    /// <item><description>The provider value/handle types
    /// (<see cref="IfxDecimal"/>, <see cref="IfxDateTime"/>, <see cref="IfxTimeSpan"/>,
    /// <see cref="IfxMonthSpan"/>, <see cref="IfxBlob"/>, <see cref="IfxClob"/>) route to
    /// their typed accessors and therefore keep <em>those</em> accessors' SQL-NULL
    /// semantics: a NULL cell yields the type's <c>.Null</c> value, it does <b>not</b>
    /// throw.</description></item>
    /// <item><description>For every other <typeparamref name="T"/> the ADO.NET contract
    /// applies: a NULL cell throws <see cref="InvalidCastException"/>, except
    /// <c>GetFieldValue&lt;object&gt;</c> which returns <see cref="DBNull.Value"/>.</description></item>
    /// </list>
    /// A non-NULL value already of type <typeparamref name="T"/> is returned directly;
    /// otherwise a primitive <typeparamref name="T"/> follows the rules of the matching
    /// getter (e.g. <c>GetFieldValue&lt;long&gt;</c> on an INT column behaves like
    /// <see cref="GetInt64"/>: a wider type is fine, an unrelated one throws
    /// <see cref="InvalidCastException"/>). The one difference is
    /// <c>GetFieldValue&lt;string&gt;</c>, which is the exact-type read and so, unlike
    /// <see cref="GetString"/>, does not format an integer column as text. Any other
    /// <typeparamref name="T"/> uses <see cref="Convert.ChangeType(object, Type)"/>.
    /// </remarks>
    public override T GetFieldValue<T>(int ordinal)
    {
        // Streaming accessors.
        if (typeof(T) == typeof(Stream)) return (T)(object)GetStream(ordinal);
        if (typeof(T) == typeof(TextReader)) return (T)(object)GetTextReader(ordinal);

        // JSON DOM accessors — for JSON/BSON columns (and any string column holding JSON).
        if (typeof(T) == typeof(JsonDocument)) return (T)(object)GetJsonDocument(ordinal);
        if (typeof(T) == typeof(JsonElement)) return (T)(object)GetJsonElement(ordinal);
        // GetFieldValue<byte[]> on a JSON/BSON column yields the RAW payload (BSON bytes /
        // JSON UTF-8), since its decoded value is a string; BYTE/BLOB fall through below.
        if (typeof(T) == typeof(byte[]) && Column(ordinal).SqliTypeCode is SqliTypeCode.Json or SqliTypeCode.Bson)
            return (T)(object)(Cursor(ordinal).GetRawBytes(ordinal) ?? throw NullColumnCast(ordinal, "GetFieldValue<byte[]>"));

        // Provider types keep their own SQL-NULL -> .Null semantics (see remarks).
        if (typeof(T) == typeof(IfxDecimal)) return (T)(object)GetIfxDecimal(ordinal);
        if (typeof(T) == typeof(IfxDateTime)) return (T)(object)GetIfxDateTime(ordinal);
        if (typeof(T) == typeof(IfxTimeSpan)) return (T)(object)GetIfxTimeSpan(ordinal);
        if (typeof(T) == typeof(IfxMonthSpan)) return (T)(object)GetIfxMonthSpan(ordinal);
        if (typeof(T) == typeof(IfxBlob)) return (T)(object)GetIfxBlob(ordinal);
        if (typeof(T) == typeof(IfxClob)) return (T)(object)GetIfxClob(ordinal);
        if (typeof(T) == typeof(TimeSpan)) return (T)(object)GetTimeSpan(ordinal);

        // A DECIMAL/MONEY column reads as a double straight from its packed digits, the one
        // accessor that survives an out-of-decimal-range magnitude (see GetDouble). Anything
        // else — including a SQL NULL cell — falls through to the generic path below, which
        // keeps its own NULL diagnostics.
        if (typeof(T) == typeof(double) && Cursor(ordinal).GetDecimalAsDouble(ordinal) is { } packedAsDouble)
            return (T)(object)packedAsDouble;

        object value = GetValue(ordinal);
        if (value is DBNull)
        {
            if (typeof(T) == typeof(object) || typeof(T) == typeof(DBNull))
                return (T)value; // GetFieldValue<object>/<DBNull> surface DBNull.Value
            throw new InvalidCastException(
                $"Column {ordinal} is SQL NULL; call IsDBNull first or use GetFieldValue<object> or GetFieldValue<DBNull>.");
        }
        if (value is T typed)
            return typed; // fast path: already the requested type

        // Cross-conversions for the DATE/DATETIME/INTERVAL default mappings: a DATE
        // column's native DateOnly, a time-only DATETIME's native TimeOnly, and a
        // year-month INTERVAL's native IfxMonthSpan each have one documented escape
        // hatch beyond their own provider-type accessor (already routed above).
        if (typeof(T) == typeof(DateTime))
        {
            if (value is DateOnly d) return (T)(object)d.ToDateTime(TimeOnly.MinValue);
            if (value is TimeOnly t) return (T)(object)LegacyDateTimeFromTimeOnly(t);
        }
        else if (typeof(T) == typeof(DateOnly) && value is DateTime dtToDate)
            return (T)(object)DateOnly.FromDateTime(dtToDate);
        else if (typeof(T) == typeof(TimeOnly) && value is DateTime dtToTime)
            return (T)(object)TimeOnly.FromDateTime(dtToTime);
        else if (typeof(T) == typeof(long) && value is IfxMonthSpan monthSpan)
            return (T)(object)monthSpan.TotalMonths;

        // The primitive targets follow the matching GetXxx getter's rules (IfxConvert).
        if (IfxConvert.TryConvertPrimitive(typeof(T), value, Column(ordinal), out object? converted))
            return (T)converted!;

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Retrieves the column as a <see cref="Stream"/>: a read-only, non-writable view over
    /// the byte array of a BYTE column (no copy), or an incrementally streamed
    /// <see cref="IfxLobStream"/> over a BLOB smart large object.
    /// </summary>
    /// <remarks>
    /// A BLOB stream holds the connection's session while open and is only valid until the
    /// next <see cref="Read"/> (which disposes it) or the reader closes; read it before
    /// advancing. Throws <see cref="InvalidCastException"/> for a SQL NULL cell (check
    /// <see cref="IsDBNull"/> first) or a column that is neither byte-backed nor a BLOB.
    /// </remarks>
    public override Stream GetStream(int ordinal)
    {
        var col = Column(ordinal);
        if (col.SqliTypeCode is SqliTypeCode.Blob)
        {
            byte[]? locator = Cursor(ordinal).GetSmartLobLocator(ordinal);
            if (locator is null)
                throw NullColumnCast(ordinal, "GetStream"); // SQL NULL BLOB
            var blob = new IfxBlob(RequireConnection(), new IfxSmartLOBLocator(locator));
            return TrackLobStream(new IfxLobStream(blob));
        }
        object value = GetValue(ordinal);
        if (value is byte[] bytes)
            return new MemoryStream(bytes, 0, bytes.Length, writable: false);
        if (value is DBNull)
            throw NullColumnCast(ordinal, "GetStream");
        throw new InvalidCastException($"Column {ordinal} ({col.SqliTypeCode}) cannot be read as a Stream.");
    }

    /// <summary>
    /// Retrieves the column as a <see cref="TextReader"/>: a <see cref="StringReader"/> over
    /// the decoded string of a CHAR/VARCHAR/LVARCHAR/TEXT column, or a
    /// <see cref="StreamReader"/> (driver default encoding) over an incrementally streamed
    /// CLOB smart large object.
    /// </summary>
    /// <remarks>
    /// A CLOB reader holds the connection's session while open and is only valid until the
    /// next <see cref="Read"/> (which disposes it) or the reader closes; read it before
    /// advancing. Throws <see cref="InvalidCastException"/> for a SQL NULL cell (check
    /// <see cref="IsDBNull"/> first) or a non-character column.
    /// </remarks>
    public override TextReader GetTextReader(int ordinal)
    {
        var col = Column(ordinal);
        if (col.SqliTypeCode is SqliTypeCode.Clob)
        {
            byte[]? locator = Cursor(ordinal).GetSmartLobLocator(ordinal);
            if (locator is null)
                throw NullColumnCast(ordinal, "GetTextReader"); // SQL NULL CLOB
            var clob = new IfxClob(RequireConnection(), new IfxSmartLOBLocator(locator));
            var stream = TrackLobStream(new IfxLobStream(clob));
            return new StreamReader(stream, RequireConnection().DbEncoding,
                detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        }
        object value = GetValue(ordinal);
        if (value is string s)
            return new StringReader(s);
        if (value is DBNull)
            throw NullColumnCast(ordinal, "GetTextReader");
        throw new InvalidCastException($"Column {ordinal} ({col.SqliTypeCode}) cannot be read as a TextReader.");
    }

    private IfxConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("Smart-LOB streaming requires an open connection.");

    private static InvalidCastException NullColumnCast(int ordinal, string accessor) =>
        new($"Column {ordinal} is SQL NULL; check IsDBNull before calling {accessor}.");

    private Stream TrackLobStream(IfxLobStream stream)
    {
        (_liveLobStreams ??= []).Add(stream);
        return stream;
    }

    // Disposes (closes the server LO of) any smart-LOB stream handed out for the current
    // row. Called on the next Read() and on Close(): the session is message-at-a-time, so a
    // stream left open would desynchronize the next command.
    // An un-consumed (never-opened) stream disposes with no round trip.
    private void InvalidateLiveLobStreams()
    {
        if (_liveLobStreams is null || _liveLobStreams.Count == 0)
            return;
        foreach (var s in _liveLobStreams)
            s.Dispose();
        _liveLobStreams.Clear();
    }

    // ---- Informix-typed accessors (official IfxDataReader surface) ----

    /// <summary>The column value as a 64-bit INT8/BIGINT.</summary>
    public long GetBigInt(int ordinal) => Convert.ToInt64(GetValue(ordinal));

    /// <summary>The date part of a DATE/DATETIME column.</summary>
    public DateTime GetDate(int ordinal) => GetValue(ordinal) switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        var v => Convert.ToDateTime(v).Date,
    };

    /// <summary>The time-of-day of a DATETIME column, or a day-time interval, as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan GetTime(int ordinal) => GetTimeSpan(ordinal);

    /// <summary>A day-time interval / DATETIME time-of-day as a CLR <see cref="TimeSpan"/>.</summary>
    public TimeSpan GetTimeSpan(int ordinal) => GetValue(ordinal) switch
    {
        IfxTimeSpan ts => new TimeSpan((long)ts.Ticks),
        TimeSpan t => t,
        TimeOnly t => t.ToTimeSpan(),
        DateTime dt => dt.TimeOfDay,
        var v => throw new InvalidCastException($"Column {ordinal} ({v?.GetType().Name}) is not a time/interval value."),
    };

    /// <summary>The column value as an <see cref="IfxDecimal"/> (SQL NULL → <see cref="IfxDecimal.Null"/>).</summary>
    public IfxDecimal GetIfxDecimal(int ordinal) => GetValue(ordinal) switch
    {
        DBNull => IfxDecimal.Null,
        decimal d => new IfxDecimal(d),
        var v => new IfxDecimal(Convert.ToDecimal(v)),
    };

    /// <summary>The column value as an <see cref="IfxDateTime"/> (SQL NULL → <see cref="IfxDateTime.Null"/>).</summary>
    public IfxDateTime GetIfxDateTime(int ordinal) => GetValue(ordinal) switch
    {
        DBNull => IfxDateTime.Null,
        DateTime dt => new IfxDateTime(dt),
        DateOnly d => new IfxDateTime(d.ToDateTime(TimeOnly.MinValue), IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Day)),
        TimeOnly t => new IfxDateTime(LegacyDateTimeFromTimeOnly(t), Column(ordinal).PrecisionScale),
        var v => new IfxDateTime(Convert.ToDateTime(v)),
    };

    /// <summary>The column value as an <see cref="IfxTimeSpan"/> day-time interval.</summary>
    public IfxTimeSpan GetIfxTimeSpan(int ordinal) => GetValue(ordinal) switch
    {
        DBNull => IfxTimeSpan.Null,
        IfxTimeSpan ts => ts,
        TimeSpan t => new IfxTimeSpan(t),
        var v => throw new InvalidCastException($"Column {ordinal} ({v?.GetType().Name}) is not a day-time interval."),
    };

    /// <summary>The column value as an <see cref="IfxMonthSpan"/> year-month interval.</summary>
    public IfxMonthSpan GetIfxMonthSpan(int ordinal) => GetValue(ordinal) switch
    {
        DBNull => IfxMonthSpan.Null,
        IfxMonthSpan ms => ms,
        var v => throw new InvalidCastException($"Column {ordinal} ({v?.GetType().Name}) is not a year-month interval."),
    };

    // Backs GetFieldValue<JsonDocument>/<JsonElement>. The JsonDocument is the caller's to
    // dispose; the JsonElement is Clone()d so it outlives the parsed document.
    private JsonDocument GetJsonDocument(int ordinal)
    {
        // A BSON column goes straight from its raw tuple bytes to the DOM: one streaming
        // BSON -> UTF-8 JSON transcode + one parse, no intermediate UTF-16 string. Safe
        // regardless of DB_LOCALE because BSON strings are UTF-8 by spec. A JSON (text)
        // column stays on the decoded-string path below — its raw bytes are in the DB code
        // set, and the string decode is what applies it.
        if (Column(ordinal).SqliTypeCode is SqliTypeCode.Bson
            && Cursor(ordinal).GetRawBytes(ordinal) is { Length: > 0 } rawBson)
            return BsonMediaTypeFormatter.ToJsonDocument(rawBson);

        return GetValue(ordinal) switch
        {
            string s => JsonDocument.Parse(s),
            DBNull => throw NullColumnCast(ordinal, "GetFieldValue<JsonDocument>"),
            var v => throw new InvalidCastException($"Column {ordinal} ({v?.GetType().Name}) is not a JSON/BSON value."),
        };
    }

    private JsonElement GetJsonElement(int ordinal)
    {
        using JsonDocument doc = GetJsonDocument(ordinal);
        return doc.RootElement.Clone();
    }

    /// <summary>A lazy <see cref="IfxBlob"/> over a BLOB column — wraps the row's locator without
    /// materializing the data (open it for random access). SQL NULL → <see cref="IfxBlob.Null"/>.</summary>
    public IfxBlob GetIfxBlob(int ordinal)
    {
        byte[]? locator = Cursor(ordinal).GetSmartLobLocator(ordinal);
        return locator is null || _connection is null ? IfxBlob.Null : new IfxBlob(_connection, new IfxSmartLOBLocator(locator));
    }

    /// <summary>A lazy <see cref="IfxClob"/> over a CLOB column — wraps the row's locator without
    /// materializing the data. SQL NULL → <see cref="IfxClob.Null"/>.</summary>
    public IfxClob GetIfxClob(int ordinal)
    {
        byte[]? locator = Cursor(ordinal).GetSmartLobLocator(ordinal);
        return locator is null || _connection is null ? IfxClob.Null : new IfxClob(_connection, new IfxSmartLOBLocator(locator));
    }

    // Raw payload of BYTE, JSON/BSON, opaque and complex columns. With a null buffer it returns the total length.
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        ValidateBufferArguments(dataOffset, buffer?.Length, bufferOffset, length);
        // A ROW/collection column decodes to a structured value and a BSON column to a JSON
        // string, but GetBytes must still yield the raw undecoded payload. Try that raw path
        // first so GetBytes stays a pure byte escape hatch — it never runs structured/text
        // decode (and so never throws on BSON content the decoder can't represent). Other
        // columns fall back to their byte[] value (BYTE/BLOB) via GetValue.
        var data = Cursor(ordinal).GetRawBytes(ordinal)
            ?? GetValue(ordinal) as byte[]
            ?? throw new InvalidCastException($"Column {ordinal} is not a byte array.");
        if (buffer is null)
            return data.LongLength;
        long available = Math.Min(length, data.LongLength - dataOffset);
        if (available <= 0)
            return 0;
        Array.Copy(data, dataOffset, buffer, bufferOffset, available);
        return available;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        ValidateBufferArguments(dataOffset, buffer?.Length, bufferOffset, length);
        string s = GetString(ordinal);
        if (buffer is null)
            return s.Length;
        long available = Math.Min(length, s.Length - dataOffset);
        if (available <= 0)
            return 0;
        s.CopyTo((int)dataOffset, buffer, bufferOffset, (int)available);
        return available;
    }

    // A null buffer is the "how long is it?" query and takes no copy arguments. Otherwise the
    // offsets have to make sense: a bufferOffset past the end of the buffer used to slip through
    // whenever nothing was left to copy (length 0 or dataOffset past the data) and silently return 0.
    // A window that is merely longer than the room left in the buffer is still left to the copy
    // itself, which only complains when there is actually that much data to move.
    private static void ValidateBufferArguments(long dataOffset, int? bufferLength, int bufferOffset, int length)
    {
        if (bufferLength is not { } size)
            return;
        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        if ((uint)bufferOffset > (uint)size)
            throw new ArgumentOutOfRangeException(nameof(bufferOffset), bufferOffset, "Buffer offset is outside the buffer.");
        ArgumentOutOfRangeException.ThrowIfNegative(length);
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <summary>
    /// Returns the typed column schema for the current result set as a collection of
    /// <see cref="IfxDbColumn"/> (upcast to <see cref="DbColumn"/>) — the modern
    /// replacement for <see cref="GetSchemaTable"/>. This method is the
    /// <see cref="IDbColumnSchemaGenerator"/> implementation that
    /// <see cref="System.Data.Common.DbDataReaderExtensions.GetColumnSchema"/> and other
    /// tooling type-test for.
    /// </summary>
    public ReadOnlyCollection<DbColumn> GetColumnSchema() => IfxDbColumn.BuildSchema(Open().Columns);

    /// <summary>
    /// Builds the ADO.NET schema table from the described columns. The rows are
    /// projected from <see cref="GetColumnSchema"/> so the two views never drift.
    /// </summary>
    public override DataTable GetSchemaTable()
    {
        Open();
        var table = new DataTable("SchemaTable");
        table.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        table.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        table.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        table.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        table.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        table.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));
        table.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        table.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        table.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        // Informix extended-type detail (opaque/UDT/DISTINCT columns).
        table.Columns.Add("ExtendedTypeName", typeof(string));
        table.Columns.Add("ExtendedOwner", typeof(string));
        table.Columns.Add("IsDistinct", typeof(bool));
        table.Columns.Add("IsOpaqueComplex", typeof(bool));
        if (BaseTable is not null)
        {
            // What DbCommandBuilder reads to write INSERT / UPDATE / DELETE for a one-table SELECT.
            table.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
            table.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
            table.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
            table.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
            table.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
            table.Columns.Add(SchemaTableOptionalColumn.IsRowVersion, typeof(bool));
            table.Columns.Add(SchemaTableOptionalColumn.IsReadOnly, typeof(bool));
            table.Columns.Add(SchemaTableOptionalColumn.IsHidden, typeof(bool));
            table.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
            table.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        }

        foreach (var dbColumn in GetColumnSchema())
        {
            var col = (IfxDbColumn)dbColumn;
            var row = table.NewRow();
            row[SchemaTableColumn.ColumnName] = col.ColumnName;
            row[SchemaTableColumn.ColumnOrdinal] = col.ColumnOrdinal ?? 0;
            row[SchemaTableColumn.ColumnSize] = col.ColumnSize ?? 0;
            // DECIMAL/MONEY carry precision/scale; other types report (short)0 as before.
            row[SchemaTableColumn.NumericPrecision] = (short)(col.NumericPrecision ?? 0);
            row[SchemaTableColumn.NumericScale] = (short)(col.NumericScale ?? 0);
            row[SchemaTableColumn.DataType] = col.DataType!;
            row[SchemaTableColumn.ProviderType] = (int)col.ProviderType;
            row[SchemaTableColumn.IsLong] = col.IsLong ?? false;
            row[SchemaTableColumn.AllowDBNull] = col.AllowDBNull ?? false;
            row[SchemaTableColumn.BaseColumnName] = col.BaseColumnName;
            row["ExtendedTypeName"] = col.ExtendedTypeName;
            row["ExtendedOwner"] = col.ExtendedOwner;
            row["IsDistinct"] = col.IsDistinct;
            row["IsOpaqueComplex"] = col.IsOpaqueComplex;
            if (BaseTable is { } baseTable)
            {
                // Without the catalog lookup (no KeyInfo) every column is taken as a plain table column.
                string name = col.ColumnName;
                bool expression = baseTable.Loaded && !baseTable.Columns.Contains(name);
                row[SchemaTableColumn.BaseSchemaName] = (object?)baseTable.Owner ?? DBNull.Value;
                row[SchemaTableColumn.BaseTableName] = baseTable.Name;
                row[SchemaTableColumn.IsKey] = baseTable.KeyColumns.Contains(name);
                row[SchemaTableColumn.IsUnique] = false;
                row[SchemaTableOptionalColumn.IsAutoIncrement] = baseTable.AutoIncrementColumns.Contains(name);
                row[SchemaTableOptionalColumn.IsRowVersion] = false;
                row[SchemaTableOptionalColumn.IsReadOnly] = expression;
                row[SchemaTableOptionalColumn.IsHidden] = false;
                row[SchemaTableColumn.IsExpression] = expression;
                row[SchemaTableColumn.IsAliased] = false;
            }
            table.Rows.Add(row);
        }
        return table;
    }

    // Closes the cursor (releasing or caching the statement), ends the trace span and runs the CloseConnection action.
    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        InvalidateLiveLobStreams(); // close any smart-LOB stream the caller left open
        _cursor.Dispose();
        // The ExecuteReader span covers the fetch phase — end it when the reader closes
        // (covers an abandoned/undisposed reader too, via the connection-close teardown).
        if (_activity is not null)
            IfxActivitySource.CommandStop(_activity);
        _onClose?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }
}
