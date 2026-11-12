using System.Text;

namespace Informix.Data;

/// <summary>
/// The kind of parameter placeholders a statement uses, as classified by
/// <see cref="IfxSqlParser"/>.
/// </summary>
internal enum IfxPlaceholderKind
{
    /// <summary>No placeholders at all.</summary>
    None,

    /// <summary>Only the native Informix positional <c>?</c> placeholder.</summary>
    Positional,

    /// <summary>Only named placeholders (<c>@name</c> / <c>:name</c>), rewritten to <c>?</c>.</summary>
    Named,
}

/// <summary>
/// The result of parsing a <c>CommandType.Text</c> command string: the SQL actually
/// sent to the server (with any named placeholders rewritten to the Informix-native
/// positional <c>?</c>) plus, for a named statement, the parameter name of every
/// placeholder occurrence in wire order.
/// </summary>
internal sealed class IfxParsedCommand
{
    private static readonly IReadOnlyList<string> NoNames = Array.Empty<string>();

    private IfxParsedCommand(string text, IfxPlaceholderKind kind, IReadOnlyList<string> names, int placeholderCount = 0)
    {
        Text = text;
        Kind = kind;
        ParameterNames = names;
        PlaceholderCount = kind == IfxPlaceholderKind.Named ? names.Count : placeholderCount;
    }

    /// <summary>How many <c>?</c> markers <see cref="Text"/> carries (named placeholders count as the
    /// <c>?</c> they were rewritten to). A '?' inside a literal, quoted identifier or comment is not one.</summary>
    public int PlaceholderCount { get; }

    /// <summary>
    /// <see langword="true"/> when the command text was the ODBC <c>{? = call ...}</c>
    /// escape: its leading <c>?</c> is the routine's return value, not a bound argument, so
    /// it contributes no placeholder to <see cref="Text"/> and the first parameter in the
    /// collection receives the routine's RETURNed value whatever its
    /// <see cref="System.Data.ParameterDirection"/> says.
    /// </summary>
    public bool HasReturnValuePlaceholder { get; private init; }

    /// <summary><see langword="true"/> when the text came from a <c>{call ...}</c> /
    /// <c>{? = call ...}</c> escape and was rewritten to <c>EXECUTE PROCEDURE</c>.</summary>
    public bool IsCallEscape { get; private init; }

    // Copy of this result flagged as having come from a <c>{call ...}</c> escape.
    internal IfxParsedCommand WithCallEscape(bool hasReturnValue) => new(Text, Kind, ParameterNames, PlaceholderCount)
    {
        IsCallEscape = true,
        HasReturnValuePlaceholder = hasReturnValue,
    };

    /// <summary>The statement text to send to the server. For a positional-only or
    /// parameterless statement this is the original command-text string instance (no
    /// rewrite, no allocation); for a named statement it is the rewritten text with
    /// <c>?</c> in place of each <c>@name</c>/<c>:name</c>.</summary>
    public string Text { get; }

    /// <summary>How the statement places its parameters.</summary>
    public IfxPlaceholderKind Kind { get; }

    /// <summary><see langword="true"/> when the statement used named placeholders and
    /// its binds must be resolved by <see cref="IfxParameter.ParameterName"/>.</summary>
    public bool IsNamed => Kind == IfxPlaceholderKind.Named;

    /// <summary>For a named statement, the parameter name behind each <c>?</c> in wire
    /// order — one entry per placeholder occurrence (a name repeated in the SQL repeats
    /// here). Empty otherwise.</summary>
    public IReadOnlyList<string> ParameterNames { get; }

    public static IfxParsedCommand NoParameters(string sql) => new(sql, IfxPlaceholderKind.None, NoNames);

    public static IfxParsedCommand Positional(string sql, int placeholderCount = 1) =>
        new(sql, IfxPlaceholderKind.Positional, NoNames, placeholderCount);

    public static IfxParsedCommand Named(string rewritten, IReadOnlyList<string> names) =>
        new(rewritten, IfxPlaceholderKind.Named, names);
}

