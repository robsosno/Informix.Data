# Informix.Data — connection string

The `Informix.Data` provider is configured with an ADO.NET connection string, exactly like
`SqlConnection` or `NpgsqlConnection`:

```csharp
using Informix.Data;

using var cn = new IfxConnection(
    "Host=192.168.88.11;Service=9088;Server=ol_darkstar;Database=stores_demo;" +
    "User ID=informix;Password=secret");
cn.Open();
```

Keys are **case-insensitive** and order does not matter. Every key has a strongly-typed
property on `IfxConnectionStringBuilder`, which is the recommended way to build a string
programmatically (it quotes and escapes values for you):

```csharp
var b = new IfxConnectionStringBuilder
{
    Host = "192.168.88.11",
    Service = "9089",
    Server = "ol_ssl",
    Database = "stores_demo",
    UserId = "root",
    Password = "s3cr3t",
    Authentication = IfxAuthentication.Pam,
    SslConnection = true,
    TrustServerCertificate = true,
};
using var cn = new IfxConnection(b.ConnectionString);
```

Any key the builder does not recognize is **forwarded verbatim** to the server handshake as
an Informix environment property (e.g. `DBPATH`, `IFX_SOC_TIMEOUT`, `SQLH_TYPE`), so the
dozens of Informix environment variables remain reachable without a dedicated property.

Related: [wire-protocol.md](wire-protocol.md) for what happens on the socket once these
settings are applied (handshake, PAM exchange, statement pipeline).

---

## 1. Keyword reference

