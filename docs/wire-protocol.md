# Informix.Data — the SQLI wire protocol

How this driver talks to an Informix server, from the TCP socket up to tuples. This is
reference material for anyone reading the `Informix.Data.Asf` / `Informix.Data.Sqli` /
`Informix.Data.Wire` sources, extending the codec, or staring at an `IFX_WIRE_TRACE`
hex dump. Everything here is either ported from the Informix JDBC driver or
was captured live against IDS 9.59.FC5 with tcpdump; the **Trap** call-outs mark spots
where the wire disagrees with what you'd guess (each one cost a live debugging session).

Layering, bottom-up:

```
TCP socket ── optionally SslStream (onsocssl = implicit TLS)
   │
   ├── ASF handshake        one request frame, one response frame   (Informix.Data.Asf)
   │
   └── SQLI message stream  everything after the handshake          (Informix.Data.Sqli)
        ├── session bring-up   SQ_PROTOCOLS → [PAM] → SQ_VERSION
        ├── statement pipeline prepare/describe/bind/open/fetch
        ├── large objects      BYTE/TEXT locators, smart-blob FastPath + SQ_LODATA
        └── transactions       begin/commit/rollback/savepoints
```

Related: [connection-string.md](connection-string.md) for how connection-string keys map
onto the handshake inputs.

---

## 1. Transport conventions

- **Everything is big-endian** — every integer on the wire, both directions
  (`IfxWireConverter` delegates to `BinaryPrimitives.*BigEndian`).
- **Default text code set is ISO-8859-1** (Informix "NOENCODING"); `DB_LOCALE` changes
  it, and it is the only thing that does — `CLIENT_LOCALE` is never sent. Decoded CHAR
  data strips trailing NULs.
- **Even-length padding**: variable-length fields are `[len:int16][payload]`, and an
  odd-length payload is followed by one pad byte, which the reader consumes
  (`ReadPadded`/`WritePadded`). Forgetting the pad byte desyncs the stream.
- **No outer framing after the handshake.** The ASF handshake frames are
  length-prefixed; the SQLI stream that follows is *not*. It is a bare sequence of
  2-byte message codes, each followed by a message-specific body. There is no way to
  skip a message you cannot parse — an unknown reply code is a hard protocol error
  (the driver throws -408 rather than guess at the body length).
- **Buffering**: reads and writes are buffered **separately** over the one socket
  (`AsfConnection.OpenSocket`). A single duplex `BufferedStream` cannot interleave
  read/write on a non-seekable stream — a write while read-ahead bytes sit in the
  buffer throws `NotSupportedException`, and even when it doesn't, the leftover bytes
  desync the exchange.
- **TLS** (`onsocssl` listeners) is *implicit*: the TLS handshake runs immediately on
  connect, before the first ASF byte, and everything below is unchanged inside the
  encrypted channel.
- **Out-of-band cancel**: a running statement is interrupted by sending the single
  byte `66` with `SocketFlags.OutOfBand` (TCP URG) on the raw socket — it bypasses the
  stream layers entirely. The server aborts the statement and replies with SQ_ERR
  **-213** ("Statement interrupted"), read by the in-flight receive loop; the
  connection remains usable.
- **Tracing**: set the environment variable `IFX_WIRE_TRACE=<file>` to get a hex dump
  of every byte in both directions (`WireTraceStream`, wrapped *below* the buffers so
  the dump reflects real socket I/O).

---

## 2. ASF handshake

The Association Services Facility handshake is one client frame and one server frame,
exchanged before any SQLI message (`AsfFrameCodec`, port of
`com.informix.asf.Connection`). Only the modern binary form is implemented (the
pre-6.00 ASCII handshake is not ported).

### 2.1 SL header

Both directions start with a 6-byte session-layer header:

```
[totalLength:int16]   includes these 6 bytes
[slType:byte]         1=CONREQ  2=CONACC  3=CONREJ  13=REDIRECT
[attribute:byte]      60 = PF_PROT_SQLI_0600 (the protocol level)
[options:int16]       0
```

### 2.2 Connection request (client → server)

The PDU after the header is a chain of numbered segments. Strings use the recurring ASF
shape `[len:int16 = bytes+1][bytes][NUL]` ("counted string" below); fixed identity
strings are written raw at their declared width, NUL-padded.

```
int16 100                      SQ_ASSOC
int16 101                      SQ_ASCBINARY
int32 61                       protocol level word (PF_PROT_SQLI_WITH_CSS)
counted "IEEEM"                float format

int16 108                      SQ_ASCBPARMS
raw   "sqlexec"+5×NUL          application type, 12 bytes
counted driverVersion          "9.280"        (the JDBC identity this port reuses)
counted serialNumber           "RDS#R000000"
counted "sqli"                 application id
int32 capabilities             316
int32 0, int32 0, int16 1
counted userName
counted password               (or int16 0 when absent — but see PAM, §4)
raw   "ol"+6×NUL               protocol name, 8 bytes
int32 61
raw   "tlitcp"+2×NUL           network type, 8 bytes
int32 1

int16 104                      SQ_ASCINITREQ
int16 11                       ASF_XCONNECT
int32 options                  0x3 base | 0x2000000 sqlhosts-group | 0x4000000 trusted-context
counted serverName             INFORMIXSERVER
counted database               (or int16 0)
int16 0 ×4                     reserved

int16 106                      SQ_ASCENV
int16 count { counted name, counted value }×count
                               DB_LOCALE plus the always-sent
                               DBPATH=. IFX_UPDDESC=1 CLNT_PAM_CAPABLE=1

int16 107                      SQ_ASCPINFO
int32 0, int32 pid, int32 tid
counted hostName, int16 0, counted cwd

int16 116                      SQ_ASCMISC_60
int16 10+len(appName)+1, int32 0, int32 0
counted applicationName

int16 127                      SQ_ASCEOT
```