/// <summary>
/// A single-pass SQL tokenizer that rewrites named parameter placeholders
/// (<c>@name</c> / <c>:name</c>) to the Informix-native positional <c>?</c>, skipping
/// string literals, quoted identifiers and comments so placeholders inside them are
/// left untouched. Similar to the SQL-parser approach other providers use (the analogous
/// <c>@p</c> → <c>$1</c> rewrite) but with Informix lexical rules — no dollar-quoting,
/// no E-strings, non-nesting block comments, curly-brace comments (but not the braces of a
/// <c>SET{...}</c>/<c>LIST{...}</c>/<c>MULTISET{...}</c> collection constructor), the <c>::</c> cast
/// operator guard, and the cross-database qualifier rule (a <c>:</c>/<c>@</c> glued to the
/// end of an identifier is part of a qualified object name — <c>dbname:object</c>,
/// <c>dbname@server:object</c> — not a placeholder).
/// </summary>
/// <remarks>
/// Only the Informix positional <c>?</c> is native; ORMs and hand-written ADO.NET code
/// commonly use named placeholders, so the driver rewrites them client-side. A
/// statement that mixes named and <c>?</c> placeholders is rejected. The parser
/// allocates nothing when the text contains no named placeholder — it returns the
/// original string instance for the positional/parameterless fast path.
/// </remarks>
internal static class IfxSqlParser
{
    // IBM JDBC's -79888 ("Parameters indicated by both name and index"), which it raises for the
    // same mistake on a CallableStatement.
    internal const int MixedPlaceholdersSqlCode = -79888;

    /// <summary>
    /// Parses <paramref name="sql"/>, returning the (possibly rewritten) statement text
    /// and the ordered placeholder names.
    /// </summary>
    /// <exception cref="IfxException">The statement mixes named and positional
    /// placeholders.</exception>
    public static IfxParsedCommand Parse(string sql)
    {
        if (TryTranslateCallEscape(sql, out string translated, out bool hasReturnValue))
            return ParseCore(translated).WithCallEscape(hasReturnValue);
        return ParseCore(sql);
    }

    /// <summary>
    /// Rewrites the ODBC call escape — <c>{call routine(...)}</c> or
    /// <c>{? = call routine(...)}</c> — to the Informix-native
    /// <c>EXECUTE PROCEDURE routine(...)</c>. The escape is what the IBM Informix .NET
    /// provider's documentation uses to invoke SPL with OUT/INOUT parameters, and the
    /// Informix server itself does not understand it (it parses as a syntax error).
    ///
    /// <para>The leading <c>?</c> of the second form is the routine's return value, not an
    /// argument: it is dropped here so the placeholder count matches the routine's declared
    /// arguments, and reported back through
    /// <see cref="IfxParsedCommand.HasReturnValuePlaceholder"/>.</para>
    ///
    /// <para>Note this takes precedence over the Informix <c>{ ... }</c> comment syntax for
    /// a statement that is nothing <em>but</em> a brace group whose first token is
    /// <c>call</c> (or <c>? = call</c>) — the same precedence every ODBC-style driver
    /// applies.</para>
    /// </summary>
    private static bool TryTranslateCallEscape(string sql, out string translated, out bool hasReturnValue)
    {
        translated = "";
        hasReturnValue = false;

        ReadOnlySpan<char> s = sql.AsSpan().Trim();
        while (s.Length > 0 && s[^1] == ';')
            s = s[..^1].TrimEnd();
        if (s.Length < 2 || s[0] != '{' || s[^1] != '}')
            return false;

        ReadOnlySpan<char> body = s[1..^1].Trim();
        if (body.Length > 0 && body[0] == '?')
        {
            ReadOnlySpan<char> afterMark = body[1..].TrimStart();
            if (afterMark.Length == 0 || afterMark[0] != '=')
                return false;
            hasReturnValue = true;
            body = afterMark[1..].TrimStart();
        }

        const string Call = "call";
        if (body.Length <= Call.Length
            || !body[..Call.Length].Equals(Call, StringComparison.OrdinalIgnoreCase)
            || IsNameChar(body[Call.Length]))
            return false;

        ReadOnlySpan<char> routine = body[Call.Length..].Trim();
        if (routine.Length == 0)
            return false;

        // "proc" (no argument list) is a legal escape; EXECUTE PROCEDURE needs none either.
        translated = string.Concat("execute procedure ", routine);
        return true;
    }

