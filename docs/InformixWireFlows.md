# Informix.Data — package flow on the wire

How each execution API maps onto SQLI messages, and what the two tuning switches
(`OPTOFC`, the prepared-statement cache) change about that flow.

Notation: every client block below is one PDU, terminated by `SQ_EOT`; the server's
answer is likewise one PDU. One send + one reply = one **round trip (RT)**, which is
the unit that costs latency. Sources: `Informix.Data/Sqli/SqliSession.cs`,
`Informix.Data/IfxBatch.cs`, `Informix.Data/IfxRowBatch.cs`,
`Informix.Data/IfxBulkLoader.cs`, `Informix.Data/docs/wire-protocol.md`.

---

## 1. Plain — two inserts

### Parameterized (`insert into t values(?)`), cache off

Three round trips per insert, six in total.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: insert 1 — RT 1, prepare
    C->>S: SQ_PREPARE(text, nParams) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns) + SQ_DONE

    Note over C,S: insert 1 — RT 2, execute
    C->>S: SQ_ID(stmtId) + SQ_BIND(values) + SQ_EXECUTE
    S-->>C: SQ_DONE(1 row affected)

    Note over C,S: insert 1 — RT 3, release
    C->>S: SQ_ID(stmtId) + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)

    Note over C,S: insert 2 — the same three round trips again
```

With the cache **on**: insert 1 pays prepare (miss) + execute = 2 RTs and the release
is free; insert 2 is a cache hit (`Activate`, zero wire) + execute = 1 RT. **3 RTs total.**

### Parameterless, cache off, no `Prepare()`

The fused one-round-trip path (`IfxCommand.ExecuteNonQuery` →
`SqliSession.ExecuteImmediate`) — prepare, execute and release are a single opcode
sequence with no statement id to reuse. **2 RTs for two inserts.**

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: insert 1 — RT 1
    C->>S: SQ_COMMAND(0, "insert into t values(1)") + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE
    S-->>C: SQ_DESCRIBE + SQ_DONE + optional SQ_COST

    Note over C,S: insert 2 — RT 2
    C->>S: SQ_COMMAND(0, "insert into t values(2)") + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE
    S-->>C: SQ_DESCRIBE + SQ_DONE
```

---

## 2. Plain — two queries

Per query, cache off, fixed-width row. The first fetch **piggybacks the same PDU as
`SQ_OPEN`**, so one round trip opens the cursor and delivers the first tuple buffer.
When the result fits that buffer the server appends `SQ_CLOSE` (auto-close at end of
rows), so the explicit close costs nothing.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: RT 1 — prepare
    C->>S: SQ_PREPARE(select ...) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)

    Note over C,S: RT 2 — open + first fetch in one PDU
    C->>S: SQ_ID + SQ_CURNAME + optional SQ_BIND + SQ_OPEN + SQ_NFETCH(bufSize)
    S-->>C: SQ_TUPLE... + SQ_CLOSE (auto) + SQ_DONE

    opt rows exceed one buffer
        C->>S: SQ_NFETCH(bufSize)
        S-->>C: SQ_TUPLE... (next buffer)
    end

    Note over C,S: RT 3 — release (close skipped, server auto-closed)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)

    Note over C,S: query 2 — the same round trips again
