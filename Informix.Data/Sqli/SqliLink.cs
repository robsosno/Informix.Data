using System.Threading;
using Informix.Data.Asf;

namespace Informix.Data.Sqli;

/// <summary>
/// A minimal, net8.0-compatible stand-in for <c>System.Threading.Lock</c> (.NET 9+
/// only — this project targets net8.0). Built on <see cref="Monitor"/>, which has always been
/// reentrant for the owning thread, matching the semantics <see cref="SqliLink.Gate"/> needs
/// (verified experimentally for the real <c>System.Threading.Lock</c> during design — the
/// <c>Monitor</c> primitive it will eventually be swappable for behaves identically here).
/// Exposes just enough of <c>Lock</c>'s shape (<see cref="EnterScope"/> returning a disposable
/// scope) that call sites read exactly as they would against the real type, so upgrading this
/// project to net9.0+ later is a one-line swap in this file alone.
/// </summary>
internal sealed class SqliGate
{
    private readonly object _sync = new();
    private int _depth; // reentrant entries currently held; only touched while owning _sync

    /// <summary>True while some thread is inside a wire exchange (holds the gate). Readable
    /// from any thread without taking it — that is the point: a canceller must not block on
    /// the exchange it wants to interrupt.</summary>
    public bool IsHeld => Volatile.Read(ref _depth) > 0;

    public Scope EnterScope()
    {
        Monitor.Enter(_sync);
        Volatile.Write(ref _depth, _depth + 1);
        return new Scope(this);
    }

    internal readonly struct Scope(SqliGate gate) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref gate._depth, gate._depth - 1);
            Monitor.Exit(gate._sync);
        }
    }
}

/// <summary>
/// The state one physical connection's <see cref="SqliSession"/>s share — port of the slice
/// of <c>IfxSqliConnect</c> that <c>IfxSqli</c> reads through its <c>conn</c> back-reference
/// (<c>this.conn.optofc</c>, <c>this.conn.fetchBufSize</c>, <c>this.conn.lockSession()</c>,
/// <c>this.conn.getGeneratedCursor()</c>, the connection's <c>implicitCache</c>/FastPath handle
/// cache, …) rather than owning locally. The real driver mints a fresh <c>IfxSqli</c> per
/// statement (<c>IfxSqliConnect.createProto()</c> and every one of them
/// shares this connection-wide state.
///
/// <para>Everything here is either read-only handshake state, a connection-wide tuning knob,
/// a resource cache keyed by something the server tracks per-connection (prepared statements,
/// FastPath routine handles), or bookkeeping for coordinating multiple sessions on the same
/// socket (the cursor-name sequence, the session registry). Per-statement state — the tuple
/// buffer, the current statement id, cursor-open flags — stays on <see cref="SqliSession"/>.</para>
/// </summary>
internal sealed class SqliLink(AsfConnection asf)
{
    public AsfConnection Asf { get; } = asf;

    /// <summary>
    /// Serializes every wire exchange (and every shared-cache mutation) across every session on
    /// this link. <see cref="SqliGate"/>
    /// is the net8.0-compatible equivalent: reentrant on the same thread (verified experimentally
    /// against the real <c>System.Threading.Lock</c> during design, before discovering this
    /// project's net8.0 target doesn't have it — a thread already holding it can re-enter, e.g.
    /// when one wrapped <see cref="SqliSession"/> method calls another), and blocks a different
    /// thread until the holder's <c>using</c> scope ends.
    ///
    /// <para>Each <see cref="SqliSession"/> method that touches the wire acquires this for its
    /// whole body — composing the request, flushing it, and draining the reply to SQ_EOT — never
    /// just around the socket call, since <c>_out</c>/<c>_in</c> are shared: two sessions
    /// composing their own PDUs at the same time would interleave bytes on the wire. It is never
    /// held while control returns to the caller (no session sits on this lock while an open
    /// reader is idle in application code), so contention is bounded by how long a single PDU
    /// round trip takes — not by how long a caller holds a reader open. This can't
    /// deadlock: one lock (no ordering to get wrong), held only for one exchange at a time,
    /// released at a clean message boundary.</para>
    /// </summary>
    public SqliGate Gate { get; } = new();