    // Single pass over the text. Literals and comments are skipped. '?' marks a positional placeholder. '@name' and ':name'
    // are rewritten to '?' (copying the untouched text in between lazily, so statements without named parameters allocate nothing).
    private static IfxParsedCommand ParseCore(string sql)
    {
        int n = sql.Length;
        StringBuilder? rewritten = null;   // built lazily on the first named placeholder
        List<string>? names = null;
        int copied = 0;                    // sql copied into 'rewritten' up to here (exclusive)
        bool sawPositional = false;
        int positionalCount = 0;

        int lastText = -1;                 // see IsCollectionConstructor

        int i = 0;
        while (i < n)
        {
            char c = sql[i];
            switch (c)
            {
                case '\'':
                    lastText = -1;
                    i = SkipSingleQuoted(sql, i);
                    break;
                case '"':
                    lastText = -1;
                    i = SkipDoubleQuoted(sql, i);
                    break;
                case '-':
                    // "-- ..." line comment; a lone '-' is ordinary text.
                    lastText = -1;
                    i = i + 1 < n && sql[i + 1] == '-' ? SkipLineComment(sql, i + 2) : i + 1;
                    break;
                case '/':
                    // "/* ... */" block comment (does NOT nest in Informix); a lone '/' is text.
                    lastText = -1;
                    i = i + 1 < n && sql[i + 1] == '*' ? SkipBlockComment(sql, i + 2) : i + 1;
                    break;
                case '{':
                    // SET{...} / LIST{...} / MULTISET{...} is a collection constructor: its
                    // braces are ordinary text and what is inside them is scanned as usual.
                    i = IsCollectionConstructor(sql, lastText) ? i + 1 : SkipBraceComment(sql, i + 1);
                    lastText = -1;
                    break;
                case '?':
                    lastText = -1;
                    sawPositional = true;
                    positionalCount++;
                    i++;
                    break;
                case ':':
                    lastText = -1;
                    // The "::type" cast operator: two colons swallow both and never start a
                    // name (so a ':' preceded by ':' can never start one either).
                    if (i + 1 < n && sql[i + 1] == ':')
                    {
                        i += 2;
                        break;
                    }
                    // A ':' glued to the end of an identifier is the Informix cross-database
                    // qualifier "dbname:object", not a placeholder — leave it literal.
                    if (i > 0 && IsNameChar(sql[i - 1]))
                    {
                        i++;
                        break;
                    }
                    i = TryNamedParam(sql, i, ref rewritten, ref names, ref copied);
                    break;
                case '@':
                    // An '@' glued to the end of an identifier is the Informix cross-server
                    // qualifier "dbname@server", not a placeholder (a '@@' run is likewise
                    // left literal). Leave it, and its identifier, alone.
                    if (i > 0 && (IsNameChar(sql[i - 1]) || sql[i - 1] == '@'))
                    {
                        lastText = -1;
                        i++;
                        break;
                    }
                    lastText = -1;
                    i = TryNamedParam(sql, i, ref rewritten, ref names, ref copied);
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                        lastText = i;
                    i++;
                    break;
            }
        }

        if (rewritten is null)
        {
            // No named placeholder was rewritten: return the original instance verbatim.
            return sawPositional ? IfxParsedCommand.Positional(sql, positionalCount) : IfxParsedCommand.NoParameters(sql);
        }

        if (sawPositional)
        {
            throw new IfxException(
                "A statement cannot mix named parameters (@name/:name) with positional '?' placeholders " +
                $"({MixedPlaceholdersSqlCode}).", MixedPlaceholdersSqlCode);
        }

        rewritten.Append(sql, copied, n - copied);
        return IfxParsedCommand.Named(rewritten.ToString(), names!);
    }