| Keyword | Synonym | Type | Default | Meaning |
|---------|---------|------|---------|---------|
| `Host` | | string | — | Host name or IP of the Informix server (`IFXHOST`). |
| `Service` | | string | — | TCP port (numeric) **or** a service name resolved via the services database. |
| `Server` | | string | — | Informix `dbservername` (`INFORMIXSERVER`). Enables **sqlhosts** resolution (§2). |
| `Database` | | string | — | Database to open. **Required.** |
| `User ID` | `UID`, `UserId`, `User` | string | — | Login user (Informix `USER`). |
| `Password` | `PWD` | string | — | Login password. |
| `Authentication` | | enum | `Password` | `Password` or `Pam` — see §3. |
| `SSL Connection` | `Encrypt` | bool | `false` | Negotiate TLS against an `onsocssl` listener (§4). |
| `Trust Server Certificate` | | bool | `false` | Accept a server cert that does not chain to a trusted root (self-signed). Only with SSL. |
| `Login Timeout` | | int | `0` | Connect/login timeout in seconds; `0` = infinite. |
| `DB Locale` | `DB_LOCALE` | string | — | `DB_LOCALE`. The only locale key: it selects the code set all text is converted with. There is deliberately no `Client Locale` key — a managed client is UTF-16, so it never needs the server to transcode. |
| `DB Locale Discovery` | | bool | `true` | When no locale is given, look the database's code set up from `sysmaster:sysdbslocale` on first open, so zero-config connects to a non-Latin database get the right encoding. A locale in the connection string (either spelling) always wins and skips the lookup; `false` skips it as well and falls back to the default (ISO-8859-1). |
| `TrimTrailingSpaces` | | bool | `false` | Trim trailing spaces from fixed-width `CHAR`/`NCHAR` values on read (Informix `IFX_TRIMTRAILINGSPACES`). `VARCHAR`/`NVARCHAR`/`LVARCHAR` are never trimmed. |
| `Fetch Buffer Size` | | int | `0` | `FET_BUF_SIZE` in bytes; `0` = classic auto-sizing. Sizes **both** fetch buffers and insert-cursor (`IfxBulkLoader`) buffers, as in ESQL/C: larger = fewer fetch round trips on scans *and* more rows per `SQ_PUT` on bulk loads. |
| `Optofc` | | bool | `false` | OPTOFC — request the first tuple buffer with the cursor open (saves a round trip on var-length result sets). |
| `Large Row Formats` | | bool | `false` | Ask the server for the Informix 15 large-row wire formats (4-byte tuple size for >32 KB rows, 8-byte row ids, 68-byte simple-LOB descriptor). Only a request — the formats used follow the server's reply, so enabling it against an older server changes nothing. Without it the classic formats are used on every server version: Informix 15.0.1.0 advertises the wide formats even to a client that did not ask, and the driver ignores that. Live-checked against 15.0.1.0 with a plain query only; rows over 32 KB and BYTE/TEXT under the wide formats are not yet exercised. |
| `Pooling` | | bool | `true` | Draw connections from the client-side pool keyed by the connection string. |
| `Min Pool Size` | | int | `0` | Connections the pool keeps warm. |
| `Max Pool Size` | | int | `100` | Max concurrent physical connections before `Open()` blocks (up to `Login Timeout`). |
| `Connection Lifetime` | | int | `0` | Max age (seconds) of a pooled connection; `0` = unlimited. |
| `Enlist` | | bool | `true` | Auto-enlist in the ambient `System.Transactions.Transaction` (`TransactionScope`) at `Open()`. Single-connection only: a scope spanning a second resource manager would require XA promotion, which is not implemented and throws. `false` ignores the ambient transaction; explicit `EnlistTransaction` still works. |
| `Isolation Level` | `IsolationLevel` | `IsolationLevel`, or a numeric ODBC `ISOLATIONLEVEL` code (`0`-`5`) | `Unspecified` | Pins the session isolation level at `Open()`. **`Unspecified` sends nothing at all** — see the note below; this is deliberate and is what makes the driver usable against a database without logging. `Chaos` and any other unrecognized name or number (including `6`, the Windows DSN dialog's "Transaction") throw rather than being ignored. |
| `Read Only` | | bool | `false` | Default access mode for every transaction the driver begins in the session — `BeginTransaction()`/`BeginTransaction(IsolationLevel)`, and auto-enlist/explicit `EnlistTransaction`. `true` sends `SET TRANSACTION READ ONLY` right after `SQ_BEGIN` (server-enforced, SQLCODE -878 on a write, live-verified); `false` (the default) sends nothing, so it costs nothing and still works against an unlogged database. Autocommit statements are **not** covered — see `Select Only` below. An explicit `isReadOnly` argument to `BeginTransaction(IsolationLevel, bool)` overrides this keyword for that one transaction either way. |
| `Select Only` | | bool | `false` | Client-side guard — note this is a *different* keyword from `Read Only` above, despite the overlapping name: accepts only plain SELECT statements, rejecting everything else with SQLCODE -878 before it can run — classification comes from the server's own `SQ_DESCRIBE` reply, not SQL-text parsing. Unlike `Read Only`, it also covers autocommit statements, and it is purely client-side (a convenience guard, not a security boundary — see the property's doc comment). Independent of `Read Only`; neither implies the other. |
| `Persist Security Info` | `PersistSecurityInfo` | bool | `false` | Keep the password in `IfxDataSource.ConnectionString`; off by default (ADO.NET norm). |
| `Prepared Statement Cache Size` | `IFX_PREPAREDSTATEMENT_CACHE_SIZE` | int | `0` | Size of the session-level prepared-statement cache (JDBC `preparedStatementCacheSize` parity); `0` = off. Statements released without an explicit `Prepare` are kept server-side and reused on the next identical SQL. |
| `DB Date` | `DBDATE` | string | — | `DBDATE` for the session (`Y4MD-`, `MDY4/`, `DMY2.` …): the DATE format the **server** uses when it converts text to or from a DATE — date literals in SQL text, string parameters bound to DATE columns, `d::LVARCHAR`/`CAST(d AS CHAR)`/`TO_CHAR(d)`. Unset: the server process's own `DBDATE`. `DateOnly`/`DateTime` parameters and DATE values read back are binary and unaffected. Also feeds `IfxConnection.TextFormat` (see below). |
| `GL Date` | `GL_DATE` | string | — | `GL_DATE` (strftime-style DATE format). The server consults it only when no `DBDATE` applies — a server started with `DBDATE` set ignores it — so prefer `DB Date`. |
| `DB Century` | `DBCENTURY` | `R`/`P`/`F`/`C` | — | How the server expands a one- or two-digit year in a DATE/DATETIME literal: present century (default), past, future, closest. Case-sensitive. |
| `GL DateTime` | `GL_DATETIME` | string | — | strftime-style format the server uses when it renders a DATETIME as text. Output only; parsing always reads the ANSI form. |
| `Statement Cache` | `STMT_CACHE` | bool | — | `STMT_CACHE`: `true` = use the server's shared statement cache for this session, `false` = don't, unset = send nothing. Needs the cache enabled on the server (`STMT_CACHE` in ONCONFIG); `SET STMT_CACHE` overrides it. Sent; effect not measured. |
| `Long Identifiers` | `IFX_LONGID` | bool | — | `IFX_LONGID`: `true` = this client handles identifiers over 18 bytes, `false` = it does not, unset = send nothing (the server decides from the protocol version). Sent; no visible effect on IDS 9.59. |
| `Delimited Identifiers` | `DELIMIDENT` | bool | `false` | `DELIMIDENT`: `true` makes double-quoted strings delimited identifiers — what `IfxCommandBuilder`'s `"name"` quoting needs (without it the builder leaves names unquoted). `false` sends **nothing** (the server only tests for the variable's presence, so even `n` would switch it on). Changes the meaning of any `"abc"` used as a string literal. |
| `Strict Thousands Separator` | | bool | `true` | Client-side only, never sent: `false` is `IFX_NOT_STRICT_THOUS_SEP=1` — `IfxTextConvert` accepts thousands separators outside three-digit groups. |


