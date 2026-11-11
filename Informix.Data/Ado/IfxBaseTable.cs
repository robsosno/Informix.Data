using System.Data;

namespace Informix.Data;

/// <summary>
/// The single base table of a SELECT, as far as the driver can tell. The server's describe reply
/// carries no table name, so it is read from the text: <c>SELECT ... FROM [db:][owner.]table [alias] ...</c>
/// with exactly one table, no join, no union. That is what <see cref="IfxCommandBuilder"/> 
/// / <see cref="System.Data.Common.DbCommandBuilder"/> can work with anyway.
/// The key and column details are looked up in the catalog on demand.
/// </summary>
internal sealed class IfxBaseTable
{
    private static readonly HashSet<string> EndKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "ORDER", "HAVING", "INTO", "FOR", "UNION", "INTERSECT", "EXCEPT", "MINUS",
        "LIMIT", "OFFSET", "SKIP", "FIRST", "PDQPRIORITY", "WITH", "LOCK",
    };

    private static readonly HashSet<string> JoinKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "JOIN", "INNER", "LEFT", "RIGHT", "OUTER", "FULL", "CROSS", "NATURAL", "STRAIGHT_JOIN",
    };

    private IfxBaseTable(string? owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    public string? Owner { get; }
    public string Name { get; }

    /// <summary>Columns of the table (empty until <see cref="Load"/> ran).</summary>
    public HashSet<string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Primary-key columns (empty until <see cref="Load"/> ran, and for a table without one).</summary>
    public HashSet<string> KeyColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SERIAL / SERIAL8 / BIGSERIAL columns.</summary>
    public HashSet<string> AutoIncrementColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once <see cref="Load"/> has filled the column and key sets.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Reads the columns, the primary key and the serial columns of the table from the catalog.</summary>
    public void Load(IfxConnection connection)
    {
        foreach (DataRow row in connection.GetSchema("Columns", [null, Owner, Name]).Rows)
        {
            string column = (string)row["COLUMN_NAME"];
            Columns.Add(column);
            if (row["TYPE_NAME"] is string type && type.Contains("serial", StringComparison.OrdinalIgnoreCase))
                AutoIncrementColumns.Add(column);
        }
        foreach (DataRow row in connection.GetSchema("PrimaryKeys", [null, Owner, Name]).Rows)
            KeyColumns.Add((string)row["COLUMN_NAME"]);
        Loaded = Columns.Count > 0;
    }

    /// <summary>
    /// The base table of <paramref name="sql"/>, or <c>null</c> when it is not a plain one-table SELECT.
    /// </summary>
    public static IfxBaseTable? Find(string sql)
    {
        var tokens = Tokenize(sql);
        int i = 0;
        if (tokens.Count == 0 || !tokens[0].IsWord("SELECT"))
            return null;

        // The first FROM outside any parentheses ends the select list.
        int depth = 0;
        for (i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Text == "(") depth++;
            else if (t.Text == ")") depth--;
            else if (depth == 0 && t.IsWord("FROM")) break;
        }
        if (i >= tokens.Count)
            return null;
        i++;

        // Any set operator at the top level makes it a different statement shape.
        depth = 0;
        foreach (var t in tokens)
        {
            if (t.Text == "(") depth++;
            else if (t.Text == ")") depth--;
            else if (depth == 0 && (t.IsWord("UNION") || t.IsWord("INTERSECT") || t.IsWord("EXCEPT") || t.IsWord("MINUS")))
                return null;
        }

        // FROM ONLY ( name ) or FROM name
        bool only = i < tokens.Count && tokens[i].IsWord("ONLY") && i + 1 < tokens.Count && tokens[i + 1].Text == "(";
        if (only) i += 2;

        string? owner = null;
        string? name = null;
        // [db:] [owner.] table, each part a word or a quoted identifier
        var parts = new List<(string Text, char Sep)>();
        while (i < tokens.Count && tokens[i].IsName)
        {
            string part = tokens[i].NameText;
            i++;
            char sep = '\0';
            if (i < tokens.Count && (tokens[i].Text == "." || tokens[i].Text == ":"))
            {
                sep = tokens[i].Text[0];
                i++;
            }
            parts.Add((part, sep));
            if (sep == '\0') break;
        }
        if (parts.Count == 0 || parts[^1].Sep != '\0')
            return null;
        name = parts[^1].Text;
        if (parts.Count >= 2 && parts[^2].Sep == '.')
            owner = parts[^2].Text;

        if (only)
        {
            if (i >= tokens.Count || tokens[i].Text != ")") return null;
            i++;
        }

        // What follows the name: nothing, an alias, or a clause. A comma, a join or a parenthesis
        // means more than one table (or a table function).
        if (i < tokens.Count && tokens[i].IsWord("AS")) i++;
        if (i < tokens.Count && tokens[i].IsName && !tokens[i].IsKeyword(EndKeywords))
        {
            if (tokens[i].IsKeyword(JoinKeywords)) return null;
            i++;                                                   // an alias
        }
        if (i < tokens.Count)
        {
            var next = tokens[i];
            if (next.Text == "," || next.Text == "(" || next.IsKeyword(JoinKeywords))
                return null;
        }

        return new IfxBaseTable(owner, name);
    }

    // ---- a minimal tokenizer: words, quoted identifiers/strings, and single punctuation characters ----

    private readonly record struct Token(string Text, bool Quoted)
    {
        public bool IsName => Text.Length > 0 && (Quoted || char.IsLetter(Text[0]) || Text[0] == '_');
        public bool IsWord(string word) => !Quoted && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
        public bool IsKeyword(HashSet<string> set) => !Quoted && set.Contains(Text);

        // Unquoted names are case-insensitive and stored lower-case; a quoted one is taken as written.
        public string NameText => Quoted ? Text : Text.ToLowerInvariant();
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                while (i < sql.Length && sql[i] != '}') i++;
                i++;
                continue;
            }
            if (c is '"' or '\'')
            {
                int end = sql.IndexOf(c, i + 1);
                if (end < 0) end = sql.Length - 1;
                string text = sql.Substring(i + 1, end - i - 1);
                // A quoted name is an identifier only in a name position; the parser decides. Strings never
                // reach it because the table name is looked for directly after FROM.
                tokens.Add(new Token(text, Quoted: true));
                i = end + 1;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '$')
            {
                int start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                tokens.Add(new Token(sql[start..i], Quoted: false));
                continue;
            }
            tokens.Add(new Token(c.ToString(), Quoted: false));
            i++;
        }
        return tokens;
    }
}
