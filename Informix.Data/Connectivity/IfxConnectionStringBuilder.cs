using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Informix.Data;

/// <summary>How the client authenticates to the Informix listener.</summary>
public enum IfxAuthentication
{
    /// <summary>Send the password inline in the connection request (trusted/non-PAM ports).</summary>
    Password,
    /// <summary>Run the PAM challenge/response exchange (ports configured with <c>s=4,pamauth=...</c>).</summary>
    Pam,
}

/// <summary>
/// Strongly-typed builder for Informix connection strings.
///
/// <para>The common connection keywords are exposed as typed properties with the
/// usual ADO.NET/ODBC synonyms ("User ID"/"UID"/"User", "Password"/"PWD"). Any other
/// Informix property (<c>IFX_SOC_TIMEOUT</c>, <c>SQLH_TYPE</c>, ...) can still be set
/// through the inherited dictionary indexer; it is kept with the connection string, and
/// only the keywords documented on the typed properties are acted on.</para>
/// </summary>
public sealed class IfxConnectionStringBuilder : DbConnectionStringBuilder
{
    // Canonical (Informix-native) keyword => accepted synonyms. The first entry
    // is the name we persist; lookups are case-insensitive.
    internal const string KeyHost = "Host";
    internal const string KeyService = "Service";
    internal const string KeyServer = "Server";
    internal const string KeyDatabase = "Database";
    internal const string KeyUser = "User ID";
    internal const string KeyPassword = "Password";

    // Accepted synonyms for the credential keywords, in lookup order. The ODBC/OLE DB
    // spellings ("UID"/"PWD") are common enough in existing Informix connection strings
    // that they have to work interchangeably with the ADO.NET names.
    private static readonly string[] UserKeys = [KeyUser, "UID", "UserId", "User"];
    private static readonly string[] PasswordKeys = [KeyPassword, "PWD"];
    internal const string KeyDbLocale = "DB Locale";
    internal const string KeyDbLocaleDiscovery = "DB Locale Discovery";

    // The Informix-native spelling of the locale keyword. Existing ESQL/C, JDBC and ODBC
    // connection strings all write DB_LOCALE, so it has to be interchangeable with the
    // ADO.NET-style "DB Locale" — an unrecognized spelling would silently fall back to
    // ISO-8859-1 and trigger the sysmaster locale lookup the caller just avoided.
    private static readonly string[] DbLocaleKeys = [KeyDbLocale, "DB_LOCALE"];
    internal const string KeySsl = "SSL Connection";
    internal const string KeyTrustServerCertificate = "Trust Server Certificate";
    internal const string KeyLoginTimeout = "Login Timeout";
    internal const string KeyAuthentication = "Authentication";
    internal const string KeyFetchBufferSize = "Fetch Buffer Size";
    internal const string KeyOptofc = "Optofc";
    internal const string KeyPooling = "Pooling";
    internal const string KeyMinPoolSize = "Min Pool Size";
    internal const string KeyMaxPoolSize = "Max Pool Size";
    internal const string KeyConnectionLifetime = "Connection Lifetime";
    internal const string KeyPersistSecurityInfo = "Persist Security Info";
    internal const string KeyTrimTrailingSpaces = "TrimTrailingSpaces";
    internal const string KeyEnlist = "Enlist";
    internal const string KeyIsolationLevel = "Isolation Level";

    // "IsolationLevel" (unspaced) is what a caller coming from a DSN or from the
    // property name itself will reach for.
    private static readonly string[] IsolationLevelKeys = [KeyIsolationLevel, "IsolationLevel"];
    internal const string KeyLargeRowFormats = "Large Row Formats";
    internal const string KeyPreparedStatementCacheSize = "Prepared Statement Cache Size";
    internal const string KeySelectOnly = "Select Only";
    internal const string KeyReadOnly = "Read Only";

