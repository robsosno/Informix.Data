using System.Globalization;
using Informix.Data.Sqli;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Shared conversion helpers used by both <see cref="IfxDataReader"/> and
/// <see cref="IfxScrollReader"/> to avoid logic duplication across readers.
/// </summary>
internal static class IfxConvert
{
    /// <summary>
    /// The legacy 1700/current-month DateTime default for a time-only DATETIME value.
    /// WARNING: The returned value depends on <see cref="DateTime.Now"/>.Month at call
    /// time; two reads of the same row around midnight on the 1st may yield different
    /// results.
    /// </summary>
    internal static DateTime LegacyDateTimeFromTimeOnly(TimeOnly t) =>
        new DateTime(1700, DateTime.Now.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).Add(t.ToTimeSpan());

    /// <summary>
    /// Resolves a column ordinal by name (case-insensitive) from a column list.
    /// Throws <see cref="IndexOutOfRangeException"/> when the column is not found.
    /// </summary>
    internal static int GetOrdinal(IReadOnlyList<IfxColumnMetadata> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        throw new IndexOutOfRangeException($"Column '{name}' not found.");
    }

    // ---- typed accessors: the conversion policy ------------------------------------
    //
    // IfxDataReader and IfxScrollReader share these (they used to carry one copy each and
    // had drifted). The rules were settled against AdoNet.Specification.Tests and four
    // reference drivers; see Informix.Data.SpecTests/ConversionComparison.md.
    //
    //  * A value reads as its own type or any wider one; integers also narrow by value
    //    (OverflowException outside the target range).
    //  * Unrelated kinds never convert: boolean <-> number, string <-> number, floating
    //    point -> decimal and double -> float throw InvalidCastException.
    //  * GetString returns text only where the text cannot depend on the culture: a string
    //    column, or one of the integer types (formatted with the invariant culture).
    //    Callers format everything else themselves, which also keeps the code working
    //    after a move to another database.
    //  * GetChar reads exactly one character. CHAR/NCHAR drop their trailing blanks first
    //    (an all-blank value is ' '); VARCHAR and the other string types are not trimmed.
    //  * Deliberate exception: GetDateTime also reads DATE and time-only DATETIME values
    //    (DateOnly/TimeOnly bridge). Every reference driver accepts the latter.
    //  * Conversions the specification does not exercise (DECIMAL -> int, string ->
    //    DateTime, ...) keep the System.Convert behaviour they always had.
    //
    // A SQL NULL throws InvalidCastException from every getter.

