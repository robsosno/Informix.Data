using Informix.Data.Types;

namespace Informix.Data.Sqli;

/// <summary>
/// Metadata for one column of a result set or one bound parameter, decoded from
/// a SQLI row descriptor.
/// </summary>
internal sealed class IfxColumnMetadata
{
    public required string Name { get; init; }

    /// <summary>The base Informix built-in type.</summary>
    public required SqliTypeCode SqliTypeCode { get; init; }

    /// <summary>The raw descriptor type word (base type OR-ed with the flag bits).</summary>
    public short TypeWord { get; init; }

    /// <summary>Declared byte length / display size from the descriptor (<c>sqllen</c>).</summary>
    public int Length { get; init; }

    /// <summary>Encoded precision/scale word for DECIMAL/MONEY and the qualifier for DATETIME/INTERVAL.</summary>
    public short PrecisionScale { get; init; }

    public int ExtendedTypeId { get; init; }

    /// <summary>
    /// True when the column is a DISTINCT type (the descriptor's <c>0x800</c> flag): the
    /// value decodes as its base <see cref="SqliTypeCode"/>, but <see cref="ExtendedOwner"/>/
    /// <see cref="ExtendedTypeName"/> name the distinct type (e.g. "root"/"percent").
    /// </summary>
    public bool IsDistinct { get; init; }

    /// <summary>Owner (schema) name of an extended/opaque type from the descriptor (e.g. "informix" for LVARCHAR).</summary>
    public string ExtendedOwner { get; init; } = "";

    /// <summary>Type name of an extended/opaque type from the descriptor (e.g. "lvarchar").</summary>
    public string ExtendedTypeName { get; init; } = "";

    /// <summary>Source (base) type id of a DISTINCT type, from the descriptor; used when a
    /// DISTINCT wraps a variable-length base to pick the SQ_RET_TYPE mapping.</summary>
    public int SourceType { get; init; }

    /// <summary>Byte offset of this column within a fixed-layout tuple (<c>ColStartPos</c>).</summary>
    public int StartPosition { get; init; }

    /// <summary>
    /// The column's byte footprint in the tuple buffer — <see cref="Length"/>,
    /// except for the packed types (DECIMAL/MONEY/DATETIME/INTERVAL) where the
    /// descriptor length is a precision/qualifier word and the footprint is
    /// derived from it (port of <c>IfxColumnInfo.getAdjustedColumnLength</c>).
    /// </summary>
    public int ByteLength =>
        SqliTypeCode is SqliTypeCode.Decimal or SqliTypeCode.Money or SqliTypeCode.DateTime or SqliTypeCode.Interval && Length != -1
            ? ((Length >> 8 & 0xFF) + (Length & 0xFF & 1) + 3) / 2
            : Length;

    public bool IsNullable => !IfxTypeCodes.IsNotNullable(TypeWord);

    /// <summary>The CLR type <c>GetFieldType</c> reports for this column.</summary>
    public Type ClrType => SqliTypeCode switch
    {
        // JSON and BSON both surface as a JSON-text string (BSON is decoded to JSON on read,
        // matching how other ADO.NET serves a BSON column).
        // The raw BSON bytes stay reachable through GetBytes / GetFieldValue<byte[]>.
        SqliTypeCode.Char or SqliTypeCode.VarChar or SqliTypeCode.NChar or SqliTypeCode.NVarChar
            or SqliTypeCode.LVarChar or SqliTypeCode.Text or SqliTypeCode.Clob
            or SqliTypeCode.Json or SqliTypeCode.Bson => typeof(string),
        SqliTypeCode.SmallInt => typeof(short),
        SqliTypeCode.Int or SqliTypeCode.Serial => typeof(int),
        SqliTypeCode.Int8 or SqliTypeCode.Serial8 or SqliTypeCode.BigInt or SqliTypeCode.BigSerial => typeof(long),
        SqliTypeCode.SmFloat => typeof(float),
        SqliTypeCode.Float => typeof(double),
        SqliTypeCode.Decimal or SqliTypeCode.Money => typeof(decimal),
        // DATE reads as DateOnly. A DATETIME whose start unit is HOUR or finer (no date
        // part) reads as TimeOnly; one with a date part (start DAY or coarser) keeps DateTime.
        SqliTypeCode.Date => typeof(DateOnly),
        SqliTypeCode.DateTime => IfxQualifier.GetStartCode(PrecisionScale) >= 6 ? typeof(TimeOnly) : typeof(DateTime),
        // A day-time interval (start DAY or finer) reads as the CLR TimeSpan; a
        // year-month one (start YEAR/MONTH) keeps IfxMonthSpan — TimeSpan cannot
        // represent months without a lossy day convention.
        SqliTypeCode.Interval => IfxQualifier.GetStartCode(PrecisionScale) < 4 ? typeof(IfxMonthSpan) : typeof(TimeSpan),
        SqliTypeCode.Bool => typeof(bool),
        SqliTypeCode.Byte or SqliTypeCode.Blob => typeof(byte[]),
        // ROW decodes to an IfxRow; SET/LIST/MULTISET/COLLECTION to an object[] of
        // CLR-typed elements (IfxComplexDecoder). Opaque UDTs stay a raw byte[]
        // payload — only a registered custom-type mapper could do more.
        SqliTypeCode.Row => typeof(IfxRow),
        SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List or SqliTypeCode.Collection => typeof(object[]),
        SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed => typeof(byte[]),
        _ => typeof(object),
    };

