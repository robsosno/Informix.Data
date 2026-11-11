using System;
using System.Data;
using System.Data.Common;
using System.Reflection;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxDataAdapter"/>'s <see cref="ICloneable"/> implementation: copies the
/// DataAdapter-level settings (table mappings, missing-mapping/-schema actions,
/// <see cref="DbDataAdapter.AcceptChangesDuringFill"/>) via the base <see cref="DbDataAdapter"/>
/// copy constructor, but keeps the four command properties pointed at the SAME
/// <see cref="IfxCommand"/> instances -- not clones of them -- and never carries
/// <see cref="IfxDataAdapter.RowUpdated"/>/<see cref="IfxDataAdapter.RowUpdating"/>
/// subscribers over to the copy.
/// </summary>
public class IfxDataAdapterCloneTests
{
    [Fact]
    public void DataAdapter_implements_the_non_generic_ICloneable()
    {
        Assert.IsAssignableFrom<ICloneable>(new IfxDataAdapter());
    }

    [Fact]
    public void Clone_copies_DataAdapter_level_settings_and_table_mappings()
    {
        using var source = new IfxDataAdapter
        {
            MissingSchemaAction = MissingSchemaAction.AddWithKey,
            MissingMappingAction = MissingMappingAction.Ignore,
            AcceptChangesDuringFill = false,
        };
        source.TableMappings.Add("SourceTable", "DataTable1");

        using var clone = source.Clone();

        Assert.Equal(MissingSchemaAction.AddWithKey, clone.MissingSchemaAction);
        Assert.Equal(MissingMappingAction.Ignore, clone.MissingMappingAction);
        Assert.False(clone.AcceptChangesDuringFill);
        Assert.Single(clone.TableMappings);
        Assert.Equal("DataTable1", clone.TableMappings[0].DataSetTable);
    }

    [Fact]
    public void Clone_points_the_four_commands_at_the_same_instances_not_clones()
    {
        using var select = new IfxCommand("select * from t");
        using var insert = new IfxCommand("insert into t values (?)");
        using var update = new IfxCommand("update t set a = ?");
        using var delete = new IfxCommand("delete from t");
        using var source = new IfxDataAdapter
        {
            SelectCommand = select,
            InsertCommand = insert,
            UpdateCommand = update,
            DeleteCommand = delete,
        };

        using var clone = source.Clone();

        Assert.Same(select, clone.SelectCommand);
        Assert.Same(insert, clone.InsertCommand);
        Assert.Same(update, clone.UpdateCommand);
        Assert.Same(delete, clone.DeleteCommand);
    }

    [Fact]
    public void Clone_through_the_ICloneable_facade_returns_an_independent_IfxDataAdapter()
    {
        using var select = new IfxCommand("select 1 from t");
        using var source = new IfxDataAdapter { SelectCommand = select };

        object copy = ((ICloneable)source).Clone();

        var clone = Assert.IsType<IfxDataAdapter>(copy);
        Assert.NotSame(source, clone);
        Assert.Same(select, clone.SelectCommand);
    }

    [Fact]
    public void Clone_does_not_carry_RowUpdated_or_RowUpdating_subscribers()
    {
        using var source = new IfxDataAdapter();
        source.RowUpdated += (_, _) => { };
        source.RowUpdating += (_, _) => { };

        using var clone = source.Clone();

        Assert.NotNull(GetSubscriberDelegate(source, "RowUpdated"));
        Assert.NotNull(GetSubscriberDelegate(source, "RowUpdating"));
        Assert.Null(GetSubscriberDelegate(clone, "RowUpdated"));
        Assert.Null(GetSubscriberDelegate(clone, "RowUpdating"));
    }

    private static Delegate? GetSubscriberDelegate(IfxDataAdapter adapter, string eventFieldName)
    {
        FieldInfo field = typeof(IfxDataAdapter).GetField(eventFieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Delegate?)field.GetValue(adapter);
    }
}
