using System.Text;

namespace Informix.Data.Wire;

/// <summary>
/// Maps an Informix locale / code-set name (as it appears in <c>DB_LOCALE</c>,
/// e.g. <c>pl_pl.8859-2</c>, <c>en_US.utf8</c>, <c>en_us.CP1250</c>)
/// onto the .NET <see cref="Encoding"/> that produces the matching single- or
/// multi-byte representation on the SQLI wire.
///
/// <para>Informix does no code-set conversion when the client and database locales
/// agree, so the bytes on the wire are exactly the DB_LOCALE code set. The managed
/// driver must therefore encode/decode CHAR/NCHAR/VARCHAR/LVARCHAR text with that
/// same code set; using the wrong one (the historical hard-coded ISO-8859-1) folds
/// or drops every character outside Latin-1 — Polish <c>ł ż ć ę …</c>, Cyrillic,
/// Greek, CJK, etc.</para>
///
/// <para>.NET Core ships only ASCII/UTF-8/UTF-16/Latin-1 in the box; the legacy
/// single-byte and DBCS code pages live in <c>System.Text.Encoding.CodePages</c>,
/// which the <see cref="ModuleInitializerAttribute">module initializer</see> registers
/// once so <see cref="Encoding.GetEncoding(int)"/> can resolve them.</para>
/// </summary>
internal static class IfxCodeSet
{
    // Make the legacy code pages (ISO-8859-x, windows-125x, Shift-JIS, …) resolvable.
    // The explicit static constructor guarantees this runs before the first Resolve.
    static IfxCodeSet() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Resolves the client code set from a locale string, falling back to
    /// <see cref="IfxWireConverter.DefaultEncoding"/> (ISO-8859-1) when the locale is
    /// absent or its code set cannot be mapped to an available .NET encoding.
    /// </summary>
    public static Encoding Resolve(string? locale) => TryResolve(locale) ?? IfxWireConverter.DefaultEncoding;

    /// <summary>Resolves the code set, or <c>null</c> when it is absent/unrecognised.</summary>
    public static Encoding? TryResolve(string? locale)
    {
        string codeset = ExtractCodeset(locale);
        if (codeset.Length == 0)
            return null;

        // Normalise to a compact key: lower-case, punctuation removed.
        // "8859-2" -> "88592", "ISO-8859-2" -> "iso88592", "CP1250" -> "cp1250",
        // "UTF-8" -> "utf8".
        string k = Normalize(codeset);

        // Informix aliases (8859-x, numeric IBM codes such as 819/912, Windows and CJK names) -> .NET code page numbers.
        int? cp = k switch
        {
            "88591" or "iso88591" or "latin1" or "8859" or "iso8859" or "819" or "cp819" => 28591,
            "88592" or "iso88592" or "latin2" or "912" => 28592,
            "88593" or "iso88593" or "latin3" => 28593,
            "88594" or "iso88594" or "latin4" => 28594,
            "88595" or "iso88595" or "cyrillic" => 28595,
            "88596" or "iso88596" or "arabic" => 28596,
            "88597" or "iso88597" or "greek" => 28597,
            "88598" or "iso88598" or "hebrew" => 28598,
            "88599" or "iso88599" or "latin5" => 28599,
            "885913" or "iso885913" or "latin7" => 28603,
            "885915" or "iso885915" or "latin9" => 28605,
            "utf8" or "utf" or "unicode" => 65001,
            "ascii" or "usascii" or "646" => 20127,
            "cp1250" or "windows1250" or "win1250" or "mscp1250" => 1250,
            "cp1251" or "windows1251" or "win1251" or "mscp1251" => 1251,
            "cp1252" or "windows1252" or "win1252" or "mscp1252" => 1252,
            "cp1253" or "windows1253" or "win1253" => 1253,
            "cp1254" or "windows1254" or "win1254" => 1254,
            "cp1255" or "windows1255" or "win1255" => 1255,
            "cp1256" or "windows1256" or "win1256" => 1256,
            "cp1257" or "windows1257" or "win1257" => 1257,
            "cp1258" or "windows1258" or "win1258" => 1258,
            "sjis" or "shiftjis" or "cp943" or "cp932" => 932,
            "big5" or "cp950" => 950,
            "gb" or "gb2312" or "cp936" or "gbk" => 936,
            "ksc" or "ksc5601" or "cp949" or "euckr" => 949,
            _ => null,
        };

        if (cp is int known)
            return GetCodePage(known);

        // Bare numeric code set (e.g. "en_us.1252", "en_us.932") — treat as a .NET code page.
        if (int.TryParse(k, out int numeric))
            return GetCodePage(numeric);

        // Last resort: let .NET try the code-set name verbatim ("iso-8859-2", "windows-1250").
        // NotSupportedException: a name .NET knows but refuses (UTF-7 and its aliases).
        try { return Encoding.GetEncoding(codeset); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    // The code set is the token after the last '.', with any '@modifier' stripped.
    // A bare code set (no '.') is accepted as-is.
    private static string ExtractCodeset(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return "";
        string s = locale.Trim();
        int at = s.IndexOf('@');
        if (at >= 0)
            s = s[..at];
        int dot = s.LastIndexOf('.');
        if (dot >= 0)
            s = s[(dot + 1)..];
        return s.Trim();
    }

    private static string Normalize(string codeset)
    {
        var sb = new StringBuilder(codeset.Length);
        foreach (char c in codeset)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ISO-8859-1 is built in; anything else may be missing on a trimmed runtime, which we report as "unknown".
    private static Encoding? GetCodePage(int codePage)
    {
        if (codePage == 28591)
            return Encoding.Latin1;
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }
}
