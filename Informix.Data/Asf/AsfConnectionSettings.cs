namespace Informix.Data.Asf;

/// <summary>
/// Inputs of the ASF handshake — the parameters the driver passed into
/// <c>AsfConnection</c>'s constructor plus the option properties
/// it read via <c>GetoptProperties</c>. Client identity fields
/// (<see cref="ProcessId"/>, <see cref="HostName"/>, ...) default from the
/// environment but are settable so tests can produce deterministic frames.
/// </summary>
internal sealed class AsfConnectionSettings
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>Login user; required — the driver never falls back to the OS user name.</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>The INFORMIXSERVER (dbservername).</summary>
    public required string ServerName { get; init; }

    /// <summary>Database to open; <c>null</c> connects without opening a database.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>Environment variables forwarded in the SQ_ASCENV segment (DB_LOCALE, DBPATH, ...).</summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Character encoding for CHAR/NCHAR/VARCHAR text on the wire — the database code set
    /// (DB_LOCALE). Defaults to ISO-8859-1 (the Informix "NOENCODING" single-byte default).
    /// The <see cref="AsfConnection"/> stamps this onto the wire reader/writer so every code
    /// path shares one code set.
    /// </summary>
    public System.Text.Encoding DbEncoding { get; set; } = System.Text.Encoding.Latin1;

    /// <summary>Client version string sent in the handshake.</summary>
    public string DriverVersion { get; init; } = AsfProtocol.DefaultDriverVersion;

    public string SerialNumber { get; init; } = AsfProtocol.DefaultSerialNumber;

    /// <summary>Client capability word.</summary>
    public int Capabilities { get; init; } = AsfProtocol.DefaultCapabilities;

    /// <summary>Requests a trusted-context session (<c>TRUSTED_CONTEXT</c>).</summary>
    public bool TrustedContext { get; init; }

    /// <summary>Set when the server name came from a sqlhosts group lookup (<c>ASF_GRPREF</c>).</summary>
    public bool SqlhostsGroup { get; init; }

    /// <summary>Connect retries after a failure (<c>INFORMIXCONRETRY</c>).</summary>
    public int RetryCount { get; init; }

    /// <summary>Seconds allowed for the TCP connect; 0 = system default (<c>LOGINTIMEOUT</c>).</summary>
    public int LoginTimeoutSeconds { get; init; }

    /// <summary>Socket read timeout in seconds; 0 = infinite (<c>IFX_SOC_TIMEOUT</c>).</summary>
    public int SocketTimeoutSeconds { get; init; }

    public bool SocketKeepAlive { get; init; }

    /// <summary>Overall connect-time budget in seconds, also spacing the retries (<c>INFORMIXCONTIME</c>).</summary>
    public int ConnectTimeSeconds { get; init; }

    // ---- TLS (SSLCONNECTION) ----

    /// <summary>
    /// Wraps the socket in a TLS <c>SslStream</c> before the ASF handshake — the
    /// client side of an Informix <c>onsocssl</c> listener (<c>SSLCONNECTION=true</c>).
    /// The TLS handshake runs immediately after TCP connect (implicit TLS), so the
    /// whole SQLI conversation, PAM included, is encrypted.
    /// </summary>
    public bool UseSsl { get; init; }

    /// <summary>
    /// Accepts the server certificate even when the platform cannot build/trust its
    /// chain (self-signed test certs, host-name mismatch).
    /// Leave <c>false</c> in production and install the server CA in the trust store.
    /// </summary>
    public bool TrustServerCertificate { get; init; }

    // ---- Client identity sent in SQ_ASCPINFO / SQ_ASCMISC_60 ----

    public int ProcessId { get; init; } = Environment.ProcessId;

    public int ThreadId { get; init; } = Environment.CurrentManagedThreadId;

    public string HostName { get; init; } = Environment.MachineName;

    public string CurrentDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Application name reported to the server.</summary>
    public string ApplicationName { get; init; } =
        $"Thread[id:{Environment.CurrentManagedThreadId}, name:.NET, path:{AppContext.BaseDirectory}]";

    /// <summary>
    /// Validates the user/password combination.
    /// </summary>
    internal string ResolveUserName()
    {
        if (string.IsNullOrEmpty(UserName))
            throw new IfxException(
                "A user name is required: set 'User ID' in the connection string (the driver never falls back to the OS user name).",
                -25563);
        if (Password is null)
            throw new IfxException("A user name was given without a password.", -952);
        return UserName;
    }
}
