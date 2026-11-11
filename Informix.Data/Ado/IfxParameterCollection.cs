using System.Collections;
using System.Data.Common;

namespace Informix.Data;

/// <summary>
/// The parameter collection of an <see cref="IfxCommand"/>. Informix SQLI binds
/// parameters positionally (by "?" placeholder order), so named lookups resolve
/// to the first matching <see cref="IfxParameter.ParameterName"/>.
/// </summary>
public sealed class IfxParameterCollection : DbParameterCollection
{
    private readonly List<IfxParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot { get; } = new();

    public new IfxParameter this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _items[index] = value;
        }
    }

    /// <summary>
    /// Adds a parameter carrying <paramref name="value"/>, with its Informix type inferred
    /// from the value. Note the <see cref="IfxType"/> overload takes precedence when you
    /// pass an <see cref="Informix.Data.IfxType"/> — that declares the parameter's type, it
    /// does not store the enum as the value.
    /// </summary>
    public IfxParameter Add(string parameterName, object? value)
    {
        var p = new IfxParameter(parameterName, value);
        _items.Add(p);
        return p;
    }

    /// <summary>Adds a parameter of the given Informix type, with no value yet.</summary>
    public IfxParameter Add(string parameterName, IfxType ifxType) =>
        Add(new IfxParameter(parameterName, ifxType));

    /// <summary>Adds a parameter of the given Informix type and size, with no value yet.</summary>
    public IfxParameter Add(string parameterName, IfxType ifxType, int size) =>
        Add(new IfxParameter(parameterName, ifxType) { Size = size });

    /// <summary>
    /// Adds a parameter of the given Informix type and size, bound to
    /// <paramref name="sourceColumn"/> for <see cref="IfxDataAdapter"/> updates.
    /// </summary>
    public IfxParameter Add(string parameterName, IfxType ifxType, int size, string sourceColumn) =>
        Add(new IfxParameter(parameterName, ifxType) { Size = size, SourceColumn = sourceColumn });

    /// <summary>Adds <paramref name="value"/> and returns it, for call chaining.</summary>
    public IfxParameter Add(IfxParameter value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _items.Add(value);
        return value;
    }

    /// <summary>
    /// Adds a parameter carrying <paramref name="value"/>. Unlike <see cref="Add(string, object?)"/>
    /// this never competes with the <see cref="IfxType"/> overload, so it is the unambiguous
    /// way to store a value that happens to be an <see cref="Informix.Data.IfxType"/>.
    /// </summary>
    public IfxParameter AddWithValue(string parameterName, object? value) =>
        Add(new IfxParameter(parameterName, value));

    public override int Add(object value)
    {
        _items.Add(Cast(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
            _items.Add(Cast(v));
    }

    /// <summary>Adds every parameter in <paramref name="values"/>, in order.</summary>
    public void AddRange(IfxParameter[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _items.AddRange(values);
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => value is IfxParameter p && _items.Contains(p);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => value is IfxParameter p ? _items.IndexOf(p) : -1;

    // Case-insensitive, exact name match (the sigil is not stripped here).
    public override int IndexOf(string parameterName)
    {
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    public override void Remove(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is IfxParameter p)
            _items.Remove(p);
    }

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        int i = IndexOf(parameterName);
        if (i < 0)
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
        _items.RemoveAt(i);
    }

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        int i = IndexOf(parameterName);
        if (i < 0)
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
        return _items[i];
    }

    protected override void SetParameter(int index, DbParameter value) => _items[index] = Cast(value);

    // Assigning to an unknown name appends the parameter instead of failing.
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int i = IndexOf(parameterName);
        if (i < 0)
            _items.Add(Cast(value));
        else
            _items[i] = Cast(value);
    }

    // null is an argument error (ArgumentNullException); a non-null value of another type stays an InvalidCastException.
    private static IfxParameter Cast(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value as IfxParameter ?? throw new InvalidCastException("Only IfxParameter instances are supported.");
    }
}