    // Session environment sent to the server in the handshake (see the per-property docs). Each
    // has the ADO.NET-style spelling first and the Informix environment-variable spelling as a
    // synonym, like DB Locale / DB_LOCALE above.
    internal const string KeyDbDate = "DB Date";
    internal const string KeyGlDate = "GL Date";
    internal const string KeyDbCentury = "DB Century";
    internal const string KeyGlDateTime = "GL DateTime";
    internal const string KeyStatementCache = "Statement Cache";
    internal const string KeyLongIdentifiers = "Long Identifiers";
    internal const string KeyDelimitedIdentifiers = "Delimited Identifiers";
    internal const string KeyStrictThousandsSeparator = "Strict Thousands Separator";
    private static readonly string[] DbDateKeys = [KeyDbDate, "DBDATE"];
    private static readonly string[] GlDateKeys = [KeyGlDate, "GL_DATE"];
    private static readonly string[] DbCenturyKeys = [KeyDbCentury, "DBCENTURY"];
    private static readonly string[] GlDateTimeKeys = [KeyGlDateTime, "GL_DATETIME"];
    private static readonly string[] StatementCacheKeys = [KeyStatementCache, "STMT_CACHE"];
    private static readonly string[] LongIdentifiersKeys = [KeyLongIdentifiers, "IFX_LONGID"];
    private static readonly string[] DelimitedIdentifiersKeys = [KeyDelimitedIdentifiers, "DELIMIDENT"];

    public IfxConnectionStringBuilder() { }

    public IfxConnectionStringBuilder(string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
            ConnectionString = connectionString;
    }

    /// <summary>Host name or IP of the Informix server (Informix <c>IFXHOST</c>).</summary>
    public string Host
    {
        get => GetString(KeyHost);
        set => this[KeyHost] = value;
    }

    /// <summary>TCP port or service name of the sqlexec listener (Informix <c>PORTNO</c>/service).</summary>
    public string Service
    {
        get => GetString(KeyService);
        set => this[KeyService] = value;
    }

    /// <summary>Informix server (dbservername) — <c>INFORMIXSERVER</c>.</summary>
    public string Server
    {
        get => GetString(KeyServer);
        set => this[KeyServer] = value;
    }

    /// <summary>Database name to open.</summary>
    public string Database
    {
        get => GetString(KeyDatabase);
        set => this[KeyDatabase] = value;
    }

    /// <summary>Login user (Informix <c>USER</c>). Synonyms: <c>UID</c>, <c>UserId</c>, <c>User</c>.</summary>
    public string UserId
    {
        get => GetString(UserKeys);
        set => SetWithSynonyms(UserKeys, value);
    }

    /// <summary>Login password (Informix <c>PASSWORD</c>). Synonym: <c>PWD</c>.</summary>
    public string Password
    {
        get => GetString(PasswordKeys);
        set => SetWithSynonyms(PasswordKeys, value);
    }

    /// <summary>Database locale (Informix <c>DB_LOCALE</c>). Synonym: <c>DB_LOCALE</c>.</summary>
    public string DbLocale
    {
        get => GetString(DbLocaleKeys);
        set => SetWithSynonyms(DbLocaleKeys, value);
    }

    /// <summary>
    /// When neither <c>DB Locale</c> nor its <c>DB_LOCALE</c> synonym is set, look the
    /// database's code set up from <c>sysmaster:sysdbslocale</c> on first open (default
    /// <c>true</c>). This lets the driver pick the right character encoding — and satisfy
    /// the server's locale check — without the caller having to know the database's
    /// <c>DB_LOCALE</c>. A locale given in the connection string always wins and skips the
    /// lookup; set this to <c>false</c> to skip it as well (e.g. when <c>sysmaster</c> is
    /// not reachable).
    /// </summary>
    public bool DbLocaleDiscovery
    {
        get => GetBool(KeyDbLocaleDiscovery, defaultValue: true);
        set => this[KeyDbLocaleDiscovery] = value;
    }

    /// <summary>Whether to negotiate TLS (Informix <c>SSLCONNECTION</c>; synonym <c>Encrypt</c>).
    /// Requires an Informix <c>onsocssl</c> listener.</summary>
    public bool SslConnection
    {
        get => GetBool(KeySsl) || GetBool("Encrypt");
        set => this[KeySsl] = value;
    }

    /// <summary>
    /// Accepts the server's TLS certificate even when its chain cannot be validated
    /// (self-signed / host-name mismatch). Only meaningful with
    /// <see cref="SslConnection"/>. Keep <c>false</c> in
    /// production and trust the server CA instead.
    /// </summary>
    public bool TrustServerCertificate
    {
        get => GetBool(KeyTrustServerCertificate);
        set => this[KeyTrustServerCertificate] = value;
    }

