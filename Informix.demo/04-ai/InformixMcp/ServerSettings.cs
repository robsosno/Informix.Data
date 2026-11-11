using System.Data.Common;

namespace Demos.InformixMcp;

/// <summary>
/// Where the server gets its connection string, and what it does to it. Only this process reads the
/// string: tool results and error messages go through <see cref="Scrub"/>, so the assistant never sees it.
/// </summary>
internal sealed class ServerSettings
{
    public const string Variable = "INFORMIX_MCP_CONNECTION";
    public const string EnvFileVariable = "INFORMIX_MCP_ENV";

    // Keywords the server forces, whatever the .env says: a changed .env cannot loosen them.
    private static readonly (string Key, string Value)[] Forced =
    [
        ("Select Only", "true"),     // the driver rejects every statement the server does not describe as SELECT (-878)
        ("Read Only", "true"),       // every transaction the driver begins is SET TRANSACTION READ ONLY
    ];

    private static readonly string[] SecretKeys = ["Password", "PWD", "User ID", "UID", "User", "Host"];

    private readonly string[] _secrets;

    public string ConnectionString { get; }

    private ServerSettings(string connectionString, string[] secrets)
    {
        ConnectionString = connectionString;
        _secrets = secrets;
    }

    /// <summary>
    /// The variable INFORMIX_MCP_CONNECTION from the process environment, or else from the .env file
    /// (INFORMIX_MCP_ENV, or the first .env found walking up from the working directory and from the
    /// program's folder). The message of the exception says what to do and is safe to show.
    /// </summary>
    public static ServerSettings Load()
    {
        string? raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            string? file = FindEnvFile();
            if (file is not null) raw = ReadValue(file, Variable);
        }
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"The Informix connection is not configured: {Variable} is missing. Tell the user to create the .env file " +
                "(copy .env.example) next to .mcp.json and restart the MCP server. Do not look for the file or the value yourself.");

        var builder = new DbConnectionStringBuilder { ConnectionString = raw };
        var secrets = new List<string> { raw };
        foreach (string key in SecretKeys)
            if (builder.TryGetValue(key, out object? v) && v?.ToString() is { Length: > 0 } s) secrets.Add(s);
        foreach (var (key, value) in Forced) builder[key] = value;
        return new ServerSettings(builder.ConnectionString, [.. secrets.OrderByDescending(s => s.Length)]);
    }

    /// <summary>Replaces every secret value (the whole string, password, user, host) in <paramref name="text"/> with ***.</summary>
    public string Scrub(string text)
    {
        foreach (string secret in _secrets)
            text = text.Replace(secret, "***", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string? FindEnvFile()
    {
        string? explicitPath = Environment.GetEnvironmentVariable(EnvFileVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;

        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string path = Path.Combine(d.FullName, ".env");
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>One KEY=VALUE per line; blank lines, # comments, an "export " prefix and surrounding quotes are handled.</summary>
    private static string? ReadValue(string file, string key)
    {
        foreach (string line in File.ReadLines(file))
        {
            string l = line.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            if (l.StartsWith("export ", StringComparison.Ordinal)) l = l[7..].TrimStart();
            int eq = l.IndexOf('=');
            if (eq <= 0 || !l[..eq].Trim().Equals(key, StringComparison.Ordinal)) continue;
            string value = l[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            return value;
        }
        return null;
    }
}
