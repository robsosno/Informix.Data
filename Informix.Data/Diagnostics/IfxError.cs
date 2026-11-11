using System.Collections;

namespace Informix.Data;

/// <summary>
/// One diagnostic record from the Informix server — the element type of
/// <see cref="IfxException.Errors"/> and <see cref="IfxInfoMessageEventArgs.Errors"/>.
/// </summary>
public sealed class IfxError
{
    internal IfxError(string message, int nativeError, string source, string? sqlState)
    {
        Message = message;
        NativeError = nativeError;
        Source = source;
        SQLState = sqlState ?? "";
    }

    /// <summary>The human-readable error text.</summary>
    public string Message { get; }

    /// <summary>The Informix native error code (the SQLCODE), negative for errors.</summary>
    public int NativeError { get; }

    /// <summary>The provider that raised the error (<c>"Informix.Data"</c>).</summary>
    public string Source { get; }

    /// <summary>The five-character SQLSTATE, or <c>""</c> when unknown.</summary>
    public string SQLState { get; }

    public override string ToString() =>
        $"{Source}: {Message} (NativeError={NativeError}, SQLState={SQLState})";
}

/// <summary>
/// A read-only collection of <see cref="IfxError"/> diagnostic records.
/// </summary>
public sealed class IfxErrorCollection : ICollection, IEnumerable
{
    private readonly List<IfxError> _errors = [];

    internal IfxErrorCollection() { }

    internal void Add(IfxError error) => _errors.Add(error);

    public int Count => _errors.Count;

    public IfxError this[int index] => _errors[index];

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public void CopyTo(Array array, int index) => ((ICollection)_errors).CopyTo(array, index);

    public void CopyTo(IfxError[] array, int index) => _errors.CopyTo(array, index);

    public IEnumerator GetEnumerator() => _errors.GetEnumerator();
}
