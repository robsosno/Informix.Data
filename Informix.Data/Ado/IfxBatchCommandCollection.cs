using System.Collections;
using System.Data.Common;

namespace Informix.Data;

/// <summary>Ordered commands of an <see cref="IfxBatch"/>; only <see cref="IfxBatchCommand"/> instances are accepted.</summary>
public sealed class IfxBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<IfxBatchCommand> _items = [];

    public override int Count => _items.Count;

    public override bool IsReadOnly => false;

    public new IfxBatchCommand this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>Creates a command from <paramref name="commandText"/>, adds it, and returns it.</summary>
    public IfxBatchCommand Add(string commandText)
    {
        var command = new IfxBatchCommand(commandText);
        _items.Add(command);
        return command;
    }

    public void Add(IfxBatchCommand item) => _items.Add(item);

    public override void Add(DbBatchCommand item) => Add(Cast(item));

    public override void Clear() => _items.Clear();

    public bool Contains(IfxBatchCommand item) => _items.Contains(item);

    public override bool Contains(DbBatchCommand item) => item is IfxBatchCommand p && Contains(p);

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => ((ICollection)_items).CopyTo(array, arrayIndex);

    public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();

    public int IndexOf(IfxBatchCommand item) => _items.IndexOf(item);

    public override int IndexOf(DbBatchCommand item) => item is IfxBatchCommand p ? IndexOf(p) : -1;

    public void Insert(int index, IfxBatchCommand item) => _items.Insert(index, item);

    public override void Insert(int index, DbBatchCommand item) => Insert(index, Cast(item));

    public bool Remove(IfxBatchCommand item) => _items.Remove(item);

    public override bool Remove(DbBatchCommand item) => item is IfxBatchCommand p && Remove(p);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    protected override DbBatchCommand GetBatchCommand(int index) => _items[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) => _items[index] = Cast(batchCommand);

    private static IfxBatchCommand Cast(DbBatchCommand value) =>
        value as IfxBatchCommand ?? throw new InvalidCastException("Only IfxBatchCommand instances are supported.");
}
