using System.ComponentModel;
using System.Data;
using System.Data.Common;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Generates single-table INSERT/UPDATE/DELETE commands for an
/// <see cref="IfxDataAdapter"/> from its <see cref="IfxDataAdapter.SelectCommand"/>,
/// and derives the parameters of a stored routine from the system catalogs.
/// </summary>
public sealed class IfxCommandBuilder : DbCommandBuilder
{
    public IfxCommandBuilder()
    {
        // Informix delimited identifiers use double quotes (when DELIMIDENT is on).
        QuotePrefix = "\"";
        QuoteSuffix = "\"";
    }

    public IfxCommandBuilder(IfxDataAdapter adapter) : this() => DataAdapter = adapter;

    public new IfxDataAdapter? DataAdapter
    {
        get => (IfxDataAdapter?)base.DataAdapter;
        set => base.DataAdapter = value;
    }

    public new IfxCommand GetInsertCommand() => (IfxCommand)base.GetInsertCommand();
    public new IfxCommand GetInsertCommand(bool useColumnsForParameterNames) => (IfxCommand)base.GetInsertCommand(useColumnsForParameterNames);
    public new IfxCommand GetUpdateCommand() => (IfxCommand)base.GetUpdateCommand();
    public new IfxCommand GetUpdateCommand(bool useColumnsForParameterNames) => (IfxCommand)base.GetUpdateCommand(useColumnsForParameterNames);
    public new IfxCommand GetDeleteCommand() => (IfxCommand)base.GetDeleteCommand();
    public new IfxCommand GetDeleteCommand(bool useColumnsForParameterNames) => (IfxCommand)base.GetDeleteCommand(useColumnsForParameterNames);

    // Informix binds parameters positionally by '?' placeholder order; the parameter
    // names are for the builder's own bookkeeping only.
    protected override string GetParameterName(int parameterOrdinal) => $"p{parameterOrdinal}";
    protected override string GetParameterName(string parameterName) => parameterName;
    protected override string GetParameterPlaceholder(int parameterOrdinal) => "?";

    protected override void ApplyParameterInfo(DbParameter parameter, DataRow row, StatementType statementType, bool whereClause)
    {
        // IfxParameter infers the wire type from the value, and Informix ignores the sent
        // precision for fixed-width types. The one thing the value cannot carry is a DATETIME
        // column's qualifier: a TimeOnly read from DATETIME MINUTE TO SECOND would bind as
        // HOUR TO SECOND, which the server extends with the current hour and never matches in
        // the optimistic-concurrency WHERE (and a DateTime at midnight would bind as a DATE).
        if (parameter is IfxParameter p
            && row.Table.Columns.Contains(IfxDataReader.StartTimeUnitColumn)
            && row.Table.Columns.Contains(IfxDataReader.EndTimeUnitColumn)
            && row[SchemaTableColumn.ProviderType] is int providerType
            && (IfxType)providerType == IfxType.DateTime
            && row[IfxDataReader.StartTimeUnitColumn] is IfxTimeUnit start
            && row[IfxDataReader.EndTimeUnitColumn] is IfxTimeUnit end)
        {
            p.ColumnDateTimeQualifier = IfxQualifier.ForDateTime(start, end);
        }
    }

    protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
    {
        var ifxAdapter = (IfxDataAdapter)adapter;
        if (ReferenceEquals(adapter, base.DataAdapter))
            ifxAdapter.RowUpdating -= RowUpdatingHandler;
        else
            ifxAdapter.RowUpdating += RowUpdatingHandler;
    }

    private void RowUpdatingHandler(object? sender, IfxRowUpdatingEventArgs e) => base.RowUpdatingHandler(e);

    // Wraps the name in double quotes and doubles any embedded quote.
    public override string QuoteIdentifier(string unquotedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(unquotedIdentifier);
        return QuotePrefix + unquotedIdentifier.Replace(QuoteSuffix, QuoteSuffix + QuoteSuffix) + QuoteSuffix;
    }

