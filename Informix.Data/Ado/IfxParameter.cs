using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Informix.Data;

/// <summary>
/// A single Informix command parameter (<see cref="DbParameter"/>). Informix binds
/// parameters positionally, in <c>?</c> placeholder order.
/// </summary>
public sealed class IfxParameter : DbParameter, ICloneable
{
    private object? _value;
    private string _parameterName = string.Empty;
    private string _sourceColumn = string.Empty;

    public IfxParameter() { }

    public IfxParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    public IfxParameter(string parameterName, IfxType ifxType)
    {
        ParameterName = parameterName;
        IfxType = ifxType;
    }

    /// <summary>The Informix server type this parameter binds as. When unset (the
    /// default) it is inferred from <see cref="Value"/>.</summary>
    public IfxType IfxType { get; set; } = default;

    /// <summary>
    /// For a <see cref="IfxType.Clob"/> / <see cref="IfxType.Blob"/> parameter: the column the value
    /// is going into, as <c>"table.column"</c>. When set, the smart large object is created with that
    /// column's storage attributes - its sbspace (<c>PUT col IN (sbspace2)</c>) and the other settings
    /// the column was declared with - instead of the server-wide default sbspace. When <c>null</c>
    /// (the default) the LOB goes to the default sbspace, whatever the column declares.
    ///
    /// <para>The server resolves the name (<c>ifx_lo_col_sbinfo</c>), so it must name a column in the
    /// connected database; an unknown column fails the command. Costs one extra round trip per LOB.
    /// Ignored for every other parameter type.</para>
    /// </summary>
    public string? SmartLobColumn { get; set; }

    // Kept for ADO.NET compatibility only; the binding logic uses IfxType and the runtime type of Value.
    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = value ?? string.Empty;
    }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    /// <summary>
    /// Which version of the <see cref="SourceColumn"/>'s value a <see cref="DbDataAdapter"/> reads from a
    /// <see cref="DataRow"/> when it runs the command: <see cref="DataRowVersion.Current"/> (the default)
    /// for the value to write, <see cref="DataRowVersion.Original"/> for the value the row was loaded
    /// with, e.g. in the <c>WHERE</c> of a hand-written <c>UpdateCommand</c>.
    /// </summary>
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    // Output and InputOutput parameters receive their value here after execution.
    public override object? Value
    {
        get => _value;
        set => _value = value;
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override int Size { get; set; }

    public override byte Precision { get; set; }

    public override byte Scale { get; set; }

    public override void ResetDbType()
    {
        DbType = DbType.Object;
        IfxType = default;
    }

    /// <summary>Resets <see cref="IfxType"/> to its unset default (so the bind type is
    /// inferred from <see cref="Value"/>).</summary>
    public void ResetIfxType() => IfxType = default;

    /// <summary>Creates a new <see cref="IfxParameter"/> that is a copy of the current instance.</summary>
    public IfxParameter Clone() => new()
    {
        ParameterName = _parameterName,
        Value = _value,
        IfxType = IfxType,
        SmartLobColumn = SmartLobColumn,
        DbType = DbType,
        Direction = Direction,
        IsNullable = IsNullable,
        SourceColumn = _sourceColumn,
        SourceColumnNullMapping = SourceColumnNullMapping,
        Size = Size,
        Precision = Precision,
        Scale = Scale,
        SourceVersion = SourceVersion,
    };

    object ICloneable.Clone() => Clone();

    public override string ToString() => ParameterName;
}