> **Trap.** A PAM-protected listener (`s=4` in sqlhosts) rejects any client whose env
> block lacks `CLNT_PAM_CAPABLE=1` with **-1809** — before authentication even starts.
> The driver always sends it.

### 2.3 Connection response (server → client)

`[len:int16]` then `len-2` bytes of body: the SL header tail, then the same
SQ_ASSOC/SQ_ASCBINARY/SQ_ASCBPARMS shape echoing the server's identity — version
string, serial, application id, three capability ints, user/password echoes, the
`ol`/`tlitcp` block — and finally one tail segment:

| Tail segment | Meaning |
|---|---|
| `127` SQ_ASCEOT | plain accept |
| `102` SQ_ASCINITRESP | accept **or** reject detail: 6 skip bytes, `svcError:int16`, `osError:int16`, `warnings:int16`, error count + counted messages. On an `slType=3` reject frame the svcError becomes the thrown `IfxSqlCode` (e.g. -951 bad password, -387 no connect permission). |
| `103` SQ_ASCDBLIST | redirect: a counted detail string (`server,host,port`) — surfaced as `AsfRedirectException` (-79998) |

Quirk kept from the JDBC driver: a version string containing "9.50" is rewritten to
"10.00" (internal-version renumbering).

After an accept, the socket switches to the SQLI message stream. Orderly shutdown later
is `SQ_EXIT` (56), draining any `SQ_XACTSTAT` (99, three int16s) frames until the exit
ack.

---

## 3. SQLI framing: PDUs, EOT, and the receive loop

SQLI is message-at-a-time, half-duplex:

- A **request PDU** is one or more messages chained back-to-back, terminated by
  `SQ_EOT` (12) and flushed (`SqliSession.Flip()`). Chaining is the protocol's batching
  mechanism — e.g. `SQ_COMMAND + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE` execute an
  immediate statement in a single round trip.
- The **reply** is likewise a chain of messages ending in `SQ_EOT`. The receive loop
  (`ReceiveMessage`/`Dispatch`) reads message codes and dispatches handlers until EOT,
  collecting any `SQ_ERR` along the way and throwing it *after* the stream is fully
  drained — that discipline is what keeps the connection usable after an error.
- The server never speaks unprompted; every reply is a response to a flushed PDU.

Reply messages this driver handles: `SQ_VERSION SQ_PROTOCOLS SQ_DONE SQ_DESCRIBE
SQ_TUPLE SQ_TUPID SQ_BLOB SQ_CLOSE SQ_COST SQ_INSERTDONE SQ_PUTERR SQ_GETROUTINE
SQ_FPROUTINE SQ_LODATA SQ_XACTSTAT SQ_ERR SQ_EOT SQ_EXIT`. Anything else is a loud
-408 (a message body of unknown layout cannot be skipped, §1).

Common reply bodies:

```
SQ_DONE  (15)   [warnings:int16][rowsProcessed:int32][rowid:int32][serialInserted:int32]
SQ_ERR   (13)   [sqlcode:int16][isamcode:int16][offset:int16|int32*][message:char]
                * int32 when the remove-64K capability is on (§5)
                sqlcode 100 = "no rows" and is swallowed as a non-error
SQ_COST  (55)   [estRows:int32][estIO:int32]
SQ_XACTSTAT(99) [int16 ×3]
```

---

## 4. Session bring-up

Immediately after the ASF accept, in this exact order
(`SqliSession.BringUp`):

```
1. SQ_PROTOCOLS (126)   capability-vector exchange
2. PAM                  only on PAM ports
3. SQ_VERSION  (53)     server version string
```

> **Trap.** The order is load-bearing. Sending SQ_VERSION before SQ_PROTOCOLS is
> tolerated on a loopback connection but makes a remote server close the socket
> silently. PAM, when required, sits strictly between the two.

**SQ_PROTOCOLS**: `[126][len:int16][vector, even-padded]`. The client vector is the
8-byte constant `ff fc 7f fc 3c 8c aa 97` (verbatim from `IfxSqliConnect`); with the
`Large Row Formats` connection keyword it is the 9-byte form `ff fc 7f fc 3c 8c aa 97 06`
(what JDBC 15.0.1.3 sends), the extra byte asking for bits 69 and 70 below. The server
replies with its own vector in the same shape.

> **Trap (bits 69/70 are negotiated, not announced).** Informix 15.0.1.0 sets bits 69
> and 70 in its reply even when the client sent the 8-byte vector — and then keeps the
> classic layouts for that client. Taking the reply at face value reads an int32 tuple
> size out of an int16 field: login succeeds, and the first SQ_DESCRIBE blocks until the
> socket times out. The two bits therefore count only when the client asked for them
> (`ApplyServerProtocol(vector, largeRowFormatsRequested)`); 14.10 and older never set
> them, so nothing changes there.

**PAM** (`AuthenticatePam`, port of `handlePAMAuthentication`) is a challenge/response
loop on the SQLI stream:

```
client:  SQ_ACK (128) + EOT
server:  SQ_CHALLENGE (129) [style:int16][len:int16][text, even-padded] + EOT
client:  SQ_RESPONSE (130) [len:int16][text, even-padded] + EOT
         ... repeat for any number of challenges ...
server:  SQ_ACCEPT (127) + EOT          success
```

