using System.Text;
using System.Text.RegularExpressions;

namespace SuperstoreLoader;

/// <summary>
/// Splits a dbaccess-style script into statements. Comments are dropped, and the semicolons
/// inside a CREATE PROCEDURE / CREATE FUNCTION body do not end the statement.
/// </summary>
internal static partial class SqlScript
{
    [GeneratedRegex(@"^\s*create\s+(dba\s+)?(procedure|function)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoutineStart();

    [GeneratedRegex(@"\bend\s+(procedure|function)(\s+document\s+(""[^""]*""|'[^']*'))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RoutineEnd();

    public static List<string> Split(string text)
    {
        var statements = new List<string>();
        var sb = new StringBuilder();
        bool inRoutine = false;

        void Flush()
        {
            string s = sb.ToString().Trim();
            sb.Clear();
            inRoutine = false;
            if (s.Length > 0) statements.Add(s);
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (c is '\'' or '"')
            {
                // A doubled quote ends one literal and starts the next, which this handles naturally.
                int end = text.IndexOf(c, i + 1);
                if (end < 0) end = text.Length - 1;
                sb.Append(text, i, end - i + 1);
                i = end;
            }
            else if (c == ';')
            {
                if (inRoutine)
                {
                    if (RoutineEnd().IsMatch(sb.ToString())) Flush();
                    else sb.Append(c);
                }
                else if (RoutineStart().IsMatch(sb.ToString()))
                {
                    inRoutine = true;
                    sb.Append(c);
                }
                else Flush();
            }
            else sb.Append(c);
        }
        Flush();
        return statements;
    }
}
