using Informix.Data.Sqli;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxCommand"/>-level coverage of the session prepared-statement cache
/// (Stage 2): unlike an explicit <see cref="IfxCommand.Prepare()"/> hold (Stage 1,
/// per-command), the cache is session-wide -- two unrelated <see cref="IfxCommand"/>
/// objects with the same SQL text share one prepared statement. Enabled per test via
/// the <c>Prepared Statement Cache Size</c> connection-string keyword.
/// </summary>
public class PreparedStatementCacheEndToEndTests
{
    [Fact]
    public void Two_commands_with_the_same_SQL_text_share_the_session_cache()
    {
        byte[] script = SqliStatementKit.Join(
            // cmd1.ExecuteNonQuery(): genuine prepare (zero params, but caching forces the
            // prepared path) + execute; released into the cache instead of SQ_RELEASE.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // cmd2.ExecuteNonQuery(): identical SQL text -> cache hit, reactivated (SQ_ID),
            // no SQ_PREPARE. If it mistakenly re-prepared it would try to read a describe
            // frame from what is really just another execute reply, and fail.
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Prepared Statement Cache Size=2");
        using var cmd1 = cn.CreateCommand();
        cmd1.CommandText = "update t set a = 1";
        Assert.Equal(1, cmd1.ExecuteNonQuery());

        using var cmd2 = cn.CreateCommand();
        cmd2.CommandText = "update t set a = 1"; // identical text, different command object
        Assert.Equal(2, cmd2.ExecuteNonQuery());
    }

    [Fact]
    public void Cache_hits_and_misses_are_visible_on_the_connection()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Prepared Statement Cache Size=2");
        using var cmd1 = cn.CreateCommand();
        cmd1.CommandText = "update t set a = 1";
        cmd1.ExecuteNonQuery();
        Assert.Equal(0, cn.PreparedStatementCacheHits);
        Assert.Equal(1, cn.PreparedStatementCacheMisses);

        using var cmd2 = cn.CreateCommand();
        cmd2.CommandText = "update t set a = 1";
        cmd2.ExecuteNonQuery();
        Assert.Equal(1, cn.PreparedStatementCacheHits);
        Assert.Equal(1, cn.PreparedStatementCacheMisses);
    }

    [Fact]
    public void A_prepared_command_takes_precedence_over_the_cache_for_its_own_executions()
    {
        // cmd1 explicitly Prepare()s (Stage 1) -- it must reactivate its own held handle,
        // never consult or populate the session cache, even though caching is on.
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            // A second execute on the SAME held command must reactivate, not touch the
            // cache -- only one more execute reply is scripted, no release/checkout frames.
            SqliStatementKit.DoneFrame(rows: 5),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Prepared Statement Cache Size=2");
        var cmd1 = cn.CreateCommand();
        cmd1.CommandText = "update t set a = 1";
        cmd1.Prepare();

        Assert.Equal(1, cmd1.ExecuteNonQuery());
        Assert.Equal(5, cmd1.ExecuteNonQuery());
        // Nothing was ever cached for this command's own SQL text: no hit/miss recorded.
        Assert.Equal(0, cn.PreparedStatementCacheHits);
        Assert.Equal(0, cn.PreparedStatementCacheMisses);
    }

    [Fact]
    public void A_stale_plan_error_drops_the_statement_instead_of_caching_it_and_the_next_execute_re_prepares()
    {
        byte[] script = SqliStatementKit.Join(
            // cmd1: prepare + execute fails with -710 (stale plan) -- must be released for
            // real, not returned to the cache.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.ErrorReply(-710, "Table has been altered."),
            SqliStatementKit.EotFrame(), // release reply for the broken statement
            // cmd2: same SQL text -- must miss (nothing cached) and prepare fresh.
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 2, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 3),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _, extraSettings: "Prepared Statement Cache Size=2");
        using var cmd1 = cn.CreateCommand();
        cmd1.CommandText = "update t set a = 1";
        var ex = Assert.Throws<IfxException>(() => cmd1.ExecuteNonQuery());
        Assert.Equal(-710, ex.IfxSqlCode);

        using var cmd2 = cn.CreateCommand();
        cmd2.CommandText = "update t set a = 1";
        Assert.Equal(3, cmd2.ExecuteNonQuery()); // re-prepared, not a broken cache hit
        Assert.Equal(0, cn.PreparedStatementCacheHits);
        Assert.Equal(2, cn.PreparedStatementCacheMisses);
    }
}
