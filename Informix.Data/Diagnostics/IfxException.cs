using System.Data.Common;

namespace Informix.Data;

/// <summary>
/// The exception thrown for all Informix provider errors; the idiomatic .NET base
/// is <see cref="DbException"/>.
/// </summary>
// Not sealed: internal subclasses (AsfRemoteException, AsfRedirectException) extend this.
public class IfxException : DbException
{
    /// <summary>The Informix SQLCODE (negative for errors), e.g. -908, -408.</summary>
    public int IfxSqlCode { get; }

    /// <summary>The underlying ISAM/OS error code when the server supplied one; 0 otherwise.</summary>
    public int IsamCode { get; }

    /// <summary>The five-character SQLSTATE, when known.</summary>
    public override string? SqlState { get; }

    public IfxException(string message, int sqlCode = 0, int isamCode = 0, string? sqlState = null, Exception? inner = null)
        : base(message, inner)
    {
        IfxSqlCode = sqlCode;
        IsamCode = isamCode;
        SqlState = sqlState;
    }

    /// <summary><see cref="System.Runtime.InteropServices.ExternalException.ErrorCode"/> surfaces the Informix SQLCODE.</summary>
    public override int ErrorCode => IfxSqlCode;

    /// <summary>
    /// True when retrying the failed operation without changing anything is plausible:
    /// lock/deadlock conflicts, lock-wait timeouts, statement interrupt (a
    /// <see cref="IfxCommand.CommandTimeout"/> expiry), and connectivity loss (SQLCODE
    /// -908 covers every connect/IO-failure path this driver synthesizes, including
    /// pool-wait timeouts). Consulted by retry frameworks (EF Core execution strategies,
    /// Polly).
    /// </summary>
    /// <remarks>
    /// Checked against both <see cref="IfxSqlCode"/> and <see cref="IsamCode"/>: a few
    /// ambiguous primary SQLCODEs (row-positioning/read failures) are only a transient
    /// lock conflict when their ISAM companion code says so, and some ISAM lock codes
    /// surface as the primary SQLCODE on their own.
    /// </remarks>
    public override bool IsTransient => IsTransientCode(IfxSqlCode) || IsTransientCode(IsamCode);

    // Transient (retry is plausible): -908 attempt to connect failed (every network-failure
    // path in this driver, incl. pool-wait timeout); -213 statement interrupted (this
    // driver's CommandTimeout OOB cancel); -233/-263/-289/-347/-378 and the ISAM companions
    // -106/-107/-113/-134/-143/-144/-154 are all lock/deadlock/timeout conflicts.
    //
    // Deliberately NOT transient (do not add): -243, -244, -245, -246 
    // (positioning/read failures -- transient only when the ISAM companion is a
    // lock code, which the IsamCode check above already covers, so the primary codes stay
    // out).
    private static bool IsTransientCode(int code) => code switch
    {
        -908 or -213
            or -233 or -263 or -289 or -347 or -378
            or -106 or -107 or -113 or -134 or -143 or -144 or -154 => true,
        _ => false,
    };

    /// <summary>
    /// The message text for an error whose reply carried none, given IBM's own wording
    /// for the SQLCODE where this driver knows it.
    /// </summary>
    /// <remarks>
    /// Informix keeps the text of most SQLCODEs in a client-side message catalog and puts
    /// only the substitution parameter on the wire — a missing table comes back as
    /// SQLCODE -206 whose whole message is the table's name, and the sentence around it
    /// ("The specified table ... is not in the database") never leaves the client. Some
    /// codes send nothing at all, and those used to surface as a bare
    /// "Informix error -9636.".
    /// <para>
    /// This is not a catalog and is not meant to grow into one: it covers the codes this
    /// driver has actually been observed to raise with an empty message, quoting IBM's
    /// published text so the wording matches what <c>finderr</c> and every other Informix
    /// client show. The code itself is not repeated in the text — server-supplied messages
    /// never carry it either, and it is already on <see cref="IfxSqlCode"/>.
    /// </para>
    /// </remarks>
    internal static string TextForEmptyMessage(int sqlCode) => sqlCode switch
    {
        // Raised by the server when an opaque type's value outgrows its declared maximum
        // — live-observed from binaryvar past 253 bytes, with an empty message field.
        -9636 => "Opaque type exceeded its maximum length.",
        _ => $"Informix error {sqlCode}.",
    };

    // The IfxBatchCommand whose execution raised this error, when it happened partway
    // through an IfxBatch — set by IfxBatch so DbException.BatchCommand can tell the
    // caller which batch entry failed. Null for a non-batch execution.
    private DbBatchCommand? _batchCommand;

    /// <inheritdoc/>
    protected override DbBatchCommand? DbBatchCommand => _batchCommand;

    /// <summary>Attaches the failing <see cref="IfxBatchCommand"/> — called by
    /// <see cref="IfxBatch"/> when an execution within it fails.</summary>
    internal void SetBatchCommand(IfxBatchCommand command) => _batchCommand = command;

    private IfxErrorCollection? _errors;

    /// <summary>
    /// The server diagnostic records for this error: the primary error, plus a
    /// second entry mirroring the ISAM/OS diagnostic when the server supplied one
    /// (matching the official provider's one-entry-per-diagnostic-record habit).
    /// </summary>
    public IfxErrorCollection Errors => _errors ??= BuildErrors();

    private IfxErrorCollection BuildErrors()
    {
        var errors = new IfxErrorCollection();
        errors.Add(new IfxError(Message, IfxSqlCode, Source ?? "Informix.Data", SqlState));
        if (IsamCode != 0)
            errors.Add(new IfxError($"ISAM error {IsamCode}.", IsamCode, Source ?? "Informix.Data", SqlState));
        return errors;
    }
}
