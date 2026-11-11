namespace Informix.Data;

/// <summary>
/// The public Informix provider data types, matching the names and numeric values
/// of the official Informix.Net.Core provider's <c>IfxType</c> (an ODBC-flavoured
/// enumeration). Set <see cref="IfxParameter.IfxType"/> to pin a parameter's
/// server type; the same values are reported as the provider type column of
/// <see cref="IfxDataReader.GetSchemaTable"/>.
/// </summary>
/// <remarks>These are deliberately NOT the on-the-wire SQLI type codes — the
/// internal wire enum carries those and is mapped to and from this enum at the
/// provider boundary. Both the ODBC generics (<see cref="Binary"/>, <see cref="Bit"/>,
/// <see cref="Image"/>, …) and the Informix-native types are present for drop-in
/// parity with callers coming from the official provider.</remarks>
public enum IfxType
{
    BigInt = 1,
    Binary = 2,
    Bit = 3,
    Char = 4,
    DateTime = 5,
    Decimal = 6,
    Numeric = 7,
    Double = 8,
    Image = 9,
    Int = 10,
    NChar = 11,
    NText = 12,
    NVarChar = 13,
    Real = 14,
    UniqueIdentifier = 15,
    SmallDateTime = 16,
    SmallInt = 17,
    Text = 18,
    Timestamp = 19,
    TinyInt = 20,
    VarBinary = 21,
    VarChar = 22,
    Date = 23,
    Time = 24,
    Integer = 25,
    Float = 26,
    SmallFloat = 27,
    Serial = 28,
    Money = 29,
    Null = 30,
    Byte = 31,
    Int8 = 32,
    Serial8 = 33,
    Set = 34,
    MultiSet = 35,
    List = 36,
    Row = 37,
    SQLUDTVar = 40,
    SQLUDTFixed = 41,
    BigSerial = 53,
    Other = 99,
    LVarChar = 101,
    Blob = 110,
    Clob = 111,
    SmartLOBLocator = 112,
    /// <summary>The <c>JSON</c> built-in type. Reads as a decoded JSON <c>string</c>;
    /// bind a JSON <c>string</c> to write.</summary>
    Json = 113,
    /// <summary>The <c>BSON</c> built-in type. Reads as the raw encoded <c>byte[]</c>
    /// (decode with <see cref="BsonMediaTypeFormatter"/>); bind a JSON <c>string</c>
    /// (auto-encoded) or a raw BSON <c>byte[]</c> to write.</summary>
    Bson = 114,
    /// <summary>The <c>BINARYVAR</c> type (IBM Informix Database Extensions, variable-length
    /// binary, at most 253 bytes). Reads as <c>byte[]</c>; bind a <c>byte[]</c> with this type
    /// to write. Without it a <c>byte[]</c> binds as <see cref="Byte"/>, which the server cannot
    /// cast to <c>BINARYVAR</c> (-9634).</summary>
    BinaryVar = 115,
    /// <summary>The <c>BINARY18</c> type (fixed 18-byte binary). Reads as <c>byte[]</c>; bind a
    /// <c>byte[]</c> of at most 18 bytes with this type to write (a shorter value is padded with
    /// zero bytes to 18).</summary>
    Binary18 = 116,
    Boolean = 126,
    Char1 = 1001,
    IntervalYearMonth = 1400,
    IntervalDayFraction = 1499,
}