    /// <summary>True for the complex/opaque types surfaced as a raw <c>byte[]</c> payload.</summary>
    public bool IsOpaqueComplex => SqliTypeCode is SqliTypeCode.Row or SqliTypeCode.Set or SqliTypeCode.MultiSet
        or SqliTypeCode.List or SqliTypeCode.Collection or SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed;

    /// <summary>True for the "long" (potentially large) types: BYTE, TEXT, LVARCHAR,
    /// and JSON/BSON (which share LVARCHAR's inline framing and can be just as large).</summary>
    public bool IsLong => SqliTypeCode is SqliTypeCode.Byte or SqliTypeCode.Text or SqliTypeCode.LVarChar
        or SqliTypeCode.Json or SqliTypeCode.Bson;

    /// <summary>DECIMAL/MONEY pack precision in the high byte, scale in the low byte of <see cref="Length"/>.</summary>
    private bool HasPackedNumeric => SqliTypeCode is SqliTypeCode.Decimal or SqliTypeCode.Money;

    /// <summary>The numeric precision for a DECIMAL/MONEY column, or <c>null</c> for other types.</summary>
    public int? NumericPrecision => HasPackedNumeric ? (Length >> 8 & 0xFF) : null;

    /// <summary>The numeric scale for a DECIMAL/MONEY column, or <c>null</c> for other types.</summary>
    public int? NumericScale => HasPackedNumeric ? (Length & 0xFF) : null;

    /// <summary>
    /// The type name ADO.NET reports for this column (<c>GetDataTypeName</c> /
    /// <see cref="System.Data.Common.DbColumn.DataTypeName"/>): a DISTINCT type or a
    /// named opaque/ROW type reports its own extended name; everything else reports the
    /// base built-in type name.
    /// </summary>
    public string DataTypeName =>
        (IsDistinct || IsOpaqueComplex) && ExtendedTypeName.Length > 0 ? ExtendedTypeName : SqliTypeCode.ToString();
}

/// <summary>
/// Internal cursor abstraction over an open SQLI result set. The streaming SQLI
/// codec (task: <c>IfxSqli</c>) implements this; <see cref="IfxDataReader"/>
/// consumes it. Keeping the reader decoupled from the wire codec behind this
/// seam is the .NET-idiomatic.
/// </summary>
internal interface IIfxResultCursor : IDisposable
{
    IReadOnlyList<IfxColumnMetadata> Columns { get; }

    /// <summary>Advances to the next tuple; false at end of the result set.</summary>
    bool Read();

    /// <summary>The value of the current row's column, or <see cref="DBNull.Value"/> for SQL NULL.</summary>
    object GetValue(int ordinal);

    /// <summary>
    /// The raw self-describing payload of a complex (ROW/collection/opaque-UDT)
    /// column in the current row, or <c>null</c> for a non-complex column — the
    /// escape hatch that keeps <see cref="Informix.Data.IfxDataReader.GetBytes"/>
    /// returning the undecoded bytes after structured decode was introduced.
    /// </summary>
    byte[]? GetRawBytes(int ordinal);

    /// <summary>
    /// The current row's DECIMAL/MONEY column converted straight from its packed digits,
    /// or <c>null</c> when that fast path does not apply (a non-DECIMAL column or a SQL
    /// NULL cell). Backs <see cref="Informix.Data.IfxDataReader.GetDouble"/>, the one
    /// accessor that survives a value whose magnitude exceeds the decimal range.
    /// </summary>
    double? GetDecimalAsDouble(int ordinal);

    byte[]? GetSmartLobLocator(int ordinal);

    int RecordsAffected { get; }

    /// <summary>Whether the result set produced at least one row.</summary>
    bool HasRows { get; }
}