    // DbCommandBuilder quotes the names it writes into INSERT / UPDATE / DELETE with QuotePrefix and
    // QuoteSuffix. Without DELIMIDENT (connection keyword "Delimited Identifiers") a double-quoted word
    // is a string literal and the generated commands fail with -201, so on such a connection the default
    // quote characters are dropped just before the commands are derived. Quote characters the caller set
    // themselves are left alone.
    protected override DataTable? GetSchemaTable(DbCommand sourceCommand)
    {
        if (sourceCommand.Connection is IfxConnection { DelimitedIdentifiersEnabled: false } &&
            QuotePrefix == "\"" && QuoteSuffix == "\"")
        {
            QuotePrefix = "";
            QuoteSuffix = "";
        }
        return base.GetSchemaTable(sourceCommand);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public string QuoteIdentifier(string unquotedIdentifier, IfxConnection connection) => QuoteIdentifier(unquotedIdentifier);

    public override string UnquoteIdentifier(string quotedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(quotedIdentifier);
        string s = quotedIdentifier;
        if (QuotePrefix.Length > 0 && QuoteSuffix.Length > 0 &&
            s.StartsWith(QuotePrefix, StringComparison.Ordinal) && s.EndsWith(QuoteSuffix, StringComparison.Ordinal) &&
            s.Length >= QuotePrefix.Length + QuoteSuffix.Length)
        {
            s = s[QuotePrefix.Length..^QuoteSuffix.Length].Replace(QuoteSuffix + QuoteSuffix, QuoteSuffix);
        }
        return s;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public string UnquoteIdentifier(string quotedIdentifier, IfxConnection connection) => UnquoteIdentifier(quotedIdentifier);

    /// <summary>
    /// Populates <paramref name="command"/>'s parameter collection from the system
    /// catalogs (<c>sysprocedures</c>/<c>sysproccolumns</c>) for a
    /// <see cref="CommandType.StoredProcedure"/> command. Best-effort: the parameter
    /// direction is read from <c>sysproccolumns.paramattr</c>; each value the routine RETURNs
    /// becomes a <see cref="ParameterDirection.ReturnValue"/> parameter, ahead of the
    /// arguments, in the order the server declares them.
    /// </summary>
    /// <remarks>
    /// The command text is the routine name, optionally qualified as <c>owner.name</c>. The
    /// routine is identified by its <c>procid</c>, so the parameters of two overloads are never
    /// mixed; when the name matches more than one routine this throws
    /// <see cref="InvalidOperationException"/> listing each candidate's <c>procid</c>, and the
    /// overload must be chosen with <see cref="DeriveParameters(IfxCommand, int)"/>.
    /// </remarks>
    public void DeriveParameters(IfxCommand command) => DeriveParameters(command, null);

    /// <summary>
    /// <see cref="DeriveParameters(IfxCommand)"/> for the overload whose
    /// <c>sysprocedures.procid</c> is <paramref name="procId"/>, as listed by the exception that
    /// call throws for an overloaded name. The id must belong to a routine the command text
    /// names (and owns, when it is qualified); a procid is not stable across a DROP and re-CREATE.
    /// </summary>
    public void DeriveParameters(IfxCommand command, int procId) => DeriveParameters(command, (int?)procId);

    private void DeriveParameters(IfxCommand command, int? procId)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandType != CommandType.StoredProcedure)
            throw new InvalidOperationException("DeriveParameters requires CommandType.StoredProcedure.");
        if (command.Connection is not { } connection || connection.State != ConnectionState.Open)
            throw new InvalidOperationException("DeriveParameters requires an open connection.");

        var (owner, name) = SplitRoutineName(command.CommandText.Trim());
        var derived = new List<IfxParameter>();
        if (FindRoutine(connection, owner, name, procId) is { } routine)
            ReadParameters(connection, routine.ProcId, derived);

        command.Parameters.Clear();
        foreach (var p in derived)
            command.Parameters.Add(p);
    }

    private readonly record struct Routine(int ProcId, string Owner, int NumArgs);

