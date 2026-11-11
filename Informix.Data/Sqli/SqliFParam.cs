namespace Informix.Data.Sqli;

/// <summary>
/// The routine descriptor the server sends in an SQ_FPARAM reply (port of
/// <c>IfxFParam</c> + <c>IfxSqli.receiveFParam</c>). It is requested by asking
/// SQ_GETROUTINE for the "fparam" follow-up and describes the routine's
/// arguments and return values — crucially each argument's <em>mode</em>, which
/// is how the driver learns which arguments are SPL <c>OUT</c>/<c>INOUT</c>.
///
/// <para>The same object is echoed back to the server in the SQ_FPARAM segment
/// that closes an SQ_EXFPROUTINE request, so <see cref="FuncState"/>,
/// <see cref="Request"/> and <see cref="IsDone"/> must survive the round trip
/// verbatim.</para>
/// </summary>
internal sealed class SqliFParam
{
    /// <summary>Argument mode meaning "this argument is written back to the caller"
    /// (SPL <c>OUT</c> and <c>INOUT</c> both report it).</summary>
    public const short ModeOut = 1;

    /// <summary>Opaque server function state, echoed back verbatim in the request's SQ_FPARAM.</summary>
    public long FuncState { get; set; }

    /// <summary>Opaque request word, echoed back verbatim.</summary>
    public int Request { get; set; }

    /// <summary>Server-side completion flag; the request's SQ_FPARAM sends its inverse.</summary>
    public bool IsDone { get; set; }

    /// <summary>Number of values the routine RETURNs.</summary>
    public short ReturnCount { get; set; }

    /// <summary>Number of declared arguments.</summary>
    public short ArgumentCount { get; set; }

    /// <summary>Each argument's mode word, in declaration order (<see cref="ModeOut"/> = written back).</summary>
    public short[] ArgumentModes { get; set; } = [];

    /// <summary>Bit set in an argument's flag byte when the argument declares a DEFAULT value, so a
    /// caller may leave it out (<c>IfxResultSetMetaData.hasDefaultValue</c>). The flag byte is only
    /// sent by servers that support named parameters; elsewhere every flag reads 0.</summary>
    public const byte FlagHasDefault = 1;

    /// <summary>Each argument's flag byte, in declaration order (see <see cref="FlagHasDefault"/>).</summary>
    public byte[] ArgumentFlags { get; set; } = [];

    /// <summary>Each argument's type word as the descriptor reports it, in declaration order.</summary>
    public short[] ArgumentTypes { get; set; } = [];

    /// <summary>
    /// The zero-based argument indexes of the OUT/INOUT arguments, in the order their
    /// values appear at the front of the SQ_FPROUTINE reply.
    /// </summary>
    public int[] OutParameterSequence { get; set; } = [];

    /// <summary>How many arguments are OUT/INOUT (<c>numberOfOutParams</c>).</summary>
    public int OutParameterCount => OutParameterSequence.Length;

    /// <summary>
    /// Total values the SQ_FPROUTINE reply carries: the OUT/INOUT argument values first,
    /// then the RETURNed values (<c>totalOutValues</c>).
    /// </summary>
    public int TotalOutValues => OutParameterCount + ReturnCount;

    /// <summary>True when the routine declares at least one OUT/INOUT argument.</summary>
    public bool HasOutParameters => OutParameterCount > 0;

    /// <summary>
    /// Set once the full descriptor has been read. A later SQ_FPARAM for the same routine
    /// carries only the opaque state to refresh — <see cref="FuncState"/>,
    /// <see cref="Request"/> and <see cref="IsDone"/> — and stops there (the
    /// <c>partialFParam</c> flag in <c>IfxSqli.receiveFParam</c>).
    /// </summary>
    public bool IsDescribed { get; set; }
}

/// <summary>
/// The routine identity the server hands back with error -9752 ("this routine has OUT
/// parameters, prepare/execute cannot run it") as a <c>db:handle:unique</c> triple —
/// everything needed to re-drive the call over the FastPath channel instead.
/// </summary>
/// <param name="Database">The database the routine lives in.</param>
/// <param name="Handle">The routine's FastPath handle.</param>
/// <param name="Unique">Whether the routine name resolved to exactly one routine.</param>
internal readonly record struct SqliOutParameterRoutine(string Database, int Handle, bool Unique);
