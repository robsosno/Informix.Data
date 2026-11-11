# Informix.Data — usage guide for ODBC / CSDK-SQLI / DRDA veterans

This guide is for developers who have talked to Informix from .NET before — through the
**Informix ODBC driver** (`System.Data.Odbc`), the **legacy Client SDK provider**
(`IBM.Data.Informix`, the SQLI one), or a **DRDA provider** (`IBM.Data.Informix`/`IBM.Data.DB2`
common-drivers build, or `HCL.Data.Db.Informix`). It maps what you already know onto
`Informix.Data` and then shows the features that are new here.

`Informix.Data` is a **fully managed ADO.NET provider** (`net8.0`, no CSDK, no ODBC DSN, no
IBM DB2 client). It speaks the native **SQLI** wire protocol directly over a socket — the same
protocol family the old Client SDK provider used, not DRDA. If you know the classic
`IBM.Data.Informix` provider, most of your code compiles after a namespace swap; the rest of
this document is the delta.

See also: [connection-string.md](connection-string.md) (every keyword),
[wire-protocol.md](wire-protocol.md) (what goes on the socket).

---

## 1. Where you're coming from — the 30-second orientation

| You used… | Namespace / access | Wire | Moving here means |
|---|---|---|---|
| **Informix ODBC** | `System.Data.Odbc` + a DSN / `Driver=` string | SQLI (in the CSDK) | Drop the DSN. Types become `Ifx*` (strongly typed) instead of `Odbc*`. Connection string is keyword-based, no `Driver={IBM INFORMIX...}`. |
| **Legacy Client SDK provider** | `IBM.Data.Informix` (SQLI build) — `IfxConnection`, `IfxCommand`, `IfxType`… | SQLI | **Near drop-in.** Same type names, same `Ifx*` value structs. Change the `using`, adjust the connection string keys. |
| **DRDA provider** | `IBM.Data.Informix` (common) / `IBM.Data.DB2` / `HCL.Data.Db.Informix` | DRDA (DB2 protocol) | Behaviour differences: this is SQLI, so Informix-native features (BYTE/TEXT, smart LOBs, SPL out params, sqlhosts, `dbinfo`) behave like ESQL/C, not like DB2. Type names line up with the *legacy* provider, not the DRDA one. |

The single most important fact for **CSDK-SQLI users**: the public type names were chosen to
match the classic provider — `IfxConnection`, `IfxCommand`, `IfxDataReader`, `IfxParameter`,
`IfxDataAdapter`, `IfxCommandBuilder`, `IfxTransaction`, `IfxError`, `IfxException`, and the
value structs `IfxDecimal`, `IfxDateTime`, `IfxTimeSpan`, `IfxMonthSpan`. The `IfxType` enum
keeps the classic member names too (`Char`, `LVarChar`, `Clob`, `Blob`, `Byte`, `Text`, `Int8`,
`Serial8`, `Set`, `MultiSet`, `List`, `Row`, `Money`, …). Most porting is mechanical.

---

## 2. The unchanged core — this still looks like ADO.NET

Nothing about the basic loop changed. If you have this today (any of the three drivers, modulo
the type prefix), it works verbatim:

```csharp
using Informix.Data;

using var cn = new IfxConnection(
    "Host=192.168.88.11;Service=9088;Server=ol_darkstar;Database=stores_demo;" +
    "User ID=informix;Password=secret");
cn.Open();

using var cmd = cn.CreateCommand();
cmd.CommandText = "SELECT customer_num, fname, lname FROM customer WHERE state = ?";
cmd.Parameters.Add(new IfxParameter { Value = "CA" });

using var r = cmd.ExecuteReader();
while (r.Read())
    Console.WriteLine($"{r.GetInt32(0)} {r.GetString(1)} {r.GetString(2)}");
```

- **`?` positional placeholders** work exactly as in ESQL/C, the ODBC driver and the CSDK
  provider. (Named placeholders are *new* and optional — §4.1.)
- **`CommandType.StoredProcedure`**, output/return parameters, `IfxDataAdapter` +
  `IfxCommandBuilder`, `IfxTransaction` with isolation levels and savepoints, BYTE/TEXT and
  smart-LOB columns — all present and behave the SQLI way.
- **`DbProviderFactories`**: register `InformixClientFactory.Instance` under the invariant name
  `"Informix.Data"` and existing factory-based/DbConnectionStringBuilder code keeps working.

The rest of this guide is only the **new** surface.

---

## 3. Connecting — the connection-string deltas

Full keyword reference is in [connection-string.md](connection-string.md). The parts that trip
up migrators:

### From ODBC
- No `Driver={...}`, no DSN, no `DBPATH`-only connect. You give `Host`, `Service` (port),
  `Server` (= `INFORMIXSERVER`), `Database`, `User ID`, `Password`.
- The old `Protocol=onsoctcp`/`Options` mess is gone. TLS is one keyword: `SSL Connection=true`
  (synonym `Encrypt=true`) against an `onsocssl` listener.

### From the CSDK provider
- Same idea, ADO-standard keys. `Host` / `Service` / `Server` / `Database` / `User ID` /
  `Password`. Unrecognized keys are **forwarded verbatim** as Informix environment properties
  (`IFX_SOC_TIMEOUT`, `DBPATH`, …), so niche CSDK env vars remain reachable.

### From DRDA
- DRDA connect strings (`Database=…;Server=host:port;`) don't carry over; use the SQLI shape
  above. There is **no** `CurrentSchema`, `ConnectTimeout`-style DB2 knob — see the reference.

### sqlhosts and PAM still work
- Connecting by `Server` triggers **sqlhosts** resolution (host, port, and — from `s=4` /
  `onsocssl` options — PAM and TLS auto-enable), exactly like ESQL/C. See
  [connection-string.md](connection-string.md) §2–§4.

### New locale behaviour you'll notice immediately (ODBC/CSDK code-page veterans)
If you ever fought `DB_LOCALE`/`CLIENT_LOCALE`/`GL_USEGLU` mismatches:

- The client is **managed UTF-16**. The *only* conversion performed is .NET string ⇆ the
  **`DB_LOCALE`** code set. There is **no `Client Locale` connection key** and `CLIENT_LOCALE`
  is never sent — there's no client-locale transcode step to misconfigure. A `CLIENT_LOCALE`
  carried over from an ODBC DSN is dropped during migration.
- **`DB Locale` auto-discovery**: if you don't set `DB Locale`, the driver looks the database's
  code set up from `sysmaster:sysdbslocale` on first open (default on). So zero-config connects
  to a non-Latin database get the right encoding without you knowing its locale. Turn it off
  with `DB Locale Discovery=false`. A locale you *do* put in the connection string skips the
  lookup entirely — the Informix-native spelling `DB_LOCALE=...` counts, so a migrated ESQL/C
  or ODBC connection string keeps its locale and costs no extra `sysmaster` connection.