    /// <summary>
    /// Counts the positional <c>?</c> placeholders in <paramref name="sql"/>, skipping
    /// string literals, quoted identifiers and comments so a <c>?</c> inside one is not
    /// counted. Throws if the statement uses a named placeholder (<c>@name</c>/<c>:name</c>) —
    /// the bulk loader is positional-only in v1.
    /// </summary>
    /// <exception cref="IfxException">The statement uses named placeholders.</exception>
    public static int CountPositionalPlaceholders(string sql)
    {
        int n = sql.Length, count = 0, i = 0;
        int lastText = -1; // see IsCollectionConstructor
        while (i < n)
        {
            char c = sql[i];
            if (c is '\'' or '"' or '-' or '/' or '?' or ':' or '@')
                lastText = -1;
            switch (c)
            {
                case '\'': i = SkipSingleQuoted(sql, i); break;
                case '"': i = SkipDoubleQuoted(sql, i); break;
                case '-': i = i + 1 < n && sql[i + 1] == '-' ? SkipLineComment(sql, i + 2) : i + 1; break;
                case '/': i = i + 1 < n && sql[i + 1] == '*' ? SkipBlockComment(sql, i + 2) : i + 1; break;
                case '{':
                    i = IsCollectionConstructor(sql, lastText) ? i + 1 : SkipBraceComment(sql, i + 1);
                    lastText = -1;
                    break;
                case '?': count++; i++; break;
                case ':':
                    // "::" cast operator and the "dbname:object" qualifier are not placeholders.
                    if ((i + 1 < n && sql[i + 1] == ':') || (i > 0 && IsNameChar(sql[i - 1]))) { i += (i + 1 < n && sql[i + 1] == ':') ? 2 : 1; break; }
                    if (i + 1 < n && IsNameStart(sql[i + 1]))
                        throw NamedNotSupported();
                    i++;
                    break;
                case '@':
                    // The "dbname@server" qualifier is not a placeholder.
                    if (i > 0 && (IsNameChar(sql[i - 1]) || sql[i - 1] == '@')) { i++; break; }
                    if (i + 1 < n && IsNameStart(sql[i + 1]))
                        throw NamedNotSupported();
                    i++;
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                        lastText = i;
                    i++;
                    break;
            }
        }
        return count;
    }

    /// <summary>
    /// Whether the <c>{</c> the scanner has just reached opens a collection constructor —
    /// <c>SET{...}</c>, <c>LIST{...}</c> or <c>MULTISET{...}</c> — rather than a comment.
    /// </summary>
    /// <remarks>
    /// <para>The rule is the server's own (live-verified against IDS 9.59): a brace that follows
    /// one of those three keywords, with nothing but white space in between, is always a
    /// constructor. That holds even where the keyword is the SET of an UPDATE —
    /// <c>update t set {a comment} a = 1</c> is a syntax error (-201) on the server, not a
    /// commented statement — so no statement the server accepts changes meaning here. A
    /// comment between the keyword and the brace (<c>SET /* c */ {1}</c>) is likewise refused
    /// by the server, so it needs no special handling.</para>
    /// <para><paramref name="lastText"/> is the index of the last character the scanner consumed
    /// as plain statement text, white space aside, or -1 when the thing before the brace was a
    /// literal, a comment, a placeholder or an operator. Tracking it in the scan — instead of
    /// looking back through the string — is what keeps a keyword inside a preceding comment
    /// (<c>-- set</c> + newline + <c>{comment}</c>) from being taken for the real one.</para>
    /// </remarks>
    private static bool IsCollectionConstructor(string sql, int lastText)
    {
        if (lastText < 0)
            return false;
        int start = lastText;
        while (start >= 0 && IsNameChar(sql[start]))
            start--;
        // A non-ASCII letter glued in front makes it a longer identifier, not the keyword.
        if (start >= 0 && char.IsLetter(sql[start]))
            return false;
        ReadOnlySpan<char> word = sql.AsSpan(start + 1, lastText - start);
        return word.Equals("SET", StringComparison.OrdinalIgnoreCase)
            || word.Equals("LIST", StringComparison.OrdinalIgnoreCase)
            || word.Equals("MULTISET", StringComparison.OrdinalIgnoreCase);
    }

