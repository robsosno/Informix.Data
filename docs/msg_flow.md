# Informix.Data — SQLI message flows per execution API

How each execution API of the driver travels on the wire, compared: a plain
`IfxCommand` (two inserts, two queries, `EXECUTE PROCEDURE`,
`ExecuteScalar`), `IfxRowBatch`, `IfxBatch`, `IfxBulkLoader`, plus what the
`Optofc` switch and the prepared-statement cache change. Companion to
[wire-protocol.md](wire-protocol.md) (framing, encodings, traps) — this file
is only about *which messages, in which round trips*.

Source references name the method rather than a line number, which rots: the
flows live in [SqliSession.cs](../Informix.Data/Sqli/SqliSession.cs),
[IfxCommand.cs](../Informix.Data/Ado/IfxCommand.cs),
[IfxRowBatch.cs](../Informix.Data/Ado/IfxRowBatch.cs),
[IfxBatch.cs](../Informix.Data/Ado/IfxBatch.cs) and
[IfxBulkLoader.cs](../Informix.Data/Ado/IfxBulkLoader.cs).

Notation: each `→` block is one client PDU (always terminated by `SQ_EOT`),
each `←` block is the server reply PDU; one `→`/`←` pair = one round trip
(RT). In the diagrams, solid arrows are client PDUs, dashed arrows are server
replies.

## 1. Plain: two inserts (`IfxCommand.ExecuteNonQuery` ×2)

**Parameterless, cache off** — the one-round-trip `SQ_COMMAND` path
([SqliSession.ExecuteImmediate](../Informix.Data/Sqli/SqliSession.cs)): prepare/execute/release are fused
into one opcode sequence, there is no statement id to reuse. **2 RTs total.**

```
→ SQ_COMMAND(0, "insert into t values(1)") + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE + SQ_EOT
← SQ_DESCRIBE + SQ_DONE [+ SQ_COST] + SQ_EOT
→ SQ_COMMAND(0, "insert into t values(2)") + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE + SQ_EOT
← SQ_DESCRIBE + SQ_DONE + SQ_EOT
```

**Parameterized (`insert into t values(?)`), cache off** — 3 RTs per insert,
6 total:

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — prepare
    C->>S: SQ_PREPARE(text, nParams) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns) + SQ_DONE
    Note over C,S: RT 2 — execute
    C->>S: SQ_ID(stmtId) + SQ_BIND(values) + SQ_EXECUTE
    S-->>C: SQ_DONE(1 row affected)
    Note over C,S: RT 3 — release
    C->>S: SQ_ID(stmtId) + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
    Note over C,S: insert no. 2 — the same three round trips again (6 total)
```

**Same, cache on** — insert 1: prepare (miss) + execute = 2 RTs,
return-to-cache is free; insert 2: cache hit (`Activate`, zero wire) +
execute = 1 RT. **3 RTs total.**

## 2. Plain: two queries (`ExecuteReader` ×2)

Per query (cache off, fixed-width row):

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — prepare
    C->>S: SQ_PREPARE(select …) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)
    Note over C,S: RT 2 — open + first buffer
    C->>S: SQ_ID + SQ_CURNAME + [SQ_BIND] + SQ_OPEN + SQ_NFETCH(bufSize)
    S-->>C: SQ_TUPLE… + SQ_CLOSE(auto at end of rows) + SQ_DONE
    opt rows exceed one buffer
        C->>S: SQ_NFETCH(bufSize)
        S-->>C: SQ_TUPLE… (next buffer)
    end
    Note over C,S: RT 3 — release
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
    Note over C,S: query no. 2 — the same round trips again
```

For **fixed-width rows** the first fetch piggybacks in the same PDU as
`SQ_OPEN` — one RT opens the cursor *and* delivers the first tuple buffer.
When the result fits one buffer the server appends `SQ_CLOSE` (auto-close at
end of rows, handled in `SqliSession.ReceiveMessage`'s `SqliMessage.Close` case,
[SqliSession.cs](../Informix.Data/Sqli/SqliSession.cs)), so the explicit
close costs nothing. Best case per query: prepare + open/fetch + release =
3 RTs → **6 RTs for two queries**; with cache on and warm: **1 RT per
query**.

**Variable-length rows** (any walked row — VARCHAR, DECIMAL, DATETIME…) are
where OFC matters — see §6.

## 3. `IfxRowBatch` — one statement, N value rows