`style` is the PAM message type (1=echo-off prompt, 2=echo-on prompt, 3=error,
4=info); info/error challenges expect no response. Either side can send `SQ_EXIT` to
abort. A response is capped at 512 characters. Failure surfaces as **-79885**. Note
that with an inline password on a PAM port the server may accept without issuing any
challenge. Who *answers* the challenges is the pluggable `IIfxPamHandler`
([connection-string.md §3](connection-string.md)).

**SQ_VERSION**: request `[53]+EOT`; reply `SQ_VERSION [version:char]` — e.g.
`"9.59.FC5"`.

### 4.1 Capabilities

Wire formats downstream are gated by a 64-bit capability set (`SqliCapabilities`,
port of the `dbVersion` BitSet), filled from two sources in order:

1. the **version string** through the `getServerVer` ordinal-compare ladder
   (e.g. `"9.59"` ⇒ bits {3, 2, 49, 0, 51}), then
2. the **server's SQ_PROTOCOLS vector**, OR-ed in MSB-first: byte *i* maps onto bits
   *8i..8i+7* (port of `enhancedProtocolMechanism`).

The bits that change on-wire layouts:

| Bit | Property | Effect on the wire |
|----:|----------|--------------------|
| 2 | `IsExtendedDescriptor` | describe: int32 start positions + the extended type block (§7.2); tuples: int32 size |
| 8/9 | `IsFetchArray` | SQ_NFETCH carries a trailing fetch-array `int16` |
| 13 | `IsVarcharVarLenSupported` | VARCHAR tuple fields carry only actual data after the length byte (rows must be walked) |
| 50 | `Is4ByteOffsetSupported` | describe string-table size and field indexes are int32 |
| 52 | `IsNamedParametersSupported` | SQ_GETROUTINE carries a leading resolve-by-id byte and its reply a database name; SQ_EXFPROUTINE opens with that name; SQ_FPARAM has a named-flags word; -9752 carries the `db:handle:unique` triple (§8.2.1) |
| 54 | `IsBigIntSupported` | BIGINT/BIGSERIAL exist; SQ_INSERTDONE carries a trailing BIGSERIAL (**not** bit 52 — the two are easy to conflate) |
| 62 | `IsRemove64KLimitSupported` | **statement text gets a 4-byte length prefix**; SQ_ERR offset is int32 |
| 63 | `Is2GBFetchBufferSupported` | fetch-buffer size is int32 (else int16) |
| 69 | `IsLargeTupleSize` | the tuple size in SQ_DESCRIBE / SQ_PREPARE is int32 (rows over 32 KB). Informix 15; in effect only when the client asked via the 9-byte vector — the server sets the bit either way (see the trap in §4) |
| 70 | `IsLongRowId` | row ids and SQ_DONE / SQ_PUTERR row counts are 8 bytes; the in-tuple BYTE/TEXT descriptor grows from 56 to 68 bytes (`tb_size` moves from offset 16 to 32). Informix 15; opt-in as above. Live against 15.0.1.0 a plain query round-trips with it on; the BYTE/TEXT descriptor path is not yet exercised |

A real 9.59 server announces 4-byte offsets, remove-64K, 2-GB fetch, fetch-array and
var-length VARCHAR via the vector — the version ladder alone claims none of them.

> **Trap (statement text).** With bit 62 set, the SQL text in SQ_PREPARE/SQ_COMMAND is
> `[len:int32][bytes, even-padded]` — but **cursor names keep the 2-byte prefix**.
> Sending 2-byte statement text makes the server wait forever for bytes that will
> never come.

---

## 5. Wire primitives (`Informix.Data.Wire`)

| Type | Wire form |
|------|-----------|
| SMALLINT / INT | 2 / 4 bytes big-endian; in tuples, NULL = `MinValue` sentinel |
| BIGINT / BIGSERIAL | 8 bytes big-endian two's complement |
| INT8 / SERIAL8 | **10 bytes**: `[sign:int16][low32][high32]` — sign 0 ⇒ NULL, ±1 the sign; magnitude split into two int32 halves, *low half first* |
| SMALLFLOAT / FLOAT | IEEE-754 big-endian 4 / 8 bytes; NULL = all `0xFF` |
| DATE | int32 signed day count; day 1 = 1900-01-01 (`DAYS1900` epoch math); NULL = `int.MinValue` |
| CHAR / VARCHAR | `[len:int16][bytes][pad-if-odd]`; trailing NULs stripped on decode; in-tuple NULL: CHAR ⇒ first byte 0, VARCHAR ⇒ len 1 + byte 0 |
| DECIMAL / MONEY / DATETIME / INTERVAL | packed decimal (below), framed `[len:int16][payload][pad-if-odd]` |

**Packed decimal** (`IfxDecimal`, port of `lang.Decimal`): payload =
`[expByte][base-100 digit bytes 0..99]`. A positive value's exponent byte is
`(exp+64) | 0x80`; a negative value stores the digits in 100's-complement with the
exponent byte XOR-ed with `0x7F`; NULL = zero exponent byte and no digits. DATETIME and
INTERVAL are packed-decimal digit strings of their field values, positioned by the
**qualifier** word `(length<<8)|(start<<4)|end` in `TU_*` units — `length` is the total digit
count (high byte), `start` and `end` the first and last field codes (two nibbles of the low byte);
e.g. `DATETIME YEAR TO SECOND` is `0x0E0A`: 14 digits, start 0, end 10.

> **Trap (decompiler bug, corrected here).** The decompiled Java computes a leading
> field width as `len - end - start`; the real Informix macro is
> `TU_LEN - TU_END + TU_START`. The Java formula breaks HOUR TO SECOND / DAY TO
> FRACTION decode; this port uses the corrected formula
> (`IfxQualifier.GetLeadingFieldWidth`) with regression tests.

