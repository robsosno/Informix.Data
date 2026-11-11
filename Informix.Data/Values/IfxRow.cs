using System.Globalization;
using System.Text;
using Informix.Data.Sqli;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// A read-only, decoded Informix ROW value — the materialized form of a named or
/// unnamed <c>ROW(...)</c> column produced by <see cref="IfxDataReader"/>. Fields
/// are exposed positionally and by name, with the same typed getters as the data
/// reader.
/// </summary>
public sealed class IfxRow
{
    private readonly IReadOnlyList<IfxComplexTypeInfo> _fields;
    private readonly object[] _values;

    internal IfxRow(string typeName, IReadOnlyList<IfxComplexTypeInfo> fields, object[] values)
    {
        TypeName = typeName ?? "";
        _fields = fields;
        _values = values;
    }

    /// <summary>The named-ROW type name (e.g. <c>"addr_t"</c>), or <c>""</c> for an unnamed ROW.</summary>
    public string TypeName { get; }

    /// <summary>The number of fields in the ROW.</summary>
    public int FieldCount => _values.Length;

    /// <summary>The declared name of field <paramref name="ordinal"/>.</summary>
    public string GetName(int ordinal) => _fields[ordinal].Name;

    /// <summary>The ordinal of the field named <paramref name="name"/> (case-insensitive), or -1 if absent.</summary>
    public int GetOrdinal(string name)
    {
        for (int i = 0; i < _fields.Count; i++)
            if (string.Equals(_fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>The CLR value of field <paramref name="ordinal"/> (<see cref="DBNull.Value"/> for SQL NULL).</summary>
    public object GetValue(int ordinal) => _values[ordinal];

    /// <summary>The CLR value of the field named <paramref name="name"/>.</summary>
    public object GetValue(string name)
    {
        int i = GetOrdinal(name);
        if (i < 0)
            throw new ArgumentOutOfRangeException(nameof(name), $"ROW '{TypeName}' has no field named '{name}'.");
        return _values[i];
    }

    /// <summary>True when field <paramref name="ordinal"/> is SQL NULL.</summary>
    public bool IsDBNull(int ordinal) => _values[ordinal] is DBNull;

    /// <summary>The CLR <see cref="Type"/> field <paramref name="ordinal"/> decodes to.</summary>
    public Type GetFieldType(int ordinal) => ClrTypeOf(_fields[ordinal]);

    /// <summary>The Informix type name of field <paramref name="ordinal"/> (extended name when present).</summary>
    public string GetDataTypeName(int ordinal)
    {
        var f = _fields[ordinal];
        return !string.IsNullOrEmpty(f.ExtendedName) ? f.ExtendedName : f.Type.ToString().ToLowerInvariant();
    }

    public short GetInt16(int ordinal) => (short)_values[ordinal];
    public int GetInt32(int ordinal) => (int)_values[ordinal];
    public long GetInt64(int ordinal) => (long)_values[ordinal];
    public float GetFloat(int ordinal) => (float)_values[ordinal];
    public double GetDouble(int ordinal) => (double)_values[ordinal];
    public decimal GetDecimal(int ordinal) => (decimal)_values[ordinal];
    public bool GetBoolean(int ordinal) => (bool)_values[ordinal];
    public string GetString(int ordinal) => (string)_values[ordinal];

    public DateTime GetDateTime(int ordinal) => _values[ordinal] switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeOnly t => new DateTime(1700, DateTime.Now.Month, 1).Add(t.ToTimeSpan()),
        var v => (DateTime)v,
    };

    private static Type ClrTypeOf(IfxComplexTypeInfo f) => f.Type switch
    {
        SqliTypeCode.Char or SqliTypeCode.VarChar or SqliTypeCode.NChar or SqliTypeCode.NVarChar
            or SqliTypeCode.LVarChar or SqliTypeCode.Text or SqliTypeCode.Clob => typeof(string),
        SqliTypeCode.SmallInt => typeof(short),
        SqliTypeCode.Int or SqliTypeCode.Serial => typeof(int),
        SqliTypeCode.Int8 or SqliTypeCode.Serial8 or SqliTypeCode.BigInt or SqliTypeCode.BigSerial => typeof(long),
        SqliTypeCode.SmFloat => typeof(float),
        SqliTypeCode.Float => typeof(double),
        SqliTypeCode.Decimal or SqliTypeCode.Money => typeof(decimal),
        SqliTypeCode.Date => typeof(DateOnly),
        SqliTypeCode.DateTime => IfxQualifier.GetStartCode((short)f.ColLength) >= 6 ? typeof(TimeOnly) : typeof(DateTime),
        SqliTypeCode.Interval => IfxQualifier.GetStartCode((short)f.ColLength) < 4 ? typeof(IfxMonthSpan) : typeof(TimeSpan),
        SqliTypeCode.Bool => typeof(bool),
        SqliTypeCode.Byte or SqliTypeCode.Blob => typeof(byte[]),
        SqliTypeCode.Row => typeof(IfxRow),
        SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List or SqliTypeCode.Collection => typeof(object[]),
        _ => typeof(object),
    };

    /// <summary>Renders the ROW in Informix literal form, e.g. <c>ROW('Main St','12345')</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder("ROW(");
        for (int i = 0; i < _values.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(FormatLiteral(_values[i]));
        }
        return sb.Append(')').ToString();
    }

    private static string FormatLiteral(object value) => value switch
    {
        DBNull => "NULL",
        string s => "'" + s.Replace("'", "''") + "'",
        DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
        DateOnly d => "'" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
        TimeOnly t => "'" + t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
        bool b => b ? "'t'" : "'f'",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        object[] arr => "SET{" + string.Join(",", arr.Select(FormatLiteral)) + "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