```csharp
// Explicit is still fine; discovery just means you can omit it.
var b = new IfxConnectionStringBuilder
{
    Host = "192.168.88.11", Service = "9088", Server = "ol_darkstar",
    Database = "stores_demo", UserId = "informix", Password = "secret",
    DbLocale = "pl_pl.8859-2",   // or omit and let discovery find it
};
```

---

## 4. New features

### 4.1 Named parameters (`@name` / `:name`)

The classic providers were **positional-only** (`?`). You can now write named placeholders and
bind by name — the driver rewrites them to native `?` before sending (a SQL-aware tokenizer, so
markers inside strings, comments, and the `dbname@server:object` qualifier are left alone).

```csharp
cmd.CommandText = "SELECT * FROM orders WHERE customer_num = @cust AND ship_date > @since";
cmd.Parameters.Add(new IfxParameter { ParameterName = "@cust",  Value = 104 });
cmd.Parameters.Add(new IfxParameter { ParameterName = "since",  Value = new DateOnly(2026, 1, 1) });
```

- Both `@name` and `:name` are accepted; matching against the collection is **sigil-tolerant and
  case-insensitive** (`@cust`, `:cust`, `cust` all match).
- A name reused in the text binds its value at **each** occurrence.
- You **cannot mix** named and positional `?` in one statement (throws) — pick one style per
  command. Existing `?` code is untouched.

### 4.2 Default read types for date/time — a deliberate breaking change

Reading temporal columns now yields the **modern .NET types** by default. If you're moving code
that assumed `DateTime` for everything, read this.

| Informix column | Old default | **New default** |
|---|---|---|
| `DATE` | `DateTime` | **`DateOnly`** |
| time-only `DATETIME HOUR TO …` | `DateTime` | **`TimeOnly`** |
| day-time `INTERVAL` (e.g. `DAY TO SECOND`) | `IfxTimeSpan` / `DateTime` | **`TimeSpan`** |
| date-bearing `DATETIME YEAR TO …` | `DateTime` | `DateTime` (unchanged) |
| year-month `INTERVAL` | `IfxMonthSpan` | `IfxMonthSpan` (unchanged) |

So `reader.GetValue(dateOrdinal)` returns a boxed `DateOnly`, and `GetFieldType` reports
`typeof(DateOnly)`. To keep the old shape, ask for it explicitly — the legacy types are still
reachable:

```csharp
DateOnly d  = reader.GetFieldValue<DateOnly>(0);   // the new default
DateTime dt = reader.GetFieldValue<DateTime>(0);   // old behaviour, on demand
```

`reader.GetDateTime(...)` still returns `DateTime` (widening `DateOnly`→midnight). On the **bind**
side this is purely additive: `DateOnly`, `TimeOnly`, `TimeSpan`, plus the Informix structs
`IfxTimeSpan`/`IfxMonthSpan`, are now all bindable as parameter values alongside `DateTime`.

### 4.3 `TrimTrailingSpaces` — CHAR padding

CSDK/ODBC users know Informix pads `CHAR(n)` to full width. `IFX_TRIMTRAILINGSPACES` parity is
here as an **opt-in** connection-string key:

```
...;TrimTrailingSpaces=true
```

- **Off by default** — with it off, `CHAR`/`NCHAR` decode is byte-for-byte what you got before,
  padding included.
- On, trailing spaces are trimmed from **`CHAR`/`NCHAR` only**. `VARCHAR`/`NVARCHAR`/`LVARCHAR`
  are **never** trimmed (matching the JDBC driver — its VARCHAR flag only affected internal
  numeric coercion, never `getString()`).

### 4.4 JSON and BSON columns

The built-in `JSON` and `BSON` types now decode as **JSON text through the ordinary reader**, so
Dapper/row-mappers on `GetString`/`GetValue` just work — the standard ADO.NET approach for binary JSON.

```csharp
string doc = reader.GetString(ordinal);                 // JSON or BSON → JSON text
using var dom = reader.GetFieldValue<JsonDocument>(ord); // parsed DOM (opt-in)
byte[] raw    = reader.GetFieldValue<byte[]>(ord);       // raw BSON bytes / JSON UTF-8 (escape hatch)
```

`GetDataTypeName` still reports `"Json"`/`"Bson"` so provider-aware code can tell them apart.
To **write**, bind a JSON `string` with `IfxType.Json`, or a JSON `string` (auto-encoded) / raw
`byte[]` with `IfxType.Bson`. The public `BsonMediaTypeFormatter` (`ToJson`/`ToBson`, built on
`System.Text.Json`, no external BSON package) is the codec. Wire-level detail:
[wire-protocol.md](wire-protocol.md) §8.3.

### 4.5 Bulk load via insert cursor (the `USEPUT` capability, as a first-class API)

The CSDK provider gated bulk insert behind `IFX_USEPUT`. Here it's an explicit type,
`IfxBulkLoader`, created off the connection:

```csharp
using var loader = cn.CreateBulkLoader("INSERT INTO sales VALUES (?, ?, ?)");
loader.DefineParameters(IfxType.Integer, IfxType.VarChar, IfxType.Decimal);
long inserted = loader.Execute(l =>
{
    foreach (var row in source)
        l.Put(row.Id, row.Name, row.Amount); // also: Put(object?[]) / Put(IDataRecord)
});
```

- `Open` / `Put` / `Flush` / `Complete` / `Discard` are also available for manual control; wrap
  in an `IfxTransaction` and set `loader.Transaction`. `Put(IDataRecord)` streams straight from
  another reader. `RowsPut`, `RowsInserted` and `PendingRows` track progress — `RowsInserted`
  counts what the server has confirmed, so it trails `RowsPut` by whatever is still buffered.
- **Two engines, picked for you.** The native `SQ_PUT` insert cursor — raw fixed-width tuple
  images, no per-row type words — is used when the INSERT's columns are *all* fixed width.
  Otherwise the loader falls back to the chained `SQ_BIND`/`SQ_EXECUTE` batch engine (the same
  one `IfxRowBatch` uses), which still costs one round trip per buffer-full instead of per row.
  The API is the same either way; [msg_flow.md](msg_flow.md) §5 has both flows.
- `Mode = IfxBulkLoaderMode.PlainInserts` turns buffering off and executes every `Put` as its
  own round trip — for pinpointing the bad row in a failing batch and for benchmarking buffered
  against plain inserts, not for production loads. `Mode` and `HoldCursor` cannot be changed
  once the loader has opened.
- **Wire packing is automatic** and follows ESQL/C: rows are packed into `SQ_PUT` round trips
  up to the connection-string **`Fetch Buffer Size`** (`FET_BUF_SIZE`), which sizes
  insert-cursor buffers too — a larger value packs more rows per round trip. You don't tune
  this per load. There is no `BatchSize`/`OnBatch` cadence knob — a large load's own `foreach`
  is already the natural place to count rows, exactly as the ESQL/C original does; see below.

