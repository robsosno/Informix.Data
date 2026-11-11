namespace Informix.Data;

/// <summary>
/// Stable event-id numbers for the structured log messages Informix.Data emits
/// (via <see cref="IfxLoggingConfiguration"/>). Grouped by logger category —
/// connection (1xxx), command (2xxx) and transaction (3xxx)
/// so log sinks can filter by a well-known id.
/// </summary>
public static class IfxEventId
{
    // ---- connection (category "Informix.Data.Connection") ----

    /// <summary>A logical connection is opening (renting a physical connection).</summary>
    public const int OpeningConnection = 1000;
    /// <summary>A logical connection finished opening.</summary>
    public const int OpenedConnection = 1001;
    /// <summary>A logical connection is closing (returning its physical connection).</summary>
    public const int ClosingConnection = 1002;
    /// <summary>A logical connection finished closing.</summary>
    public const int ClosedConnection = 1003;

    /// <summary>A new physical connection (socket + SQLI bring-up) was established.</summary>
    public const int OpenedPhysicalConnection = 1100;
    /// <summary>A physical connection's socket was torn down.</summary>
    public const int ClosedPhysicalConnection = 1101;
    /// <summary>A physical connection was retired rather than reused (see the reason).</summary>
    public const int PhysicalConnectionRetired = 1102;

    /// <summary>A low-severity server diagnostic (warning) was surfaced via <c>InfoMessage</c>.</summary>
    public const int InfoMessageReceived = 1200;
    /// <summary>The out-of-band cancel byte was sent to interrupt a running statement.</summary>
    public const int SentOutOfBandCancel = 1201;

    /// <summary>A Min-Pool-Size warm-up connection could not be pre-created.</summary>
    public const int PoolWarmUpFailed = 1300;
    /// <summary>A rent timed out because Max Pool Size connections were already checked out.</summary>
    public const int PoolRentTimeout = 1301;

    // ---- command (category "Informix.Data.Command") ----

    /// <summary>A command is about to execute (carries the SQL text).</summary>
    public const int CommandExecuting = 2000;
    /// <summary>The bound parameter values for a command (only when parameter logging is enabled).</summary>
    public const int ParameterValues = 2001;
    /// <summary>A command finished executing (rows affected + duration).</summary>
    public const int CommandCompleted = 2002;
    /// <summary>A command failed with a server error (carries the sqlcode).</summary>
    public const int CommandFailed = 2003;

    // ---- transaction (category "Informix.Data.Transaction") ----

    /// <summary>A transaction began (BEGIN WORK).</summary>
    public const int TransactionBegan = 3000;
    /// <summary>A transaction committed.</summary>
    public const int TransactionCommitted = 3001;
    /// <summary>A transaction rolled back.</summary>
    public const int TransactionRolledBack = 3002;
}