---

## 6. Statement pipeline

All ports of `IfxSqli` methods; each block below is one PDU unless noted.

```
immediate    SQ_COMMAND(1) + count:int16(0) + text + SQ_NDESCRIBE(22) + SQ_EXECUTE(7) + SQ_RELEASE(11)
prepare      SQ_PREPARE(2) + paramCount:int16 + text + SQ_NDESCRIBE(22) + SQ_WANTDONE(49)
             ← reply carries SQ_DESCRIBE (§7.2) with the statement id + columns
execute      SQ_ID(4)+stmtId + [SQ_BIND(5) …] + SQ_EXECUTE(7)          (non-cursor DML)
open cursor  SQ_ID + SQ_CURNAME(3)+name + [SQ_BIND] + [SQ_SCROLL(24)] + [SQ_HOLD(43)] + SQ_OPEN(6)
fetch        SQ_NFETCH(9) + bufSize:int16|int32* + [fetchArray:int16*]
close        SQ_CLOSE(10)          release   SQ_RELEASE(11)            (* capability-gated)
```

**Fixed-width rows** piggyback the first fetch in the same PDU as SQ_OPEN — one round
trip opens the cursor *and* returns the first tuple buffer. **Variable-length rows**
(anything walked, §7.3) classically take two phases: receive the open reply, then send
`SQ_RETTYPE(100) + SQ_NFETCH`. The `OPTOFC` option piggybacks that pair after SQ_OPEN
for forward SELECTs and EXECUTE PROCEDURE cursors, saving the extra round trip.

**SQ_RETTYPE** announces the client's receive representation per column:
`[type:int16]` + — for extended types (word > 18, except BIGINT/BIGSERIAL) —
`[owner:char][typeName:char]`, then `[length:int32]`.

> **Trap.** The RETTYPE type for a complex/opaque column is *not* the describe's type:
> LVARCHAR announces as 43, but ROW/SET/MULTISET/LIST/COLLECTION/UDT/CLOB/BLOB must be
> announced as **SENDRECV (44)** keyed off the extended id (xid 3→46, 4→47, 1→43,
> 5→45, else→44). Echoing the describe code (22, 19, 41…) draws **-9971**.

> **Trap (driver-internal, not a wire quirk).** When a column's describe-time base type
> gets collapsed to a *different* internal `SqliTypeCode` — as JSON(25)/BSON(26)/LVARCHAR(1)/
> CLOB(11)/BLOB(10) all do, going from wire base type UdtVar(40)/UdtFixed(41) to their own
> dedicated marker — every switch that decides "is this variable-length" must be updated to
> recognize the *new* marker, not just the original wire base type. `SqliSession
> .IsVariableLengthColumn` gates whether `SendRetType` remaps the type at all (§ above); miss
> a marker there and the column's real `SqliTypeCode` (an internal, non-wire number) gets sent
> as the RETTYPE type word, which the server doesn't recognize — it silently closes the
> connection rather than replying with a parseable error. This is exactly what happened when
> JSON/BSON were first added: the describe-time collapse (§7.4) was right, but
> `IsVariableLengthColumn` still only listed the *raw* `UdtVar`/`UdtFixed` codes, not the new
> `Json`/`Bson` ones. Any future extended-type collapse should grep every
> `SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed`-shaped condition, not just the describe switch.

**SQ_BIND** (§6.1) and the fetch/tuple formats (§7) carry the densest traps.

### 6.1 Bind (SQ_BIND)

`[5][count:int16]` then per parameter: `[type:int16]` — for an opaque/UDT type word
(> 18, excluding 52/53) followed by `[owner:char][typeName:char]` *before* the
indicator, so a typed NULL still names its type — then `[indicator:int16]`
(0 = value, negative = NULL), `[precision:int16]`, and the data.

> **Trap (data encodings are per-type, not uniform).**
> - CHAR/string data is **length-prefixed**: `[len:int16][bytes]`. Sending bare bytes
>   makes the server read the first two data bytes as a length and block on a phantom
>   read (`"he"` = 0x6865 = a 26 KB wait).
> - DECIMAL/DATETIME/INTERVAL **keep their 2-byte length prefix** in binds (only tuple
>   data is bare packed bytes). DECIMAL's bind precision is its scale.
> - DATE (4 bytes) and INT (4 bytes) bind **bare**. Their precisions are magic
>   constants the server ignores: INT = 2560, INT8 = 4864.
> - BYTE/TEXT parameters put their 56-byte descriptor inline here; the payload
>   follows in SQ_BBIND (§8.1). Smart-blob parameters bind the 72-byte locator with
>   precision 72 + owner/name `informix`/`clob|blob` (§8.2).

### 6.2 Batch, scroll, insert cursor

**Batch** (`ExecuteBatch`): `SQ_ID` once, then `SQ_BIND + SQ_EXECUTE` per row, all
chained in one PDU (flushed and re-`SQ_ID`-ed if the PDU approaches ~32 000 bytes).
The reply is one `SQ_DONE` per row — the per-row affected counts.

**Scroll cursors**: open with `SQ_SCROLL(24)` (+ `SQ_HOLD(43)` if hold; never for
ExecProc) before `SQ_OPEN`; the open returns no rows. Positioned fetch:

```
SQ_ID + [SQ_RETTYPE if var-length] + SQ_SFETCH(23) + [direction:int16][index:int32] + bufSize
```

Direction codes actually used: NEXT=1, LAST=4, ABSOLUTE=6 (first/prev/relative are
client-side arithmetic over ABSOLUTE). The reply prefixes the tuples with
`SQ_TUPID(25) [rowId:int32]` — the absolute id of the buffer's first row.

