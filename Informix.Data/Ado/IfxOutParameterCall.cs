using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Runs an SPL routine that declares <c>OUT</c>/<c>INOUT</c> parameters, and writes the
/// values it produces back into the command's parameters.
///
/// <para>Such a routine cannot go through the ordinary prepare/execute path at all: the
/// server rejects the SQ_PREPARE with sqlcode
/// <see cref="SqliSession.RoutineHasOutParametersSqlCode"/> (-9752) and, instead of a
/// statement, hands back the routine's <c>db:handle:unique</c> identity. The call then
/// re-drives over the FastPath channel — SQ_GETROUTINE (asking for the routine's SQ_FPARAM
/// descriptor, which is what says <em>which</em> arguments are written back) followed by
/// SQ_EXFPROUTINE with every declared argument bound. This is the port of the
/// <c>hasOutParameter</c> path in <c>IfxCallableStatement</c>.</para>
///
/// <para>The SQ_FPROUTINE reply carries the OUT/INOUT argument values first — in the
/// order the descriptor's <see cref="SqliFParam.OutParameterSequence"/> gives — and then
/// the routine's RETURNed values. Informix allows only one such reply per execution, so a
/// routine with OUT parameters can never also stream a result set.</para>
/// </summary>
internal static class IfxOutParameterCall
{
    // The server's own code for a call whose argument count the routine cannot accept
    // ("Routine (%s) can not be resolved"). The server normally draws it at prepare, before
    // this class runs; it is raised here only as a guard against more arguments than declared.
    private const int RoutineNotResolvedSqlCode = -674;

    // IBM JDBC's "IN parameter has not been set": an argument left out that has no DEFAULT to fall back on.
    private const int OmittedArgumentSqlCode = -79823;

    // IBM JDBC's code for an OUT parameter whose type cannot be inferred ("Ambiguous
    // java.sql.Type, use IfxRegisterOutParameter()"), which is what an Output parameter
    // with neither an IfxType nor a value amounts to.
    private const int AmbiguousOutParameterTypeSqlCode = -79826;

    /// <summary>
    /// Executes <paramref name="routine"/> and assigns its OUT/INOUT and RETURNed values
    /// to <paramref name="parameters"/>. Returns the number of rows to report — always 1,
    /// the single set of values the routine produced.
    /// </summary>
    /// <param name="session">The session whose SQ_PREPARE was just rejected with -9752.</param>
    /// <param name="routine">The routine identity carried by that rejection.</param>
    /// <param name="parameters">
    /// The command's parameters. Everything except a <see cref="ParameterDirection.ReturnValue"/>
    /// parameter is a declared routine argument, in collection order.
    /// </param>
    /// <param name="returnValueIsFirst">
    /// Set for the ODBC <c>{? = call ...}</c> escape, whose leading <c>?</c> makes the first
    /// parameter the routine's return value positionally — whatever direction it carries.
    /// </param>
    /// <param name="described">
    /// A descriptor already fetched for this routine on this session, to reuse instead of
    /// asking again; <see langword="null"/> to fetch one. Returned in
    /// <paramref name="described"/> so the caller can keep it.
    /// </param>
    public static int Execute(
        SqliSession session, SqliOutParameterRoutine routine, IfxParameterCollection parameters,
        bool returnValueIsFirst, ref SqliFParam? described)
    {
        var arguments = new List<IfxParameter>(parameters.Count);
        var returnValues = new List<IfxParameter>();
        bool first = true;
        foreach (IfxParameter p in parameters)
        {
            if (first && returnValueIsFirst)
            {
                first = false;
                returnValues.Add(p);
                continue;
            }
            first = false;
            if (p.Direction == ParameterDirection.ReturnValue)
                returnValues.Add(p);
            else
                arguments.Add(p);
        }

        SqliFParam fparam = described ?? session.DescribeOutParameterRoutine(routine);
        described = fparam;

        // Fewer arguments than declared is legal when the omitted ones have DEFAULTs (IBM JDBC
        // sends a default marker for each; live-verified). Likewise a routine may
        // RETURN more values than the command has ReturnValue parameters for; the extras are
        // simply not delivered. More arguments than declared cannot be bound at all.
        if (arguments.Count > fparam.ArgumentCount)
            throw new IfxException(
                $"The routine declares {fparam.ArgumentCount} argument(s) but the command supplies " +
                $"{arguments.Count} (a ParameterDirection.ReturnValue parameter is not an argument).",
                RoutineNotResolvedSqlCode);

        var binds = new List<SqliBindValue>(fparam.ArgumentCount);
        for (int i = 0; i < arguments.Count; i++)
            binds.Add(BuildArgumentBind(session, arguments[i], fparam.ArgumentModes[i]));
        // The server is told about every declared argument: one the caller left out must have a
        // DEFAULT (IBM JDBC's rule, with the same -79823 when it does not) and is sent as such.
        for (int i = arguments.Count; i < fparam.ArgumentCount; i++)
        {
            if ((fparam.ArgumentFlags[i] & SqliFParam.FlagHasDefault) == 0)
                throw new IfxException(
                    $"Argument {i + 1} of the routine was not supplied and declares no DEFAULT value ({OmittedArgumentSqlCode}).",
                    OmittedArgumentSqlCode);
            binds.Add(SqliBindValue.DefaultValue(fparam.ArgumentTypes[i]));
        }

        IReadOnlyList<object?> values = session.ExecuteOutParameterRoutine(routine, fparam, binds);

        // OUT/INOUT argument values come first, in descriptor order, then the RETURNed values.
        int count = Math.Min(fparam.OutParameterCount, values.Count);
        for (int i = 0; i < count; i++)
        {
            // An OUT argument the caller left out (a trailing DEFAULT) has nowhere to land.
            int slot = fparam.OutParameterSequence[i];
            if (slot < arguments.Count)
                arguments[slot].Value = values[i];
        }
        for (int i = 0; i < returnValues.Count && fparam.OutParameterCount + i < values.Count; i++)
            returnValues[i].Value = values[fparam.OutParameterCount + i];

        return 1;
    }

