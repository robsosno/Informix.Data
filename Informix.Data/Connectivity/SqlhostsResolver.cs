using System.Runtime.Versioning;

namespace Informix.Data;

/// <summary>
/// The resolved network endpoint for a <c>Server=</c> (INFORMIXSERVER) name or a
/// non-numeric <c>Service=</c>, produced by <see cref="SqlhostsResolver"/>.
/// </summary>
internal readonly record struct SqlhostsEntry(
    string Host, int Port, string NetType, bool UseSsl, bool IsGroup, IfxAuthentication? Authentication);

/// <summary>
/// Resolves an Informix <c>dbservername</c> (or a non-numeric service name) to a
/// TCP endpoint. Only the file, Windows-registry and OS
/// services-database sources are ported; LDAP/JNS server lookup stays out of scope.
///
/// <para><b>Resolution order</b> for a server name:
/// <list type="number">
/// <item>the first existing sqlhosts file — <c>$INFORMIXSQLHOSTS</c> (a full file
///   path), then <c>$INFORMIXDIR/etc/sqlhosts</c>;</item>
/// <item>the Windows registry (<c>HKLM\SOFTWARE\Informix\SQLHOSTS\&lt;server&gt;</c>,
///   where setnet32 stores it), Windows only;</item>
/// </list>
/// A sqlhosts line is <c>dbservername nettype hostname servicename [options]</c>.
/// The service field, and a non-numeric <c>Service=</c>, are resolved to a port
/// via the OS services database (<c>/etc/services</c> or
/// <c>%SystemRoot%\System32\drivers\etc\services</c>). The file system, registry
/// and services database are all injectable so the resolution is unit-testable
/// without touching the machine.</para>
/// </summary>
internal sealed class SqlhostsResolver
{
    private readonly Func<string, string[]?> _readLines;
    private readonly Func<string, string, string?>? _readRegistryValue;
    private readonly Func<string, string?> _getEnv;

