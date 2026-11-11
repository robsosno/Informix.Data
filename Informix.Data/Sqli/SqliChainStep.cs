namespace Informix.Data.Sqli;

/// <summary>Which wire segment <see cref="SqliChainStep"/> writes.</summary>
internal enum SqliChainStepKind
{
    /// <summary>A parameterless statement: SQ_COMMAND+NDESCRIBE+EXECUTE+RELEASE (port of
    /// <see cref="SqliSession.ExecuteImmediate"/>'s wire shape, chainable per probe P1).</summary>
    Immediate,

    /// <summary>A bind+execute against an already-prepared statement: SQ_ID+[BIND]+EXECUTE
    /// (port of <see cref="SqliSession.ExecuteStatement"/>'s wire shape, chainable per probe P2).</summary>
    Execute,
}

/// <summary>
/// One statement to run within <see cref="SqliSession.ExecuteChain"/> — already fully
/// resolved by the caller (which wire path, which statement id, which encoded binds).
/// </summary>
internal readonly struct SqliChainStep
{
    public required SqliChainStepKind Kind { get; init; }

    /// <summary>The statement text — <see cref="SqliChainStepKind.Immediate"/> only.</summary>
    public string? Sql { get; init; }

    /// <summary>The already-prepared statement id to target — <see cref="SqliChainStepKind.Execute"/> only.</summary>
    public short StatementId { get; init; }

    /// <summary>The encoded input parameters — <see cref="SqliChainStepKind.Execute"/> only; empty for none.</summary>
    public IReadOnlyList<SqliBindValue> Binds { get; init; }
}

/// <summary>One chained step's outcome: the affected-row count plus any serial values
/// its SQ_DONE/SQ_INSERTDONE reply carried.</summary>
internal readonly struct SqliChainStepResult
{
    public int RowsProcessed { get; init; }
    public int SerialInserted { get; init; }
    public long Serial8Inserted { get; init; }
    public long BigSerialInserted { get; init; }
}

/// <summary>
/// The result of <see cref="SqliSession.ExecuteChain"/>: one <see cref="SqliChainStepResult"/>
/// per step that actually ran (null for a step the server never got to — see
/// <see cref="FailedIndex"/>), plus the first failure, if any.
///
/// <para>Probe P4 (InformixDataDbBatchPrompt.md §2) showed the server aborts a chained PDU
/// fail-fast: once one statement in it errors, no later statement already queued in that
/// same PDU runs — its reply simply never arrives. <see cref="FailedIndex"/> is the (0-based,
/// whole-chain) index of the statement whose error was observed; every step from there
/// onward — including ones in PDUs this call never even got to send — has a null result.</para>
/// </summary>
internal sealed class SqliChainOutcome
{
    public required IReadOnlyList<SqliChainStepResult?> Results { get; init; }

    public int FailedIndex { get; init; } = -1;

    public IfxException? Error { get; init; }
}