    private static IfxException NamedNotSupported() => new(
        "IfxBulkLoader supports only positional '?' placeholders; named parameters (@name/:name) are not " +
        $"supported ({MixedPlaceholdersSqlCode}).", MixedPlaceholdersSqlCode);

    // Handles a '@' or ':' at index 'at'. If a valid name follows it, emits the literal
    // run since the last copy point plus a '?', records the name, and returns the index
    // just past the name. Otherwise the '@'/':' is ordinary text and only it is consumed.
    private static int TryNamedParam(string sql, int at,
        ref StringBuilder? rewritten, ref List<string>? names, ref int copied)
    {
        int n = sql.Length;
        int nameStart = at + 1;
        if (nameStart >= n || !IsNameStart(sql[nameStart]))
            return at + 1; // e.g. ':5', ':=', a trailing '@' — literal text

        int j = nameStart + 1;
        while (j < n && IsNameChar(sql[j]))
            j++;

        rewritten ??= new StringBuilder(sql.Length);
        names ??= [];
        rewritten.Append(sql, copied, at - copied); // literal text before the placeholder
        rewritten.Append('?');
        names.Add(sql.Substring(nameStart, j - nameStart));
        copied = j;
        return j;
    }

    // Skips a single-quoted string starting at the opening quote; '' is an embedded
    // quote. Returns the index just past the closing quote (or end if unterminated).
    private static int SkipSingleQuoted(string sql, int at)
    {
        int n = sql.Length;
        for (int i = at + 1; i < n; i++)
        {
            if (sql[i] != '\'')
                continue;
            if (i + 1 < n && sql[i + 1] == '\'')
            {
                i++; // doubled quote escapes itself
                continue;
            }
            return i + 1;
        }
        return n;
    }

    // Skips a double-quoted region (delimited identifier or string, per DELIMIDENT)
    // starting at the opening quote; "" is an embedded quote.
    private static int SkipDoubleQuoted(string sql, int at)
    {
        int n = sql.Length;
        for (int i = at + 1; i < n; i++)
        {
            if (sql[i] != '"')
                continue;
            if (i + 1 < n && sql[i + 1] == '"')
            {
                i++;
                continue;
            }
            return i + 1;
        }
        return n;
    }

    // Skips a "-- ..." line comment body; stops at the newline (which the main loop
    // then steps over) or end of text.
    private static int SkipLineComment(string sql, int at)
    {
        int n = sql.Length;
        int i = at;
        while (i < n && sql[i] != '\n' && sql[i] != '\r')
            i++;
        return i;
    }

    // Skips a "/* ... */" block comment body. Informix block comments do NOT nest, so a
    // nested "/*" is ignored and the first "*/" closes it.
    private static int SkipBlockComment(string sql, int at)
    {
        int n = sql.Length;
        for (int i = at; i < n; i++)
        {
            if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/')
                return i + 2;
        }
        return n;
    }

    // Skips an Informix "{ ... }" curly-brace comment body up to the closing '}'.
    private static int SkipBraceComment(string sql, int at)
    {
        int n = sql.Length;
        for (int i = at; i < n; i++)
        {
            if (sql[i] == '}')
                return i + 1;
        }
        return n;
    }

    // ASCII letter or underscore; the "| 0x20" folds upper case onto lower case for a single range check.
    private static bool IsNameStart(char c) =>
        (uint)((c | 0x20) - 'a') <= 'z' - 'a' || c == '_';

    private static bool IsNameChar(char c) =>
        (uint)((c | 0x20) - 'a') <= 'z' - 'a' || (uint)(c - '0') <= '9' - '0' || c == '_';
}