> **Trap.** `SQ_SFETCH` does **not** carry the fetch-array short that `SQ_NFETCH`
> appends when the fetch-array capability is on.

**Insert cursor** (bulk loader): prepare the INSERT, then
`SQ_ID + SQ_CURNAME + [SQ_HOLD] + SQ_OPEN`; rows stream as
`SQ_PUT(17) [rowSize:int32][nrows:int16]` messages carrying pre-encoded tuple images,
chained many per PDU; the server acks with `SQ_INSERTDONE(94)` counts and reports
per-row failures via `SQ_PUTERR(18)`; flush is implicit in EOT boundaries and close is
a plain `SQ_CLOSE + SQ_RELEASE`. A PUT PDU is filled up to the **insert buffer** —
`FET_BUF_SIZE` when configured (in Informix that variable sizes insert-cursor buffers
as well as fetches), else ~32 KB — and never more than 32 767 rows (`nrows` is int16).

> **Trap (live-verified against IDS 9.59, 2026-07-23).** `SQ_OPEN` for a **non-hold**
> insert cursor (no `SQ_HOLD` in the chain) fails with **-255** ("not in transaction")
> on a **logged** database unless a transaction is already active — the server appears
> to require *something* for the cursor's lifetime to be bounded by, and without HOLD
> that can only be the transaction. Confirmed both ways: the identical open with
> `SQ_HOLD` present succeeds with no active transaction, and the identical non-hold
> open succeeds unconditionally on an *unlogged* database. `IfxBulkLoader.HoldCursor`
> defaults to `false` (§4.5 of the migration guide), so this now surfaces for the
> simplest no-transaction load against a logged database — it did not surface before,
> when the loader always opened `WITH HOLD`.

### 6.3 Transactions

```
SQ_BEGIN(35)   SQ_CMMTWORK(19)   SQ_RBACKWORK(20) + savepointIndicator:int16 (0 = whole txn)
savepoints: [137|138|139] + name:char   (137 SET also appends unique:int16)
```

`SET ISOLATION` has no dedicated opcode — it is an ordinary immediate statement.
`SQ_DBOPEN(36) [name:char][flags:int16]` exists but a connection opened *with* a
database rejects it (-759), so `ChangeDatabase` reconnects instead.

> **Trap.** Unlike SQ_CMMTWORK, `SQ_RBACKWORK` carries a trailing savepoint word,
> written unconditionally (0 rolls back the whole transaction). Omitting it desyncs
> the stream — this was the "rollback hang" bug: the server waited for the word while
> the client waited for the reply (ESQL/C sends it too).

> **Trap (live-verified against IDS 9.59, 2026-07-23).** `SQ_CMMTWORK` and
> `SQ_RBACKWORK` both close any open cursor that was not declared `WITH HOLD` —
> **symmetrically**: a non-hold cursor does not survive a rollback any more than it
> survives a commit (easy to assume otherwise, since a rollback "undoes" things). The
> next operation on such a cursor (`SQ_NFETCH`, `SQ_PUT`, …) fails; the driver has to
> know this to avoid sending into a cursor the server already discarded — see
> `IfxBulkLoader.HoldCursor` and `ISqliTransactionAwareStatement`, which flush
> (commit) or drop (rollback) any buffered rows *before* the wire commit/rollback and
> track the cursor as closed afterward when hold was not requested.

---

## 7. Describe and tuple decode

### 7.1 The reply to prepare/describe

`SQ_DESCRIBE (8)`:

```
[statementType:int16]        e.g. 2=SELECT, 56=EXECUTE PROCEDURE
[statementId:int16]          echo back in SQ_ID for every later operation
[estimatedCost:int32]
[tupleSize:int16]            fixed row image size
[fieldCount:int16]
[stringTableSize:int16|int32 when 4-byte-offset]
per field:
  [fieldIndex:int16|int32 when 4-byte-offset]
  [startPosition:int16|int32 when extended-descriptor]   row-relative
  [typeWord:int16]                                        base|flags (§7.4)
  extended-descriptor block (when capability bit 2):
    [extendedId:int32]        IFX_XID_* (LVARCHAR=1, BLOB=10, CLOB=11, …)
    [owner:char][typeName:char]
    [reference:int16][alignment:int16]
    [sourceType:int32]        DISTINCT base type
    [length:int32]
  else: [length:int16]
[stringTable: NUL-separated column names, even-padded]
```

Identity collapses the driver applies (all observed live): LVARCHAR arrives as
UdtVar(40)/UdtFixed(41) with extended id 1 (or directly as base 43); LLVC — extended id
24, `informix`/`longlvarchar`, the >32 KB lvarchar — collapses to LVARCHAR too, same
framing and same inline character payload; CLOB/BLOB arrive as UdtFixed(41) with extended
id 11/10 and owner/name `informix`/`clob|blob`; JSON/BSON likewise arrive as
UdtVar(40)/UdtFixed(41) with extended id 25/26 and owner/name `informix`/`json|bson`; a
DISTINCT type is its base code with flag 0x800 and the distinct name in the extended
owner/name fields. Each collapse maps onto its own internal
`SqliTypeCode` marker (`LVarChar`/`Clob`/`Blob`/`Json`/`Bson` — none are real wire codes);
see the driver-internal trap under §6 about keeping every "is this variable-length" switch
in sync with new markers.

### 7.2 Tuples

`SQ_TUPLE (14)`: `[warnings:int16][size:int16|int32 when extended-descriptor]` then
`size` raw bytes — one row image appended to the client's tuple buffer. Multiple
SQ_TUPLE messages arrive per fetch reply until the buffer the client requested is
full. An `SQ_CLOSE` inside a fetch reply means the server auto-closed the cursor at
end-of-rows (scroll and hold cursors are never auto-closed and need an explicit
SQ_CLOSE + SQ_RELEASE).

