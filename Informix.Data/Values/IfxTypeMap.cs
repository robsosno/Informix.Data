using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Bridges the public, ODBC-shaped <see cref="IfxType"/> and the internal
/// <see cref="SqliTypeCode"/> wire codes. <see cref="ToPublic"/> is used when
/// reporting metadata (the schema-table provider type, parameter type inference);
/// <see cref="ToWire"/> is used when turning a caller-pinned parameter type into a
/// bind hint. Where two public values map to one wire code (or vice versa) the
/// Informix-native member is chosen and the ODBC generics fold onto the nearest
/// wire type.
/// </summary>
internal static class IfxTypeMap
{
    /// <summary>Maps an internal wire type code to the public provider type.</summary>
    public static IfxType ToPublic(SqliTypeCode wire) => wire switch
    {
        SqliTypeCode.Char => IfxType.Char,
        SqliTypeCode.SmallInt => IfxType.SmallInt,
        SqliTypeCode.Int => IfxType.Integer,
        SqliTypeCode.Float => IfxType.Float,
        SqliTypeCode.SmFloat => IfxType.SmallFloat,
        SqliTypeCode.Decimal => IfxType.Decimal,
        SqliTypeCode.Serial => IfxType.Serial,
        SqliTypeCode.Date => IfxType.Date,
        SqliTypeCode.Money => IfxType.Money,
        SqliTypeCode.Null => IfxType.Null,
        SqliTypeCode.DateTime => IfxType.DateTime,
        SqliTypeCode.Byte => IfxType.Byte,
        SqliTypeCode.Text => IfxType.Text,
        SqliTypeCode.VarChar => IfxType.VarChar,
        SqliTypeCode.Interval => IfxType.IntervalDayFraction,
        SqliTypeCode.NChar => IfxType.NChar,
        SqliTypeCode.NVarChar => IfxType.NVarChar,
        SqliTypeCode.Int8 => IfxType.Int8,
        SqliTypeCode.Serial8 => IfxType.Serial8,
        SqliTypeCode.Set => IfxType.Set,
        SqliTypeCode.MultiSet => IfxType.MultiSet,
        SqliTypeCode.List => IfxType.List,
        SqliTypeCode.Row => IfxType.Row,
        SqliTypeCode.UdtVar => IfxType.SQLUDTVar,
        SqliTypeCode.UdtFixed => IfxType.SQLUDTFixed,
        SqliTypeCode.LVarChar => IfxType.LVarChar,
        SqliTypeCode.SendRecv => IfxType.SQLUDTVar,
        SqliTypeCode.Bool => IfxType.Boolean,
        SqliTypeCode.BigInt => IfxType.BigInt,
        SqliTypeCode.BigSerial => IfxType.BigSerial,
        SqliTypeCode.Clob => IfxType.Clob,
        SqliTypeCode.Blob => IfxType.Blob,
        SqliTypeCode.Json => IfxType.Json,
        SqliTypeCode.Bson => IfxType.Bson,
        // Collection/RowRef/RefSer8/ImpExp*/SqlUdrDefault/Unknown/Max have no direct
        // official equivalent — report the generic Other.
        _ => IfxType.Other,
    };

    /// <summary>
    /// Maps a public provider type to the internal wire type code used as a bind
    /// hint. <see cref="IfxType.Other"/> and the unset default (0) fold onto the
    /// wire "unknown" sentinel, which the binder reads as "infer from the value".
    /// </summary>
    public static SqliTypeCode ToWire(IfxType type) => type switch
    {
        IfxType.BigInt => SqliTypeCode.BigInt,
        IfxType.Binary => SqliTypeCode.Byte,
        IfxType.Bit => SqliTypeCode.Bool,
        IfxType.Char => SqliTypeCode.Char,
        IfxType.Char1 => SqliTypeCode.Char,
        IfxType.DateTime => SqliTypeCode.DateTime,
        IfxType.Decimal => SqliTypeCode.Decimal,
        IfxType.Numeric => SqliTypeCode.Decimal,
        IfxType.Double => SqliTypeCode.Float,
        IfxType.Image => SqliTypeCode.Byte,
        IfxType.Int => SqliTypeCode.Int,
        IfxType.Integer => SqliTypeCode.Int,
        IfxType.NChar => SqliTypeCode.NChar,
        IfxType.NText => SqliTypeCode.Text,
        IfxType.NVarChar => SqliTypeCode.NVarChar,
        IfxType.Real => SqliTypeCode.SmFloat,
        IfxType.UniqueIdentifier => SqliTypeCode.Char,
        IfxType.SmallDateTime => SqliTypeCode.DateTime,
        IfxType.SmallInt => SqliTypeCode.SmallInt,
        IfxType.Text => SqliTypeCode.Text,
        IfxType.Timestamp => SqliTypeCode.DateTime,
        IfxType.TinyInt => SqliTypeCode.SmallInt,
        IfxType.VarBinary => SqliTypeCode.Byte,
        IfxType.VarChar => SqliTypeCode.VarChar,
        IfxType.Date => SqliTypeCode.Date,
        IfxType.Time => SqliTypeCode.DateTime,
        IfxType.Float => SqliTypeCode.Float,
        IfxType.SmallFloat => SqliTypeCode.SmFloat,
        IfxType.Serial => SqliTypeCode.Serial,
        IfxType.Money => SqliTypeCode.Money,
        IfxType.Null => SqliTypeCode.Null,
        IfxType.Byte => SqliTypeCode.Byte,
        IfxType.Int8 => SqliTypeCode.Int8,
        IfxType.Serial8 => SqliTypeCode.Serial8,
        IfxType.Set => SqliTypeCode.Set,
        IfxType.MultiSet => SqliTypeCode.MultiSet,
        IfxType.List => SqliTypeCode.List,
        IfxType.Row => SqliTypeCode.Row,
        IfxType.SQLUDTVar => SqliTypeCode.UdtVar,
        IfxType.SQLUDTFixed => SqliTypeCode.UdtFixed,
        IfxType.BigSerial => SqliTypeCode.BigSerial,
        IfxType.LVarChar => SqliTypeCode.LVarChar,
        IfxType.Blob => SqliTypeCode.Blob,
        IfxType.Clob => SqliTypeCode.Clob,
        IfxType.Json => SqliTypeCode.Json,
        IfxType.Bson => SqliTypeCode.Bson,
        IfxType.BinaryVar or IfxType.Binary18 => SqliTypeCode.SendRecv,
        IfxType.SmartLOBLocator => SqliTypeCode.SendRecv,
        IfxType.Boolean => SqliTypeCode.Bool,
        IfxType.IntervalYearMonth => SqliTypeCode.Interval,
        IfxType.IntervalDayFraction => SqliTypeCode.Interval,
        _ => SqliTypeCode.Unknown,
    };
}