> **Note — the session-environment keywords.** `DB Date`, `GL Date`, `DB Century`, `GL DateTime`,
> `Statement Cache`, `Long Identifiers` and `Delimited Identifiers` are sent to the server in the
> connection handshake as the corresponding Informix environment variables; whatever is left unset
> falls back to the *server process's* environment, so the same SQL text can be accepted by one
> server and rejected by another (`'2026-07-29'` is -1204 against a server started with
> `DBDATE=DMY4/`). Only the handshake is honoured for these — a later `SQ_INFO` message has no
> effect on `DBDATE`/`DELIMIDENT` — so they are fixed for the life of the physical connection, and
> since they are part of the connection string they are part of the pool key. The process
> environment is never read: set them here.
>
> `IfxConnection.TextFormat` (also `IfxConnectionStringBuilder.TextFormat`) turns the same keywords
> into an `IfxTextFormat`, so `IfxTextConvert.FormatDate(d, cn.TextFormat)` writes and
> `ParseDate(text, cn.TextFormat)` reads a DATE exactly as the server does for that connection.
> `IfxTextConvert` called without a format uses `IfxTextFormat.Default`, not the environment.

> **Note — `Isolation Level` is off by default, on purpose.** Informix rejects
> `SET ISOLATION` on a database created **without logging**, so a driver that always issued
> one could not talk to an unlogged database at all. The driver therefore sends the statement
> **only** when the connection string names a level; leave the keyword out and the server's own
> default stands, with no extra round trip. Mapping onto Informix:
> `ReadUncommitted` → `DIRTY READ`, `ReadCommitted` → `COMMITTED READ`,
> `RepeatableRead` and `Serializable` → `REPEATABLE READ` (Informix has no stricter level),
> `Snapshot` → `COMMITTED READ LAST COMMITTED`. This keyword pins the **session** level at
> `Open()` via `SET ISOLATION`, so — unlike `BeginTransaction(IsolationLevel)`, see below — it
> re-applies on every `Open()` and also reasserts itself over a level a previous
> `BeginTransaction(…)` left on a pooled physical connection; a connection string that leaves
> it unspecified inherits whatever that pooled session was last set to.
>
> **`BeginTransaction(IsolationLevel[, bool])` no longer leaks its level onto the session**
> (live-verified 2026-09-14): it issues `SET TRANSACTION ISOLATION LEVEL …` right after
> `SQ_BEGIN` instead of `SET ISOLATION`, and `SET TRANSACTION`'s characteristics last only
> until the transaction ends — Commit or Rollback both restore whatever the session's own
> level was before. `Snapshot` is the one exception: Informix's `SET TRANSACTION` has no LAST
> COMMITTED equivalent, so `BeginTransaction(Snapshot)` still issues `SET ISOLATION TO
> COMMITTED READ LAST COMMITTED` and that **does** leak onto the session after the transaction
> ends, the same as IBM's own drivers.
>
> **`Snapshot` combined with a read-only transaction** (`Read Only=true` or
> `isReadOnly: true`) sends both statements — `SET ISOLATION TO COMMITTED READ LAST
> COMMITTED` followed by `SET TRANSACTION READ ONLY` (two round trips) — and is live-verified
> sound: an ordinary `SELECT` keeps working and every write is rejected with -878, genuinely
> read-only. The one caveat is LAST COMMITTED itself, which needs **both** a server new enough
> to have the feature at all (introduced in IDS **11.10**, "Cheetah") **and** the target table
> using **ROW-level locking** (a `PAGE`-locked table inhibits it) — without both, the isolation
> half quietly behaves as plain `ReadCommitted` instead of skipping locked rows, still correct,
> just without that lock-avoidance benefit. This is not specific to read-only transactions: the
> plain `Isolation Level=Snapshot` keyword and `BeginTransaction(Snapshot)` on their own are
> equally subject to it. (The live test server, IDS 9.59, predates the feature entirely, which
> is what its own tests actually exercised — **not** the `USELASTCOMMITTED` `onconfig`
> parameter: per IBM's docs that parameter only upgrades a *plain* `COMMITTED READ`/`DIRTY
> READ` implicitly and has no bearing on this driver's explicit `LAST COMMITTED` clause.) The
> driver cannot detect or enforce the server version or a table's locking mode from the client
> side.
>
> **`Chaos` throws.** `IsolationLevel.Chaos` (name or, from `System.Transactions`, the
> matching value) has no Informix equivalent and is rejected — by `Isolation Level=Chaos` in
> the connection string, by `BeginTransaction(IsolationLevel.Chaos)`, and by an ambient
> `TransactionScope` whose `IsolationLevel` is `Chaos` — before any statement is sent.
>
> **`Select Only` vs. `Read Only`.** Despite the overlapping name, these are two independent
> keywords covering different scopes, and neither implies the other:
>
> | | `Select Only` | `Read Only=true` |
> |---|---|---|
> | Enforced by | This driver, client-side | The Informix server (SQLCODE -878) |
> | Scope | Every statement, including autocommit | Only inside a transaction the driver began |
> | Also forbids | Nothing beyond non-SELECT statements | DDL, temp tables, `GRANT`/`REVOKE`, `UPDATE STATISTICS`, `RENAME` — even from a SELECT-shaped caller |
> | A hand-written `SET TRANSACTION` | Rejected (it isn't a SELECT) | N/A |
> | Works on an unlogged database | Yes | N/A — `BeginTransaction()` itself needs logging |
>
> Use `Select Only` to catch a caller's own mistake early (an LLM- or tool-generated statement
> that turned out not to be a SELECT, say) on every connection, including outside any
> transaction. Use `Read Only` when a real, server-enforced guarantee for a specific
> transaction matters. Combining both is fine — `Select Only=true` plus a read-only
> transaction still allows a SELECT.

> **Note — synonyms.** The alternate spellings honored by the typed getters are:
> `UID`/`UserId`/`User` (for `User ID`), `PWD` (for `Password`), `Encrypt` (for
> `SSL Connection`), `PersistSecurityInfo`, `IsolationLevel`, and `IFX_PREPAREDSTATEMENT_CACHE_SIZE`.
> Everything else must use its canonical spelling. Setting the `UserId`/`Password`
> properties always stores the canonical keyword and removes the synonym, so the two
> spellings can never disagree; `ToStringWithoutPassword()` strips both `Password`
> and `PWD`.

`IFX_SOC_TIMEOUT` (forwarded verbatim) sets the per-socket receive/send timeout in seconds.

---

## 2. Endpoint resolution

How the driver turns the connection string into a `host:port` (see
`IfxConnection.ResolveEndpoint`):

1. **Numeric `Service` + `Host`** → used directly, no lookup. (Fastest, fully explicit.)
2. **`Server` given** → looked up in **sqlhosts**, which supplies the host and port and may
   also imply **SSL** (an `onsocssl`/`ontlissl` nettype) and **PAM** (an `s=4` option — §3).
3. **`Host` + non-numeric `Service`** → the service name is resolved to a port via the
   services database.

**Validation** (`ValidateSettings`): `Database` is always required; you must supply either a
`Server` (resolvable via sqlhosts) **or** a `Host` — and, without a `Server`, a `Service`.

Supported network types over TCP: `onsoctcp`, `ontlitcp`, `onsocssl`. Shared-memory
(`onipc*`) and DRDA (`dr*`) entries are rejected — this is a SQLI network driver.

---

## 3. Authentication

The `Authentication` keyword selects **how credentials reach the listener**. It does not
change *what* the credentials are (that is always `User ID` / `Password`) — only the wire
exchange.

| Mode | Value | When to use |
|------|-------|-------------|
| Password (default) | `Authentication=Password` | Trusted / non-PAM listeners. The password is sent **inline** in the connection request PDU. |
| PAM | `Authentication=Pam` | Listeners configured with `s=4,pam_serv=(...),pamauth=(...)`. Runs a PAM **challenge/response** exchange after the handshake. |

```
; trusted port — password sent inline
Host=host;Service=9088;Database=stores_demo;User ID=informix;Password=secret

; PAM port — challenge/response
Host=host;Service=9088;Server=ol_darkstar;Database=stores_demo;User ID=root;Password=123456;Authentication=Pam
```

### 3.1 What PAM does automatically

- **`CLNT_PAM_CAPABLE=1`** is always sent in the handshake. A PAM listener rejects a client
  that does not advertise this with error **-1809** ("Client without PAM support connecting
  to PAM port"), so you never set it yourself.
- **sqlhosts `s=4` implies PAM.** When you connect by `Server` and the sqlhosts entry carries
  `s=4`, the driver switches to PAM automatically *unless* you set `Authentication` explicitly.
  So with a properly configured sqlhosts you often don't need the keyword at all.

### 3.2 The default PAM handler

With `Authentication=Pam` and no custom handler, the driver uses `PasswordPamHandler`, which
answers the standard `pamauth=(password)` service:

| PAM prompt | Answer |
|------------|--------|
| hidden prompt — `PromptEchoOff` (typically "Password:") | the `Password` |
| visible prompt — `PromptEchoOn` (typically a user name / token) | the `User ID` |
| informational / error message | no reply |

This covers any PAM stack that is fundamentally a *password check* — `pam_unix`, `pam_ldap`,
`pam_krb5` (password), `pam_sss`, etc. The strength is entirely server-side and invisible to
the client.

> **Warning — the sharp edge.** The default handler distinguishes prompts **only by echo
> type**, not by prompt text. A scheme that issues *two hidden prompts* (e.g. `Password:`
> then `PIN:`) would get the connection password for **both**. Anything past "one password
> (and maybe a user name)" needs a custom handler (§3.3).

### 3.3 Custom PAM handler (MFA / OTP / RADIUS)

For interactive or multi-factor schemes, implement `IIfxPamHandler` and route on the
challenge's `Message` and/or `Type`.
`IfmxPAM` callback — the blessed extension point, not a workaround.

```csharp
sealed class OtpPamHandler(string password, Func<string> getOtp) : IIfxPamHandler
{
    public IfxPamResponse Respond(IfxPamChallenge c) => c.Type switch
    {
        // route by prompt text when several hidden prompts exist
        PamMessageType.PromptEchoOff when c.Message.Contains("code", StringComparison.OrdinalIgnoreCase)
            => new IfxPamResponse(getOtp()),
        PamMessageType.PromptEchoOff => new IfxPamResponse(password),
        PamMessageType.PromptEchoOn  => new IfxPamResponse("myuser"),
        _                            => new IfxPamResponse(null),   // info/error: no reply
    };
}

using var cn = new IfxConnection(
    "Host=host;Service=9088;Server=ol_darkstar;Database=stores_demo;Authentication=Pam");
cn.PamHandler = new OtpPamHandler("s3cr3t", () => TotpGenerator.Now());
cn.Open();
```

The exchange loops over **any number** of challenges of any type until the server accepts;
return `IfxPamResponse.Terminate()` from `Respond` to abort and close the connection.

On an `IfxDataSource`, set a source-wide handler with
`IfxDataSourceBuilder.UsePamHandler(...)`; a bound connection's own `PamHandler` (if set)
takes precedence.

---

## 4. TLS / SSL

Set `SSL Connection=true` (or `Encrypt=true`) to speak TLS to an Informix `onsocssl`
listener. `onsocssl` is **implicit TLS** — the handshake runs immediately on connect, before
any SQLI byte, and the whole session (including PAM) is encrypted.

```
; PAM over TLS — the common production shape for a remote listener
Host=192.168.88.11;Service=9089;Server=ol_ssl;Database=stores_demo;User ID=root;Password=123456;Authentication=Pam;SSL Connection=true;Trust Server Certificate=true
```

- `Trust Server Certificate=true` accepts a cert that cannot be validated against a trusted
  root (self-signed lab certs). Leave it **off** in production and trust the server CA
  instead — the semantics match SqlClient/Npgsql.
- Certificate host-name checking uses `Host` (SNI/`TargetHost`).
- SSL and PAM compose freely: PAM runs inside the encrypted channel.
- Connecting by `Server` to a sqlhosts `onsocssl`/`ontlissl` entry turns SSL on
  automatically.

---

## 5. Security notes

- The password is **never written to a log**. `IfxConnectionStringBuilder.ToStringWithoutPassword()`
  returns the string with the password removed; the driver logs the connection through it.
- `IfxDataSource.ConnectionString` strips the password unless `Persist Security Info=true`.
- `Persist Security Info` (and its `PersistSecurityInfo` synonym) never affects the
  credentials actually sent to the server — only whether they survive in the retained string.