`Execute()` → `SqliSession.ExecuteBatch`
([SqliSession.ExecuteBatch](../Informix.Data/Sqli/SqliSession.cs)). `SQ_ID` is sent **once**,
then bind+execute repeats per row, all in one PDU:

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — prepare (0 RTs on a cache hit)
    C->>S: SQ_PREPARE(text, nParams) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)
    Note over C,S: RT 2 — one PDU, single flush
    C->>S: SQ_ID(stmtId)<br/>+ SQ_BIND(row 1) + SQ_EXECUTE<br/>+ SQ_BIND(row 2) + SQ_EXECUTE<br/>+ …<br/>+ SQ_BIND(row N) + SQ_EXECUTE
    S-->>C: SQ_DONE(row 1) + … + SQ_DONE(row N) — per-row affected counts
    Note over C,S: near 32 KB the PDU is flushed, replies consumed,<br/>and the chain restarts with a fresh SQ_ID
    Note over C,S: RT 3 — release (free return to cache when caching is on)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

So 1000 small inserts ≈ prepare + a handful of 32 KB round trips, instead of
1000+.

## 4. `IfxBatch` (DbBatch) — heterogeneous statements

`ExecuteNonQuery` pipelines via `ExecuteChain`
([SqliSession.ExecuteChain](../Informix.Data/Sqli/SqliSession.cs)). Phase 1: each **distinct**
parameterized statement not found in the cache costs its own prepare RT
(deliberately not chained — the driver must see `DescribesResultSet` before
deciding it is safe to run). Phase 2: everything executes behind **one
flush**, mixing two segment shapes — parameterless commands ride as inline
`SQ_COMMAND` segments, parameterized ones as `SQ_ID + SQ_BIND + SQ_EXECUTE`
against their prepared ids:

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    loop per cold (uncached) parameterized statement
        C->>S: SQ_PREPARE + SQ_NDESCRIBE + SQ_WANTDONE
        S-->>C: SQ_DESCRIBE — result-set check
    end
    Note over C,S: one PDU, single flush (~32 KB guard)
    C->>S: SQ_ID(A) + SQ_BIND + SQ_EXECUTE<br/>SQ_COMMAND(text) + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE<br/>SQ_ID(B) + SQ_BIND + SQ_EXECUTE<br/>SQ_ID(A) + SQ_BIND + SQ_EXECUTE
    S-->>C: SQ_DONE + (SQ_DESCRIBE + SQ_DONE) + SQ_DONE + SQ_DONE — in send order
    Note over C,S: release per distinct statement (free return to cache when caching is on)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

On the first error the server aborts the rest of the PDU fail-fast — nothing
further arrives, and unsent segments are never sent. With a warm cache, a
batch of N different DML statements = **1 RT total**. Note `ExecuteReader` on
IfxBatch is *not* pipelined — one open per command as `NextResult` advances.

## 5. `IfxBulkLoader` — native insert cursor

Buffered mode + all-fixed-width INSERT selects the `SQ_PUT` engine; otherwise
it falls back to the §3 bind/execute chain. The native flow:

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — prepare
    C->>S: SQ_PREPARE(insert …) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)
    Note over C,S: RT 2 — open insert cursor
    C->>S: SQ_ID + SQ_CURNAME + [SQ_HOLD] + SQ_OPEN
    S-->>C: SQ_EOT (cursor open)
    Note over C: Put() ×N — rows buffer client-side, no wire traffic
    loop per flush (insert buffer full, or explicit Flush)
        C->>S: SQ_PUT [rowSize:int32][nrows:int16] + raw tuple images
        S-->>C: SQ_INSERTDONE(count) — SQ_PUTERR(row) on failure
    end
    Note over C,S: close + release
    C->>S: SQ_ID + SQ_CLOSE
    S-->>C: SQ_EOT
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: SQ_EOT
```

Rows travel as **raw fixed-width tuple images with no per-row type words** —
denser than `SQ_BIND`, which re-sends type/indicator/precision for every
value of every row. A flush PDU fills up to the insert buffer
(`FET_BUF_SIZE`, else ~32 KB; `nrows` caps at 32 767). Two live-verified
traps: a **non-hold** open on a logged database needs an active transaction
(-255), and commit/rollback closes a non-HOLD cursor server-side, so the
loader flushes (commit) or drops (rollback) buffered rows first.

## 6. OFC 0 vs 1 (`Optofc`, default 0)

Only affects **opening a forward-only cursor over a prepared SELECT or EXECUTE
PROCEDURE with a variable-length row** (fixed-width rows already piggyback the
fetch either way; scroll cursors never qualify — `OpenScrollCursor` doesn't
gate on `Optofc` at all). Port of JDBC `IfxSqliConnect.optofc`
([SqliSession.OpenCursor](../Informix.Data/Sqli/SqliSession.cs)), extended to EXECUTE PROCEDURE
because IDS accepts the merged PDU for it verbatim (live-verified 2026-08-03).

`Optofc = false` — two round trips to the first row:

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — open
    C->>S: SQ_ID + SQ_CURNAME + [SQ_BIND] + SQ_OPEN
    S-->>C: open reply (no rows yet)
    Note over C,S: RT 2 — announce receive types, fetch
    C->>S: SQ_RETTYPE(client receive types) + SQ_NFETCH
    S-->>C: SQ_TUPLE… (first buffer)
```