    /// <summary>Login timeout in seconds (Informix <c>LOGINTIMEOUT</c>); 0 = infinite.</summary>
    public int LoginTimeout
    {
        get => GetInt(KeyLoginTimeout);
        set => this[KeyLoginTimeout] = value;
    }

    /// <summary>
    /// Default tuple-buffer size in bytes for cursors on this connection (Informix
    /// <c>FET_BUF_SIZE</c>). 0 (the default) leaves the classic tuple-size-derived
    /// sizing (4 KB / 8 KB for fetches, ~32 KB for insert cursors). Despite the
    /// historical name, <c>FET_BUF_SIZE</c> is not fetch-only — exactly as in ESQL/C
    /// it sizes <b>insert-cursor</b> buffering too: a larger value packs more rows
    /// per server reply on big scans <i>and</i> more <see cref="IfxBulkLoader"/> rows
    /// per SQ_PUT round trip on bulk loads. A per-command
    /// <see cref="IfxCommand.FetchBufferSize"/> overrides this for queries only.
    /// </summary>
    public int FetchBufferSize
    {
        get => GetInt(KeyFetchBufferSize);
        set => this[KeyFetchBufferSize] = value;
    }

    /// <summary>
    /// Enables OPTOFC (Informix <c>OPTOFC</c>): the first tuple buffer of a
    /// forward-only read-only SELECT is requested in the same PDU as the cursor
    /// open, saving one round trip per cursor (matters most for variable-length
    /// result sets). Off by default.
    /// </summary>
    public bool Optofc
    {
        get => GetBool(KeyOptofc);
        set => this[KeyOptofc] = value;
    }

    /// <summary>Whether connections are drawn from a client-side pool keyed by the
    /// connection string (default <c>true</c>). Set <c>false</c> to build a fresh
    /// physical connection every <see cref="IfxConnection.Open"/>.</summary>
    public bool Pooling
    {
        get => GetBool(KeyPooling, defaultValue: true);
        set => this[KeyPooling] = value;
    }

    /// <summary>Minimum number of connections the pool keeps warm (default 0).</summary>
    public int MinPoolSize
    {
        get => GetInt(KeyMinPoolSize);
        set => this[KeyMinPoolSize] = value;
    }

    /// <summary>Maximum number of physical connections the pool will hand out
    /// concurrently; further <see cref="IfxConnection.Open"/> calls block up to
    /// <see cref="LoginTimeout"/> then fail (default 100).</summary>
    public int MaxPoolSize
    {
        get => GetInt(KeyMaxPoolSize, defaultValue: 100);
        set => this[KeyMaxPoolSize] = value;
    }

    /// <summary>Maximum age in seconds of a pooled connection; older ones are closed
    /// on checkout instead of reused (default 0 = unlimited).</summary>
    public int ConnectionLifetime
    {
        get => GetInt(KeyConnectionLifetime);
        set => this[KeyConnectionLifetime] = value;
    }

    /// <summary>
    /// When <c>true</c>, the password is kept in the connection string returned by
    /// <see cref="IfxDataSource.ConnectionString"/> after the source is built; when
    /// <c>false</c> (the default, and the ADO.NET security norm) the password is
    /// stripped. Synonym: <c>PersistSecurityInfo</c>. Never affects the actual
    /// credentials sent to the server.
    /// </summary>
    public bool PersistSecurityInfo
    {
        get => GetBool(KeyPersistSecurityInfo) || GetBool("PersistSecurityInfo");
        set => this[KeyPersistSecurityInfo] = value;
    }

    /// <summary>
    /// Returns this connection string with the password removed. Used for
    /// <see cref="IfxDataSource.ConnectionString"/> (unless
    /// <see cref="PersistSecurityInfo"/> is set) and anywhere a connection string
    /// is logged — the password must never appear in a log.
    /// </summary>
    public string ToStringWithoutPassword()
    {
        if (!Array.Exists(PasswordKeys, ContainsKey))
            return ConnectionString;
        var copy = new IfxConnectionStringBuilder(ConnectionString);
        foreach (var key in PasswordKeys) // case-insensitive; also matches a stored "password"
            copy.Remove(key);
        return copy.ConnectionString;
    }

