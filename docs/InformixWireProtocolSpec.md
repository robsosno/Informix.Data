# Informix SQLI / ASF — Wire Protocol Specification

## Scope

This document summarizes the Informix client protocol (ASF + SQLI). It is not a complete server specification.

# Glossary

ASF - Application Support Facility. The session-layer sub-protocol Informix carries for connection negotiation. The ASF body in a LOGIN request carries cmd / user / password / version / mode / default_db / flags plus KEY=VALUE environment variables; the server's accept response carries a session-layer-type outcome byte (CONACC / CONREJ) and a sequence of sub-token records. ASF completes the handshake before any SQL flows. 

PDU  [Protocol Data Unit](https://en.wikipedia.org/wiki/Protocol_data_unit) / PFPDU (Physical Format PDU) - The unit of message exchange in Informix's SQLI wire protocol — the term IBM's wire-format spec uses for one self-contained protocol record. Structurally a PDU is a 16-bit token id followed by an optional, token-specific payload. A single SQL statement typically traverses many PDUs in each direction (e.g. SELECT → PREPARE, NDESCRIBE, OPEN, NFETCH, TUPLE, EOT, CLOSE). 

Even-padding - A SQLI wire-format convention: variable-length string fields inside a PDU are followed by enough NUL pad bytes that the next token starts on an even byte offset. Every Informix protocol decoder applies the rule. 

Round trip (RP) - One request burst from the client (one or more PDUs ending in ONI_EOT) plus the server's response burst (one or more PDUs also ending in ONI_EOT).

SL - Session Layer is the thin framing layer that sits below the SQLI token stream and carries the connection handshake. Each SL frame begins with a fixed 6-byte header: a 16-bit total length (the PDU size plus 6), a 1-byte SL type (CONREQ=1, CONACC=2, CONREJ=3, REDIRECT=13), a 1-byte attribute holding the protocol level (PF_PROT_SQLI_0600), and a 16-bit options field. The SL header tells the peer how large the frame is and what kind of handshake message it is. The protocol data unit (PDU) follows it as an opaque payload.

SQLI - SQL Interface. The wire protocol Informix clients and the IBM Informix server speak to each other over TCP. Every ESQL/C program, every dbaccess session, every JDBC / ODBC / .NET connection ultimately frames its requests and responses in SQLI.

UDTVAR - User-Defined Type VARiable — a wire-format encoding category Informix uses for several extended types including LVARCHAR, JSON, and BSON. On the wire UDTVAR appears as sql_type=40 with an ext_name string ("json", "lvarchar", "bson", or empty) reporting the registered datablade type behind it.

## 1. Conventions

All multi-byte values are big-endian. `i16`, `i32`, and `i64` are signed integers of the indicated size; `real` and `double` are IEEE-754; `date` is a 4-byte day count; `byte` is unsigned. `padded(N)` is N bytes plus one `0x00` byte when N is odd. A regular `char` is `i16 L` (byte length) followed by `padded(L)`, decoded with the database encoding and with trailing NUL bytes removed. `stmt-text` uses an `i32` length when capability bit 62 is set, otherwise `i16`, followed by padded SQL text. `int8` is a special 10-byte format (11.4).

ASF NUL strings are unpadded: `i16 (n+1)`, n bytes, then `0x00`. Some ASF response strings are counted (`i16 len` + bytes, no padding); others use padded `char` (even-padding).

```text
TCP (optional TLS from the first byte)
 └─ ASF: SL frame and login PDU (one exchange at connection start)
     └─ SQLI: [i16 tag][tag-specific payload]...; requests end with SQ_EOT
```

## 2. Transport and connection lifecycle

The client uses TCP (`NoDelay = true`; optional keepalive), optional TLS 1.2/1.3, and buffered reads/writes. The ASF exchange is sent through TLS when enabled. Requests are flushed at `SQ_EOT` (end of transmission). Cancellation uses urgent TCP byte `0x42`. Connection setup is ASF login, `SQ_PROTOCOLS` exchange, optional PAM exchange, then `SQ_VERSION`; initialization SQL may follow. The database is selected in the ASF login PDU. `ChangeDatabase` reconnects.

```text
C: TCP connect (+ TLS)
C: ASF ConnectionRequest
S: ASF Accept / Reject / Redirect
C/S: SQ_PROTOCOLS ... SQ_EOT
(PAM only: SQ_ACK → SQ_CHALLENGE/SQ_RESPONSE ... → SQ_ACCEPT)
C/S: SQ_VERSION ... SQ_EOT
... SQLI operations ...
C: SQ_EXIT (without SQ_EOT)
```

## 3. ASF login

### 3.1 Constants

| Item | Value |
|---|---|
| SL types: Request / Accept / Reject / Redirect | 1 / 2 / 3 / 13 |
| SL protocol byte / header size | 60 / 6 |
| PDU segments: ASSOC / ASCBINARY / INITRESPONSE / DBLIST / INITREQUEST | 100 / 101 / 102 / 103 / 104 |
| ENV / PROCESSINFO / BINDPARAMS / MISC60 / EOT | 106 / 107 / 108 / 116 / 127 |
| XCONNECT / SQLI-with-CSS protocol | 11 / 61 |
| Default driver version / serial / app id / capabilities | `9.280` / `RDS#R000000` / `sqli` / 316 |

### 3.2 Request and response

The six-byte SL header is `i16 total_length` (including the header), byte type `1`, byte protocol `60`, and `i16 0`. The request PDU contains ASSOC and ASCBINARY segments; protocol number 61; NUL strings for float type `IEEEM`, driver version, serial, application ID, user and password; BINDPARAMS; fixed `sqlexec`, `ol`, and `tlitcp` fields; then INITREQUEST/XCONNECT with options, server name and optional database. ENV carries name/value NUL-string pairs; PROCESSINFO carries process/thread IDs, host and working directory; MISC60 carries the application name; EOT terminates the PDU.

Common ENV entries are `DB_LOCALE` when configured, `DBPATH=.` and `IFX_UPDDESC=1`; `CLNT_PAM_CAPABLE=1` advertises PAM. `CLIENT_LOCALE` is not sent.

The response starts with length, SL type, ignored protocol bytes, ASSOC/ASCBINARY identifiers, then BINDPARAMS containing counted version, serial and application ID strings and three capability integers. INITRESPONSE contains status, errors and warnings; DBLIST contains redirect detail; EOT ends the response. Accept yields server information; Reject reports service/OS errors; Redirect is reported as an error and is not followed.

## 4. SQLI framing and capabilities

A SQLI message is `i16 tag` plus tag-specific payload; there is no outer message length. A request is one or more messages followed by `SQ_EOT (12)` and a flush (“flip”). The response is read through `SQ_EOT` or `SQ_EXIT`. Complete request/response exchanges are serialized on the connection. Large batch operations may be split at about 32,000 bytes.

Capability bits are MSB-first in each byte. The client sends `SQ_PROTOCOLS` with `FF FC 7F FC 3C 8C AA 97` (8 bytes), optionally followed by `06` for “Large Row Formats.” The server returns a length-prefixed padded vector. Effective capabilities combine server bits with version-derived bits.

| Bit | Effect |
|---:|---|
| 2 | Extended descriptors and 32-bit positions/tuple sizes |
| 8 or 9 | Fetch array mode; extra `i16 0` in `SQ_NFETCH` |
| 13 | Variable-length VARCHAR rows |
| 18 or 63 | 32-bit fetch-buffer size and larger limit |
| 50 | 32-bit string-table size and name offsets |
| 52 | Named routine parameters |
| 54 | Additional `i64` in `SQ_INSERTDONE` |
| 62 | 32-bit statement-text length and error offset |
| 69 | 32-bit tuple size in `SQ_DESCRIBE` |
| 70 | 64-bit row counts; 68-byte BYTE/TEXT descriptor |

Fetch-buffer maximum is `int.MaxValue` with bit 18 or 63; otherwise 32767.

## 5. SQLI tag registry

| Tag | Name | Direction |
|---:|---|---|
| 1–7 | SQ_COMMAND, SQ_PREPARE, SQ_CURNAME, SQ_ID, SQ_BIND, SQ_OPEN, SQ_EXECUTE | C→S |
| 8 | SQ_DESCRIBE | S→C |
| 9 | SQ_NFETCH | C→S |
| 10–15 | SQ_CLOSE, SQ_RELEASE, SQ_EOT, SQ_ERR, SQ_TUPLE, SQ_DONE | C→S/S→C as applicable |
| 17–20 | SQ_PUT, SQ_PUTERR, SQ_CMMTWORK, SQ_RBWORK | C→S/S→C as applicable |
| 22–25 | SQ_NDESCRIBE, SQ_SFETCH, SQ_SCROLL, SQ_TUPID | C→S/S→C as applicable |
| 35 | SQ_BEGIN | C→S |
| 38–39 | SQ_FETCHBLOB, SQ_BLOB | C→S/S→C |
| 41, 43, 49 | SQ_BBIND, SQ_HOLD, SQ_WANTDONE | C→S |
| 53, 55–56 | SQ_VERSION, SQ_COST, SQ_EXIT | C→S/S→C as applicable |
| 94, 97, 99–104 | SQ_INSERTDONE, SQ_LODATA, SQ_XACTSTAT, SQ_RETTYPE, SQ_GETROUTINE, SQ_EXFPROUTINE, SQ_FPROUTINE, SQ_FPARAM | C→S/S→C as applicable |
| 126–130 | SQ_PROTOCOLS, SQ_ACCEPT, SQ_ACK, SQ_CHALLENGE, SQ_RESPONSE | C→S/S→C (PAM as applicable) |
| 137–139 | SQ_SETSVPT, SQ_RELSVPT, SQ_RBACKSVPT | C→S |

## 6. Server-to-client message layouts

`SQ_VERSION`: `char version`. `SQ_PROTOCOLS`: `i16 len ; padded(len)`. `SQ_DONE`: `i16 warnings`, row count (`i32`, or `i64` with bit 70), a second ignored row count of equal width, and `i32 serial_inserted`. Each `SQ_DONE` contributes a result count in batch/chain mode.

`SQ_DESCRIBE` contains `i16 statement_type`, `i16 statement_id`, ignored `i32`, tuple size (`i32` with bit 69, else `i16`), `i16 field_count`, string-table size (`i32` with bit 50, else `i16`), then one entry per field: name offset, start position, type word, and either extended ID/owner/name/source type/length or ordinary `i16 length`. A padded string table follows when nonempty. Type-word flags include NOT NULL `0x0100`, DISTINCT `0x0800`, NAMED ROW `0x1000`, DBOOLEAN `0x4000`, COLLECTION_CLIENT `0x8000`.

`SQ_TUPLE`: ignored `i16`, size (`i32` with bit 2, else `i16`), row bytes and a pad byte for odd size. `SQ_TUPID` contains the first row ID in a batch (`i32`). `SQ_BLOB` contains `i16 len` and padded data. `SQ_CLOSE` has no payload. `SQ_COST` has two ignored `i32` values. `SQ_INSERTDONE` contains 10-byte INT8 and optionally an `i64` with bit 54. `SQ_PUTERR` begins with a row count and carries an error payload.

`SQ_ERR`: `i16 sqlcode`, `i16 isam_code`, ignored offset (`i32` with bit 62, else `i16`), and (except SQLCODE -368) a trimmed `char` message. SQLCODE 100 means end-of-data. Codes 0 and -937 also carry padded five-byte SQLSTATE and diagnostic `char`; 0 is diagnostic information and -937 is an error. -9752 identifies procedures with OUT/INOUT parameters.

`SQ_GETROUTINE` response contains `char database` and `i32 routine_handle`. `SQ_FPROUTINE` contains `i16 count` and per value: type word, optional owner/name for selected extended types, `i16 indicator` (-1 means NULL), `i16 precision`, and a type-specific value. `SQ_FPARAM` carries routine state, request, completion flag and (on first description) return/argument metadata; argument mode 1 denotes OUT.

`SQ_LODATA` response has `i16 op_type`, `i32 file_size`, then read chunks as `i16 chunk_size ; padded(chunk_size)`. `SQ_XACTSTAT` carries three ignored `i16` values. `SQ_EOT` and `SQ_EXIT` have no payload.

## 7. Client-to-server operations

Messages acting on a prepared statement are preceded by `SQ_ID ; i16 id`.

### 7.1 Bind and execute

`SQ_BIND`: `i16 5 ; i16 count`, then each parameter’s `i16 type`, optional owner/type name for extended types, and either NULL markers (`i16 -1 ; i16 0`) or present marker, `i16 precision`, and padded encoded data. BYTE/TEXT blobs follow in `SQ_BBIND`, using `SQ_BLOB` chunks (up to 1024 bytes) and a zero-length terminator.

Immediate command: `SQ_COMMAND ; i16 0 ; stmt-text ; SQ_NDESCRIBE ; SQ_EXECUTE ; SQ_RELEASE ; SQ_EOT`. Prepare: `SQ_PREPARE ; i16 param_count ; stmt-text ; SQ_NDESCRIBE ; SQ_WANTDONE ; SQ_EOT`. Execute: `SQ_ID ; [SQ_BIND] ; SQ_EXECUTE ; SQ_EOT`. Responses normally include `SQ_DONE` and, where applicable, `SQ_DESCRIBE` or `SQ_INSERTDONE`.

### 7.2 Cursors and fetches

Open uses `SQ_ID`, `SQ_CURNAME ; char name`, optional bind, optional `SQ_SCROLL`/`SQ_HOLD`, then `SQ_OPEN`. Fixed-layout cursors may fetch in the same request using `SQ_NFETCH`; variable-layout cursors may first flip, then send `SQ_RETTYPE` and `SQ_NFETCH`. Scroll cursors use `SQ_SFETCH ; i16 direction ; i32 index ; buf_size ; SQ_EOT` (directions: 1 NEXT, 4 LAST, 6 ABSOLUTE). `SQ_RETTYPE` carries a column count and type/length metadata. An empty fetch marks end-of-data. Close and release use `SQ_CLOSE` and `SQ_RELEASE` under `SQ_ID`.

### 7.3 Transactions, batches and insert cursors

BEGIN uses `SQ_BEGIN ; EOT`; COMMIT uses `SQ_CMMTWORK ; EOT`; ROLLBACK uses `SQ_RBWORK ; i16 0 ; EOT`. Savepoint messages use tags 137–139 and a `char` name. Isolation/read-only settings are sent as ordinary SQL with `SQ_COMMAND` after beginning the transaction.

Batches repeat `SQ_BIND ; SQ_EXECUTE` under one statement ID and receive one `SQ_DONE` per execution. Command chains send immediate commands without EOT between steps, or ID/bind/execute for prepared steps.

Insert cursor: open with ID, cursor name, optional hold and `SQ_OPEN`; insert with `SQ_PUT ; i32 row_size ; i16 row_count ; padded rows`; close with `SQ_CLOSE`. It applies to supported fixed-width INSERT columns; other cases use normal BIND. Errors are reported as `SQ_PUTERR`.

BYTE/TEXT fetch uses `SQ_FETCHBLOB` with the row descriptor (56 bytes, or 68 with bit 70), then reads `SQ_BLOB` chunks. Smart LOBs use FastPath routines such as `ifx_lo_create/open/close/seek/stat/truncate`; LOB payloads move through chunked `SQ_LODATA`. Routine procedures with OUT/INOUT parameters use `SQ_GETROUTINE`, `SQ_FPARAM`, `SQ_EXFPROUTINE` and BIND; results follow the order in routine metadata.

Disconnect sends `SQ_EXIT` without EOT and reads any transaction-status messages followed by EXIT or EOT.

## 8. Authentication

Password credentials are carried in the ASF PDU. In PAM mode, after protocols negotiation the client sends `SQ_ACK ; EOT`. The server may return ACCEPT, EXIT, or CHALLENGE (`i16 style ; i16 len ; padded prompt ; EOT`). Styles 1/2 are password/user prompts; 3/4 are error/informational text. A response is `SQ_RESPONSE ; i16 char_count ; padded(response) ; EOT`, limited to 512 characters. The default handler uses the password for style 1 and user name for style 2.

## 9. Parameter encoding

Wire type generally follows the .NET value type. Common mappings: null/DBNull→CHAR(0); string/char/bool→CHAR(0); small integral types→SMALLINT(1); int→INT(2); long→INT8(17); float→SMFLOAT(4); double→FLOAT(3); decimal→DECIMAL(5); DateOnly/midnight DateTime→DATE(7); other DateTime/TimeOnly→DATETIME(10); TimeSpan/IfxTimeSpan/IfxMonthSpan→INTERVAL(14); BYTE→11; TEXT→12; smart LOB→SENDRECV(44); JSON/BSON use internal extended codes. Precision is encoded per type; decimal uses .NET `decimal.Scale`. Unsigned 32/64-bit integers and Guid are unsupported. Bulk-loader mappings differ for some declared types (e.g. Boolean to CHAR, Money to DECIMAL).

## 10. Result value encoding

### 10.1 Type codes

| Code | Type | Code | Type |
|---:|---|---:|---|
| 0–8 | CHAR, SMALLINT, INT, FLOAT, SMFLOAT, DECIMAL, SERIAL, DATE, MONEY | 40–49 | UDTVAR, UDTFIXED, REFSER8, LVARCHAR, SENDRECV, BOOL, IMPEXP, IMPEXPBIN, SQLUDRDEFAULT, UNKNOWN |
| 9–18 | NULL, DATETIME, BYTE, TEXT, VARCHAR, INTERVAL, NCHAR, NVARCHAR, INT8, SERIAL8 | 52–53 | BIGINT, BIGSERIAL |
| 19–24 | SET, MULTISET, LIST, ROW, COLLECTION, ROWREF | 101–104* | CLOB, BLOB, JSON, BSON |

`101`–`104` are internal classifications derived from extended type IDs.

### 10.2 Rows, NULLs and scalar values

Rows occupy `tuple_size` bytes. Fixed-layout columns use descriptor start positions; variable “walk” rows are read sequentially. VARCHAR/NVARCHAR may have a leading one-byte length; LVARCHAR/LOB/JSON/BSON/UDT/BOOL use flag + `i32` length + payload; complex types use `i32` length + payload.

NULL markers include: SMALLINT `0x8000`; INT/SERIAL and DATE `0x80000000`; BIGINT/BIGSERIAL `0x8000000000000000`; INT8 sign 0; FLOAT/SMFLOAT all `0xFF`; CHAR/NCHAR first byte 0; variable extended values flag 1; ROW/collection length ≤ 0. BYTE/TEXT use descriptor flags.

INT8 is 10 bytes: `i16 sign`, low 32 bits, high 32 bits, all big-endian. Sign -1 means negative; 0 means NULL. DATE is an `i32` day count where 1 = 1900-01-01 and 0 = 1899-12-31.

DECIMAL/MONEY/DATETIME/INTERVAL use packed base-100 digits: a header byte and digit bytes; bit 7 of header indicates positive, and exponent is `(header & 0x7F) - 64`. Packed BIND/FastPath values are prefixed with `i16 length` and padded. DATETIME/INTERVAL qualifiers are `i16 (len<<8)|(start<<4)|end`; codes are YEAR 0, MONTH 2, DAY 4, HOUR 6, MINUTE 8, SECOND 10, FRACTION 11–15. IEEE float NULL is all `0xFF`.

BYTE/TEXT rows contain a 56-byte descriptor (68 bytes with bit 70), with `tb_start` at offset 8, `tb_end` at 12 and flags at 38. Data is fetched separately. CLOB/BLOB rows contain a flag, length and 72-byte locator.

ROW/SET/MULTISET/LIST/COLLECTION values contain a length-prefixed block with member descriptors and payloads. Descriptors include names, type, length and nesting level. Variable-length collection elements may use a reverse-read `i32` offset table terminated by -1; fixed-width elements may be sequential. Nested containers are length-delimited.

## 11. Character encoding

The wire character set is selected from `DB_LOCALE` for ASF strings, SQL text, `char`, tuple text, TEXT and CLOB. Recognized mappings include ISO-8859 variants, UTF-8, ASCII, Windows-125x, Shift-JIS/932, Big5/950, GB/936 and Korean/949. Numeric suffixes are treated as code pages; otherwise a .NET encoding name is used. Missing or unrecognized locales fall back to ISO-8859-1. If DB_LOCALE is absent, the client can query `sysmaster.sysdbslocale` for database collation and cache the result by host/service/server/database.

## 12. Common operation patterns

| Operation | Request | Response |
|---|---|---|
| DDL/DML without parameters | COMMAND, NDESCRIBE, EXECUTE, RELEASE, EOT | optional DESCRIBE, DONE, EOT |
| Prepare | PREPARE, NDESCRIBE, WANTDONE, EOT | DESCRIBE, optional DONE, EOT |
| Parameterized execute | ID, BIND, EXECUTE, EOT | DONE, optional INSERTDONE, EOT |
| SELECT | ID, CURNAME, optional BIND, OPEN, NFETCH, EOT | TUPLE*, optional TUPID, DONE, EOT |
| Subsequent fetch | ID, NFETCH, EOT | TUPLE*; empty fetch marks end |
| Cursor close / release | ID, CLOSE/RELEASE, EOT | EOT |
| Batch | ID, (BIND, EXECUTE)×N, EOT | DONE×N, EOT |
| Disconnect | EXIT without EOT | optional XACTSTAT, then EXIT/EOT |

Prepared-statement caching, connection pooling and SQL preprocessing are client behaviors. Named `@x`/`:x` parameters are converted to positional `?`; supported procedure-call syntax becomes Informix `execute procedure` text.

## 13. Client-side error codes

| Code | Meaning |
|---:|---|
| -408 | Invalid message type received from the sqlexec process. |
| -908 | Attempt to connect to database server (servername) failed. |
| -952 | User's password is not correct for the database server. |
| -25563 | Missing user or unresolved endpoint |
| -878 | Select-only mode used with a non-SELECT statement |
| -9752 | Argument must be a Statement Local Variable or SPL variable or argument for an OUT or INOUT parameter. |
| -9810 | Smart-large-object error. |
| -79716 | Invalid complex-type payload |
| -79885 / -79886 | PAM failure / response too long |
| -79998 | ASF redirect not followed |

## Appendix A. Statement types

Common `SQ_DESCRIBE.statement_type` values: 2 SELECT, 6 INSERT, 34 BEGIN WORK, 35 COMMIT, 36 ROLLBACK, 37 SAVEPOINT, 40 CREATE VIEW, 53 CREATE PROCEDURE, 54 DROP PROCEDURE, 56 EXECUTE PROCEDURE. SELECT and EXECUTE PROCEDURE with result fields return rows; insert cursors require INSERT.

## Appendix B. `IfxType` mapping

| Declared type | Wire code |
|---|---:|
| BigInt | 52 |
| Binary / Byte / Image / VarBinary | 11 |
| Bit / Boolean | 45 |
| Char / Char1 / UniqueIdentifier | 0 |
| DateTime / SmallDateTime / Timestamp / Time | 10 |
| Decimal / Numeric | 5 |
| Double / Float | 3 |
| Int / Integer | 2 |
| NChar / NText / Text / NVarChar | 15 / 12 / 12 / 16 |
| Real / SmallFloat | 4 |
| SmallInt / TinyInt | 1 |
| VarChar / Date / Serial / Money / Null | 13 / 7 / 6 / 8 / 9 |
| Int8 / Serial8 / BigSerial | 17 / 18 / 53 |
| Set / MultiSet / List / Row | 19 / 20 / 21 / 22 |
| SQLUDTVar / SQLUDTFixed / LVarChar | 40 / 41 / 43 |
| Blob / Clob / Json / Bson | 102 / 101 / 103 / 104 (internal codes) |
| SmartLOBLocator / BinaryVar / Binary18 | 44 |
| IntervalYearMonth / IntervalDayFraction | 14 |
| Other | 49 |

The Blob/Clob/Json/Bson codes (102/101/103/104) are the driver's internal `SqliTypeCode`
values, used when classifying a described column. As *bind* values these four, and the two
BINARY types, all travel as opaque UDTs: `SendRecv` (44), extended owner `informix`, and
the type name (`"json"`, `"bson"`, `"binaryvar"`, `"binary18"`) — a Clob/Blob parameter is
staged as a smart large object first and binds its 72-byte locator. So the type name, not
the code, is what tells them apart on the wire. `Binary18` is zero-padded to its 18 bytes
client-side; `BinaryVar` is sent as-is (the server enforces its own 253-byte ceiling).
