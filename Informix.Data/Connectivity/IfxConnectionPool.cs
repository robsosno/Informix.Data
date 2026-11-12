using System.Collections.Concurrent;
using Informix.Data.Asf;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// One physical Informix connection (ASF transport + SQLI session) that a pool owns and
/// hands out to successive <see cref="IfxConnection"/> facades. Pure client-side state —
/// there is no wire concept of a pooled connection; this simply keeps the live socket and
/// session alive between an <see cref="IfxConnection.Close"/> and the next matching
/// <see cref="IfxConnection.Open"/>.
/// </summary>
internal sealed class PooledSession(AsfConnection asf, SqliSession sqli, string serverVersion, int generation) : IDisposable
{
    public AsfConnection Asf { get; } = asf;
    public SqliSession Sqli { get; } = sqli;
    public string ServerVersion { get; } = serverVersion;

    /// <summary>The pool's clear-generation at creation; a later <c>ClearPool</c> bumps the
    /// pool generation so any still-checked-out connection is discarded, not reused, on return.</summary>
    public int Generation { get; } = generation;

    private readonly DateTime _createdUtc = DateTime.UtcNow;

    // Connection Lifetime in seconds; 0 means unlimited.
    public bool IsExpired(int lifetimeSeconds) =>
        lifetimeSeconds > 0 && (DateTime.UtcNow - _createdUtc).TotalSeconds >= lifetimeSeconds;

    /// <summary>Tears the physical connection down (orderly SQ_EXIT, then socket close).</summary>
    public void Dispose()
    {
        // An interrupted connection (an out-of-band cancel byte was sent) is torn down
        // without the SQ_EXIT exchange: a late interrupt could still be pending and would
        // hit the very statement the exit is waiting on.
        if (Asf.OutOfBandSent)
            Asf.DisconnectAbortive();
        else
        {
            try { Asf.DisconnectOrderly(); }
            catch (IfxException) { /* a failed orderly exit still disposes the socket */ }
            catch (IOException) { }
        }
        Asf.Dispose();
    }
}

/// <summary>
/// A client-side connection pool keyed by the exact connection string (standard ADO.NET
/// semantics). Because Informix cannot switch database in a
/// session, the string key already guarantees every physical connection in a pool targets
/// the same database — no per-checkout database match is needed.
/// </summary>
internal sealed class IfxConnectionPool
{
    /// <summary>Builds a fresh physical connection (ASF transport + brought-up SQLI session).</summary>
    public delegate (AsfConnection Asf, SqliSession Sqli, string ServerVersion) PhysicalFactory();

    // How often idle connections are checked for expiry or a dead socket.
    private const int IdlePruneIntervalMs = 30_000; // 30 seconds between prune sweeps

    private readonly PhysicalFactory _factory;
    private readonly int _minSize;
    private readonly int _lifetimeSeconds;
    // One slot per connection that may be handed out at once; Rent waits on it, Return/Discard release it.
    private readonly SemaphoreSlim _slots;             // permits == Max Pool Size (concurrent checkouts)
    private readonly ConcurrentStack<PooledSession> _idle = new();
    private readonly Microsoft.Extensions.Logging.ILogger _log;
    private readonly IfxMetricsReporter? _metrics;      // null unless owned by a data source
    private readonly Timer? _pruneTimer;               // periodic idle-connection pruner
    private int _total;                                // physical connections created but not disposed
    // Incremented by Clear(). Busy connections of an older generation are retired when they come back.
    private int _generation;                           // bumped by Clear() to retire live connections

    public IfxConnectionPool(int minSize, int maxSize, int lifetimeSeconds, PhysicalFactory factory,
        IfxLoggingConfiguration? logging = null, IfxMetricsReporter? metrics = null)
    {
        _factory = factory;
        _minSize = minSize;
        _lifetimeSeconds = lifetimeSeconds;
        _log = (logging ?? IfxLoggingConfiguration.NullConfiguration).ConnectionLogger;
        _metrics = metrics;
        _slots = new SemaphoreSlim(maxSize, maxSize);
        WarmUp();
        // Start background pruning only when connections can expire.
        if (lifetimeSeconds > 0)
            _pruneTimer = new Timer(_ => PruneIdle(), null, IdlePruneIntervalMs, IdlePruneIntervalMs);
    }

    // Test/inspection aids.
    public int IdleCount => _idle.Count;
    public int TotalCount => Volatile.Read(ref _total);

    // Min Pool Size: pre-create idle connections (best effort — a server that refuses the
    // warm-up connections just leaves the pool to fill on demand).
    // NOTE: warm connections do not consume a semaphore slot at creation time; slots are
    // acquired only when a client Rent()s a connection.
    private void WarmUp()
    {
        for (int i = 0; i < _minSize; i++)
        {
            try
            {
                _idle.Push(NewPhysical());
                Interlocked.Increment(ref _total);
            }
            catch (Exception ex)
            {
                LogMessages.PoolWarmUpFailed(_log, ex);
                break;
            }
        }
    }