    /// <summary>
    /// Trims trailing spaces from fixed-width CHAR/NCHAR values on read (Informix
    /// <c>IFX_TRIMTRAILINGSPACES</c>). Off by default — with it off, CHAR/NCHAR decode
    /// is byte-for-byte identical to a driver build without this property.
    /// VARCHAR/NVARCHAR/LVARCHAR values are never trimmed.
    /// </summary>
    public bool TrimTrailingSpaces
    {
        get => GetBool(KeyTrimTrailingSpaces);
        set => this[KeyTrimTrailingSpaces] = value;
    }

    /// <summary>
    /// Client-side guard: when <c>true</c>, this connection accepts only plain SELECT
    /// statements. Every other statement type — INSERT/UPDATE/DELETE, DDL, EXECUTE
    /// PROCEDURE, BEGIN/COMMIT/ROLLBACK, ... — is rejected with SQLCODE -878 ("Invalid
    /// operation for a READ-ONLY transaction") as soon as it is described, before it can
    /// run. Off by default.
    ///
    /// <para>Classification comes from the server's own SQ_DESCRIBE reply for the
    /// prepared statement (see <c>SqliStatementType</c>), not from parsing the SQL text
    /// client-side, so it cannot be fooled by comments, string literals, or unusual
    /// formatting the way a naive keyword check could.</para>
    ///
    /// <para><b>This is a convenience guard, not a security boundary.</b> It runs in this
    /// driver, not the server, so it only ever helps a well-behaved caller catch its own
    /// mistake early (an LLM- or tool-generated statement that turned out not to be a
    /// SELECT, say) — it does nothing against a connection whose underlying Informix user
    /// already has write privileges and a caller willing to route around this library
    /// entirely. Its scope is also wider than a transaction: it covers autocommit
    /// statements too, which <see cref="ReadOnly"/> does not. The only real boundary is
    /// database-level: connect as an Informix user granted <c>CONNECT</c>/<c>SELECT</c> and
    /// nothing else.</para>
    ///
    /// <para><b>vs. <see cref="ReadOnly"/>:</b> IDS does have a real, server-enforced
    /// transaction-level read-only mode (<c>SET TRANSACTION READ ONLY</c>, live-verified
    /// with this same SQLCODE -878) — that's what <see cref="ReadOnly"/> drives. The two
    /// are independent and neither implies the other: <see cref="SelectOnly"/> is
    /// client-side, always-on (autocommit included), and blind to what the server would
    /// actually have allowed; <see cref="ReadOnly"/> is server-enforced but
    /// transaction-scoped only, and also forbids DDL/temp tables/GRANT/REVOKE/UPDATE
    /// STATISTICS/RENAME inside the transaction (see its own doc comment) — things a
    /// read-write SELECT-shaped statement would never trigger anyway. A hand-written
    /// <c>SET TRANSACTION</c> statement is itself rejected under <see cref="SelectOnly"/>
    /// (it isn't a SELECT) — that's correct, not a bug.</para>
    /// </summary>
    public bool SelectOnly
    {
        get => GetBool(KeySelectOnly);
        set => this[KeySelectOnly] = value;
    }

    /// <summary>
    /// The default access mode for every transaction this connection begins in the session —
    /// <see cref="IfxConnection.BeginTransaction()"/>,
    /// <see cref="IfxConnection.BeginTransaction(IsolationLevel)"/>, and auto-enlist/explicit
    /// <see cref="IfxConnection.EnlistTransaction"/> alike. Off by default (read-write), which
    /// sends nothing extra — zero cost, and it still works against an unlogged database.
    /// <c>true</c> makes every one of those transactions start with <c>SET TRANSACTION READ
    /// ONLY</c> (server-enforced, SQLCODE -878 on a write attempt, live-verified), which
    /// forbids INSERT/UPDATE/DELETE, CREATE/ALTER/DROP of any object — including temp tables —
    /// GRANT/REVOKE, UPDATE STATISTICS and RENAME for the lifetime of that transaction.
    ///
    /// <para>Autocommit statements run outside any transaction are <b>not</b> covered by this
    /// keyword — use <see cref="SelectOnly"/> for that (see its doc comment for how the two
    /// compare). An explicit <c>isReadOnly</c> argument to
    /// <see cref="IfxConnection.BeginTransaction(IsolationLevel, bool)"/> overrides this keyword
    /// either way, for that one transaction.</para>
    /// </summary>
    public bool ReadOnly
    {
        get => GetBool(KeyReadOnly);
        set => this[KeyReadOnly] = value;
    }