    /// <summary>
    /// The user-facing cancel (<c>IfxCommand.Cancel</c>/<c>IfxBatch.Cancel</c>): sends the
    /// out-of-band interrupt only while a wire exchange is in flight (<see cref="Gate"/>
    /// held), and returns whether it did. The byte is not tied to a statement, so one sent
    /// while nothing runs would wait in the socket and interrupt whatever runs next on this
    /// connection. The check is inherently racy — the exchange can finish between it and the
    /// send — which is why a connection that was sent a byte is also marked
    /// (<see cref="AsfConnection.OutOfBandSent"/>) and never pooled again.
    /// </summary>
    public bool TryInterrupt()
    {
        if (!Gate.IsHeld)
            return false;
        Asf.SendOutOfBand();
        return true;
    }

    /// <summary>Negotiated server capabilities; populated by the primary session's <see cref="SqliSession.BringUp"/>.</summary>
    public SqliCapabilities Capabilities { get; } = new();

    /// <summary>The server's protocol-capability reply from the SQ_PROTOCOLS exchange.</summary>
    public byte[] ServerProtocol { get; set; } = [];

    /// <summary>The SQLI-level server version string (distinct from the ASF handshake version).</summary>
    public string ServerVersion { get; set; } = "";

    // ---- connection-wide tuning knobs (IfxSqliConnect.fetchBufSize / optofc / …) ----
    public int ConnectionFetchBufferSize { get; set; }
    public bool Optofc { get; set; }
    public bool TrimTrailingSpaces { get; set; }
    public bool RequestLargeRowFormats { get; set; }
    public int PreparedStatementCacheSize { get; set; }

    /// <summary>
    /// When set, every statement <see cref="SqliSession.Prepare"/> describes must be a plain
    /// SELECT (<see cref="SqliStatementType.Select"/>) or it is rejected client-side before
    /// it can run — see <see cref="SqliSession.Prepare"/>.
    /// </summary>
    public bool SelectOnly { get; set; }

    // ---- shared caches (server-side resources scoped to the connection, not a statement) ----
    private SqliStatementCache? _statementCache;

    /// <summary>The prepared-statement cache, shared by every session on this link
    /// — a checkout on one session removes the entry, so
    /// two sessions can never be handed the same cached statement handle at once.</summary>
    public SqliStatementCache? StatementCache =>
        PreparedStatementCacheSize > 0
            ? _statementCache ??= new SqliStatementCache(PreparedStatementCacheSize)
            : null;

    /// <summary>Resolved FastPath routine handles (SQ_GETROUTINE), keyed by signature and
    /// shared across every session on this connection — a routine handle is a server-side
    /// resource scoped to the connection, the same category as a prepared-statement id.</summary>
    public Dictionary<string, (int Handle, string Db)> FpHandleCache { get; } = new(StringComparer.Ordinal);

    // ---- generated cursor names (IfxSqliConnect.nextID / getGeneratedCursor) ----
    // Must be link-wide: two sessions each counting from zero would mint colliding
    // cursor names (e.g. both "ifxnet_c1") the instant two cursors are open at once.
    private int _cursorSequence;
    // Source of unique cursor names on this connection.
    public int NextCursorSequence() => ++_cursorSequence;

    // ---- measurement (IfxCommand/tests read this across the whole connection) ----
    public long RequestRoundTrips { get; private set; }
    public void IncrementRoundTrips() => RequestRoundTrips++;
    public void ResetRoundTrips() => RequestRoundTrips = 0;

    // ---- session registry ----
    // Lets a link-wide operation (commit/rollback ending non-hold cursors — see Phase 2)
    // enumerate every session currently open on this connection. Plain list with explicit
    // register/unregister rather than weak references: sessions are connection-lifetime,
    // few in number, and explicitly disposed by their owner, so there is no leak/GC-timing
    // concern that would justify WeakReference's extra indirection.
    private readonly List<SqliSession> _sessions = [];

    public void Register(SqliSession session)
    {
        lock (_sessions) _sessions.Add(session);
    }

    public void Unregister(SqliSession session)
    {
        lock (_sessions) _sessions.Remove(session);
    }

    /// <summary>A snapshot of every <see cref="SqliSession"/> currently registered on this link.</summary>
    public IReadOnlyList<SqliSession> Sessions
    {
        get { lock (_sessions) return [.. _sessions]; }
    }

    /// <summary>
    /// True when every session on this connection is idle (<see cref="SqliSession.IsIdle"/>) —
    /// the link-wide aggregate pooling needs (a single session's own <c>IsIdle</c> only speaks
    /// for itself, and with MARS a connection can have more than one). A physical connection is
    /// safe to return to the pool only when this is true; an abandoned reader on any sibling
    /// session makes the whole connection unsafe to reuse.
    /// </summary>
    public bool IsIdle
    {
        get
        {
            foreach (var session in Sessions)
                if (!session.IsIdle)
                    return false;
            return true;
        }
    }
}