`Optofc = true` — one round trip; the `SQ_RETTYPE + SQ_NFETCH` pair rides in
the same PDU as `SQ_OPEN` and the first tuple buffer comes back with the open
reply:

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — open + fetch piggybacked
    C->>S: SQ_ID + SQ_CURNAME + [SQ_BIND] + SQ_OPEN + SQ_RETTYPE + SQ_NFETCH
    S-->>C: open reply + SQ_TUPLE… (first buffer)
```

Measured on the GetParameters baseline (2026-07-25): leaving OFC off costs
one round trip per variable-length query open.

## 7. Statement cache on vs off (`PreparedStatementCacheSize`)

The cache ([SqliStatementCache.cs](../Informix.Data/Sqli/SqliStatementCache.cs)) changes
which of the three phases touch the wire:

| Phase | Cache off | Cache on |
|---|---|---|
| Prepare | `SQ_PREPARE + SQ_NDESCRIBE + SQ_WANTDONE` RT, every time | Hit: `Activate` — **zero wire**. Miss: normal prepare RT |
| Execute | `SQ_ID + [SQ_BIND] + SQ_EXECUTE` RT | identical |
| Release | `SQ_ID + SQ_RELEASE` RT | Return-to-cache — **zero wire** (LRU evictee gets a real release) |

```mermaid
flowchart LR
    P[PrepareCached] -->|hit| H[Activate — zero wire]
    P -->|miss or cache off| M[SQ_PREPARE round trip]
    R[ReleaseOrCache] -->|cache off, or -710 stale plan| W[SQ_ID + SQ_RELEASE round trip]
    R -->|cache on| K[return to cache — zero wire]
    K -->|cache at capacity| E[LRU evictee gets a real SQ_RELEASE]