**Committing periodically — avoiding `-458` "Long transaction aborted".** On a logged
database, loading a large volume inside one transaction fills the logical logs and the
engine kills the load. The fix is the classic `commit work; begin work` cadence:
commit after each portion the engine can absorb. Drive it from your own loop — count rows,
`Flush()` the portion, then commit:

```csharp
using var loader = cn.CreateBulkLoader("INSERT INTO sales VALUES (?, ?, ?)");
loader.DefineParameters(IfxType.Integer, IfxType.VarChar, IfxType.Decimal);
loader.HoldCursor = true;                     // required — see below

var tx = cn.BeginTransaction();
loader.Transaction = tx;
int n = 0;
long inserted = loader.Execute(l =>
{
    foreach (var row in source)
    {
        l.Put(row.Id, row.Name, row.Amount);
        if (++n % 10_000 == 0)                // the portion size, your call
        {
            l.Flush();                        // send it…
            tx.Commit();                      // …then make it durable
            Console.WriteLine($"{l.RowsInserted:N0} rows loaded…");
            l.Transaction = tx = cn.BeginTransaction();
        }
    }
});
tx.Commit();                                  // the tail
```

**`HoldCursor` — required for the pattern above.** Default `false` (matching ESQL/C):
the insert cursor is declared *without* `WITH HOLD`, so a `Commit`/`Rollback` while the
loader is still active closes it server-side (verified live against IDS 9.59 — commit and
rollback close a non-hold cursor identically, not just commit). Set `HoldCursor = true`
before the first `Put`/`Open` to keep loading across those commits. `Commit`/`Rollback`
never silently drop rows either way: a commit **flushes whatever is buffered first** (it
joins the transaction that is committing), and a rollback **discards it** (it was never
sent, so there is nothing to undo) — you never lose a `Put`. What `HoldCursor` decides is
only whether the *cursor* survives to accept more rows afterward. If it doesn't
(`HoldCursor` false and you keep loading), the next `Put`/`Flush` throws a clear
`InvalidOperationException`; `Complete()` still returns the correct `RowsInserted`.