### 7.3 Row image layout

Fixed-width columns sit at their describe `startPosition`; a row is only *walked*
(field-by-field scan per row) when it contains variable-width fields:

| Field kind | In-row bytes |
|---|---|
| fixed scalar | the §5 wire form at its start position; packed types occupy `((len>>8 & 255) + (len & 1) + 3) / 2` bytes |
| VARCHAR (var-len capability) | `[len:byte][data]` — actual length only |
| BYTE / TEXT | a fixed **56-byte locator descriptor** (§8.1) — fixed-width! |
| LVARCHAR, CLOB/BLOB, JSON/BSON, **opaque UDT** | `[flag:byte][len:int32][data]` — flag 1 + len 0 ⇒ SQL NULL; for smart blobs the data is the 72-byte `ifx_lo_t` locator, for JSON/BSON and opaque UDTs it is the actual inline payload |
| ROW / SET / MULTISET / LIST / COLLECTION | `[len:int32][data]` — **no flag byte**, a self-describing SENDRECV blob (type descriptor + values); a NULL still arrives as a shorter descriptor blob |

> **Trap.** The dividing line is *not* "scalar vs complex", and not "did we request the
> SENDRECV(44) representation" — an opaque UDT is announced as SENDRECV and still carries
> the flag byte. It is exactly `IfxRowColumn.setOffsetInTuple`: base type **40/41/43/45/46**
> reads the length at `offset + 1` (footprint `5 + len`), and only `IfxTypes.isComplexType`
> — ROW(22) and the collections(19/20/21) — reads it at `offset` (footprint `4 + len`).
> Putting UDTVAR/UDTFIXED on the wrong side costs you the whole row, not just that column:
> the walk drifts one byte and every following column decodes from the wrong offset. Live
> capture of `select first 2 indexkeys, idxname from sysindices` (IDS 14.10), one 25-byte
> row image — `00 | 00 00 00 0c | "1 [1], 2 [1]" | 07 | "tabname"` — flag, int32 length,
> 12 bytes of payload, then the VARCHAR's own length byte.

**Nested ROW/collection members.** Inside a ROW/collection SENDRECV blob, a member that
is *itself* a ROW or collection is framed `[size:int16][size bytes]`
(`IfxComplexDecoder.ReadNestedComplex`): `size == 1` marks SQL NULL (one marker byte
follows, 3 bytes total); otherwise the block opens with an alignment header — padding out
to an 8-byte boundary plus a constant flags word, skipped wholesale — followed by the
member's value block. The nested payload does **not** repeat the type descriptor; decode
follows the child subtree already built from the outer describe. The size word makes the
framing self-delimiting, so slack bytes inside a nested slot are harmless.

> **Trap.** An earlier decode treated a nested member as running to the end of the outer
> value block — swallowing every sibling member after it. The `[size:int16]` word is
> what bounds the member; always advance by it, never by "whatever is left".

In-band NULL sentinels for fixed scalars: INT/SMALLINT/BIGINT `MinValue`, INT8
sign=0, FLOAT/SMALLFLOAT all-0xFF, DATE `int.MinValue`, CHAR first byte 0, VARCHAR
len 1 + byte 0, DECIMAL/DATETIME zero exponent byte.

### 7.4 The type word

The 16-bit descriptor/bind type word is `flags | baseType`: low byte = base code,
high byte = flags. Flags: `0x100` NOT NULL, `0x800` DISTINCT, `0x1000` named ROW,
`0x4000` DBoolean, `0x8000` client collection.

Base codes (`SqliTypeCode`, = the C `SQLxxx` codes):

```
 0 CHAR      1 SMALLINT  2 INT       3 FLOAT     4 SMALLFLOAT  5 DECIMAL
 6 SERIAL    7 DATE      8 MONEY     9 NULL     10 DATETIME   11 BYTE
12 TEXT     13 VARCHAR  14 INTERVAL 15 NCHAR    16 NVARCHAR   17 INT8
18 SERIAL8  19 SET      20 MULTISET 21 LIST     22 ROW        23 COLLECTION
40 UDTVAR   41 UDTFIXED 43 LVARCHAR 44 SENDRECV 45 BOOL       52 BIGINT
53 BIGSERIAL  (driver-internal, never on the wire: 101 CLOB, 102 BLOB, 103 JSON, 104 BSON)
```

---

## 8. Large objects

### 8.1 Simple large objects — BYTE / TEXT

A BYTE/TEXT column's in-row footprint is a fixed **56-byte descriptor** (that is
literally its `syscolumns.collength`): `tb_start:int32@8`, `tb_end:int32@12`,
`tb_size:int32@16`, `tb_flags:int16@38`; data length = `tb_end - tb_start + 1`;
**`tb_flags & 1` (BLOBISNULL) ⇒ SQL NULL — do not fetch, or the server blocks.**

**Read** is a lazy separate round trip per value:

```
SQ_ID + SQ_FETCHBLOB(38) + [56-byte descriptor echoed verbatim, even-padded] + EOT
← SQ_BLOB(39) frames [size:int16][data, even-padded] …, then a zero-length SQ_BLOB
  terminator, then EOT — no SQ_DONE
```

This interleaves fine with an open cursor mid-buffer (the locator is self-contained).