    /// <summary>
    /// Checks out a connection: reuses a live, unexpired idle one, otherwise creates a new
    /// physical connection. Blocks up to <paramref name="timeoutSeconds"/> (0 = infinite)
    /// when Max Pool Size connections are already checked out, then throws.
    /// </summary>
    public PooledSession Rent(int timeoutSeconds)
    {
        int timeoutMs = timeoutSeconds > 0 ? timeoutSeconds * 1000 : Timeout.Infinite;
        // A checkout that has to wait is a pending request until a slot frees up (or times out).
        _metrics?.ReportPendingConnectionRequestStart();
        bool acquired;
        try
        {
            acquired = _slots.Wait(timeoutMs);
        }
        finally
        {
            _metrics?.ReportPendingConnectionRequestStop();
        }
        if (!acquired)
        {
            _metrics?.ReportConnectionPoolTimeout();
            LogMessages.PoolRentTimeout(_log, timeoutSeconds);
            throw new IfxException(
                "Timed out waiting for a connection from the pool; Max Pool Size was reached (-908).", -908);
        }

        try
        {
            while (_idle.TryPop(out var pooled))
            {
                if (pooled.Generation != Volatile.Read(ref _generation))
                    Retire(pooled, "pool-clear");
                else if (pooled.IsExpired(_lifetimeSeconds))
                    Retire(pooled, "lifetime");
                else if (!pooled.Asf.IsSocketConnected())
                    Retire(pooled, "dead socket");
                else
                    return pooled;
            }

            var fresh = NewPhysical();
            Interlocked.Increment(ref _total);
            return fresh;
        }
        // Opening a connection failed.
        catch
        {
            _slots.Release(); // creation failed — give the slot back
            throw;
        }
    }

    // Stamps the new physical connection with the current generation so a Clear() that lands
    // before it is returned will retire it rather than re-pool it.
    private PooledSession NewPhysical()
    {
        var (asf, sqli, version) = _factory();
        return new PooledSession(asf, sqli, version, Volatile.Read(ref _generation));
    }

    /// <summary>Returns a clean connection to the idle set (or disposes it when the pool has
    /// been cleared, it has aged out, or its socket died). Always releases the checkout slot.</summary>
    public void Return(PooledSession pooled)
    {
        string? retireReason =
            pooled.Asf.OutOfBandSent ? "interrupted" // a stray cancel byte could hit the next lease
            : pooled.Generation != Volatile.Read(ref _generation) ? "pool-clear"
            : pooled.IsExpired(_lifetimeSeconds) ? "lifetime"
            : !pooled.Asf.IsSocketConnected() ? "dead socket"
            : null;

        if (retireReason is null)
            _idle.Push(pooled);
        else
            Retire(pooled, retireReason);
        _slots.Release();
    }

    /// <summary>Discards a connection that must not be reused (checkin error / abandoned reader).</summary>
    public void Discard(PooledSession pooled)
    {
        Retire(pooled, pooled.Asf.OutOfBandSent ? "interrupted" : "abandoned reader");
        _slots.Release();
    }

    // Tears down a physical connection that will not be reused and accounts for it.
    private void Retire(PooledSession pooled, string reason)
    {
        LogMessages.PhysicalConnectionRetired(_log, reason);
        pooled.Dispose();
        LogMessages.ClosedPhysicalConnection(_log);
        Interlocked.Decrement(ref _total);
    }

    // Background timer callback: retires idle connections that have exceeded their
    // ConnectionLifetime, keeping the pool from holding stale sockets forever when
    // no client Rent() arrives to trigger the expiry check. Maintains MinPoolSize.
    private void PruneIdle()
    {
        // Drain and re-push surviving connections (ConcurrentStack has no in-place remove).
        var survivors = new List<PooledSession>();
        while (_idle.TryPop(out var pooled))
        {
            if (pooled.Generation != Volatile.Read(ref _generation))
                Retire(pooled, "prune/pool-clear");
            else if (pooled.IsExpired(_lifetimeSeconds))
                Retire(pooled, "prune/lifetime");
            else if (!pooled.Asf.IsSocketConnected())
                Retire(pooled, "prune/dead socket");
            else
                survivors.Add(pooled);
        }
        // Keep at least MinPoolSize idle connections alive.
        foreach (var s in survivors)
            _idle.Push(s);
    }

    /// <summary>
    /// Empties the idle set and retires every currently checked-out connection: bumps the
    /// generation so those connections are disposed rather than re-pooled when returned.
    /// </summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        while (_idle.TryPop(out var pooled))
            Retire(pooled, "pool-clear");
    }
}

/// <summary>
/// The process-wide registry of <see cref="IfxConnectionPool"/> instances, keyed by a
/// canonical form of the connection string.
/// </summary>
internal static class IfxConnectionPools
{
    // Lazy, because ConcurrentDictionary.GetOrAdd may run its factory more than once when two
    // threads miss at the same time and keeps only one result. Building a pool is not free -- it
    // opens the Min Pool Size connections and starts the prune timer -- so the loser of that race
    // would be a second, unreachable pool holding live server connections nobody can ever close.
    // With Lazy the race is over cheap wrappers and exactly one pool gets built.
    private static readonly ConcurrentDictionary<string, Lazy<IfxConnectionPool>> Pools = new(StringComparer.Ordinal);

    /// <summary>Returns the pool for <paramref name="key"/>, creating it with
    /// <paramref name="builder"/> only on the first (cache-miss) call for that key.</summary>
    public static IfxConnectionPool GetOrCreate(string key, Func<IfxConnectionPool> builder)
    {
        var lazy = Pools.GetOrAdd(key, _ => new Lazy<IfxConnectionPool>(builder));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A builder that threw is cached by Lazy; drop the entry so the next Open retries.
            Pools.TryRemove(new KeyValuePair<string, Lazy<IfxConnectionPool>>(key, lazy));
            throw;
        }
    }

    public static bool TryGet(string key, out IfxConnectionPool pool)
    {
        if (Pools.TryGetValue(key, out var lazy) && lazy.IsValueCreated)
        {
            pool = lazy.Value;
            return true;
        }
        pool = null!;
        return false;
    }

    public static void Clear(string key)
    {
        if (TryGet(key, out var pool))
            pool.Clear();
    }

    public static void ClearAll()
    {
        foreach (var lazy in Pools.Values)
            if (lazy.IsValueCreated)
                lazy.Value.Clear();
    }
}