> **On a logged database, `HoldCursor=false` also needs an active transaction just to
> open** — otherwise `Open()`/the first `Put()` fails with Informix **-255** ("not in
> transaction"), live-verified against IDS 9.59. This only bites the simplest form —
> `CreateBulkLoader(...)` → `Put` → `Complete()` with no transaction at all — which is
> exactly the CSDK/ODBC-era default too (a non-hold cursor is meaningless without a
> transaction to bound it). An *unlogged* database is unaffected either way. Fix it with
> either `loader.Transaction = cn.BeginTransaction()` or `loader.HoldCursor = true`.

### 4.6 `IfxDataSource` — the modern connection factory (Npgsql-style)

New entry point that owns the pool, the logger, and the telemetry config; hand it around instead
of passing connection strings. Pooling itself is on by default on plain `IfxConnection` too, but
`IfxDataSource` is where logging/tracing/PAM get configured once:

```csharp
await using var source = new IfxDataSourceBuilder(connectionString)
    .UseLoggerFactory(loggerFactory)          // Microsoft.Extensions.Logging
    .EnableParameterLogging()                 // opt-in: log parameter values (off by default)
    .UsePamHandler(myPamHandler)              // source-wide PAM/MFA handler
    .UseConnectionInitializationSql("SET LOCK MODE TO WAIT 5")
    .ConfigureTracing(t => /* ActivitySource opts */)
    .Build();

using var cn = source.OpenConnection();       // IfxConnection from the pool
var (total, idle, busy) = source.Statistics;  // live pool counters
```

`IfxDataSource.Create(connectionString)` is the one-liner form. `Clear()` drains the pool.

### 4.7 Structured logging and OpenTelemetry

Wire the driver into your observability stack — no more `IFX_WIRE_TRACE` env-var spelunking as
the only option (that still exists for raw wire dumps).

- **Logging**: pass an `ILoggerFactory` to the data source (§4.6). Passwords are never logged;
  parameter values only when you opt in via `EnableParameterLogging`.
- **Tracing**: `ActivitySource` named **`"Informix.Data"`** — add it to your tracer provider
  (`AddSource("Informix.Data")`). Each command becomes a span.
- **Metrics**: `Meter` named **`"Informix.Data"`**, emitting OpenTelemetry semantic-convention
  instruments — `db.client.operation.duration`, `db.client.operation.failed`,
  `db.client.connection.count` / `.max` / `.pending_requests` / `.timeouts` / `.create_time`,
  tagged `db.system.name=informix` and the pool name.

```csharp
tracerProviderBuilder.AddSource("Informix.Data");
meterProviderBuilder.AddMeter("Informix.Data");
```

### 4.8 Generated serial values without a second round trip

With ODBC/CSDK, reading the auto-increment value an INSERT just generated meant a
follow-up `SELECT dbinfo('sqlca.sqlerrd1') …` (or `dbinfo('serial8')` /
`dbinfo('bigserial')`). The server actually sends these values back with the INSERT's
completion reply, and `IfxCommand` now surfaces them directly:

```csharp
cmd.CommandText = "INSERT INTO orders (customer_num) VALUES (?)";
cmd.ExecuteNonQuery();

long id = cmd.LastSerialKind switch
{
    IfxSerialKind.Serial    => cmd.LastSerial,     // SERIAL     → int
    IfxSerialKind.Serial8   => cmd.LastSerial8,    // SERIAL8    → long
    IfxSerialKind.BigSerial => cmd.LastBigSerial,  // BIGSERIAL  → long
    _                       => 0,                  // no auto-increment column
};
```

Only `ExecuteNonQuery` updates these properties; `LastSerialKind` tells you which one
(if any) the statement filled, so you don't probe all three.

### 4.9 `Unprepare()` / `IsPrepared` — dropping a held prepare early

`IfxCommand.Prepare()` holds its server-side prepared statement across executions
until `CommandText`/`Connection` changes, the command is disposed, or the connection
closes. When hand-tuned code wants to drop it sooner — without any of those triggers —
call `Unprepare()`; `IsPrepared` reports whether the command is currently holding one.
Provider-specific, Npgsql-inspired (`NpgsqlCommand.Unprepare`/`IsPrepared`); plain
ADO.NET has no un-prepare, and the classic IBM DB2/Informix provider has no equivalent,
so there is nothing to change when migrating from it.

```csharp
cmd.Prepare();
cmd.ExecuteNonQuery();
// ... done with this shape for a while ...
cmd.Unprepare();          // releases the server-side statement now, not at Dispose/Close
Debug.Assert(!cmd.IsPrepared);
```

`Unprepare()` is a no-op (not an error) if the command was never prepared, was already
unprepared, or the held statement was already dropped by a `CommandText`/`Connection`
change. It throws if a data reader from this command's prepared statement is still open.

### 4.10 ODBC catalog-metadata parity — `ServerType`, pool counts, `ProcedureColumns`

Four more `Informix.Net.Core` members closed out. That provider is a thin ODBC wrapper
(`SQLGetInfo`/`SQLColAttribute`/`SQLGetEnvAttr`/`SQLProcedureColumns` calls into the native CSDK
driver), so each one had to be re-derived from what SQLI actually gives a managed client rather
than ported — and for two of them, re-deriving turned out not to be possible without guessing.

**`IfxConnection.ServerType`** — the server product name (e.g. `"Informix Dynamic Server"`). The
reference forwards to `SQLGetInfo(SQL_DBMS_NAME)`; we derive it instead from the ASF handshake's
raw version string (`ServerInfo.VersionNumber`, e.g.
`"Informix Dynamic Server Version 9.59.FC5"`), splitting at `" Version "`.

**`GetIdleConnectionsCount` / `GetActiveConnectionsCount`** — properties despite the `Get…` name,
reporting the pool this connection would use — resolved the same way `Open()` resolves it,
not a live handle on this instance. `0` when `Pooling=false`; also `0`, not a throw,
for a connection that has never been opened and whose string has no pool yet,
since the counts describe the pool rather than this connection. Sampled independently of each other
and therefore advisory under concurrency.

**`GetSchema("ProcedureColumns")`** — ODBC's `SQLProcedureColumns`. Adds
`COLUMN_TYPE`/`DATA_TYPE`/`TYPE_NAME`/`COLUMN_SIZE`/`DECIMAL_DIGITS`/`NULLABLE` to the five
columns `ProcedureParameters` already has, over the same `sysprocedures`/`sysproccolumns` rows.
`COLUMN_TYPE` is `sysproccolumns.paramattr` passed through verbatim — the catalog already speaks
ODBC's numbering, live-verified against IDS (14.10 engine): `1` = IN (`SQL_PARAM_INPUT`),
`2` = INOUT (`SQL_PARAM_INPUT_OUTPUT`), `3` = a `RETURNING` value (`SQL_RESULT_COL`), `4` = OUT
(`SQL_PARAM_OUTPUT`), `5` = return value (`SQL_RETURN_VALUE`), `0` = unknown. So OUT/INOUT SPL
parameters are reported with their real mode, and the routine's `RETURNING` values appear as
rows too — with an empty `PARAMETER_NAME` and, in `ORDINAL_POSITION` (`paramid`) order,
*before* the parameters, which is simply the catalog's own ordering. This is also exactly what
the reference JDBC driver's `getProcedureColumns` does with `paramattr`. The server-side
`ifx_param_types`/`ifx_ret_types` helpers that JDBC code additionally calls are deliberately
not used here: they only feed its display text (`TYPE_NAME`/`REMARKS`), they carry *less*
detail than the numeric `paramtype`/`paramxid`/`paramlen` catalog columns (`varchar` without
the length, `decimal` without precision/scale), and consuming them means comma-splitting free
text. The one thing they render better — `DATETIME`/`INTERVAL` qualifiers — is decoded from
`paramlen` directly, so `TYPE_NAME` reads e.g. `datetime year to second`, identical to
`ifx_ret_types` output.


**`GetSchema("Columns")`** — keeps `COLUMN_TYPE`/`COLUMN_LENGTH` as the raw `syscolumns.coltype`/
`collength` words (NOT NULL flag and packed lengths included) and appends the decoded view
ODBC's `SQLColumns` gives: `DATA_TYPE` (an `IfxType`), `TYPE_NAME`, `COLUMN_SIZE`,
`DECIMAL_DIGITS`, `NULLABLE`. `syscolumns.extended_id` is what separates BLOB (10), CLOB (11),
BOOLEAN (5), LVARCHAR (1) and JSON/BSON (25/26), which all share `coltype` 40/41 — without it a
BLOB column would read as BOOLEAN. `VARCHAR(max,min)` packs `(min<<8)|max` and reports `max`;
`DECIMAL(p)` with no scale (scale byte 255) reports `DECIMAL_DIGITS` as `NULL`; smart LOBs report
`COLUMN_SIZE` `NULL` (their `collength` 72 is the locator, not a size). Live-verified against IDS
with a 16-column table covering every case above.

### 4.11 SPL routines with `OUT` / `INOUT` parameters

Informix has *two* ways a routine hands values back, and they are not the same thing:

* **`RETURNING` values** — the classic Informix shape. They arrive as a one-row result set,
  so you can read them with `ExecuteReader`/`ExecuteScalar`, or let the driver assign them
  to `ParameterDirection.Output` parameters (which take no argument slot). Unchanged.
* **`OUT` / `INOUT` parameters** — declared arguments the routine writes back
  (Informix 11.50 / CSDK 3.50.xC4 and later). These now work too.

```csharp
// CREATE PROCEDURE test_proc(INOUT arg1 int, OUT arg2 int, INOUT arg3 int) returning int;
using var cmd = conn.CreateCommand();
cmd.CommandType = CommandType.StoredProcedure;
cmd.CommandText = "test_proc";

var ret  = new IfxParameter("ret",  IfxType.Integer) { Direction = ParameterDirection.ReturnValue };
var arg1 = new IfxParameter("arg1", IfxType.Integer) { Direction = ParameterDirection.InputOutput, Value = 5 };
var arg2 = new IfxParameter("arg2", IfxType.Integer) { Direction = ParameterDirection.Output };
var arg3 = new IfxParameter("arg3", IfxType.Integer) { Direction = ParameterDirection.InputOutput, Value = 10 };
cmd.Parameters.AddRange(new[] { ret, arg1, arg2, arg3 });

cmd.ExecuteNonQuery();          // -> ret 15, arg1 1, arg2 2, arg3 3
```

The rules:

* **Use `ExecuteNonQuery`.** Informix produces exactly one set of values per execution of
  such a routine and cannot also stream a result set, so the values come back through the
  parameters. `ExecuteReader` on one throws with that explanation rather than a bare -9752.
* **Give every `Output` parameter an `IfxType`** (or seed its `Value`). The server needs the
  slot's type to know what to write into it, and an unset direction-`Output` parameter has
  no other source for it.
* **A `ReturnValue` parameter is not an argument.** Everything else in the collection is,
  in declaration order — one parameter per declared argument, `OUT` ones included.
* An `OUT` argument the routine never assigns comes back as `null`.
* The ODBC call escape works as written in IBM's documentation:
  `cmd.CommandText = "{? = call test_proc(?,?,?)}"` — the leading `?` is the return value,
  and the first parameter receives it whatever direction you gave it. The driver rewrites
  the escape to `EXECUTE PROCEDURE`; the server does not understand it. Plain
  `{call proc(?,?)}` works too.
* Needs a server that grants capability bit 52 (any modern IDS; check
  `IfxConnection.ServerCapabilities.IsNamedParametersSupported`).

`GetSchema("ProcedureColumns")` (§4.10) tells you which mode each parameter of a routine
actually has, if you need to discover it rather than hard-code it.


### 4.12 Session environment — `DB Date`, `Delimited Identifiers`, … and `IfxTextConvert`

An Informix server reads a set of environment variables **per session**, from a block the client
sends in the connection handshake (`select * from sysmaster:sysenvses where envses_sid =
dbinfo('sessionid')` shows it). Whatever the client does not send falls back to the *server
process's* own environment — so `insert into t values ('2026-07-29')` works against a server
started with `DBDATE=Y4MD-` and fails with -1204 against one started with `DMY4/`. The old
clients (ESQL/C, ODBC, JDBC) filled the block from their own environment or properties;
this driver used to send none of it. It now takes the same settings from the **connection
string**, and only from there — the process environment is never read:

| Keyword (synonym) | Sent as | Notes |
| --- | --- | --- |
| `DB Date` (`DBDATE`) | `DBDATE` | The DATE format the server uses for text ⇄ DATE. Binary DATE parameters/results are unaffected. |
| `GL Date` (`GL_DATE`) | `GL_DATE` | Used by the server only when no `DBDATE` applies. |
| `DB Century` (`DBCENTURY`) | `DBCENTURY` | `R`/`P`/`F`/`C` for two-digit years in literals. |
| `GL DateTime` (`GL_DATETIME`) | `GL_DATETIME` | DATETIME → text only. |
| `Statement Cache` (`STMT_CACHE`) | `STMT_CACHE=1/0` | Server needs the cache enabled. Sent; effect not measured. |
| `Long Identifiers` (`IFX_LONGID`) | `IFX_LONGID=1/0` | Sent; no visible effect on IDS 9.59. |
| `Delimited Identifiers` (`DELIMIDENT`) | `DELIMIDENT=y` | `true` only; `false` sends nothing (the server tests presence, so `n` would turn it on). `IfxCommandBuilder` quotes the names it writes only when this is on (§4.15). |

Unset keywords send nothing, so a connection string that does not use them behaves exactly as
before. They are fixed for the life of the physical connection (a later `SQ_INFO` does not take
effect for `DBDATE`/`DELIMIDENT`) and are part of the pool key.

**Behaviour change in `IfxTextConvert`.** A call without an `IfxTextFormat` used to snapshot
`DBDATE`, `GL_DATE`, `DBCENTURY`, `GL_DATETIME` and `IFX_NOT_STRICT_THOUS_SEP` from the process
environment; it now uses `IfxTextFormat.Default` (`MDY4/`, `.`, `,`). To convert the way the
server does for a connection, pass `connection.TextFormat` (built from the keywords above plus
`Strict Thousands Separator`). `IfxTextFormat.FromEnvironment()` was removed — a program that
wants the shell's variables reads them itself and builds an `IfxTextFormat`:

```csharp
var cn = new IfxConnection("...;DB Date=Y4MD-");
string text = IfxTextConvert.FormatDate(date, cn.TextFormat);   // 2026-07-29, as the server prints it
```

### 4.13 Smart-LOB parameters and a statement that fails — use a transaction

Binding a `Clob`/`Blob` parameter is two steps: the driver first creates the smart large object
on the server and uploads the value (`ifx_lo_create` + `SQ_LODATA` + `ifx_lo_close`, one round trip
each), and only then runs your statement with the LOB's 72-byte locator as the parameter. That
upload is **not part of the statement**. If the statement then fails — a duplicate key on the
`INSERT`, say — the LOB is already in the sbspace and nothing refers to it.

Measured against IDS (`oncheck -pe` on the sbspace, a 1,000,000-byte `BLOB` per attempt):

* a successful `INSERT` adds one LOB of 496 pages;
* each **failed** `INSERT` (SQLCODE -268) adds another one, and it **stays**: `DELETE` of the
  row and `DROP TABLE` do not reclaim it, because no row ever referenced it;
* `ifx_lo_release` on that LOB does not remove it either — the failed statement apparently
  leaves the server holding a reference, so the release is a no-op (it does delete a LOB that was uploaded
  and never used in a statement, which is why it is not simply "broken");
* inside an explicit transaction that is **rolled back**, the LOB is removed with it.

So if a statement with a LOB parameter can fail and the space matters, run it in a transaction and
roll back on failure:

```csharp
using var tx = cn.BeginTransaction();
try
{
    cmd.Transaction = tx;
    cmd.ExecuteNonQuery();     // Clob/Blob parameter inside
    tx.Commit();
}
catch
{
    tx.Rollback();             // takes the uploaded LOB with it
    throw;
}
```

This needs a database with logging (`BeginTransaction` is rejected on an unlogged one); there is
nothing to roll back to otherwise, and orphaned LOBs have to be cleaned up on the server. The
driver does not wrap such statements in a transaction of its own, so autocommit behaves as it
always did: the statement either stores the LOB or leaves it behind. Not measured: an `UPDATE`
that matches no row, or a statement that fails before it executes, with a LOB parameter.

### 4.14 `IfxParameter.SmartLobColumn` — a smart LOB in the sbspace its column asks for

A `Clob`/`Blob` parameter is uploaded as a new smart large object before the statement runs (§4.13),
and the driver creates it with the default create-time spec: the server's default sbspace
(`SBSPACENAME`), whatever the column says. A column declared with its own storage —

```sql
create table docs (id int, body blob) put body in (sbspace2)
```

— is therefore **not** honoured: measured against IDS, a plain `insert into docs values (?, ?)` put the
LOB in `sbspace`, not `sbspace2`. (The row still reads back correctly; only where the bytes live, and the
column's other storage settings, are lost.)

Set `SmartLobColumn` to `"table.column"` and the driver first asks the server to fill the create spec in
from that column (`ifx_lo_col_sbinfo`), then creates the LOB with it:

```csharp
cmd.CommandText = "insert into docs values (?, ?)";
cmd.Parameters.Add(new IfxParameter("id", 1));
cmd.Parameters.Add(new IfxParameter("body", bytes) { IfxType = IfxType.Blob, SmartLobColumn = "docs.body" });
```

The same insert then lands in `sbspace2` (live-verified, content round-trips). It costs one extra round
trip per LOB. A name that is not a column of the connected database fails the command (SQLCODE -217)
before anything is uploaded, rather than quietly falling back to the default sbspace. Left `null` (the
default) nothing changes. It is opt-in because the driver cannot know the target column from the
statement text alone.

### 4.15 The command builder, column qualifiers, the driver's own value types, scroll cursors

Four gaps closed together; each was found by running the demo programs.

* **`IfxCommandBuilder` / `DbCommandBuilder` derive INSERT, UPDATE and DELETE.** The server's describe
  reply carries no table name, so the base table of a one-table `SELECT ... FROM [db:][owner.]table [alias]`
  is read from the statement text (a join, a union, `TABLE(...)` or a sub-select in `FROM` has none and the
  builder declines as before). A reader opened with `CommandBehavior.SchemaOnly` or `KeyInfo` adds
  `BaseTableName`, `BaseSchemaName`, `IsKey`, `IsAutoIncrement`, `IsExpression` ... to `GetSchemaTable()`;
  `KeyInfo` also reads the table's columns and primary key from the catalog (one `GetSchema` round trip
  each), which is what lets the builder write the `WHERE` clause. Without `Delimited Identifiers=true` the
  builder leaves the names unquoted (a double-quoted word is a string literal to the server and gave -201);
  set `QuotePrefix`/`QuoteSuffix` yourself and it keeps your characters.
* **`IfxDbColumn.StartTimeUnit` / `EndTimeUnit`** give the qualifier of a DATETIME or INTERVAL column
  (`YEAR`..`MINUTE`, `DAY`..`DAY`); `null` for other types. A `DateTime`/`TimeSpan` cannot carry it, but the
  UNLOAD text of the value depends on it.
* **`IfxDecimal` and `IfxDateTime` bind as parameters** (they used to throw "not supported"). A null value
  binds as a typed NULL, and an `IfxDateTime` keeps its qualifier: a `YEAR TO MINUTE` is sent as
  `YEAR TO MINUTE`, and a value at midnight stays a DATETIME instead of becoming a DATE. `IfxBulkLoader`
  takes the same types, and now also `IfxType.IntervalDayFraction` / `IntervalYearMonth` columns.
* **`IfxConnection` raises `StateChange`** (Closed to Open when `Open()` succeeds, Open to Closed on
  `Close()`/`Dispose()`), like every ADO.NET connection.
* **`IfxScrollReader.MoveNext()`** returned one row and then `false`. A `SQ_SFETCH` NEXT answers with a
  whole buffer of rows and leaves the server's cursor on the last of them; the port kept only the first and
  asked the server for row 2 from the end of the data. Rows already in the buffer are now served locally (as
  JDBC's `repositionIfInTupleBuffer` does), and a forward step is sent as NEXT only when the server really
  stands on the row before it.

---

### 4.16 `byte[]` into `BINARYVAR` / `BINARY18` — `IfxType.BinaryVar` / `IfxType.Binary18`

A `byte[]` parameter binds as `BYTE`, and the server has no cast from `BYTE` to the Database
Extensions types `BINARYVAR` and `BINARY18` (error -9634, "byte to binaryvar"). Reading
those columns already returns a `byte[]`; to write one, say which type the parameter is:

```csharp
cmd.CommandText = "INSERT INTO device (id, mac) VALUES (?, ?)";   // mac BINARY18
cmd.Parameters.Add(new IfxParameter("id", 1));
cmd.Parameters.Add(new IfxParameter("mac", new byte[] { 0x00, 0x00, 0x12, 0xDF, 0x4F, 0x6C })
                   { IfxType = IfxType.Binary18 });
```

- `Binary18` zero-pads a shorter value to 18 bytes on the client (a short byte-stream bind
  otherwise leaves the tail as uninitialised server memory) and refuses a longer one with
  `IfxException` -9636, "Opaque type exceeded its maximum length" (the server would silently
  truncate it).
- `BinaryVar` sends the bytes as they are; the server's ceiling is 253 bytes (-9636 beyond it).
  An empty array is accepted, but note the server's own `binaryvarout()` fails on a zero-length
  value in a cast or `LENGTH()`.
- `null` / `DBNull` bind a typed NULL. Only `byte[]` is accepted; a string of hex digits needs no
  `IfxType` at all (the server casts it).
- Without `IfxType` a `byte[]` still binds as `BYTE`, so a generic `DbParameter` (no access to
  `IfxType`) still cannot write to these columns. Declaring the parameter type from the server's
  description of the statement's `?` markers (`SQ_CIDESCRIBE`, which the IBM JDBC driver uses)
  would remove that restriction and is not implemented. IBM JDBC's own `setBytes` into a
  `BINARYVAR` column stores a corrupt value (live-tested on IDS 14.10), so there is no reference
  behaviour to match there; `BINARY18` through JDBC does pad correctly.

### 4.17 Pipelined batches — `IfxRowBatch` and `IfxBatch` (`DbBatch`)

Between a plain `IfxCommand` loop and a full `IfxBulkLoader` sit two batch types the
classic providers had no equivalent of. Both collapse the *execute* phase into as few
round trips as the wire's ~32 KB PDU guard allows, instead of one per statement.

**`IfxRowBatch` — one statement, many value rows.** Prepared once; every row's
`SQ_BIND + SQ_EXECUTE` chains into one PDU. `Execute()` returns the per-row
affected-row counts, in order.

```csharp
var batch = cn.CreateRowBatch("INSERT INTO sales VALUES (?, ?, ?)");
foreach (var row in source)
    batch.AddRow(row.Id, row.Name, row.Amount);
int[] affected = batch.Execute();
```

Named placeholders (`@name`/`:name`) work in the text, but a row carries only positional
values, so supply them in placeholder-occurrence order — a repeated name's value repeated.
For a large load prefer `IfxBulkLoader` (§4.5): it is denser still and streams rather than
buffering every row client-side.

**`IfxBatch` — the standard `DbBatch`, with different statements.**
`cn.CreateBatch()` (and `CanCreateBatch`) give the ADO.NET batch type, so portable
`System.Data.Common` code gets the pipelining for free.

```csharp
using var batch = cn.CreateBatch();
batch.BatchCommands.Add(new IfxBatchCommand("UPDATE stock SET qty = qty - 1 WHERE id = 7"));
batch.BatchCommands.Add(new IfxBatchCommand("DELETE FROM cart WHERE id = 7"));
int affected = batch.ExecuteNonQuery();
```

Worth knowing before you rely on it:

* `ExecuteNonQuery` is the pipelined path. A statement the batch has not prepared before
  still costs its own prepare round trip the first time — with the prepared-statement
  cache off, every time — and only its execute chains with the rest.
* `ExecuteReader` is **not** pipelined: one round trip per command as `NextResult`
  advances, because a session may hold only one open cursor.
* `ExecuteNonQuery` refuses a command whose statement describes a result set (a SELECT,
  or a RETURNING procedure) and names the offending command in
  `DbException.BatchCommand`. Use `ExecuteReader` for those.
* On the first error the server abandons the rest of the PDU, so later statements in the
  same flush never run.

Round-trip counts for both: [msg_flow.md](msg_flow.md) §3 and §4.

---

### 4.18 Smart large objects as first-class handles — `IfxSmartLOB` and `IfxType.SmartLOBLocator`

Besides the "bind the whole value" route of §4.13, the classic provider's incremental
smart-LOB API is here in full, with the same shapes: `IfxBlob`/`IfxClob` (both
`IfxSmartLOB`), `IfxSmartLOBLocator`, and the create/open factories on both the connection
and the reader. Use it when a value is too big to hold in memory, or when you want to read
or patch part of one.

**Opening an existing object** — from a row, by its locator:

```csharp
using var r = cmd.ExecuteReader();   // SELECT advert.picture FROM catalog WHERE …
r.Read();
IfxBlob blob = r.GetIfxBlob(0);      // also GetFieldValue<IfxBlob>(0); GetIfxClob for a CLOB
blob.Open(IfxSmartLOBOpenMode.ReadWrite);
blob.Seek(0, IfxSmartLOBWhence.Begin);
foreach (byte[] piece in pieces) blob.Write(piece);
blob.Truncate(blob.Position);
blob.Close();
```

**Creating a new object** — the classic lifecycle: the connection hands out a handle, and
`Open()` is what creates the object on the server:

```csharp
IfxBlob blob = cn.GetIfxBlob();            // nothing on the server yet: IsNull is true
blob.SBSpace = "sbspace2";                 // optional create spec, see below
blob.Open(IfxSmartLOBOpenMode.ReadWrite);  // ifx_lo_create; the new object is left open
foreach (byte[] piece in pieces)
    blob.Write(piece);                     // or blob.FromFile(path, false, IfxSmartLOBFileLocation.Client),
blob.Close();                              //    which opens -- and so creates -- the object itself

cmd.CommandText = "INSERT INTO docs (id, body) VALUES (?, ?)";
cmd.Parameters.Add(new IfxParameter("id", 1));
cmd.Parameters.Add(new IfxParameter("body", blob) { IfxType = IfxType.Blob });
cmd.ExecuteNonQuery();                     // binds the 72-byte locator, creates nothing
```

Writing to the handle before `Open()`, asking it for `GetLocator()`, or binding it as a
parameter fails with an `InvalidOperationException` that says `Open()` creates it. Binding
in particular is refused rather than quietly storing NULL.

The parameter value may be the `IfxBlob`/`IfxClob` itself, its `GetLocator()`, an
`IfxSmartLOBLocator` you built from saved bytes, the 72 raw bytes, or the hex text
`IfxSmartLOBLocator.ToString()` produces. All five bind the locator rather than creating a
second object, and the same locator may go into several rows or an `UPDATE`.

**`IfxType.SmartLOBLocator`** is the type that says "this value *is* a locator". It matters
because of a deliberate asymmetry: under `IfxType.Clob`/`Blob` a `string` or `byte[]` is
still the new object's **content** (§4.13), exactly as before, so handing raw locator bytes
to `IfxType.Blob` stores those 72 bytes as data. Pass an `IfxSmartLOBLocator` (or the
`IfxBlob`/`IfxClob`) and the meaning is never in doubt.

> **The `blob`/`clob` name has to match the column.** The locator is bound as an opaque UDT
> whose extended type name is `blob` or `clob`, and the server will not cast one to the
> other: live-verified, a locator sent as `blob` into a CLOB column fails with
> **SQLCODE -9634 "blob to clob"**, and the mirror case with "clob to blob". The name comes
> from the object when you pass an `IfxBlob`/`IfxClob` (which always knows), and from
> `IfxParameter.IfxType` otherwise. A bare `byte[]` or hex string under
> `IfxType.SmartLOBLocator` has neither, so it binds as `blob` — for a CLOB, wrap it:
> `new IfxSmartLOBLocator(bytes)` with `IfxType.Clob`.

**The create spec.** Whatever is set between `GetIfxBlob()` and `Open()` is the spec
`ifx_lo_create` receives:

| Property | Means | Default |
|---|---|---|
| `SBSpace` | the sbspace to create the object in | empty: the server's `SBSPACENAME` |
| `ExtentSize` | allocation extent, in KB | -1: the column's or the sbspace's own |
| `MaxBytes` | upper bound on the object's size | -1: none beyond the sbspace's |
| `EstimatedSize` | expected size, an allocation hint | -1: the server decides |
| `Flags` | `IfxSmartLOBCreateTimeFlags`: `Log`/`NoLog`, `KeepAccessTime`/`DontKeepAccessTime` | 0: the sbspace's own settings |

Live-verified by reading back the spec the server reports for the new object through
`ifx_lo_stat`: the sbspace, `ExtentSize`, `EstimatedSize` and `MaxBytes` come back as set,
and `Log | KeepAccessTime` turns around the sbspace's default of no logging and no
access-time keeping. An sbspace the server does not know fails `Open()` with SQLCODE -9810.

> **`MaxBytes` is enforced by truncation, not by an error.** With a limit of 10 000 bytes, a
> 20 000-byte `Write` stores 10 000 and *returns* 10 000; nothing is thrown. Check what
> `Write` returns when you set a limit.

**A column's storage settings** — `cn.GetIfxBlob("docs", "body")` or
`new IfxBlob(cn, "docs", "body")` — make `Open()` first ask the server for `docs.body`'s
create spec (`ifx_lo_col_sbinfo`, the same lookup `IfxParameter.SmartLobColumn` does in
§4.14), with the properties above as the template the column's own clause is folded into.
Verified on a column declared `PUT b IN (sbspace) (EXTENT SIZE 48, KEEP ACCESS TIME)`: the
object is created with extent 48 and access-time keeping, where a plain `GetIfxBlob()` gets
the defaults. Give both names or neither; half a column name is an `ArgumentException`.

After `Open()` the properties report the spec the object was actually created with, column
settings included; changing them then has no effect on it. On an object opened *by its
locator* (a reader's `GetIfxBlob`) they report the defaults, not that object's real settings.

> **Measured against `Informix.Net.Core` 4.0.2.0.** The lifecycle above is the classic one:
> its `GetIfxBlob()` also returns a handle with `IsNull` true, `Write()` before `Open()` also
> fails, and `Open()` is also what calls its internal `CreateSmartLOB`, which applies the
> table/column through a `"table.column"` column-spec lookup and the properties through the
> native create spec. What still differs:
>
> | | classic `Informix.Net.Core` | here |
> |---|---|---|
> | parameter value | `IfxSmartLOBLocator` only — its marshaller casts to it and calls `ToBytes()`; nothing converts an `IfxBlob` to its locator, and `locator.ToBytes()` is stored as content | `IfxSmartLOBLocator` as well, plus the object, the raw bytes and the hex text |
> | `ReferenceCount`, `LastAccessTime`, `LastChangeTime`, `LastModificationTime` | read from the server's LOB status block | throw `NotSupportedException`, although the server reports them in the same `ifx_lo_stat` block used above |
> | `IfxSmartLOBLocator.Length` | `static` (the fixed 72) | an instance property |
>
> Code written against the classic provider ports unchanged.

A LOB created by `Open()` but never bound into a row stays in the sbspace with nothing
referencing it, the orphan §4.13 describes; inside a transaction that is rolled back it goes
away with it.

---

## 5. Quick reference — old knob → new home

| What you did before | Now |
|---|---|
| ODBC DSN / `Driver={IBM INFORMIX ODBC DRIVER}` | keyword connection string (`Host;Service;Server;Database;User ID;Password`) |
| `IBM.Data.Informix` namespace | `Informix.Data` — same `Ifx*` type & value-struct names |
| `IFX_USEPUT` insert cursor | `IfxConnection.CreateBulkLoader(...)` → `IfxBulkLoader` |
| `IFX_TRIMTRAILINGSPACES` | `TrimTrailingSpaces=true` connection key (CHAR/NCHAR only) |
| `FET_BUF_SIZE` env var | `Fetch Buffer Size` key (sizes fetch **and** bulk-load buffers) |
| `OPTOFC` env var | `Optofc=true` key |
| `DB_LOCALE` / `CLIENT_LOCALE` juggling | set `DB Locale`, or let `DB Locale Discovery` find it; there is no client-locale key |
| `SSLCONNECTION` / stunnel | `SSL Connection=true` (`Encrypt=true`) to an `onsocssl` listener |
| PAM via CSDK callback | `Authentication=Pam` + `IIfxPamHandler` (or auto from sqlhosts `s=4`) |
| positional `?` only | still supported, **plus** `@name`/`:name` named parameters |
| `DATE`/`DATETIME` → `DateTime` | `DateOnly`/`TimeOnly`/`TimeSpan` by default; old types via `GetFieldValue<T>` |
| JSON/BSON as opaque bytes | JSON text via `GetString`; DOM via `GetFieldValue<JsonDocument>`; write with `IfxType.Json`/`Bson` |
| `IFX_WIRE_TRACE` only | `ILoggerFactory` + `ActivitySource`/`Meter` `"Informix.Data"`; wire trace still available |
| pass connection strings around | `IfxDataSource` / `IfxDataSourceBuilder` |
| a loop of `ExecuteNonQuery` calls | `IfxRowBatch` (one statement, N rows) or `IfxBatch`/`DbBatch` (different statements) — one flush instead of one round trip each; see §4.17 |
| `SELECT dbinfo('sqlca.sqlerrd1')` after INSERT | `IfxCommand.LastSerial` / `LastSerial8` / `LastBigSerial` + `LastSerialKind` (no round trip) |
| `SET ISOLATION` after every connect | `Isolation Level=RepeatableRead` key (also accepts the ODBC `ISOLATIONLEVEL` numeric codes `0`-`5` directly), applied once at `Open()`. **Leave it out on a database without logging** — unspecified (the default) sends no `SET ISOLATION` at all |
| `SET TRANSACTION READ ONLY` per transaction | `Read Only=true` key (every transaction the driver begins), or an explicit `isReadOnly` argument to `BeginTransaction(IsolationLevel, bool)`/`BeginTransaction(bool)` for one transaction — server-enforced, SQLCODE -878 on a write, live-verified |
| `SQLGetInfo(SQL_DBMS_NAME)` (`IfxConnection.ServerType` on the ODBC-native provider) | `IfxConnection.ServerType` — derived from the ASF handshake's version string, not an ODBC constant; see §4.10 |
| `SQLGetEnvAttr(SQL_INFX_ATTR_CP_TOTAL_IDLE` / `_ACTIVE)` | `IfxConnection.GetIdleConnectionsCount` / `.GetActiveConnectionsCount` (also `IfxDataSource.Statistics`); see §4.10 |
| `SQLProcedureColumns` | `GetSchema("ProcedureColumns")` — `COLUMN_TYPE` straight from `sysproccolumns.paramattr` (IN/OUT/INOUT/`RETURNING` rows, ODBC's own codes); see §4.10 |
| `{? = call proc(?,?)}` with `SQLBindParameter(SQL_PARAM_OUTPUT)` | same escape, or `CommandType.StoredProcedure` + `ParameterDirection.Output`/`InputOutput`/`ReturnValue`; read the values after `ExecuteNonQuery`; see §4.11 |
| `IfxConnection.Driver` / `.PacketSize` (ODBC-native provider) | **not implemented** — `Driver` names a native DLL we don't have; `PacketSize` was always just an alias of `FetchBufferSize` — use the `Fetch Buffer Size` key / `IfxCommand.FetchBufferSize`; see §4.10 |
| `IfxConnection.GetIfxBlob()`/`GetIfxClob()` + `IfxSmartLOB` + `IfxType.SmartLOBLocator` | all present, same lifecycle — the create-spec properties and an optional table/column, then `Open()` creates; fill incrementally and bind the object or its `GetLocator()`; see §4.18 |
| `SQLColAttribute(SQL_DESC_BASE_TABLE_NAME)` (`IfxDataReader.GetBaseTableName` on the ODBC-native provider) | **no `GetBaseTableName`** — the table name is not on the SQLI wire. `GetSchemaTable()` of a `SchemaOnly`/`KeyInfo` reader has `BaseTableName` for a one-table SELECT, read from the statement text; see §4.15 |

---

## 6. Things that are *not* here

So you don't go looking: no **DRDA/XA *distributed* transactions** — single-connection
`System.Transactions`/`TransactionScope` enlistment *is* supported (auto-enlist at `Open()`,
`Enlist=false` to opt out), but a scope spanning a second resource manager would require XA
promotion and throws. No **HDR detection**, no privilege/UDT-catalog **metadata collections**
(query `sysusers`/`systabauth`/`sysxtdtypes` yourself). No **`IfxDataReader.GetBaseTableName`**
(the schema table has `BaseTableName` for a one-table SELECT, §4.15)
and no **`IfxConnection.Driver`/`.PacketSize`** .

Core connectivity is all implemented:
execute/fetch, scroll & hold cursors, BYTE/TEXT & smart LOBs, stored procedures, savepoints,
isolation levels, batches, bulk load, and `GetSchema` with the collections `Tables`, `Views`,
`Columns`, `Indexes`, `Procedures`, `ProcedureParameters`, `ProcedureColumns`, `PrimaryKeys`,
`ForeignKeys` (plus the standard `MetaDataCollections`, `DataSourceInformation`, `DataTypes`,
`Restrictions`, `ReservedWords`).