```

So a repeated parameterized statement drops from 3 RTs to 1. Semantics worth
knowing: the key is (SQL text, scroll, hold); **checkout removes** the entry,
so concurrent identical SQL just prepares a second copy instead of racing; a
-710 stale-plan failure releases the handle for real instead of returning the
poisoned plan to the cache. One subtle flip: with the cache on, even
*parameterless* `IfxCommand` statements take the prepared path (the fused
`SQ_COMMAND` sequence has no statement id to cache) — a one-shot
parameterless statement costs 2 RTs instead of 1, but every repeat is 1 RT
with no server-side re-parse. The cache is also what makes IfxBatch's
pipelining pay off: with it off, every batch run re-pays one prepare RT per
distinct parameterized statement before the single execute PDU.

## 8. `EXECUTE PROCEDURE` — parameterized, single-row result with VARCHAR columns

Same code path as §2 (`ExecuteReaderCore` → `OpenCursor`): `StatementType == ExecProc`
with output columns satisfies `DescribesResultSet` exactly like a SELECT. `Optofc`
covers `Select` **or** `ExecProc`
(`bool optofc = Optofc && StatementType is Select or ExecProc`,
[SqliSession.OpenCursor](../Informix.Data/Sqli/SqliSession.cs)) — extended beyond the
reference JDBC driver's `Select`-only gate because IDS accepts the merged PDU for
EXECUTE PROCEDURE verbatim (§6). `SQ_HOLD` is never sent for an `ExecProc` cursor
(`_cursorIsHold = hold && StatementType != ExecProc`), whatever the caller asked for.

A VARCHAR output column sets `_rowNeedsWalk`; with `Optofc` off the
`if (_rowNeedsWalk && !optofc)` guard forces the open reply to be consumed on its own
before `SQ_RETTYPE + SQ_NFETCH` can be sent. With `Optofc` on the guard never fires, so
the pair rides in the same PDU as `SQ_OPEN`, exactly like §6's variable-length SELECT.

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — prepare (skipped on a cache hit)
    C->>S: SQ_PREPARE(text, nParams) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, ExecProc, output columns incl. VARCHAR)
    alt Optofc = false (default) — 2 RTs to the one row
        Note over C,S: RT 2 — open (no SQ_HOLD — never sent for ExecProc)
        C->>S: SQ_ID + SQ_CURNAME + SQ_BIND(params) + SQ_OPEN
        S-->>C: open reply (no rows yet)
        Note over C,S: RT 3 — typed fetch of the one row
        C->>S: SQ_ID + SQ_RETTYPE(client receive types) + SQ_NFETCH
        S-->>C: SQ_TUPLE(the row) + SQ_CLOSE (auto, single row) + SQ_DONE
    else Optofc = true — 1 RT to the one row
        Note over C,S: RT 2 — open + typed fetch piggybacked
        C->>S: SQ_ID + SQ_CURNAME + SQ_BIND(params) + SQ_OPEN + SQ_RETTYPE + SQ_NFETCH
        S-->>C: open reply + SQ_TUPLE(the row) + SQ_CLOSE (auto, single row) + SQ_DONE
    end
    Note over C,S: RT 3/4 — release (free when cache on)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

**4 RTs cold with `Optofc` off** (prepare, open, typed fetch, release); **3 RTs cold with
`Optofc` on** (prepare, open+fetch fused, release) — the same floor as the equivalent
SELECT in §6. With the prepared-statement cache warm (§7) prepare and release both go
free, leaving **2 RTs per call off / 1 RT per call on**, matching the fixed-width-column
floor without a schema change.

## 9. `ExecuteScalar()` — first column of the first row

`IfxCommand.ExecuteScalar` has no wire opcode of its own: it opens an ordinary reader and
takes `GetValue(0)` of the first row (`null` when there is no row or no column), so it
rides the §2 cursor-open path exactly. The only scalar thing about it is that the query
answers one row — `NextRow()` never has to fetch again, and the server's auto-close
piggybacks on the reply that carries the row.

```mermaid
sequenceDiagram
    participant C as Client (Informix.Data)
    participant S as IDS server
    Note over C,S: RT 1 — prepare (skipped on a cache hit)
    C->>S: SQ_PREPARE(select …) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)
    alt fixed-width scalar column (COUNT(*), MAX(int_col))
        Note over C,S: RT 2 — open + fetch fused (piggyback, as in §2)
        C->>S: SQ_ID + SQ_CURNAME + [SQ_BIND] + SQ_OPEN + SQ_NFETCH
        S-->>C: SQ_TUPLE(the one row) + SQ_CLOSE (auto) + SQ_DONE
    else variable-length scalar column (MAX(varchar_col)), Optofc off (default)
        Note over C,S: RT 2 — open, consumed alone
        C->>S: SQ_ID + SQ_CURNAME + [SQ_BIND] + SQ_OPEN
        S-->>C: open reply (no rows yet)
        Note over C,S: RT 3 — typed fetch
        C->>S: SQ_RETTYPE(client receive types) + SQ_NFETCH
        S-->>C: SQ_TUPLE(the one row) + SQ_CLOSE (auto) + SQ_DONE
    end
    Note over C,S: RT n — release (free when cache on)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

**3 RTs cold** for a fixed-width scalar (prepare, open/fetch, release) — **1 RT** once the
statement is cached and warm. A variable-length scalar costs **4 RTs cold / 2 RTs cached**
with `Optofc` off; turning `Optofc` on fuses RT 2/3 back into one PDU and restores the
fixed-width floor (**3 RTs cold / 1 RT cached**).

## 10. How the options compose

`Optofc` saves one round trip per variable-length query open. The cache saves the prepare
and release round trips around it. The three batch APIs collapse the execute phase itself
— `IfxRowBatch` for one statement over many rows, `IfxBatch` for many statements behind
one flush, `IfxBulkLoader` for the densest raw-tuple stream.

Best case for a repeated variable-length SELECT with both switches on is a **single round
trip**: open plus first buffer, with prepare and release both free.
