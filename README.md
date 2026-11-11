# Informix.Data

A fully managed ADO.NET provider for IBM Informix (`net8.0`), speaking the
native **SQLI** wire protocol directly over a socket — no CSDK, no ODBC DSN,
no IBM DB2 client required.

If you know the classic `IBM.Data.Informix` (SQLI) provider, most of your code
compiles after a namespace swap: `IfxConnection`, `IfxCommand`,
`IfxDataReader`, `IfxParameter`, `IfxDataAdapter`, `IfxCommandBuilder`,
`IfxTransaction`, `IfxError`, `IfxException`, and the value structs
`IfxDecimal`, `IfxDateTime`, `IfxTimeSpan`, `IfxMonthSpan` all keep their
classic names and shapes.

## Why it exists

I have been working with Informix since 1990. It is a fast database with
advanced data-replication features, and yet it stays simple for the user.

However, client drivers are a weak point. For years, just *obtaining* the
Informix CSDK or the JDBC driver was a problem — it was never clear where to
download them from. Cloud deployments are now the norm, so installation has to
be easy. On .NET that means a driver you can pull from NuGet.org. The only
Informix driver there is the DRDA driver for DB2, and for the Informix user
that brings several limitations. Despite the many years that have passed, the
gap in functionality between the native Informix driver and the DRDA one
hasn't narrowed. Additionally, the driver is huge and has many dependencies.
For example, I had to install an additional package, libxcrypt-compat, in my
Docker image. Worse, the native driver forces you to swap the driver version
in `.csproj` files between the development and the deployment platform.

Then came LLMs, and I could write a prompt like this: "build me a managed .NET
driver behaving like IBM's driver". I used Claude Code. The code was generated
by AI and after several iterations became quite complex. I simply do not
understand it. But I don't need to. It was vibe-coded, but I made the key
decisions during development. The AI reverse engineered the drivers installed
on my laptop: JDBC, ODBC, DB2, and the old Informix .NET driver. It ran
several experiments on the wire. It also decompiled some code.

**Is this legal?** Probably yes, but I am not a lawyer and cannot guarantee it.
The Informix license forbids reverse engineering and decompilation *except
where expressly permitted by law* — and such permission does exist: EU
Directive 2009/24/EC allows decompilation to obtain information, not otherwise
available, that is necessary to make independently created software
interoperate. I am a citizen of an EU country.

## Highlights

- native C# implementation, small footprint, `net8.0`, no CSDK / no DB2 client;
- API shaped after the classic `IBM.Data.Informix` (SQLI) provider —
  `IfxConnection`, `IfxCommand`, `IfxDataReader`, … mostly a namespace swap
  away;
- named parameters (`@name` / `:name`) alongside classic positional `?`;
- modern default read types: `DATE` → `DateOnly`, time-only `DATETIME` →
  `TimeOnly`, day-time `INTERVAL` → `TimeSpan` (the old types stay reachable
  via `GetFieldValue<T>`);
- opt-in trimming of trailing spaces on `CHAR`/`NCHAR` (`TrimTrailingSpaces` in
  the connection string);
- JSON and BSON type support;
- `IfxBulkLoader` — fast data loading, over the native `PUT` (insert-cursor)
  statement when the columns allow it and a chained bind/execute stream otherwise;
- pipelined batches: `IfxRowBatch` (one statement, many rows) and `IfxBatch`,
  the standard `DbBatch` (many different statements, one flush);
- `IfxDataSource` — the modern .NET connection-factory model;
- no XA distributed transactions — instead, single-connection
  `System.Transactions` / `TransactionScope` enlistment;
- logging through Microsoft.Extensions.Logging;
- OpenTelemetry support (tracing and metrics);
- read only mode;
- simplified locale handling: text conversion is driven by `DB_LOCALE` alone
  (no `CLIENT_LOCALE` to misconfigure), and the database locale is
  auto-discovered when not set;
- PAM authentication (including custom handlers for MFA/OTP) and TLS.

## Quick start

```csharp
using Informix.Data;

using var cn = new IfxConnection(
    "Host=myhost;Service=9088;Server=ol_myserver;Database=stores_demo;" +
    "User ID=informix;Password=secret");
cn.Open();

using var cmd = cn.CreateCommand();
cmd.CommandText = "SELECT customer_num, fname FROM customer WHERE state = ?";
cmd.Parameters.Add(new IfxParameter { Value = "CA" });

using var r = cmd.ExecuteReader();
while (r.Read())
    Console.WriteLine($"{r.GetInt32(0)} {r.GetString(1)}");
```

## Can you trust it in production?

Not yet. I do not use it on a production system myself, and testing it
properly takes more time than I can give a hobby project. There are things
you must be aware of:

- I verified the driver against the two servers I have (IDS 14.10.FC5, ); the
  commercial product runs on many platforms, and versions I cannot possibly
  test;
- the protocol contains various magic flags whose purpose is unknown to me, so
  there is no telling whether they will keep working correctly in future server
  versions.

Because the driver is small and pure .NET, it is well suited to unit tests that
need a real database, and to day-to-day development work.

One more place where the small footprint pays off: AI tooling. A driver that is
a single self-contained package — nothing to install, no native binaries tied
to the OS — drops straight into an agent skill or an MCP server as the
database-access layer. That makes it a handy backbone for code-generation tasks
that need to look at a live schema: scaffolding CRUD layers, generating
database-backed tests, producing domain objects and their EF Core or NHibernate
mappings, and the like. I have also added a read-only mode, which is a nice
safeguard when working with AI and a database.

## Build and test

Requires the .NET 10 SDK: the driver targets `net8.0`, the test project and the demo
programs target `net10.0`.

```bash
dotnet build Informix.Data/Informix.Data.csproj
```

```bash
dotnet test Informix.Data.Tests/Informix.Data.Tests.csproj
```

The tests are xUnit and need **no** Informix server — the wire codecs, the SQL parser,
the text conversions, the connection-string surface and the API shape are all exercised
offline. Anything that needs a live server lives in the demo programs instead.

```bash
dotnet pack Informix.Data/Informix.Data.csproj -c Release
```

`Informix.demo/Informix.demo.sln` builds the driver together with every demo program; it
does not include the test project.

## License

MIT — see [LICENSE](LICENSE).
