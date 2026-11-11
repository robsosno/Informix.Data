# Informix.Data demo programs

Console programs that show what the Informix.Data driver does, on the `superstore_demo2` sample
database. Each program is a small project of its own; it prints what it is doing step by step.

```
Informix.demo/
├── 01-data/            the database: scripts, data files, the loader  (start here)
├── 02-native/          16 programs that use Informix.Data directly
├── 03-abstraction/     10 programs written on System.Data.Common only
├── 04-ai/              an MCP server + skill + prompt: an AI assistant queries the database read-only, never seeing the connection string
├── Demo.Common/        console helpers + connection string handling (knows no driver)
├── Demo.Native/        UNLOAD writer for 02-native (uses the driver)
├── Demo.Bootstrap/     registers the Informix provider; the only driver reference of 03-abstraction
└── Informix.demo.sln
```

## Run them

1. Create the database and load it: see [01-data/README.md](01-data/README.md) (once).
2. Give the programs a connection string (no `Database` needed - they set it): copy `.env.example`
   to `.env` in this folder and fill in `INFORMIX_DEMO_CONNECTION`. One file serves every program
   (and the loader); it is git-ignored because it holds a password. A program that needs something
   different gets a `.env` of its own next to its `.csproj` containing only that setting; it
   overrides the root file for that program. A real environment variable of the same name beats
   both files. Optional settings (`INFORMIX_DEMO_DATABASE`, `INFORMIX_DEMO_OPTIONS`,
   `INFORMIX_DEMO_PROVIDER`) are described in `.env.example`. (`04-ai` keeps its own `.env`: the
   assistant is denied access to it, see its README.)
3. Run one:

```bash
dotnet run --project Informix.demo/02-native/Unload
dotnet run --project Informix.demo/03-abstraction/GenericBatch
```

`Informix.demo.sln` builds everything. Files the programs write (UNL files, copied pictures) go to
`<temp>/informix-demo/<program>`. Programs that change data do it in temporary tables, or in a
transaction that is rolled back, so they can be run again and again; the database stays as loaded.

## 02-native

| Program | Shows |
| ------- | ----- |
| **Unload** | Tables to UNLOAD files, streaming; `IfxTextConvert` / `IfxTextFormat` (DBDATE variants, decimal comma, DATETIME and INTERVAL text); Fetch Buffer Size |
| **Load** | UNL text to typed values with `IfxTextConvert.Parse*`; row-by-row vs `IfxRowBatch` vs `IfxBulkLoader`; unload again and compare byte for byte |
| **UnloadFromProcedure** | A routine returning many rows unloaded like a table; `EXECUTE FUNCTION`, `CommandType.StoredProcedure`, `DeriveParameters`, `ProcedureColumns`, OUT parameters |
| **LobStreaming** | BLOB/CLOB read as streams (seekable), written in pieces into a row's object; the parameter alternative |
| **ConnectionPooling** | `Pooling`, Min/Max Pool Size, Connection Lifetime, Login Timeout, `ClearPool`, counters, `IfxDataSource` and its initialization statement |
| **DateTimeTypes** | DATE → `DateOnly`, time-only DATETIME → `TimeOnly`, DATETIME → `DateTime`, INTERVAL → `TimeSpan`; `IfxDateTime` and the qualifier; binding each type |
| **NamedParameters** | `@name` / `:name`, repeated names, any order, errors; what `Prepare()` and the statement cache save |
| **ReadOnlyAccess** | Read-only transactions (`BeginTransaction(..., isReadOnly)`, `Read Only`) and the select-only connection (`Select Only`) |
| **GeneratedKeys** | `LastSerial` / `LastSerial8` / `LastSerialKind` without a second query |
| **ObjectRelational** | `IfxRow`, collections, type hierarchy and `ONLY`, a distinct type, writing ROW/LIST with SQL constructors |
| **Bson** | A BSON column as JSON text / `JsonDocument` / raw bytes; BSON parameters; `bson_value_*` |
| **Transactions** | Commit/rollback, a trigger inside a transaction, what two connections see, savepoints, `TransactionScope` |
| **Cursors** | Several readers on one connection, scroll cursors, `HoldCursor` |
| **Diagnostics** | `IfxException`, retry on `IsTransient`, logging, `ActivitySource` and `Meter` |
| **Connect** | Builder, server information, `ChangeDatabase`, session keywords (`DB Date`, `Delimited Identifiers`), SSL |
| **Schema** | `GetSchema` collections and restrictions, `IfxDataAdapter` with a `DataSet` |

## 03-abstraction

Nothing in these programs mentions the driver except `Bootstrap.Register()` (in `Demo.Bootstrap`);
the code uses `DbProviderFactory`, `DbConnection`, `DbCommand`, `DbParameter`, `DbDataReader`,
`DbTransaction`, `DbBatch`, `DbDataAdapter` and friends.

| Program | Shows |
| ------- | ----- |
| **ProviderFactory** | `DbProviderFactories`, `Create*` / `CanCreate*`, pooling off/on through the factory, `DbDataSource` |
| **GenericReader** | Named `DbParameter`s, `GetFieldValue<DateOnly/TimeOnly/TimeSpan>`, `CommandBehavior`, a dumper built on `GetColumnSchema()` |
| **GenericReadOnly** | `Read Only` / `Select Only` as connection-string keywords: the same code, three behaviours |
| **GenericUnload** | A table, a join and a stored routine written as UNL with plain .NET formatting |
| **GenericLobStreams** | `GetStream`, `GetTextReader`, `GetBytes`; what standard `DbType`s can and cannot write |
| **GenericBatch** | `DbBatch` inserts (about 10x single commands) and a batch of SELECTs |
| **GenericSerial** | The generated key through `DBINFO` - the portable way, and its cost |
| **GenericTransactions** | `DbTransaction`, savepoints, `TransactionScope`, `DbException.IsTransient`, `CommandTimeout`, `Cancel` |
| **DataSetAdapter** | `DbDataAdapter`, `DataSet` relations, `Update`; the database layout from `GetSchema()` |
| **PortableDao** | A small repository / unit-of-work layer on `DbConnection` and a row-mapper delegate |

## 04-ai

Not a console program but a demo of the driver behind an AI assistant: an MCP server on Informix.Data
(Select Only, read-only transactions, connection string only in a git-ignored `.env` that the assistant
cannot read), a skill, and a prompt that generates a C# DTO for `catalog`. See [04-ai/README.md](04-ai/README.md).