```

Best case per query: prepare + open/fetch + release = 3 RTs, so **6 RTs for two
queries**; with the cache on and warm, **1 RT per query**. Variable-length rows are
where `OPTOFC` starts to matter — see §6.

---

## 3. `IfxRowBatch` — one statement, N value rows

`Execute()` → `SqliSession.ExecuteBatch`. `SQ_ID` is sent **once**, then bind+execute
repeats per row inside the same PDU. When the outgoing PDU approaches ~32 KB it is
flushed, the replies consumed, and the chain restarts with a fresh `SQ_ID`.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: RT 1 — prepare (skipped on a cache hit)
    C->>S: SQ_PREPARE(text, nParams) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)

    Note over C,S: RT 2 — every row in one PDU
    Note over C,S: SQ_ID(stmtId)<br/>+ SQ_BIND(row 1) + SQ_EXECUTE<br/>+ SQ_BIND(row 2) + SQ_EXECUTE<br/>+ ...<br/>+ SQ_BIND(row N) + SQ_EXECUTE
    C->>S: single flush — all of the above
    S-->>C: SQ_DONE(row 1) ... SQ_DONE(row N) — per-row affected counts

    Note over C,S: RT 3 — release (free when returned to the cache)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

So 1000 small inserts cost a prepare plus a handful of 32 KB round trips instead of
1000+.

---

## 4. `IfxBatch` (`DbBatch`) — heterogeneous statements

`ExecuteNonQuery` pipelines through `SqliSession.ExecuteChain`. Phase 1: each
**distinct** parameterized statement not already in the cache costs its own prepare RT
— deliberately not chained, because the driver must inspect `DescribesResultSet`
before it can decide the statement is safe to run. Phase 2: everything executes behind
**one flush**, mixing two segment shapes in a single PDU.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: RT 1..k — one per cold (cache-miss) statement
    C->>S: SQ_PREPARE + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE — checked for DescribesResultSet

    Note over C,S: RT k+1 — the whole batch in one PDU
    Note over C,S: SQ_ID(stmt A) + SQ_BIND + SQ_EXECUTE<br/>+ SQ_COMMAND("update ...") + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE<br/>+ SQ_ID(stmt B) + SQ_BIND + SQ_EXECUTE<br/>+ SQ_ID(stmt A) + SQ_BIND + SQ_EXECUTE
    C->>S: single flush — all of the above
    S-->>C: SQ_DONE / SQ_DESCRIBE + SQ_DONE per segment, in send order

    Note over C,S: RT n — release each distinct statement (free with the cache on)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

Parameterless commands ride as inline `SQ_COMMAND` segments, parameterized ones as
`SQ_ID + SQ_BIND + SQ_EXECUTE` against their prepared ids. On the first error the
server aborts the rest of the PDU fail-fast — nothing further arrives, and unsent
segments are never sent. With a warm cache, a batch of N different DML statements is
**1 RT total**.

`IfxBatch.ExecuteReader` is *not* pipelined: one open per command as `NextResult`
advances, forced by the single-open-cursor-per-session rule.

---

## 5. `IfxBulkLoader` — native insert cursor

Buffered mode plus an all-fixed-width INSERT selects the `SQ_PUT` engine; anything
else (a variable-length column, an EXECUTE PROCEDURE, `PlainInserts` mode) falls back
to the §3 bind/execute chain.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: RT 1 — prepare
    C->>S: SQ_PREPARE("insert into t values(?,?)") + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)

    Note over C,S: RT 2 — open the insert cursor
    C->>S: SQ_ID + SQ_CURNAME + optional SQ_HOLD + SQ_OPEN
    S-->>C: SQ_EOT (cursor open)

    Note over C,S: Put() x N — buffered client-side, no wire traffic

    loop once per flush (insert buffer full, or explicit Flush)
        C->>S: SQ_PUT [rowSize:int32][nrows:int16] + raw tuple images
        S-->>C: SQ_INSERTDONE(count) — or SQ_PUTERR(row) on failure
    end

    Note over C,S: RT n-1 — close
    C->>S: SQ_ID + SQ_CLOSE
    S-->>C: SQ_EOT

    Note over C,S: RT n — release
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: SQ_EOT
```

Rows travel as **raw fixed-width tuple images with no per-row type words** — denser
than `SQ_BIND`, which re-sends type, indicator and precision for every value of every
row. A flush PDU fills up to the insert buffer (`FET_BUF_SIZE`, else ~32 KB), and
`nrows` caps at 32 767. Two live-verified traps: a **non-hold** open on a logged
database needs an active transaction (-255), and commit/rollback closes a non-HOLD
cursor server-side — so the loader flushes (commit) or drops (rollback) buffered rows
first.

---

## 6. `OPTOFC` 0 vs 1