    /// <param name="readLines">Returns a file's lines, or <c>null</c> when it does not exist.</param>
    /// <param name="readRegistryValue">Reads <c>HKLM\SOFTWARE\Informix\SQLHOSTS\&lt;server&gt;\&lt;value&gt;</c>
    /// (server, value) → string; <c>null</c> uses the built-in Windows reader (a no-op elsewhere).</param>
    /// <param name="getEnv">Environment-variable accessor (defaults to the process environment).</param>
    public SqlhostsResolver(
        Func<string, string[]?>? readLines = null,
        Func<string, string, string?>? readRegistryValue = null,
        Func<string, string?>? getEnv = null)
    {
        _readLines = readLines ?? DefaultReadLines;
        _readRegistryValue = readRegistryValue ?? (OperatingSystem.IsWindows() ? ReadWindowsRegistry : null);
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Resolves a server (dbservername) to an endpoint via the sqlhosts files then
    /// the Windows registry, following group references to their first member.
    /// Throws <see cref="IfxException"/> listing the searched locations when the server is
    /// not found, with the code IBM JDBC uses: <c>-25553</c> when no sqlhosts file could be
    /// opened, <c>-25556</c> when the server's own line is malformed, otherwise <c>-25555</c>
    /// (the server is not listed).
    /// </summary>
    public SqlhostsEntry ResolveServer(string serverName)
    {
        var searched = new List<string>();
        var lines = LoadSqlhosts(searched, out bool fileFound);
        var entries = ParseEntries(lines, out var malformedNames);

        if (entries.TryGetValue(serverName, out var e))
            return Finalize(e, entries);

        if (_readRegistryValue is not null)
        {
            searched.Add($@"HKLM\SOFTWARE\Informix\SQLHOSTS\{serverName}");
            var reg = ReadRegistryEntry(serverName);
            if (reg is not null)
                return Finalize(reg, entries);
        }

        string where = $"Searched: {string.Join(", ", searched)}.";
        if (malformedNames.Contains(serverName, StringComparer.OrdinalIgnoreCase))
            throw new IfxException(
                $"The sqlhosts line for server '{serverName}' is malformed (expected: dbservername nettype " +
                $"hostname servicename [options]). {where}",
                -25556);
        if (!fileFound)
            throw new IfxException(
                $"No sqlhosts file could be opened to look up server '{serverName}' (set INFORMIXSQLHOSTS " +
                $"or INFORMIXDIR). {where}",
                -25553);
        throw new IfxException(
            $"Server '{serverName}' is not listed as a dbserver name in sqlhosts. {where}",
            -25555);
    }

    /// <summary>
    /// Resolves a non-numeric service name to a TCP port via the OS services
    /// database. Throws <see cref="IfxException"/> <c>-931</c> ("Cannot locate %s service/tcp
    /// service", the server's own code, as IBM JDBC reports it) if unresolved.
    /// </summary>
    public int ResolveServicePort(string service)
    {
        if (int.TryParse(service, out int numeric))
            return numeric;
        var searched = new List<string>();
        if (ResolveServiceViaDatabase(service, searched) is { } port)
            return port;
        throw new IfxException(
            $"Could not resolve service name '{service}' to a port. Searched: {string.Join(", ", searched)}.",
            -931);
    }

    // Resolve the service field / group members, honoring nettype and options.
    private SqlhostsEntry Finalize(RawEntry e, Dictionary<string, RawEntry> entries)
    {
        // A group entry names no host itself: resolve to its first member (the
        // member entry carrying g=<group>). Multi-member failover is out of scope.
        if (IsGroupNetType(e.NetType))
        {
            var member = entries.Values.FirstOrDefault(m => string.Equals(m.Group, e.Name, StringComparison.OrdinalIgnoreCase));
            if (member is null)
                throw new IfxException($"sqlhosts group '{e.Name}' has no member entries.", -25563);
            var resolved = Finalize(member, entries);
            return resolved with { IsGroup = true };
        }

        RejectUnsupportedNetType(e.NetType);

        int port = int.TryParse(e.Service, out int p) ? p
            : ResolveServiceViaDatabase(e.Service, new List<string>())
              ?? throw new IfxException(
                  $"sqlhosts server '{e.Name}' names service '{e.Service}', which is not a port and is not in the services database.",
                  -931);

        return new SqlhostsEntry(
            Host: e.Host,
            Port: port,
            NetType: e.NetType,
            UseSsl: IsSslNetType(e.NetType),
            IsGroup: false,
            // s=4 marks a PAM-authenticated listener, so imply PAM when the caller
            // did not choose an authentication mode explicitly.
            Authentication: e.IsPam ? IfxAuthentication.Pam : null);
    }

    private static bool IsGroupNetType(string net) => net.Equals("group", StringComparison.OrdinalIgnoreCase);

    // onsocssl and ontlissl listeners use implicit TLS.
    private static bool IsSslNetType(string net) =>
        net.Equals("onsocssl", StringComparison.OrdinalIgnoreCase)
        || net.Equals("ontlissl", StringComparison.OrdinalIgnoreCase);

    private static void RejectUnsupportedNetType(string net)
    {
        // onipc* is shared-memory / stream-pipe (not portable across the network);
        // dr* are the DRDA protocols this SQLI driver does not speak.
        if (net.StartsWith("onipc", StringComparison.OrdinalIgnoreCase)
            || net.StartsWith("dr", StringComparison.OrdinalIgnoreCase))
            throw new IfxException(
                $"sqlhosts nettype '{net}' is not supported (only TCP onsoctcp/ontlitcp/onsocssl connect over the network).",
                -25563);
    }

    // ---- sqlhosts file loading + parsing ----

    private string[] LoadSqlhosts(List<string> searched, out bool found)
    {
        foreach (string path in CandidatePaths())
        {
            searched.Add(path);
            var lines = _readLines(path);
            if (lines is not null)
            {
                found = true;
                return lines;
            }
        }
        found = false;
        return [];
    }

    // $INFORMIXSQLHOSTS (a full file path) wins over $INFORMIXDIR/etc/sqlhosts.
    private IEnumerable<string> CandidatePaths()
    {
        string? sqlhosts = _getEnv("INFORMIXSQLHOSTS");
        if (!string.IsNullOrEmpty(sqlhosts))
            yield return sqlhosts;

        string? dir = _getEnv("INFORMIXDIR");
        if (!string.IsNullOrEmpty(dir))
        {
            yield return Path.Combine(dir, "etc", "sqlhosts");
            // The Windows Client SDK also uses a per-server file named sqlhosts.<server>,
            // but the etc\sqlhosts single-file layout is the common case both places.
        }
    }

    // One sqlhosts line: name, nettype, host, service/port, optional comma-separated options.
    private sealed record RawEntry(string Name, string NetType, string Host, string Service, string? Group, bool IsPam);

    // Whitespace-separated columns; '#' starts a comment; lines with fewer than four columns are ignored; a later duplicate wins.
    // A malformed line is skipped, but its first token is reported through malformedNames so a failed
    // lookup of that very server can say the line is malformed rather than that it is not listed.
    private static Dictionary<string, RawEntry> ParseEntries(string[] lines, out List<string> malformedNames)
    {
        var map = new Dictionary<string, RawEntry>(StringComparer.OrdinalIgnoreCase);
        malformedNames = [];
        foreach (string raw in lines)
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4)
            {
                malformedNames.Add(t[0]);
                continue; // malformed line — skip rather than fail the whole lookup
            }
            var (group, isPam) = ParseOptions(t.Length > 4 ? t[4] : "");
            var entry = new RawEntry(t[0], t[1], t[2], t[3], group, isPam);
            map[entry.Name] = entry;
        }
        return map;
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }

    // Options are comma-separated key=value tokens; only s= (security/PAM) and
    // g= (group membership) are interpreted here.
    private static (string? Group, bool IsPam) ParseOptions(string options)
    {
        string? group = null;
        bool isPam = false;
        foreach (string opt in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = opt.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = opt[..eq].Trim();
            string val = opt[(eq + 1)..].Trim();
            if (key.Equals("g", StringComparison.OrdinalIgnoreCase))
                group = val;
            else if (key.Equals("s", StringComparison.OrdinalIgnoreCase) && val == "4")
                isPam = true;
        }
        return (group, isPam);
    }

    // ---- OS services database ----

    private int? ResolveServiceViaDatabase(string service, List<string> searched)
    {
        foreach (string path in ServicesPaths())
        {
            searched.Add(path);
            var lines = _readLines(path);
            if (lines is null)
                continue;
            foreach (string raw in lines)
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0)
                    continue;
                string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // name  port/proto  [aliases...]
                if (t.Length >= 2 && t[0].Equals(service, StringComparison.OrdinalIgnoreCase))
                {
                    string portToken = t[1].Split('/')[0];
                    if (int.TryParse(portToken, out int port))
                        return port;
                }
            }
        }
        return null;
    }

    private IEnumerable<string> ServicesPaths()
    {
        string? windir = _getEnv("SystemRoot") ?? _getEnv("windir");
        if (!string.IsNullOrEmpty(windir))
            yield return Path.Combine(windir, "System32", "drivers", "etc", "services");
        yield return "/etc/services";
    }

    // ---- default (real) IO/registry seams ----

    // Missing or unreadable files count as "not found" so the next candidate is tried.
    private static string[]? DefaultReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private RawEntry? ReadRegistryEntry(string serverName)
    {
        string? host = _readRegistryValue!(serverName, "HOST");
        string? service = _readRegistryValue(serverName, "SERVICE");
        string? protocol = _readRegistryValue(serverName, "PROTOCOL");
        string? options = _readRegistryValue(serverName, "OPTIONS");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(service) || string.IsNullOrEmpty(protocol))
            return null;
        var (group, isPam) = ParseOptions(options ?? "");
        return new RawEntry(serverName, protocol, host, service, group, isPam);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsRegistry(string serverName, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Informix\SQLHOSTS\{serverName}");
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