    /// <summary>
    /// Asks the server for the Informix 15 large-row wire formats during the protocol
    /// handshake: a 4-byte tuple size (rows above 32 KB), 8-byte row ids, and the
    /// 68-byte simple-large-object descriptor. Off by default, which keeps the
    /// handshake byte-identical to a driver build without this property.
    ///
    /// <para>Only a request — the formats actually used follow the server's reply, so
    /// enabling it against a server that does not offer them changes nothing. Requires
    /// Informix 15 or later. Without it the classic formats are used on every server
    /// version, even when an Informix 15 server advertises the wide ones unasked.</para>
    /// </summary>
    public bool LargeRowFormats
    {
        get => GetBool(KeyLargeRowFormats);
        set => this[KeyLargeRowFormats] = value;
    }

    /// <summary>
    /// Whether an <see cref="IfxConnection"/> automatically enlists in the ambient
    /// <see cref="System.Transactions.Transaction"/> (<c>Transaction.Current</c>) at
    /// <see cref="IfxConnection.Open"/> — the ADO.NET <c>Enlist</c> keyword (default
    /// <c>true</c>). When <c>false</c>, the connection ignores any surrounding
    /// <c>TransactionScope</c>; a caller can still enlist explicitly with
    /// <see cref="IfxConnection.EnlistTransaction"/>.
    ///
    /// <para>The driver supports only single-connection (non-distributed) transactions:
    /// a <c>TransactionScope</c> that spans a second resource manager would require XA
    /// promotion, which is not implemented and throws on promotion.</para>
    /// </summary>
    public bool Enlist
    {
        get => GetBool(KeyEnlist, defaultValue: true);
        set => this[KeyEnlist] = value;
    }

    /// <summary>
    /// Size of the session-level prepared-statement cache. 0 (the default) turns caching off: every
    /// statement is prepared and released exactly as without this property. When positive, a
    /// statement released without an explicit <see cref="IfxCommand.Prepare"/> in effect is kept
    /// prepared on the server (keyed by SQL text + cursor kind) instead of released, so a later
    /// statement with the same SQL text reactivates it — no SQ_PREPARE round trip, no fresh query
    /// plan. The cache lives on the physical connection, so it survives pooled rent/return cycles
    /// and is torn down only when the physical connection itself closes.
    /// </summary>
    public int PreparedStatementCacheSize
    {
        get => ContainsKey(KeyPreparedStatementCacheSize)
            ? GetInt(KeyPreparedStatementCacheSize)
            : GetInt("IFX_PREPAREDSTATEMENT_CACHE_SIZE");
        set => this[KeyPreparedStatementCacheSize] = value;
    }