Off by default (`Optofc`). It only affects opening a **forward-only cursor over a
prepared SELECT or EXECUTE PROCEDURE with a variable-length row** — fixed-width rows
already piggyback the fetch either way, and scroll cursors never qualify. Port of the
JDBC `IfxSqliConnect.optofc` branch, extended to EXECUTE PROCEDURE because IDS accepts
the merged PDU for it verbatim.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    alt Optofc = false (default) — 2 RTs to the first row
        C->>S: SQ_ID + SQ_CURNAME + optional SQ_BIND + SQ_OPEN
        S-->>C: open reply (no rows yet)
        C->>S: SQ_RETTYPE(client receive types) + SQ_NFETCH
        S-->>C: SQ_TUPLE... (first buffer)
    else Optofc = true — 1 RT
        C->>S: SQ_ID + SQ_CURNAME + optional SQ_BIND + SQ_OPEN + SQ_RETTYPE + SQ_NFETCH
        S-->>C: open reply + SQ_TUPLE... (first buffer)
    end
```

With OFC on, the `SQ_RETTYPE + SQ_NFETCH` pair rides in the same PDU as `SQ_OPEN`, so
the first tuple buffer comes back with the open reply — one round trip saved per
query. The `GetParameters` perf baseline measured exactly this cost.

---

## 7. Prepared-statement cache on vs off

`PreparedStatementCacheSize` changes which of the three phases touch the wire:

| Phase | Cache off | Cache on |
|---|---|---|
| Prepare | `SQ_PREPARE + SQ_NDESCRIBE + SQ_WANTDONE` — 1 RT, every time | Hit: `Activate`, **zero wire**. Miss: normal prepare RT |
| Execute | `SQ_ID + [SQ_BIND] + SQ_EXECUTE` — 1 RT | Identical — 1 RT |
| Release | `SQ_ID + SQ_RELEASE` — 1 RT | Return to cache, **zero wire** (LRU evictee gets a real release) |

A repeated parameterized statement therefore drops from **3 RTs to 1**.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    alt cache off, or cache miss
        C->>S: SQ_PREPARE + SQ_NDESCRIBE + SQ_WANTDONE
        S-->>C: SQ_DESCRIBE(stmtId, columns)
    else cache hit
        Note over C,S: Activate(handle) — reinstates stmtId, columns,<br/>tuple size and row layout. No wire traffic.
    end

    C->>S: SQ_ID(stmtId) + optional SQ_BIND + SQ_EXECUTE
    S-->>C: SQ_DONE

    alt cache off
        C->>S: SQ_ID + SQ_RELEASE
        S-->>C: (empty reply — just SQ_EOT)
    else cache on
        Note over C,S: Return to cache — no wire traffic.<br/>Only an LRU evictee, or a -710 stale plan, is really released.
    end
```

Semantics the diagram cannot show:

- The cache key is (SQL text, scroll, hold) — the same text opened as a scroll or hold
  cursor needs its own prepared statement.
- **Checkout removes** the entry, so concurrent identical SQL never races over one
  handle: the second user simply prepares its own copy.
- A **-710 stale-plan** failure releases the handle for real instead of returning the
  poisoned plan to the cache.
- One behavioral flip: with the cache on, even *parameterless* `IfxCommand` statements
  abandon the fused 1-RT `SQ_COMMAND` path (it has no statement id to cache). A
  one-shot statement costs 2 RTs, but every repeat is 1 RT with no server-side
  re-parse.

---

## 8. `EXECUTE PROCEDURE` — parameterized, single-row result with VARCHAR columns

Same code path as §2 (`ExecuteReaderCore` → `OpenCursor`, since `StatementType ==
ExecProc` with output columns satisfies `DescribesResultSet` exactly like a SELECT).
`OPTOFC` now covers `StatementType == Select` **or** `ExecProc` —
`bool optofc = Optofc && StatementType is Select or ExecProc` — extended beyond the
reference JDBC driver's `Select`-only gate because IDS 9.61 accepts the merged PDU for
EXECUTE PROCEDURE verbatim (§6). A VARCHAR output column
also sets `_rowNeedsWalk = true`; with `Optofc` off that forces the open reply to be
consumed on its own before `SQ_RETTYPE + SQ_NFETCH` can be sent, but with `Optofc` on
the `if (_rowNeedsWalk && !optofc)` guard never fires, so the pair rides in the same
PDU as `SQ_OPEN` exactly like §6's variable-length SELECT case.

