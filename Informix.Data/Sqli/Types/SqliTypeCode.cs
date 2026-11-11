namespace Informix.Data.Types;

/// <summary>
/// Internal Informix wire SQL type codes (the "SQLxxx" / <c>IFX_TYPE_*</c> family)
/// exchanged with the server in row descriptors and binds. These numeric values
/// are the on-the-wire codes and must not be renumbered; the public, ODBC-shaped
/// <see cref="IfxType"/> is a separate enum mapped to/from these by
/// <see cref="IfxTypeMap"/>.
/// </summary>
internal enum SqliTypeCode : short
{
    Char = 0,
    SmallInt = 1,
    Int = 2,
    Float = 3,
    SmFloat = 4,
    Decimal = 5,
    Serial = 6,
    Date = 7,
    Money = 8,
    Null = 9,
    DateTime = 10,
    Byte = 11,
    Text = 12,
    VarChar = 13,
    Interval = 14,
    NChar = 15,
    NVarChar = 16,
    Int8 = 17,
    Serial8 = 18,
    Set = 19,
    MultiSet = 20,
    List = 21,
    Row = 22,
    Collection = 23,
    RowRef = 24,
    UdtVar = 40,
    UdtFixed = 41,
    RefSer8 = 42,
    LVarChar = 43,
    SendRecv = 44,
    Bool = 45,
    ImpExp = 46,
    ImpExpBin = 47,
    SqlUdrDefault = 48,
    Unknown = 49,
    BigInt = 52,
    BigSerial = 53,
    Max = 54,
    Clob = 101,
    Blob = 102,
    // Not real SQLI wire codes — internal markers assigned after ReceiveDescribe
    // collapses a UdtVar/UdtFixed column carrying extended-type id JSON(25)/BSON(26)
    // (IfxExtendedType.Json/Bson), exactly as Clob/Blob collapse from CLOB(11)/BLOB(10).
    // Unlike Clob/Blob these are NOT smart large objects: the value travels inline in
    // the tuple with the LVARCHAR-family [flag:1][len:int32][data] framing, decoded
    // directly by DecodeValue (no locator, no lazy SQ_FETCHBLOB round trip).
    Json = 103,
    Bson = 104,
}

/// <summary>
/// Bit flags OR-ed into the type word of a column/parameter descriptor.
/// </summary>
[Flags]
internal enum IfxTypeFlags : short
{
    None = 0,
    NotNullable = 256,
    Distinct = 2048,
    NamedRow = 4096,
    DBoolean = 16384,
    CollectionClient = unchecked((short)-32768),
}

/// <summary>
/// Extended-type identifiers (<c>IFX_XID_*</c>) that qualify the opaque/complex
/// built-in types (LVARCHAR, BLOB/CLOB, BSON/JSON, ...).
/// </summary>
internal enum IfxExtendedType : short
{
    LVarChar = 1,
    SendRecv = 2,
    ImpExp = 3,
    ImpExpBin = 4,
    Boolean = 5,
    Pointer = 6,
    IndexKeyArray = 7,
    RtnParamTypes = 8,
    SelFuncArgs = 9,
    Blob = 10,
    Clob = 11,
    LoList = 12,
    IfxLoSpec = 13,
    IfxLoStat = 14,
    Stat = 15,
    ClientBinVal = 16,
    Stream = 17,
    IdsSecurityLabel = 18,
    IdsElementEnc = 19,
    IdsCoveringInfo = 20,
    Llvc = 24,
    Json = 25,
    Bson = 26,
    LastBuiltin = 26,
}

/// <summary>
/// Helpers for splitting the composite type word carried in a descriptor into
/// its base <see cref="SqliTypeCode"/> and its <see cref="IfxTypeFlags"/>.
/// </summary>
internal static class IfxTypeCodes
{
    /// <summary>The low byte holds the base built-in type; the high byte holds the flag bits.</summary>
    public const short BaseTypeMask = 0xFF;

    /// <summary>Extracts the base built-in type from a composite descriptor type word.</summary>
    public static SqliTypeCode BaseType(short typeWord) => (SqliTypeCode)(short)(typeWord & BaseTypeMask);

    /// <summary>True when the column/parameter is declared NOT NULL.</summary>
    public static bool IsNotNullable(short typeWord) => (typeWord & (short)IfxTypeFlags.NotNullable) != 0;
}