    internal static bool GetBoolean(IIfxResultCursor cursor, int ordinal) => ToBoolean(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static byte GetByte(IIfxResultCursor cursor, int ordinal) => ToByte(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static char GetChar(IIfxResultCursor cursor, int ordinal) => ToChar(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static short GetInt16(IIfxResultCursor cursor, int ordinal) => ToInt16(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static int GetInt32(IIfxResultCursor cursor, int ordinal) => ToInt32(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static long GetInt64(IIfxResultCursor cursor, int ordinal) => ToInt64(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static float GetFloat(IIfxResultCursor cursor, int ordinal) => ToSingle(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static decimal GetDecimal(IIfxResultCursor cursor, int ordinal) => ToDecimal(cursor.GetValue(ordinal), cursor.Columns[ordinal]);
    internal static string GetString(IIfxResultCursor cursor, int ordinal) => ToText(cursor.GetValue(ordinal), cursor.Columns[ordinal]);

    // A DECIMAL/MONEY column converts straight from its packed digits, so this also reads
    // a magnitude outside the decimal range (see IfxDataReader.GetDouble's remarks).
    internal static double GetDouble(IIfxResultCursor cursor, int ordinal) =>
        cursor.GetDecimalAsDouble(ordinal) ?? ToDouble(cursor.GetValue(ordinal), cursor.Columns[ordinal]);

    // Convert.ToDateTime doesn't accept DateOnly/TimeOnly (neither implements
    // IConvertible), so the DATE/time-only-DATETIME mappings need an explicit bridge; a
    // genuine DateTime value passes straight through.
    internal static DateTime GetDateTime(IIfxResultCursor cursor, int ordinal) => ToDateTime(cursor.GetValue(ordinal), cursor.Columns[ordinal]);

    internal static bool ToBoolean(object value, IfxColumnMetadata column) =>
        value is bool b ? b : throw Reject(value, column, "Boolean");

    // No Informix type decodes to a single byte (BYTE columns are byte[]), so this only
    // ever passes a value that is already a byte through.
    internal static byte ToByte(object value, IfxColumnMetadata column) =>
        value is byte b ? b : throw Reject(value, column, "Byte");

    internal static char ToChar(object value, IfxColumnMetadata column)
    {
        if (value is not string s)
            throw Reject(value, column, "Char");
        if (column.SqliTypeCode is SqliTypeCode.Char or SqliTypeCode.NChar)
        {
            // Blank-padded: the padding is not data, and a CHAR cannot be empty, so a
            // blank (or, with TrimTrailingSpaces, already-trimmed) value is one space.
            string trimmed = s.TrimEnd(' ');
            return trimmed.Length switch
            {
                1 => trimmed[0],
                0 => ' ',
                _ => throw RejectLength(column, trimmed.Length),
            };
        }
        return s.Length == 1 ? s[0] : throw RejectLength(column, s.Length);
    }

    internal static short ToInt16(object value, IfxColumnMetadata column) => value switch
    {
        short s => s,
        int i => checked((short)i),
        long l => checked((short)l),
        bool or string or DBNull => throw Reject(value, column, "Int16"),
        _ => Convert.ToInt16(value),
    };

    internal static int ToInt32(object value, IfxColumnMetadata column) => value switch
    {
        int i => i,
        short s => s,
        long l => checked((int)l),
        bool or string or DBNull => throw Reject(value, column, "Int32"),
        _ => Convert.ToInt32(value),
    };

    internal static long ToInt64(object value, IfxColumnMetadata column) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        bool or string or DBNull => throw Reject(value, column, "Int64"),
        _ => Convert.ToInt64(value),
    };

    // double -> float narrows, so only the exact type, the integers and DECIMAL convert.
    internal static float ToSingle(object value, IfxColumnMetadata column) => value switch
    {
        float f => f,
        bool or string or double or DBNull => throw Reject(value, column, "Single"),
        _ => Convert.ToSingle(value),
    };

    // float/double -> decimal is not exact, so only the exact type and the integers convert.
    internal static decimal ToDecimal(object value, IfxColumnMetadata column) => value switch
    {
        decimal d => d,
        bool or string or float or double or DBNull => throw Reject(value, column, "Decimal"),
        _ => Convert.ToDecimal(value),
    };

    internal static double ToDouble(object value, IfxColumnMetadata column) => value switch
    {
        double d => d,
        float f => f,
        bool or string or DBNull => throw Reject(value, column, "Double"),
        _ => Convert.ToDouble(value),
    };

    internal static string ToText(object value, IfxColumnMetadata column) => value switch
    {
        string s => s,
        short or int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw Reject(value, column, "String"),
    };

    internal static DateTime ToDateTime(object value, IfxColumnMetadata column) => value switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeOnly t => LegacyDateTimeFromTimeOnly(t),
        bool or DBNull => throw Reject(value, column, "DateTime"),
        _ => Convert.ToDateTime(value),
    };

    /// <summary>
    /// Converts <paramref name="value"/> to the primitive <paramref name="target"/> under the
    /// same rules as the matching <c>GetXxx</c> getter, for <c>GetFieldValue&lt;T&gt;</c>.
    /// Returns false when <paramref name="target"/> is not one of the types covered here.
    /// </summary>
    /// <remarks>
    /// <c>GetFieldValue&lt;string&gt;</c> is stricter than <c>GetString</c>: only a string
    /// value reads as a string, because the generic form is the exact-type read.
    /// </remarks>
    internal static bool TryConvertPrimitive(Type target, object value, IfxColumnMetadata column, out object? result)
    {
        result = null;
        if (target == typeof(bool)) result = ToBoolean(value, column);
        else if (target == typeof(byte)) result = ToByte(value, column);
        else if (target == typeof(char)) result = ToChar(value, column);
        else if (target == typeof(short)) result = ToInt16(value, column);
        else if (target == typeof(int)) result = ToInt32(value, column);
        else if (target == typeof(long)) result = ToInt64(value, column);
        else if (target == typeof(float)) result = ToSingle(value, column);
        else if (target == typeof(double)) result = ToDouble(value, column);
        else if (target == typeof(decimal)) result = ToDecimal(value, column);
        else if (target == typeof(string)) throw Reject(value, column, "String");
        else return false;
        return true;
    }

    private static InvalidCastException Reject(object value, IfxColumnMetadata column, string target) =>
        value is DBNull
            ? new InvalidCastException($"Column '{column.Name}' is SQL NULL; check IsDBNull before reading it as {target}.")
            : new InvalidCastException(
                $"Column '{column.Name}' ({column.DataTypeName}, read as {value.GetType().Name}) cannot be read as {target}.");

    private static InvalidCastException RejectLength(IfxColumnMetadata column, int length) =>
        new($"Column '{column.Name}' ({column.DataTypeName}) holds {length} characters; Char needs exactly one.");
}