    // "owner.name", either part optionally double-quoted (a dot inside quotes is part of the name).
    private (string? Owner, string Name) SplitRoutineName(string text)
    {
        bool quoted = false;
        int dot = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == '.' && !quoted) dot = i;
        }
        if (dot < 0)
            return (null, UnquoteIdentifier(text));
        string owner = UnquoteIdentifier(text[..dot].Trim());
        return (owner.Length > 0 ? owner : null, UnquoteIdentifier(text[(dot + 1)..].Trim()));
    }

    // The routine's procid is the only identifier that names one overload. The owner is not
    // used to tell overloads apart: it narrows the lookup only when the caller supplied it, and
    // two owners' routines of the same name are reported as the ambiguity they are.
    private static Routine? FindRoutine(IfxConnection connection, string? owner, string name, int? procId)
    {
        using var lookup = connection.CreateInternalCommand();
        string sql = "SELECT procid, owner, numargs FROM sysprocedures WHERE procname = ?";
        lookup.Parameters.Add(new IfxParameter("procname", name));
        if (owner is not null)
        {
            sql += " AND owner = ?";
            lookup.Parameters.Add(new IfxParameter("owner", owner));
        }
        if (procId is { } id)
        {
            sql += " AND procid = ?";
            lookup.Parameters.Add(new IfxParameter("procid", id));
        }
        lookup.CommandText = sql + " ORDER BY procid";

        var found = new List<Routine>();
        using (var reader = lookup.ExecuteReader())
        {
            while (reader.Read())
            {
                found.Add(new Routine(
                    Convert.ToInt32(reader.GetValue(0)),
                    reader.GetString(1).Trim(),
                    reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2))));
            }
        }

        if (found.Count == 1)
            return found[0];
        if (found.Count == 0)
        {
            // An unknown name derives nothing; a procid that names no such routine is a caller error.
            return procId is null ? null : throw new InvalidOperationException(
                $"DeriveParameters found no routine named '{name}' with procid {procId}.");
        }

        string candidates = string.Join("; ", found.Select(r =>
            $"procid {r.ProcId} = {r.Owner}.{name} ({r.NumArgs} argument(s))"));
        throw new InvalidOperationException(
            $"DeriveParameters cannot choose between {found.Count} routines named '{name}': {candidates}. " +
            "Pass the intended routine's procid to DeriveParameters(command, procId), " +
            "or qualify the command text with its owner.");
    }

    private static void ReadParameters(IfxConnection connection, int procId, List<IfxParameter> derived)
    {
        using var catalog = connection.CreateInternalCommand();
        catalog.CommandText = "SELECT paramname, paramattr FROM sysproccolumns WHERE procid = ? ORDER BY paramid";
        catalog.Parameters.Add(new IfxParameter("procid", procId));

        int returnValues = 0;
        using var reader = catalog.ExecuteReader();
        while (reader.Read())
        {
            int attr = reader.IsDBNull(1) ? AttrIn : Convert.ToInt32(reader.GetValue(1));
            ParameterDirection direction = DirectionFromAttr(attr);

            string? name = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
            if (string.IsNullOrEmpty(name))
            {
                // RETURNING values have no name in the catalog.
                name = direction != ParameterDirection.ReturnValue ? $"p{derived.Count + 1}"
                    : ++returnValues == 1 ? "RETURN_VALUE" : $"RETURN_VALUE{returnValues}";
            }
            derived.Add(new IfxParameter { ParameterName = name, Direction = direction });
        }
    }

    // sysproccolumns.paramattr is an enumeration, not a set of flags: it carries ODBC's
    // SQLProcedureColumns COLUMN_TYPE numbering (live-verified, see
    // IfxSchema.BuildProcedureColumns). Both kinds of returned value map to ReturnValue:
    // RETURNed values, in order, to the command's ReturnValue parameters.
    private const int AttrIn = 1;           // SQL_PARAM_INPUT
    private const int AttrInOut = 2;        // SQL_PARAM_INPUT_OUTPUT
    private const int AttrResultColumn = 3; // SQL_RESULT_COL: a RETURNING value
    private const int AttrOut = 4;          // SQL_PARAM_OUTPUT
    private const int AttrReturnValue = 5;  // SQL_RETURN_VALUE

    private static ParameterDirection DirectionFromAttr(int attr) => attr switch
    {
        AttrInOut => ParameterDirection.InputOutput,
        AttrOut => ParameterDirection.Output,
        AttrResultColumn or AttrReturnValue => ParameterDirection.ReturnValue,
        _ => ParameterDirection.Input,
    };
}