**Write**: the bind carries an inline 56-byte descriptor that is all zero except
`tb_size` (§6.1); after the whole SQ_BIND the payload follows as
`SQ_BBIND(41) [count:int16]` then, per non-null blob in parameter order, SQ_BLOB
frames **chunked at 1024 bytes** + a zero-length terminator. Full PDU order:
`SQ_ID + SQ_BIND + SQ_BBIND + SQ_EXECUTE + EOT`. The server sizes the value from the
SQ_BLOB frames, not the descriptor.

### 8.2 Smart large objects — CLOB / BLOB

Smart blobs ride two mechanisms layered on SQLI: **FastPath** (server routine
invocation) for the `ifx_lo_*` API, and **SQ_LODATA** for the data pipe. The in-row
value is the 72-byte `ifx_lo_t` locator (§7.3).

**FastPath**: `SQ_GETROUTINE(101) [isById:byte=0][signature in statement-text
encoding — 4-byte length under remove-64K][fparam:int16=0]` resolves a routine name to
a handle (`← SQ_GETROUTINE [dbName:char][handle:int32]`, cached per session).
`SQ_EXFPROUTINE(102) [dbName:char — 2-byte form!][handle:int32][paramCount:int16]
[fparam:int16]` + a normal SQ_BIND executes it;
the reply `SQ_FPROUTINE(103)` carries `[count:int16]` then per value
`[type:int16][owner+name when >18 && not 52/53][indicator][precision][data]`, UDT data
framed `[len:int32][padded]`. Signatures are matched verbatim, including the original's
stray spaces — e.g. `function informix.ifx_lo_open(blob ,integer)`.

Routines used: `ifx_lo_create(ifx_lo_spec, integer, blob)`, `ifx_lo_open(blob,
integer)`, `ifx_lo_close(integer)`, `ifx_lo_seek(integer, int8, integer, int8)`,
`ifx_lo_stat(int, ifx_lo_stat)`, `ifx_lo_truncate`. The lo-spec is a 596-byte
descriptor (magic `0xDADAFEED`); the stat blob is 644 bytes with the size at
`[4..14)` as an "UDT long" — `[low32][high32][sign:int16]`, sign 0 ⇒ null.

**SQ_LODATA (97)** moves the bytes:

```
[subCommand:int16]   0=READ  1=READ+SEEK  2=WRITE
[loFd:int16][nbytes:int32][32000:int16]           ← R_W_BUFSIZE, always present
[offset:int8-wire][whence:int16]                  only for subCommand 1
WRITE: payload chunks [size:int16][data, even-padded], ≤32000 each, then reply opType 2 + written:int32
READ:  reply opType 0 + totalSize:int32 + chunks [size:int16][data, even-padded]
```

> **Trap.** The `R_W_BUFSIZE 32000` short in the SQ_LODATA header is easy to miss;
> omit it and the server misparses the request. Also note a
> READ always returns an `nbytes`-sized buffer — bound requests to the remaining
> length or you over-read.

### 8.2.1 FastPath again: SPL routines with OUT / INOUT parameters

An SPL routine that declares `OUT`/`INOUT` arguments **cannot run through
prepare/execute at all**. `SQ_PREPARE` of `execute procedure p(?,?,?)` is answered with
`SQ_ERR` sqlcode **-9752** whose `sqlerrm` is not a message but the routine's identity —
`dbName:handle:unique`, e.g. `testlog:571:1`. That is the server *telling* the client to
re-drive the call over FastPath, so -9752 is a routing signal, not a failure.

Two traps around getting that far:

