using System.Data;
using System.Data.Common;

namespace Informix.Data;

/// <summary>
/// A <see cref="DbDataAdapter"/> for Informix: fills a <see cref="DataSet"/>/<see cref="DataTable"/>
/// from a <see cref="SelectCommand"/> and pushes changes back through the
/// insert/update/delete commands.
/// </summary>
public sealed class IfxDataAdapter : DbDataAdapter, ICloneable
{
    public IfxDataAdapter() { }

    public IfxDataAdapter(IfxCommand selectCommand) => SelectCommand = selectCommand;

    public IfxDataAdapter(string selectCommandText, IfxConnection selectConnection) =>
        SelectCommand = new IfxCommand(selectCommandText, selectConnection);

    public IfxDataAdapter(string selectCommandText, string selectConnectionString) =>
        SelectCommand = new IfxCommand(selectCommandText, new IfxConnection(selectConnectionString));

    /// <summary>
    /// Copies the DataAdapter-level settings (table mappings, missing-mapping/-schema
    /// actions, <see cref="System.Data.Common.DataAdapter.AcceptChangesDuringFill"/>, ...) via the base
    /// <see cref="DbDataAdapter"/> copy constructor, then re-points the four command
    /// properties at the SAME <see cref="IfxCommand"/> instances as <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// The commands are deliberately reference-copied, not cloned: the inherited
    /// <c>DbDataAdapter.CloneFrom</c> would otherwise clone each command through
    /// <see cref="IfxCommand.Clone"/> (since <see cref="IfxCommand"/> implements
    /// <see cref="ICloneable"/>) and silently detach the copy's commands from the
    /// original's -- surprising for code written against the provider-agnostic
    /// <see cref="IDbDataAdapter"/> shape. (The base class's own inherited cloning
    /// behavior would do exactly that here, once the "clone the command if it's
    /// ICloneable" rule in <c>DbDataAdapter.CloneCommand</c> is followed through --
    /// this constructor re-asserts reference identity after the base call to avoid it.)
    /// </remarks>
    private IfxDataAdapter(IfxDataAdapter from) : base(from)
    {
        SelectCommand = from.SelectCommand;
        InsertCommand = from.InsertCommand;
        UpdateCommand = from.UpdateCommand;
        DeleteCommand = from.DeleteCommand;
    }

    public new IfxCommand? SelectCommand
    {
        get => (IfxCommand?)base.SelectCommand;
        set => base.SelectCommand = value;
    }

    public new IfxCommand? InsertCommand
    {
        get => (IfxCommand?)base.InsertCommand;
        set => base.InsertCommand = value;
    }

    public new IfxCommand? UpdateCommand
    {
        get => (IfxCommand?)base.UpdateCommand;
        set => base.UpdateCommand = value;
    }

    public new IfxCommand? DeleteCommand
    {
        get => (IfxCommand?)base.DeleteCommand;
        set => base.DeleteCommand = value;
    }

    /// <summary>Raised after a row has been sent to the server during <see cref="DbDataAdapter.Update(DataSet)"/>.</summary>
    public event EventHandler<IfxRowUpdatedEventArgs>? RowUpdated;

    /// <summary>Raised before a row is sent to the server during <see cref="DbDataAdapter.Update(DataSet)"/>.</summary>
    public event EventHandler<IfxRowUpdatingEventArgs>? RowUpdating;

    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(
        DataRow dataRow, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping) =>
        new IfxRowUpdatedEventArgs(dataRow, command, statementType, tableMapping);

    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(
        DataRow dataRow, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping) =>
        new IfxRowUpdatingEventArgs(dataRow, command, statementType, tableMapping);

    protected override void OnRowUpdated(RowUpdatedEventArgs value)
    {
        if (value is IfxRowUpdatedEventArgs e)
            RowUpdated?.Invoke(this, e);
    }

    protected override void OnRowUpdating(RowUpdatingEventArgs value)
    {
        if (value is IfxRowUpdatingEventArgs e)
            RowUpdating?.Invoke(this, e);
    }

    /// <summary>
    /// Creates a new <see cref="IfxDataAdapter"/> that is a copy of the current instance:
    /// same DataAdapter-level settings, same command instances (not clones -- see the copy
    /// constructor's remarks), and no <see cref="RowUpdated"/>/<see cref="RowUpdating"/>
    /// subscribers (they live in this instance's own private event fields, never touched
    /// by the copy).
    /// </summary>
    public IfxDataAdapter Clone() => new(this);

    object ICloneable.Clone() => Clone();
}

/// <summary>Provides data for the <see cref="IfxDataAdapter.RowUpdated"/> event.</summary>
public sealed class IfxRowUpdatedEventArgs(DataRow row, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping)
    : RowUpdatedEventArgs(row, command, statementType, tableMapping)
{
    /// <summary>The <see cref="IfxCommand"/> executed for the update.</summary>
    public new IfxCommand? Command => (IfxCommand?)base.Command;
}

/// <summary>Provides data for the <see cref="IfxDataAdapter.RowUpdating"/> event.</summary>
public sealed class IfxRowUpdatingEventArgs(DataRow row, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping)
    : RowUpdatingEventArgs(row, command, statementType, tableMapping)
{
    /// <summary>The <see cref="IfxCommand"/> to execute for the update.</summary>
    public new IfxCommand? Command
    {
        get => (IfxCommand?)base.Command;
        set => base.Command = value;
    }
}
