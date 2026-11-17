# Informix.Data

A fully managed ADO.NET provider for IBM Informix, speaking the native **SQLI**
wire protocol directly over a socket — no CSDK, no ODBC DSN, no IBM DB2 client,
no native binaries. Targets `net8.0`.

The API follows the classic `IBM.Data.Informix` (SQLI) provider: `IfxConnection`,
`IfxCommand`, `IfxDataReader`, `IfxParameter`, `IfxDataAdapter`,
`IfxTransaction`, `IfxDecimal`, `IfxDateTime`, … keep their names and shapes, so
existing code mostly needs only a namespace swap.

> **Experimental.** Verified against IDS 14.10.FC5 and 15.0.1.0DE only. Not yet
> recommended for production; well suited to development, integration tests and
> tooling.

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

## Features

- named parameters (`@name` / `:name`) alongside positional `?`;
- modern read types: `DATE` → `DateOnly`, time-only `DATETIME` → `TimeOnly`,
  day-time `INTERVAL` → `TimeSpan`;
- JSON and BSON types;
- `IfxBulkLoader` for fast loading, pipelined `IfxRowBatch` and `DbBatch`;
- `IfxDataSource`, `TransactionScope` enlistment (single connection, no XA);
- Microsoft.Extensions.Logging and OpenTelemetry tracing/metrics;
- read-only mode, PAM authentication (incl. MFA/OTP handlers) and TLS;
- locale handling driven by `DB_LOCALE` alone, auto-discovered when not set.

## Documentation

- [Project home and full README](https://github.com/robsosno/Informix.Data)
- [Connection string reference](https://github.com/robsosno/Informix.Data/blob/main/docs/connection-string.md)
- [Migrating from IBM.Data.Informix](https://github.com/robsosno/Informix.Data/blob/main/docs/migration-guide.md)
- [Report an issue](https://github.com/robsosno/Informix.Data/issues)

## License

MIT — see [LICENSE](https://github.com/robsosno/Informix.Data/blob/main/LICENSE).
