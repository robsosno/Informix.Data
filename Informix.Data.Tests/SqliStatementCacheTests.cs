using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Direct <see cref="SqliSession"/>-level coverage of the prepared-statement cache
/// (<see cref="SqliSession.PrepareCached"/>/<see cref="SqliSession.ReleaseOrCache"/>).
/// </summary>
public class SqliStatementCacheTests
{
    private static SqliSession CachedSession(byte[] script, int cacheSize, out DuplexStream duplex)
    {
        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out duplex);
        session.PreparedStatementCacheSize = cacheSize;
        session.BringUp();
        return session;
    }

    private static byte[] SelectDescribe(short stmtId, string column) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame(
            (short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col(column, (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    [Fact]
    public void PrepareCached_misses_and_prepares_fresh_when_nothing_is_cached_yet()
    {
        var session = CachedSession(SelectDescribe(1, "id"), cacheSize: 4, out _);

        var handle = session.PrepareCached("select id from t", 0, scroll: false, hold: false);

        Assert.Equal(1, handle.StatementId);
        Assert.Equal(0, session.PreparedStatementCacheHits);
        Assert.Equal(1, session.PreparedStatementCacheMisses);
    }

    [Fact]
    public void ReleaseOrCache_then_PrepareCached_reactivates_the_same_statement_with_no_extra_wire_traffic()
    {
        var session = CachedSession(SelectDescribe(1, "id"), cacheSize: 4, out var duplex);
        var handle = session.PrepareCached("select id from t", 0, scroll: false, hold: false);
        session.ReleaseOrCache(handle, "select id from t", scroll: false, hold: false);
        Assert.True(session.IsIdle); // cached-but-idle, not "in flight"

        long before = duplex.Output.Length;
        var reactivated = session.PrepareCached("select id from t", 0, scroll: false, hold: false);

        Assert.Equal(before, duplex.Output.Length); // no SQ_PREPARE round trip -- a pure Activate
        Assert.Same(handle, reactivated);
        Assert.Equal(1, session.StatementId);
        Assert.Equal(1, session.PreparedStatementCacheHits);
        Assert.Equal(1, session.PreparedStatementCacheMisses);
    }

    // Two overlapping users of the same text never share a handle: the second prepares statement 2.
    [Fact]
    public void Checkout_removes_so_a_second_concurrent_use_of_the_same_sql_prepares_fresh()
    {
        byte[] script = SqliStatementKit.Join(SelectDescribe(1, "id"), SelectDescribe(2, "id"));
        var session = CachedSession(script, cacheSize: 4, out _);

        // First use: checked out, never returned yet (still "in flight").
        var first = session.PrepareCached("select id from t", 0, scroll: false, hold: false);
        Assert.Equal(1, first.StatementId);

        // A second use of the exact same SQL text while the first is still checked out
        // finds nothing in the cache (it was removed on checkout) and prepares its own copy.
        var second = session.PrepareCached("select id from t", 0, scroll: false, hold: false);

        Assert.Equal(2, second.StatementId);
        Assert.Equal(0, session.PreparedStatementCacheHits);
        Assert.Equal(2, session.PreparedStatementCacheMisses);
    }

    [Fact]
    public void LRU_eviction_releases_the_oldest_entry_when_the_cache_is_full()
    {
        byte[] script = SqliStatementKit.Join(
            SelectDescribe(1, "a"),   // prepare A
            SelectDescribe(2, "b"),   // prepare B (cache now full at capacity 1, A already returned)
            SqliStatementKit.EotFrame(), // release reply for evicted A
            SelectDescribe(3, "a"));  // re-prepare "select a" -- a miss, A is gone
        var session = CachedSession(script, cacheSize: 1, out var duplex);

        var a = session.PrepareCached("select a from t", 0, scroll: false, hold: false);
        session.ReleaseOrCache(a, "select a from t", scroll: false, hold: false); // cached, capacity 1 now full

        var b = session.PrepareCached("select b from t", 0, scroll: false, hold: false);
        long before = duplex.Output.Length;
        session.ReleaseOrCache(b, "select b from t", scroll: false, hold: false); // evicts A -> real SQ_RELEASE

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, 4, 0, 1, 0, 11, 0, 12 }, sent); // SQ_ID(1) + SQ_RELEASE + EOT

        // B is the one left cached: checking out "select a" again misses (A was evicted).
        var missA = session.PrepareCached("select a from t", 0, scroll: false, hold: false);
        Assert.NotEqual(a.StatementId, missA.StatementId);
    }

    [Fact]
    public void ResetFetchStats_zeroes_the_cache_hit_and_miss_counters()
    {
        var session = CachedSession(SelectDescribe(1, "id"), cacheSize: 4, out _);
        var handle = session.PrepareCached("select id from t", 0, scroll: false, hold: false);
        session.ReleaseOrCache(handle, "select id from t", scroll: false, hold: false);
        session.PrepareCached("select id from t", 0, scroll: false, hold: false); // a hit
        Assert.Equal(1, session.PreparedStatementCacheHits);
        Assert.Equal(1, session.PreparedStatementCacheMisses);

        session.ResetFetchStats();

        Assert.Equal(0, session.PreparedStatementCacheHits);
        Assert.Equal(0, session.PreparedStatementCacheMisses);
    }

    // Without a configured size, ReleaseOrCache sends the release immediately.
    [Fact]
    public void Caching_off_by_default_releases_for_real_instead_of_caching()
    {
        byte[] script = SqliStatementKit.Join(
            SelectDescribe(1, "id"),
            SqliStatementKit.EotFrame()); // release reply
        var session = SqliTestKit.SessionOver(
            SqliTestKit.BringUpReply("9.59.FC5").Concat(script).ToArray(), out var duplex);
        session.BringUp(); // PreparedStatementCacheSize left at its default, 0 = off

        var handle = session.PrepareCached("select id from t", 0, scroll: false, hold: false);
        long before = duplex.Output.Length;
        session.ReleaseOrCache(handle, "select id from t", scroll: false, hold: false);

        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, 4, 0, 1, 0, 11, 0, 12 }, sent); // SQ_ID(1) + SQ_RELEASE + EOT
        Assert.True(session.IsIdle);
    }
}
