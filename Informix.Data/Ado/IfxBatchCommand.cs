using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// One statement within an <see cref="IfxBatch"/> (<see cref="DbBatchCommand"/>).
/// Shares its parameter-binding and <see cref="CommandType.StoredProcedure"/>
/// text-building rules with <see cref="IfxCommand"/> (see
/// <see cref="IfxParameterBinder"/>/<see cref="IfxProcedureText"/>), so a command
/// moved between a standalone <see cref="IfxCommand"/> and a batch entry behaves
/// identically.
/// </summary>
public sealed class IfxBatchCommand : DbBatchCommand
{
    private readonly IfxParameterCollection _parameters = new();
    private string _commandText = string.Empty;
    private CommandType _commandType = CommandType.Text;
    private IfxParsedCommand? _parsedCommand;

    public IfxBatchCommand() { }

    public IfxBatchCommand(string? commandText) => CommandText = commandText ?? string.Empty;

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            string newText = value ?? string.Empty;
            if (_commandText == newText)
                return;
            _commandText = newText;
            _parsedCommand = null; // invalidate the cached named-placeholder parse
        }
    }

    /// <summary>
    /// The kind of statement this batch entry runs. <see cref="CommandType.TableDirect"/>
    /// is not meaningful for Informix (there is no bare-table-name statement form) and
    /// throws <see cref="NotSupportedException"/>.
    /// </summary>
    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value == CommandType.TableDirect)
                throw new NotSupportedException("IfxBatchCommand does not support CommandType.TableDirect.");
            _commandType = value;
        }
    }

    private int _recordsAffected = -1;

    /// <summary>
    /// The number of rows this command affected, or the one row a
    /// <see cref="CommandType.StoredProcedure"/> RETURNING routine's output describes,
    /// filled in after <see cref="IfxBatch.ExecuteNonQuery"/>/<see cref="IfxBatch.ExecuteReader"/>
    /// runs it. <c>-1</c> before execution, and for a command whose result is a plain
    /// result set (a SELECT read through the batch's spanning reader).
    /// </summary>
    public override int RecordsAffected => _recordsAffected;

    /// <summary>Sets <see cref="RecordsAffected"/> — called by <see cref="IfxBatch"/> once
    /// this command has run.</summary>
    internal void SetRecordsAffected(int value) => _recordsAffected = value;

    public new IfxParameterCollection Parameters => _parameters;

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public override bool CanCreateParameter => true;

    public override IfxParameter CreateParameter() => new();

    // The (cached) parse of CommandText for named placeholders — mirrors IfxCommand's
    // ParsedCommand. Only consulted for CommandType.Text.
    internal IfxParsedCommand ParsedCommand => _parsedCommand ??= IfxSqlParser.Parse(_commandText);

    /// <summary>
    /// The SQL actually sent for this command: <see cref="CommandText"/> with any named
    /// placeholders rewritten to positional <c>?</c> for <see cref="CommandType.Text"/>,
    /// or the <c>execute procedure name(?, ...)</c> wrapping for
    /// <see cref="CommandType.StoredProcedure"/> (see <see cref="IfxProcedureText"/>).
    /// </summary>
    internal string EffectiveCommandText()
    {
        if (CommandType == CommandType.Text)
            return ParsedCommand.Text;
        if (CommandType != CommandType.StoredProcedure)
            return CommandText;

        int inputs = 0;
        foreach (IfxParameter p in _parameters)
            if (p.Direction is ParameterDirection.Input or ParameterDirection.InputOutput)
                inputs++;
        return IfxProcedureText.Build(CommandText, inputs);
    }

    // The bind count Prepare() needs (SQ_PREPARE's parameter-count word) without encoding
    // any parameter values — unlike BuildBinds, which does real work per parameter (e.g.
    // uploading a smart-LOB parameter's value right away), something a pre-warming
    // Prepare() must not trigger. Mirrors IfxCommand.CountInputParameters.
    internal int CountInputParameters()
    {
        if (CommandType == CommandType.Text)
        {
            if (ParsedCommand.IsNamed)
                return ParsedCommand.ParameterNames.Count;
            // Caller-written '?' placeholders: every parameter backs one, whatever its direction
            // (bar the "{? = call}" escape's leading '?') -- the count BuildBinds will produce.
            return ParsedCommand.HasReturnValuePlaceholder ? _parameters.Count - 1 : _parameters.Count;
        }
        int count = 0;
        foreach (IfxParameter p in _parameters)
            if (p.Direction is not (ParameterDirection.Output or ParameterDirection.ReturnValue))
                count++;
        return count;
    }

    /// <summary>The input parameters encoded for SQ_BIND, in placeholder order.</summary>
    internal List<SqliBindValue> BuildBinds(SqliSession session) =>
        IfxParameterBinder.Build(session, CommandType, CommandType == CommandType.Text ? ParsedCommand : null, _parameters);

    public override string ToString() => CommandText;
}