```mermaid
sequenceDiagram
    participant C as Client
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

**4 RTs cold with `Optofc` off** (prepare, open, typed-fetch, release); **3 RTs cold
with `Optofc` on** (prepare, open+fetch fused, release) — the same floor as the
equivalent SELECT in §6. With the prepared-statement cache on and warm (§7), prepare
and release both go free, leaving **2 RTs per call off / 1 RT per call on** —
matching the fixed-width-column floor without needing a schema change.

Cache the statement (§7) on top of this for the biggest win: it removes 2 of the 4
RTs on every repeat call regardless of `Optofc` or the procedure's column types.

---

## 9. `ExecuteScalar()` — first column of the first row

`IfxCommand.ExecuteScalar` has no dedicated wire opcode: it's `using var reader =
ExecuteReader(); return reader.Read() ? reader.GetValue(0) : null`, so it rides the
exact §2 cursor-open path. The only thing scalar about it is that the query happens to
answer one row — which means `NextRow()` never has to fetch again (the row `OpenCursor`
already buffered is the only one), and the server's auto-close piggybacks on the same
reply that carries that row.

```mermaid
sequenceDiagram
    participant C as Client
    participant S as IDS server

    Note over C,S: RT 1 — prepare (skipped on a cache hit)
    C->>S: SQ_PREPARE(select ...) + SQ_NDESCRIBE + SQ_WANTDONE
    S-->>C: SQ_DESCRIBE(stmtId, columns)

    alt fixed-width scalar column (e.g. COUNT(*), MAX(int_col))
        Note over C,S: RT 2 — open + fetch fused (piggyback, as in §2)
        C->>S: SQ_ID + SQ_CURNAME + optional SQ_BIND + SQ_OPEN + SQ_NFETCH
        S-->>C: SQ_TUPLE(the one row) + SQ_CLOSE (auto) + SQ_DONE
    else variable-length scalar column (e.g. MAX(varchar_col)), Optofc off (default)
        Note over C,S: RT 2 — open, consumed alone
        C->>S: SQ_ID + SQ_CURNAME + optional SQ_BIND + SQ_OPEN
        S-->>C: open reply (no rows yet)
        Note over C,S: RT 3 — typed fetch
        C->>S: SQ_RETTYPE(client receive types) + SQ_NFETCH
        S-->>C: SQ_TUPLE(the one row) + SQ_CLOSE (auto) + SQ_DONE
    end

    Note over C,S: RT n — release (free when cache on)
    C->>S: SQ_ID + SQ_RELEASE
    S-->>C: (empty reply — just SQ_EOT)
```

**3 RTs cold** for a fixed-width scalar (prepare, open/fetch, release) — **1 RT** once
the statement is cached and warm. A variable-length scalar costs **4 RTs cold / 2 RTs
cached** with `Optofc` off, the same split §6 describes for any SELECT with a
variable-length row.

`ExecuteScalar` over a genuine SELECT qualifies for `Optofc`
(`StatementType == Select` is one of the two statement types the gate accepts,
alongside `ExecProc` per §8) — so turning it on fuses RT 2/3 back into one PDU even
for a variable-length column, matching the fixed-width row: **3 RTs cold / 1 RT
cached-and-OPTOFC-on**, same floor as the fixed-width case.

---

## How the options compose

`OPTOFC` saves one round trip per variable-length query open. The cache saves the
prepare and release round trips around it. The three batch APIs collapse the execute
phase itself — `IfxRowBatch` for one statement over many rows, `IfxBatch` for many
statements behind one flush, `IfxBulkLoader` for the densest raw-tuple stream.

Best case for a repeated variable-length SELECT with both switches on is a **single
round trip**: open plus first buffer, with prepare and release both free.
