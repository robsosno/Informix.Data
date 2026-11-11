namespace Informix.Data.Sqli;

/// <summary>Cache key: SQL text plus cursor kind — matching text opened differently
/// (scroll vs. forward-only, hold vs. not) needs its own prepared statement.</summary>
internal readonly record struct SqliStatementCacheKey(string Sql, bool Scroll, bool Hold);

/// <summary>
/// The session-level prepared-statement cache: a size-bounded,
/// checkout-removes cache keyed by <see cref="SqliStatementCacheKey"/>.
///
/// <para><b>Checkout removes</b> (<see cref="Checkout"/>): using a cached statement
/// takes it out of the cache, so a second concurrent use of the same SQL text simply
/// prepares its own copy rather than racing over one entry — and a statement whose
/// execute fails (e.g. a -710 stale-plan error) is already out of the cache, so the
/// caller just has to release it for real instead of returning it, no separate
/// invalidation step needed.</para>
///
/// <para><b>Release returns</b> (<see cref="Return"/>): closing a statement that came
/// from (or is eligible for) the cache inserts it back instead of sending SQ_RELEASE.
/// When the cache is already at capacity, the least-recently-returned entry is evicted
/// and handed back to the caller to release for real — the entries sitting in the
/// cache are exactly the ones not currently checked out, so eviction by insertion/
/// return order already is least-recently-*used* order.</para>
/// </summary>
internal sealed class SqliStatementCache(int capacity)
{
    private readonly Dictionary<SqliStatementCacheKey, LinkedListNode<(SqliStatementCacheKey Key, SqliStatementHandle Handle)>> _index = [];
    private readonly LinkedList<(SqliStatementCacheKey Key, SqliStatementHandle Handle)> _lru = [];

    public int Capacity { get; } = capacity;

    public int Count => _index.Count;

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    /// <summary>Removes and returns the cached handle for <paramref name="key"/>, or
    /// <see langword="null"/> on a miss.</summary>
    public SqliStatementHandle? Checkout(SqliStatementCacheKey key)
    {
        if (!_index.Remove(key, out var node))
        {
            Misses++;
            return null;
        }
        _lru.Remove(node);
        Hits++;
        return node.Value.Handle;
    }

    /// <summary>
    /// Inserts <paramref name="handle"/> under <paramref name="key"/> instead of releasing
    /// it. Returns the handle that must actually be released (SQ_RELEASE) as a result of
    /// this call — the LRU evictee if the cache was at capacity, or <paramref name="handle"/>
    /// itself when <paramref name="key"/> is already occupied (a second concurrent prepare
    /// of the same SQL text/cursor kind, both finishing around the same time) — or
    /// <see langword="null"/> when nothing needs releasing.
    /// </summary>
    public SqliStatementHandle? Return(SqliStatementCacheKey key, SqliStatementHandle handle)
    {
        if (_index.ContainsKey(key))
            return handle; // already cached by a concurrent user -- this copy must be released for real

        SqliStatementHandle? evicted = null;
        if (Capacity > 0 && _index.Count >= Capacity)
        {
            var oldest = _lru.First!;
            _lru.RemoveFirst();
            _index.Remove(oldest.Value.Key);
            evicted = oldest.Value.Handle;
        }

        _index[key] = _lru.AddLast((key, handle));
        return evicted;
    }

    public void ResetStats()
    {
        Hits = 0;
        Misses = 0;
    }
}