> **Trap.** The statement text must carry one `?` per **declared** argument, OUT ones
> included, or the routine name does not resolve at all and you get -674 ("routine not
> found") instead of -9752 — the argument count participates in overload resolution.

> **Trap.** -9752 only carries the identity triple on a server that grants capability
> **bit 52** (`isNamedParametersSupported`). Note bit 52 is *not* BIGINT — that is
> **bit 54** (`isBigintSupported`, the bit that decides whether `SQ_INSERTDONE` carries a
> trailing BIGSERIAL). Both are set on 9.59, which makes them easy to conflate.

The call then goes:

```
→ SQ_GETROUTINE(101) [isById:byte=1][dbName in statement-text encoding][handle:int32][fparam:int16=1]
← SQ_GETROUTINE      [dbName:char][handle:int32]
← SQ_FPARAM(104)     the routine descriptor (below)
→ SQ_EXFPROUTINE(102)[dbName:char][handle:int32][paramCount:int16][fparam:int16=1]
  SQ_BIND            one value per declared argument — an OUT-only argument binds as a
                     NULL that still names its type, so the server knows the slot's shape
  SQ_FPARAM(104)     [funcState:int8-wire][request:int32][isDone:byte — the *inverse* of
                     what the reply said]
← SQ_FPROUTINE(103)  the produced values, in the §8.2 per-value format
```

The `SQ_FPARAM` reply descriptor:

```
[funcState:int8-wire][request:int32][isDone:byte][id:int32][row:int8-wire][colid:int32]
[nrets:int16]  then nrets × typeInfo          ← return values first, no mode word
[nargs:int16]
[namedFlags:int16]                            ← only when bit 52 is granted; &1 = names present
               then nargs × typeInfo, each ending [mode:int16]

typeInfo = [argFlags:byte][name:char]         ← only when namedFlags&1
           [argNull:byte][type:int16][xid:int32][len:int32][precision:int16][scale:int16]
```

`mode == 1` marks an argument the routine writes back — **both** `OUT` and `INOUT` report
it. The `SQ_FPROUTINE` reply then carries, in order, the `mode == 1` argument values (in
declaration order) followed by the `nrets` RETURNed values. Informix sends exactly one
such reply per execution, which is why a routine with OUT parameters can never also
stream a result set.

Live-verified 2026-07-29 against IDS 9.59.FC5 with the routine from IBM's
"The OUT and INOUT Parameters" doc page, plus OUT arguments of type `DECIMAL`, `CHAR`,
`DATE`, `INT8`, `FLOAT`, `SMALLINT` and `DATETIME` (and an unassigned one, which comes
back NULL). Driver side: `IfxOutParameterCall`, `SqliFParam`; usage-level detail in
[migration-guide.md](migration-guide.md).

### 8.3 JSON / BSON — inline opaque, *not* a large object

Despite living next to CLOB/BLOB in this document, JSON(25)/BSON(26) are **not** smart
large objects: no locator, no FastPath, no SQ_LODATA. The value travels **inline in the
tuple**, framed exactly like LVARCHAR (§7.3) — `[flag:1][len:int32][data]` — and is
decoded directly by `SqliSession.DecodeValue`, same as any other in-row field.

- **Read**: JSON *and* BSON decode to a JSON-text `string` (BSON via
  `Informix.Data.BsonMediaTypeFormatter.ToJson`), so both read through the generic
  `IDataReader` facade (the standard ADO.NET approach for binary JSON). Raw BSON bytes stay reachable via
  `GetBytes`/`GetFieldValue<byte[]>`; the parsed DOM via `GetFieldValue<JsonDocument>`
  (for BSON that path is `ToJsonDocument`: raw bytes → one streaming UTF-8 transcode →
  parse, no intermediate string).
- **Bind**: an opaque UDT bind — type `SENDRECV(44)`, owner `informix`, name `json`/`bson`,
  data `[len:int32][payload]` (§6.1's general UDT-bind shape, precision = payload length) —
  the same shape CLOB/BLOB use for their *locator*, but here the payload is the JSON text /
  BSON bytes directly, not a 72-byte handle.
- **`BsonMediaTypeFormatter`** (`Informix.Data`, no external BSON package) converts BSON
  bytes to/from JSON text using the MongoDB Extended-JSON wrapper convention (`$oid`,
  `$date`, `$binary`, `$regularExpression`, `$timestamp`, `$numberLong`/`$numberInt`/
  `$numberDouble`, `$minKey`/`$maxKey`) for values plain JSON can't represent losslessly.
- Live-verified 2026-07-13 against IDS 9.59.FC5: `genbson(row(manu_code),0,1)` and its
  `::json` cast read correctly; a real `BSON`/`JSON` column (`CREATE TABLE t (doc bson, txt
  json)`) round-tripped an insert-then-select through the bind path above byte-for-byte.
  Usage-level detail: [migration-guide.md](migration-guide.md) §4.4.

---

## 9. Message code reference

`SQ_*` opcodes (from `IfxMessageTypes`; the subset this driver exchanges — see
`SqliMessage` for the full enum):

| Code | Name | Role |
|-----:|------|------|
| 1 | COMMAND | immediate statement text |
| 2 | PREPARE | prepare statement text |
| 3 | CURNAME | cursor name |
| 4 | ID | select prepared statement |
| 5 | BIND | parameter values |
| 6 | OPEN | open cursor |
| 7 | EXECUTE | execute prepared/bound |
| 8 | DESCRIBE | ← column descriptor |
| 9 | NFETCH | fetch next buffer |
| 10 | CLOSE | close cursor (also ← auto-close) |
| 11 | RELEASE | free statement |
| 12 | EOT | end of PDU, both directions |
| 13 | ERR | ← sqlcode/isam/message |
| 14 | TUPLE | ← one row image |
| 15 | DONE | ← statement completion |
| 17 | PUT | insert-cursor row |
| 18 | PUTERR | ← per-row insert failure |
| 19/20 | CMMTWORK / RBACKWORK | commit / rollback |
| 22 | NDESCRIBE | request describe |
| 23/24/25 | SFETCH / SCROLL / TUPID | scroll fetch / open / ← row id |
| 35/36 | BEGIN / DBOPEN | begin work / open database |
| 38/39/41 | FETCHBLOB / BLOB / BBIND | BYTE-TEXT fetch / data frame / bind payload |
| 43 | HOLD | hold cursor |
| 49 | WANTDONE | request DONE after prepare |
| 53 | VERSION | version exchange |
| 55 | COST | ← optimizer estimate |
| 56 | EXIT | orderly disconnect; PAM abort |
| 94 | INSERTDONE | ← insert-cursor ack |
| 97 | LODATA | smart-blob data pipe |
| 99 | XACTSTAT | ← transaction status |
| 100 | RETTYPE | client receive types |
| 101/102/103 | GETROUTINE / EXFPROUTINE / FPROUTINE | FastPath resolve / call / ← result |
| 104 | FPARAM | routine descriptor: argument modes, return count (§8.2.1) |
| 126 | PROTOCOLS | capability vectors |
| 127/128/129/130 | ACCEPT / ACK / CHALLENGE / RESPONSE | PAM exchange |
| 137/138/139 | SETSVPT / RELSVPT / RBACKSVPT | savepoints |

---

## 10. How this was reverse-engineered (reusable recipe)

When the port disagreed with a live server, the winning workflow was:

1. Run the **original `ifxjdbc.jar`** against the live server as the reference client.
2. Capture the exchange server-side with **tcpdump**; reassemble the TCP streams
   (segments are *not* message-aligned) with a small script.
3. Dump this driver's bytes for the same operation via **`IFX_WIRE_TRACE`**.
4. **Diff the two hex streams**, anchored on unambiguous byte patterns (packed-decimal
   digits are ideal), and fix the encoder until they match.

Every trap in this document was found that way — cheaper than guessing from the
decompiled source, whose bugs (§5) and omissions (the SQ_LODATA buffer-size word) are
themselves part of the record.
