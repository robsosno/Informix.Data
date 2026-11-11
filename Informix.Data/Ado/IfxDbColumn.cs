using System.Collections.ObjectModel;
using System.Data.Common;
using Informix.Data.Sqli;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Schema information for one column of an Informix result set — the modern typed
/// counterpart of a <see cref="System.Data.DataTable"/> row from
/// <see cref="IfxDataReader.GetSchemaTable"/>, returned by
/// <see cref="IfxDataReader.GetColumnSchema"/>.
/// </summary>
/// <remarks>
/// Beyond the standard <see cref="DbColumn"/> members it carries the Informix
/// extended-type detail (<see cref="ProviderType"/>, <see cref="ExtendedTypeName"/>,
/// <see cref="ExtendedOwner"/>, <see cref="IsDistinct"/>, <see cref="IsOpaqueComplex"/>),
/// which are also reachable through the <see cref="DbColumn.this[string]"/> indexer.
/// </remarks>
public sealed class IfxDbColumn : DbColumn
{
    internal IfxDbColumn(IfxColumnMetadata column, int ordinal)
    {
        ColumnName = column.Name;
        ColumnOrdinal = ordinal;
        ColumnSize = column.Length;
        NumericPrecision = column.NumericPrecision;
        NumericScale = column.NumericScale;
        DataType = column.ClrType;
        DataTypeName = column.DataTypeName;
        IsLong = column.IsLong;
        AllowDBNull = column.IsNullable;
        BaseColumnName = column.Name;

        ProviderType = IfxTypeMap.ToPublic(column.SqliTypeCode);
        if (column.SqliTypeCode is SqliTypeCode.DateTime or SqliTypeCode.Interval)
        {
            StartTimeUnit = (IfxTimeUnit)IfxQualifier.GetStartCode(column.PrecisionScale);
            EndTimeUnit = (IfxTimeUnit)IfxQualifier.GetEndCode(column.PrecisionScale);
        }
        ExtendedTypeName = column.ExtendedTypeName;
        ExtendedOwner = column.ExtendedOwner;
        IsDistinct = column.IsDistinct;
        IsOpaqueComplex = column.IsOpaqueComplex;
    }

    /// <summary>The public, ODBC-shaped provider type of this column.</summary>
    public IfxType ProviderType { get; }

    /// <summary>
    /// For a DATETIME or INTERVAL column, the first field of its qualifier (<c>YEAR</c> in
    /// <c>DATETIME YEAR TO MINUTE</c>, <c>DAY</c> in <c>INTERVAL DAY(3) TO DAY</c>); <c>null</c> for any other type.
    /// A <see cref="DateTime"/> or <see cref="TimeSpan"/> does not carry this, but the text form of the value does.
    /// </summary>
    public IfxTimeUnit? StartTimeUnit { get; }

    /// <summary>For a DATETIME or INTERVAL column, the last field of its qualifier (<c>MINUTE</c>, <c>FRACTION(3)</c> ...); otherwise <c>null</c>.</summary>
    public IfxTimeUnit? EndTimeUnit { get; }

    /// <summary>The extended (opaque/UDT/DISTINCT) type name from the descriptor, or the empty string.</summary>
    public string ExtendedTypeName { get; }

    /// <summary>The owner (schema) of the extended type from the descriptor, or the empty string.</summary>
    public string ExtendedOwner { get; }

    /// <summary>True when the column is a DISTINCT type over a base built-in type.</summary>
    public bool IsDistinct { get; }

    /// <summary>True for a complex/opaque column (ROW, a collection, or an opaque UDT).</summary>
    public bool IsOpaqueComplex { get; }

    /// <inheritdoc />
    public override object? this[string propertyName] => propertyName switch
    {
        nameof(ProviderType) => ProviderType,
        nameof(StartTimeUnit) => StartTimeUnit,
        nameof(EndTimeUnit) => EndTimeUnit,
        nameof(ExtendedTypeName) => ExtendedTypeName,
        nameof(ExtendedOwner) => ExtendedOwner,
        nameof(IsDistinct) => IsDistinct,
        nameof(IsOpaqueComplex) => IsOpaqueComplex,
        _ => base[propertyName],
    };

    /// <summary>
    /// Builds the typed column schema for a described result set — the single source of
    /// truth shared by <see cref="IfxDataReader.GetColumnSchema"/>,
    /// <see cref="IfxDataReader.GetSchemaTable"/> and
    /// <see cref="IfxScrollReader.GetColumnSchema"/>.
    /// </summary>
    internal static ReadOnlyCollection<DbColumn> BuildSchema(IReadOnlyList<IfxColumnMetadata> columns)
    {
        var list = new DbColumn[columns.Count];
        for (int i = 0; i < columns.Count; i++)
            list[i] = new IfxDbColumn(columns[i], i);
        return new ReadOnlyCollection<DbColumn>(list);
    }
}