    /// <summary>
    /// Isolation level to pin the session to at <see cref="IfxConnection.Open"/>.
    /// Synonym: <c>IsolationLevel</c>.
    ///
    /// <para>Defaults to <see cref="IsolationLevel.Unspecified"/>, which means the driver
    /// issues <b>no</b> <c>SET ISOLATION</c> at all and the server's own default stands.
    /// That default is deliberate: <c>SET ISOLATION</c> is rejected on a database created
    /// without logging, so a driver that always sent one would break every connection to
    /// an unlogged database. The statement is sent only when a caller explicitly asks for
    /// a level — which, on an unlogged database, they simply must not do.</para>
    ///
    /// <para>Accepted values are the <see cref="IsolationLevel"/> names, case-insensitively
    /// (except <see cref="IsolationLevel.Chaos"/>, which Informix has no equivalent for and
    /// which is rejected by name exactly as it is from <see cref="IfxConnection.BeginTransaction(IsolationLevel)"/>),
    /// <b>plus</b> the numeric ODBC <c>ISOLATIONLEVEL</c> codes: <c>0</c>=Unspecified,
    /// <c>1</c>=ReadUncommitted, <c>2</c>=ReadCommitted, <c>3</c>=RepeatableRead,
    /// <c>4</c>=Serializable, <c>5</c>=Snapshot. Any other number — including <c>6</c>, the
    /// Windows DSN dialog's "Transaction" — is rejected the same as an unrecognized name.
    /// They map onto Informix as <see cref="IsolationLevel.ReadUncommitted"/> → DIRTY READ,
    /// <see cref="IsolationLevel.ReadCommitted"/> → COMMITTED READ,
    /// <see cref="IsolationLevel.RepeatableRead"/> and <see cref="IsolationLevel.Serializable"/>
    /// → REPEATABLE READ (Informix has no stricter level), and
    /// <see cref="IsolationLevel.Snapshot"/> → COMMITTED READ LAST COMMITTED. An unrecognized
    /// value throws rather than being ignored, so a typo cannot silently leave the session at
    /// the server default.</para>
    ///
    /// <para>The level is re-applied on every <c>Open</c>, so it also reasserts itself over a
    /// level left behind on a pooled physical connection by an earlier
    /// <see cref="IfxConnection.BeginTransaction(IsolationLevel)"/>. A connection string that
    /// leaves this unspecified inherits whatever the pooled session was last set to.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The stored value is neither a recognized isolation-level
    /// name nor a numeric ODBC code in the 0-5 range.</exception>
    public IsolationLevel IsolationLevel
    {
        get
        {
            var value = GetString(IsolationLevelKeys);
            if (value.Length == 0)
                return IsolationLevel.Unspecified;
            // The ODBC ISOLATIONLEVEL codes (0-5) come first: TryParse would otherwise
            // happily up-cast a bare number to an undefined IsolationLevel value and
            // Enum.IsDefined would reject it, masking a valid numeric code with the wrong error.
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                if (TryMapOdbcIsolationCode(code, out var mapped))
                    return mapped;
                throw new ArgumentException(
                    $"'{value}' is not a valid '{KeyIsolationLevel}'. Numeric values 0-5 are accepted " +
                    "(0=Unspecified, 1=ReadUncommitted, 2=ReadCommitted, 3=RepeatableRead, " +
                    "4=Serializable, 5=Snapshot).");
            }
            if (Enum.TryParse<IsolationLevel>(value, ignoreCase: true, out var level)
                && Enum.IsDefined(level) && level != IsolationLevel.Chaos)
                return level;
            throw new ArgumentException(
                $"'{value}' is not a valid '{KeyIsolationLevel}'. Expected one of: " +
                string.Join(", ", Enum.GetNames<IsolationLevel>()) +
                " (except Chaos, which Informix has no equivalent for), or a numeric ODBC " +
                "ISOLATIONLEVEL code (0-5).");
        }
        set => SetWithSynonyms(IsolationLevelKeys, value.ToString());
    }

    /// <summary>
    /// Maps an ODBC <c>ISOLATIONLEVEL</c>/<c>ISOLVL</c> code (0-5) onto the ADO.NET enum.
    /// Used by <see cref="IsolationLevel"/>'s getter, which applies its own policy for 0 and for
    /// unrecognized codes (including 6, the Windows DSN dialog's "Transaction").
    /// </summary>
    internal static bool TryMapOdbcIsolationCode(int code, out IsolationLevel level)
    {
        switch (code)
        {
            case 0: level = IsolationLevel.Unspecified; return true;
            case 1: level = IsolationLevel.ReadUncommitted; return true;
            case 2: level = IsolationLevel.ReadCommitted; return true;
            case 3: level = IsolationLevel.RepeatableRead; return true;
            case 4: level = IsolationLevel.Serializable; return true;
            case 5: level = IsolationLevel.Snapshot; return true;
            default: level = default; return false;
        }
    }

    // ---- session environment: sent to the server at connect, and the source of the text format ----
    //
    // Informix servers read a number of environment variables per session, from a block the
    // client sends in the connection handshake (visible in sysmaster:sysenvses). Anything the
    // client does not send falls back to the *server process's* own environment - so a literal
    // like '2026-07-29' is accepted or rejected depending on which DBDATE the server happened to
    // start with. These keywords make the session's value explicit. They are fixed for the life
    // of the physical connection and, being part of the connection string, of the pool key.
    // Unset sends nothing at all, leaving the server default (and a connection string that
    // does not use them byte-identical on the wire to a build without them).

    /// <summary>
    /// <c>DBDATE</c> for the session — the DATE format the <b>server</b> uses wherever it converts
    /// text to or from a DATE: date literals in SQL text, string parameters bound to DATE
    /// columns, and <c>d::LVARCHAR</c> / <c>CAST(d AS CHAR)</c> / <c>TO_CHAR(d)</c> (e.g.
    /// <c>"Y4MD-"</c>, <c>"MDY4/"</c>, <c>"DMY2."</c>). Unset: the server's own default.
    /// Dates bound as <see cref="DateOnly"/>/<see cref="DateTime"/> and dates read back as
    /// DATE values never go through text, so they are unaffected. Synonym: <c>DBDATE</c>.
    ///
    /// <para>Also drives <see cref="TextFormat"/>, so <see cref="IfxTextConvert"/> formats and
    /// parses DATE text the way the server does for this connection.</para>
    /// </summary>
    public string DbDate
    {
        get => GetString(DbDateKeys);
        set => SetWithSynonyms(DbDateKeys, value);
    }

    /// <summary>
    /// <c>GL_DATE</c> for the session — a strftime-style DATE format (e.g. <c>"%d.%m.%Y"</c>).
    /// The server uses it only when no DBDATE applies, and a server whose own environment sets
    /// DBDATE (as many do) ignores it, so prefer <see cref="DbDate"/>. Synonym: <c>GL_DATE</c>.
    /// </summary>
    public string GlDate
    {
        get => GetString(GlDateKeys);
        set => SetWithSynonyms(GlDateKeys, value);
    }

    /// <summary>
    /// <c>DBCENTURY</c> for the session — how the server expands a one- or two-digit year in a
    /// DATE/DATETIME literal: <c>R</c> (present century, the default), <c>P</c> (past), <c>F</c>
    /// (future) or <c>C</c> (closest). Case-sensitive; anything else behaves as <c>R</c>.
    /// Synonym: <c>DBCENTURY</c>.
    /// </summary>
    public string DbCentury
    {
        get => GetString(DbCenturyKeys);
        set => SetWithSynonyms(DbCenturyKeys, value);
    }

    /// <summary>
    /// <c>GL_DATETIME</c> for the session — a strftime-style format the server uses when it
    /// renders a DATETIME as text (<c>dt::LVARCHAR</c> and the like). It has no effect on
    /// parsing: the server always reads the ANSI form. Synonym: <c>GL_DATETIME</c>.
    /// </summary>
    public string GlDateTime
    {
        get => GetString(GlDateTimeKeys);
        set => SetWithSynonyms(GlDateTimeKeys, value);
    }

    /// <summary>
    /// <c>STMT_CACHE</c> for the session: <c>true</c> asks the server to use its shared
    /// statement cache for this session, <c>false</c> asks it not to, <c>null</c> (unset) sends
    /// nothing. Only meaningful when the server has the cache enabled (the <c>STMT_CACHE</c>
    /// ONCONFIG parameter); a <c>SET STMT_CACHE</c> statement in the session overrides it.
    /// Synonym: <c>STMT_CACHE</c>. The driver sends the value; its effect on the server has not
    /// been measured.
    /// </summary>
    public bool? StatementCache
    {
        get => GetNullableBool(StatementCacheKeys);
        set => SetNullableBool(StatementCacheKeys, value);
    }

    /// <summary>
    /// <c>IFX_LONGID</c> for the session: <c>true</c> tells the server this client handles
    /// identifiers longer than 18 bytes (up to 128), <c>false</c> that it does not, <c>null</c>
    /// (unset) sends nothing and lets the server decide from the client's protocol version.
    /// Synonym: <c>IFX_LONGID</c>. The driver sends the value; no effect was visible on the test
    /// server (9.59), so treat it as a declaration for servers that act on it.
    /// </summary>
    public bool? LongIdentifiers
    {
        get => GetNullableBool(LongIdentifiersKeys);
        set => SetNullableBool(LongIdentifiersKeys, value);
    }

    /// <summary>
    /// <c>DELIMIDENT</c> for the session. <c>true</c> makes the server treat strings in double
    /// quotes as delimited identifiers (so <c>"order"</c> names a column and only single quotes
    /// delimit string literals), which is what <see cref="IfxCommandBuilder"/> generates;
    /// <c>false</c> (the default) sends nothing and leaves the server's setting.
    ///
    /// <para>The server only checks whether the variable is <i>present</i> - a value of <c>n</c>
    /// switches it on just like <c>y</c> - so <c>false</c> deliberately sends nothing rather than
    /// <c>n</c>. Turning it on changes the meaning of any <c>"abc"</c> used as a string literal.
    /// Synonym: <c>DELIMIDENT</c> (<c>y</c>/<c>n</c> are accepted as values).</para>
    /// </summary>
    public bool DelimitedIdentifiers
    {
        get => GetNullableBool(DelimitedIdentifiersKeys) ?? false;
        set => SetNullableBool(DelimitedIdentifiersKeys, value ? true : null);
    }

    /// <summary>
    /// Client-side only (never sent): whether thousands separators are accepted only in correct
    /// three-digit groups by <see cref="IfxTextConvert"/> (default <c>true</c>). <c>false</c> is
    /// the Informix <c>IFX_NOT_STRICT_THOUS_SEP=1</c> behaviour: accepted wherever they appear.
    /// </summary>
    public bool StrictThousandsSeparator
    {
        get => GetBool(KeyStrictThousandsSeparator, defaultValue: true);
        set => this[KeyStrictThousandsSeparator] = value;
    }

    /// <summary>
    /// The text-conversion rules for a connection with these settings: <see cref="DbDate"/>,
    /// <see cref="GlDate"/>, <see cref="DbCentury"/>, <see cref="GlDateTime"/> and
    /// <see cref="StrictThousandsSeparator"/> on top of <see cref="IfxTextFormat.Default"/>. Pass
    /// it to <see cref="IfxTextConvert"/> to read and write text the way the server does for
    /// this connection; the process environment plays no part.
    /// </summary>
    public IfxTextFormat TextFormat => new()
    {
        DbDate = NullIfEmpty(DbDate),
        GlDate = NullIfEmpty(GlDate),
        DbCentury = NullIfEmpty(DbCentury),
        GlDateTime = NullIfEmpty(GlDateTime),
        StrictThousandsSeparator = StrictThousandsSeparator,
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>Authentication mode; defaults to <see cref="IfxAuthentication.Password"/>.</summary>
    public IfxAuthentication Authentication
    {
        get => TryGetValue(KeyAuthentication, out var v) && v is not null &&
               Enum.TryParse<IfxAuthentication>(v.ToString(), ignoreCase: true, out var mode)
               ? mode : IfxAuthentication.Password;
        set => this[KeyAuthentication] = value.ToString();
    }

    private string GetString(string key) => TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    /// <summary>First non-empty value among a keyword and its synonyms.</summary>
    private string GetString(string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetString(key);
            if (value.Length > 0)
                return value;
        }
        return "";
    }

    /// <summary>Stores under the canonical keyword and drops any synonym, so the two
    /// spellings can never disagree.</summary>
    private void SetWithSynonyms(string[] keys, string value)
    {
        for (var i = 1; i < keys.Length; i++)
            Remove(keys[i]);
        this[keys[0]] = value;
    }

    // Accepts a real bool or the text true/1/yes/on (case-insensitive); anything else is false.
    private bool GetBool(string key) =>
        TryGetValue(key, out var v) && v is not null &&
        (v is bool b ? b : v.ToString()?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on");

    private bool GetBool(string key, bool defaultValue) =>
        TryGetValue(key, out var v) && v is not null
            ? (v is bool b ? b : v.ToString()?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on")
            : defaultValue;

    /// <summary>First recognized value among a keyword and its synonyms: <c>true</c>/<c>1</c>/<c>yes</c>/<c>y</c>/<c>on</c>
    /// or <c>false</c>/<c>0</c>/<c>no</c>/<c>n</c>/<c>off</c>; <c>null</c> when absent or empty.</summary>
    /// <exception cref="ArgumentException">A value that is neither.</exception>
    private bool? GetNullableBool(string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGetValue(key, out var v) || v is null)
                continue;
            if (v is bool b)
                return b;
            string text = v.ToString()?.Trim().ToLowerInvariant() ?? "";
            if (text.Length == 0)
                continue;
            switch (text)
            {
                case "true" or "1" or "yes" or "y" or "on": return true;
                case "false" or "0" or "no" or "n" or "off": return false;
            }
            throw new ArgumentException($"'{v}' is not a valid value for '{keys[0]}'. Expected true/false (or 1/0, yes/no, y/n, on/off).");
        }
        return null;
    }

    private void SetNullableBool(string[] keys, bool? value)
    {
        foreach (var key in keys)
            Remove(key);
        if (value is { } b)
            this[keys[0]] = b;
    }

    private int GetInt(string key) =>
        TryGetValue(key, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : 0;

    // A missing, unparsable or non-positive value falls back to the default.
    private int GetInt(string key, int defaultValue) =>
        TryGetValue(key, out var v) && v is not null && int.TryParse(v.ToString(), out var i) && i > 0 ? i : defaultValue;
}