    // One declared argument's SQ_BIND value. An argument the routine only writes to still
    // occupies a bind slot: it travels as a NULL that names its type, so the server knows
    // the shape of the slot it will fill. An INOUT argument (or an OUT one the caller
    // pre-seeded) binds its value normally.
    private static SqliBindValue BuildArgumentBind(SqliSession session, IfxParameter p, short mode)
    {
        bool hasValue = p.Value is not (null or DBNull);
        if (mode == SqliFParam.ModeOut && !hasValue)
            return TypedNullFor(p);
        return IfxParameterBinder.EncodeBind(session, p);
    }

    // The type word a value-less OUT argument binds under. IfxType.Null (the unset default)
    // leaves the driver nothing to declare, so the caller must pin the parameter's type.
    private static SqliBindValue TypedNullFor(IfxParameter p)
    {
        switch (p.IfxType)
        {
            case IfxType.Clob:
                return SqliBindValue.SmartBlobLocator(null, isClob: true);
            case IfxType.Blob:
                return SqliBindValue.SmartBlobLocator(null, isClob: false);
            case IfxType.Json:
            case IfxType.Bson:
                return SqliBindValue.TypedNull(
                    SqliTypeCode.SendRecv, p.IfxType == IfxType.Bson ? "bson" : "json", "informix");
            case IfxType.BinaryVar:
            case IfxType.Binary18:
                return SqliBindValue.TypedNull(
                    SqliTypeCode.SendRecv, p.IfxType == IfxType.Binary18 ? "binary18" : "binaryvar", "informix");
        }

        SqliTypeCode wire = IfxTypeMap.ToWire(p.IfxType);
        if (wire is SqliTypeCode.Unknown or SqliTypeCode.Null)
            throw new IfxException(
                $"Output parameter '{p.ParameterName}' has no value and no IfxType, so the driver cannot " +
                "tell the server what type of value to write back. Set IfxType (or seed Value) on every " +
                $"ParameterDirection.Output parameter ({AmbiguousOutParameterTypeSqlCode}).",
                AmbiguousOutParameterTypeSqlCode);
        return SqliBindValue.TypedNull(wire);
    }
}
