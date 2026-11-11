using System.Text;
using System.Text.RegularExpressions;

namespace SuperstoreLoader;

/// <summary>Reads an Informix UNLOAD file: one row per line, '|' after every field, '\' escapes.</summary>
internal static class UnlFile
{
    public static List<string[]> Read(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();

        void EndRow()
        {
            if (field.Length > 0) { fields.Add(field.ToString()); field.Clear(); }
            if (fields.Count > 0) rows.Add(fields.ToArray());
            fields.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char n = text[++i];
                if (n == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                field.Append(n == '\r' ? '\n' : n);
            }
            else if (c == '|') { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') EndRow();
            else if (c != '\r') field.Append(c);
        }
        EndRow();
        return rows;
    }
}

internal abstract record Node;
internal sealed record ScalarNode(string Text, bool Quoted) : Node;
internal sealed record ContainerNode(string Kind, List<Node> Items) : Node;

/// <summary>
/// The text form of a ROW or collection value as an UNLOAD file writes it - ROW(a,b),
/// LIST{...}, SET{...}, MULTISET{...} - parsed and re-written as an SQL constructor.
/// </summary>
internal static partial class Literal
{
    [GeneratedRegex(@"^\s*(ROW\s*\(|LIST\s*\{|SET\s*\{|MULTISET\s*\{)", RegexOptions.IgnoreCase)]
    private static partial Regex Start();

    [GeneratedRegex(@"\G\s*(ROW\s*\(|LIST\s*\{|SET\s*\{|MULTISET\s*\{)", RegexOptions.IgnoreCase)]
    private static partial Regex StartAt();

    [GeneratedRegex(@"^[+-]?\d+(\.\d+)?$")]
    private static partial Regex Numeric();

    public static bool IsComplex(string text) => Start().IsMatch(text);

    public static Node Parse(string text)
    {
        int p = 0;
        Node n = ParseValue(text, ref p);
        SkipSpace(text, ref p);
        if (p != text.Length) throw new FormatException($"Unexpected text after position {p} in: {text}");
        return n;
    }

    public static string ToSql(Node n) => n switch
    {
        ScalarNode { Quoted: true } q => Quote(q.Text),
        ScalarNode s when s.Text.Length == 0 || s.Text.Equals("NULL", StringComparison.OrdinalIgnoreCase) => "NULL",
        // Leading zeros (a ZIP suffix written 0001) are only formatting.
        ScalarNode s when Numeric().IsMatch(s.Text) =>
            decimal.Parse(s.Text, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ScalarNode s => Quote(s.Text),
        ContainerNode { Kind: "ROW" } c => "ROW(" + string.Join(", ", c.Items.Select(ToSql)) + ")",
        ContainerNode c => c.Kind + "{" + string.Join(", ", c.Items.Select(ToSql)) + "}",
        _ => throw new InvalidOperationException(),
    };

    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static void SkipSpace(string s, ref int p)
    {
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
    }

    private static Node ParseValue(string s, ref int p)
    {
        SkipSpace(s, ref p);
        if (p >= s.Length) throw new FormatException($"Unexpected end of: {s}");

        var m = StartAt().Match(s, p);
        if (m.Success)
        {
            string kind = m.Groups[1].Value.TrimEnd('(', '{', ' ').ToUpperInvariant();
            char close = kind == "ROW" ? ')' : '}';
            p = m.Index + m.Length;
            var items = new List<Node>();
            SkipSpace(s, ref p);
            if (p < s.Length && s[p] == close) { p++; return new ContainerNode(kind, items); }
            while (true)
            {
                items.Add(ParseValue(s, ref p));
                SkipSpace(s, ref p);
                if (p >= s.Length) throw new FormatException($"Missing '{close}' in: {s}");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == close) { p++; return new ContainerNode(kind, items); }
                throw new FormatException($"Unexpected '{s[p]}' at position {p} in: {s}");
            }
        }

        if (s[p] is '\'' or '"')
        {
            char q = s[p++];
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= s.Length) throw new FormatException($"Unterminated string in: {s}");
                char c = s[p++];
                if (c != q) { sb.Append(c); continue; }
                if (p < s.Length && s[p] == q) { sb.Append(q); p++; continue; }
                return new ScalarNode(sb.ToString(), true);
            }
        }

        int start = p;
        while (p < s.Length && s[p] != ',' && s[p] != ')' && s[p] != '}') p++;
        return new ScalarNode(s[start..p].Trim(), false);
    }
}
