namespace Informix.Data;

/// <summary>
/// The rules <see cref="IfxTextConvert"/> follows when it reads or writes a value as text:
/// the date and time formats (<c>DBDATE</c>, <c>GL_DATE</c>, <c>DBCENTURY</c>,
/// <c>GL_DATETIME</c>), the decimal and thousands separators, and the
/// <c>IFX_NOT_STRICT_THOUS_SEP</c> switch.
/// </summary>
/// <remarks>
/// <para>An <see cref="IfxTextConvert"/> overload that is not given a format uses
/// <see cref="Default"/> — the process environment is <b>not</b> consulted, so a conversion
/// never changes shape with the machine it runs on. To follow a connection's settings pass
/// <see cref="IfxConnection.TextFormat"/> (built from the <c>DB Date</c>, <c>GL Date</c>,
/// <c>DB Century</c>, <c>GL DateTime</c> and <c>Strict Thousands Separator</c> connection-string
/// keywords — the same <c>DBDATE</c> the server is told for that session).</para>
/// <para>Informix takes the decimal and thousands separators from the client locale
/// (<c>CLIENT_LOCALE</c> and its <c>.lc</c> files). This provider has no GLS locale files,
/// so they are plain properties here, defaulting to the <c>C</c>/US-English locale's
/// <c>.</c> and <c>,</c>.</para>
/// <para><b>The machine's regional settings are deliberately not consulted</b> — neither
/// <see cref="System.Globalization.CultureInfo.CurrentCulture"/> nor <c>CLIENT_LOCALE</c>. With
/// no Informix variable set the format is <c>MDY4/</c> with <c>.</c> and <c>,</c> whatever the
/// workstation says, which is what the client library does as well: on a Windows machine set to
/// Polish, <c>rdatestr</c> still writes <c>12/31/2008</c>. A file that is read back by
/// <c>dbload</c> or <c>LOAD</c> must not change shape with the machine that wrote it, and most
/// system patterns cannot be expressed here anyway — Polish and French use a non-breaking space
/// as the thousands separator, Hungarian short dates carry two different separators. Set the
/// properties when the file needs another shape; for text meant
/// for a screen rather than a file, .NET's own formatting already follows the operating
/// system.</para>
/// </remarks>
public sealed class IfxTextFormat
{
    /// <summary>The default rules: separators as in the C locale, no date variables set (so DATE text is <c>MDY4/</c>).</summary>
    public static IfxTextFormat Default { get; } = new();

    /// <summary><c>DBDATE</c> — the end-user DATE format, e.g. <c>"MDY4/"</c>, <c>"DMY2."</c>, <c>"Y4MD-"</c>.</summary>
    /// <remarks>Takes precedence over <see cref="GlDate"/>; when both are unset the format is <c>MDY4/</c>.</remarks>
    public string? DbDate { get; init; }

    /// <summary><c>GL_DATE</c> — a strftime-style DATE format, e.g. <c>"%d.%m.%Y"</c>. Used only when <see cref="DbDate"/> is unset.</summary>
    public string? GlDate { get; init; }

    /// <summary><c>DBCENTURY</c> — how a one- or two-digit year is expanded: <c>R</c> (present, the default), <c>P</c> (past), <c>F</c> (future) or <c>C</c> (closest).</summary>
    public string? DbCentury { get; init; }

    /// <summary><c>GL_DATETIME</c> — a strftime-style DATETIME format used by <see cref="IfxTextConvert.FormatDateTime"/> (<c>dttoasc</c>).</summary>
    /// <remarks>The C library consults this on output only; <c>dtcvasc</c> always parses the ANSI SQL form, which
    /// <see cref="IfxTextConvert.ParseDateTime"/> mirrors.</remarks>
    public string? GlDateTime { get; init; }

    /// <summary>The decimal separator (the client locale's in the C library; <c>'.'</c> here by default).</summary>
    public char DecimalSeparator { get; init; } = '.';

    /// <summary>The thousands separator (the client locale's in the C library; <c>','</c> here by default).</summary>
    public char ThousandsSeparator { get; init; } = ',';

    /// <summary>
    /// False when <c>IFX_NOT_STRICT_THOUS_SEP</c> is <c>1</c>: thousands separators are then accepted
    /// wherever they appear instead of only in correct three-digit groups.
    /// </summary>
    public bool StrictThousandsSeparator { get; init; } = true;

    /// <summary>
    /// The "today" that <c>DBCENTURY</c>'s <c>P</c>/<c>F</c>/<c>C</c> rules measure against;
    /// <see langword="null"/> (the default) means the current date, as in the C library.
    /// </summary>
    public DateOnly? ReferenceDate { get; init; }

    internal static IfxTextFormat Resolve(IfxTextFormat? format) => format ?? Default;

    internal DateOnly Today => ReferenceDate ?? DateOnly.FromDateTime(DateTime.Now);
}
