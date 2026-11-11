using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Demo;

/// <summary>Small console output helpers, so the demos read as a sequence of short steps.</summary>
public static class Ui
{
    public static void Title(string text)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(text);
        Console.WriteLine(new string('=', 72));
    }

    public static void Section(string text)
    {
        Console.WriteLine();
        Console.WriteLine("-- " + text);
    }

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Note(string text) => Console.WriteLine("   (" + text + ")");

    public static void Kv(string key, object? value) => Console.WriteLine($"   {key,-28} {Show(value)}");

    /// <summary>A value as text: NULL, hex for bytes, [a, b] for collections, invariant numbers.</summary>
    public static string Show(object? v) => v switch
    {
        null or DBNull => "NULL",
        string s => s,
        byte[] b => b.Length <= 16 ? Convert.ToHexString(b) : $"<{b.Length} bytes>",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(dt.Ticks),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(t.Ticks),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        _ => v.ToString() ?? "",
    };

    // ".123" for the sub-second part of a time, with no trailing zeros; nothing when there is none.
    private static string Fraction(long ticks)
    {
        long sub = ticks % TimeSpan.TicksPerSecond;
        return sub == 0 ? "" : "." + sub.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    public static void Table(string[] headers, IEnumerable<object?[]> rows)
    {
        var cells = rows.Select(r => r.Select(Show).ToArray()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, cells.Select(c => i < c.Length ? Math.Min(c[i].Length, 48) : 0).DefaultIfEmpty(0).Max())).ToArray();
        string Row(string[] c) => "   " + string.Join("  ", c.Select((x, i) => Clip(x, widths[i]).PadRight(widths[i])));
        Console.WriteLine(Row(headers));
        Console.WriteLine("   " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var c in cells) Console.WriteLine(Row(c));
    }

    private static string Clip(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "~";

    /// <summary>Runs <paramref name="action"/> and returns how long it took.</summary>
    public static TimeSpan Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed;
    }

    public static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:N0} ms";

    /// <summary>
    /// Runs a step that is expected to fail and prints the database error, using only the
    /// System.Data.Common members every provider has.
    /// </summary>
    public static void ExpectError(string what, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"   {what}: succeeded (no error)");
        }
        catch (DbException e)
        {
            Console.WriteLine($"   {what}: error {e.ErrorCode} - {FirstLine(e.Message)}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"   {what}: {e.GetType().Name} - {FirstLine(e.Message)}");
        }
    }

    public static string FirstLine(string s)
    {
        int i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }

    /// <summary>The first <paramref name="count"/> lines of a text file, for showing what a demo wrote.</summary>
    public static void Head(string path, int count = 3)
    {
        Console.WriteLine($"   {path}");
        foreach (string line in File.ReadLines(path, Encoding.UTF8).Take(count))
            Console.WriteLine("   | " + (line.Length > 110 ? line[..110] + "..." : line));
    }
}
