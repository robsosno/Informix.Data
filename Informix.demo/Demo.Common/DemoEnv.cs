using System.Data.Common;

namespace Demo;

/// <summary>
/// Where the demos find their settings: one <c>.env</c> file in the Informix.demo root, shared by all
/// programs. A program that needs something different puts only that setting in a <c>.env</c> next to
/// its own project file; it overrides the root file for that program. A real environment variable
/// beats both files.
/// </summary>
/// <remarks>
/// Settings: <c>INFORMIX_DEMO_CONNECTION</c> (the connection string, no <c>Database</c> needed),
/// <c>INFORMIX_DEMO_DATABASE</c> (default <c>superstore_demo2</c>), <c>INFORMIX_DEMO_OPTIONS</c>
/// (<c>Key=Value;Key=Value</c> merged into the connection string - the way for a program-local
/// <c>.env</c> to change a keyword without repeating the host and password) and
/// <c>INFORMIX_DEMO_PROVIDER</c> (03-abstraction only).
/// <para>
/// This file has no dependencies besides the BCL, so the loader project (which does not reference
/// Demo.Common) includes it as a link.
/// </para>
/// </remarks>
public static class DemoEnv
{
    public const string Variable = "INFORMIX_DEMO_CONNECTION";
    public const string DatabaseVariable = "INFORMIX_DEMO_DATABASE";
    public const string OptionsVariable = "INFORMIX_DEMO_OPTIONS";
    public const string DefaultDatabase = "superstore_demo2";

    private const string FileName = ".env";
    private const string RootMarker = "Informix.demo.sln";

    private static readonly Lazy<List<Dictionary<string, string>>> Files = new(LoadFiles);

    /// <summary>
    /// The connection string from <c>INFORMIX_DEMO_CONNECTION</c>, with <c>Database</c> set to
    /// <paramref name="database"/> (else <c>INFORMIX_DEMO_DATABASE</c>, else the default), then
    /// <c>INFORMIX_DEMO_OPTIONS</c> merged in, then <paramref name="extra"/> added or replaced.
    /// Only System.Data.Common is used to edit it.
    /// </summary>
    public static string ConnectionString(string? database = null, params (string Key, string Value)[] extra)
    {
        string raw = Get(Variable)
            ?? throw new InvalidOperationException(
                $"No connection string: set {Variable} in Informix.demo/.env (copy .env.example there) " +
                "or as an environment variable.");

        var builder = new DbConnectionStringBuilder { ConnectionString = raw };
        builder["Database"] = database ?? Get(DatabaseVariable) ?? DefaultDatabase;
        if (Get(OptionsVariable) is { } options)
        {
            var o = new DbConnectionStringBuilder { ConnectionString = options };
            foreach (string key in o.Keys) builder[key] = o[key];
        }
        foreach (var (key, value) in extra) builder[key] = value;
        return builder.ConnectionString;
    }

    /// <summary>
    /// A setting: the environment variable if set, else the nearest <c>.env</c> that defines it
    /// (the program's own, then the Informix.demo root), else null.
    /// </summary>
    public static string? Get(string name)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        foreach (var file in Files.Value)
            if (file.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    /// <summary>A scratch folder for files the demos write; created on first use.</summary>
    public static string WorkDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "informix-demo", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Every .env from the program's folder up to and including the one next to Informix.demo.sln,
    // nearest first. The program's folder is found from the build output (bin/Debug/net10.0/), so
    // it does not matter where `dotnet run` was started.
    private static List<Dictionary<string, string>> LoadFiles()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var found = new List<string>();
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string path = Path.Combine(d.FullName, FileName);
                bool isRoot = File.Exists(Path.Combine(d.FullName, RootMarker));
                if (File.Exists(path)) found.Add(path);
                if (isRoot) return found.ConvertAll(Parse);

                // Run from the repository root: the demos sit in a subfolder.
                string sub = Path.Combine(d.FullName, "Informix.demo");
                if (File.Exists(Path.Combine(sub, RootMarker)))
                {
                    string subEnv = Path.Combine(sub, FileName);
                    if (File.Exists(subEnv)) found.Add(subEnv);
                    return found.ConvertAll(Parse);
                }
            }
        }
        return [];
    }

    /// <summary>
    /// KEY=VALUE per line. Blank lines, <c>#</c> comment lines, an <c>export </c> prefix and one pair of
    /// surrounding quotes are handled. The value is everything after the first <c>=</c>, so <c>;</c>,
    /// <c>=</c> and <c>#</c> inside a connection string are kept as they are (no inline comments).
    /// </summary>
    private static Dictionary<string, string> Parse(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            string l = line.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            if (l.StartsWith("export ", StringComparison.Ordinal)) l = l[7..].TrimStart();
            int eq = l.IndexOf('=');
            if (eq <= 0) continue;
            string value = l[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            values[l[..eq].Trim()] = value;
        }
        return values;
    }
}
