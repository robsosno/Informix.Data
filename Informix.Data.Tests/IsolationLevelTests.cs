using System.Data;
using System.Text;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The <c>Isolation Level</c> connection-string keyword: the session level is pinned at
/// <see cref="IfxConnection.Open"/> <b>only</b> when the caller asked for one.
///
/// <para>That conditionality is the whole point. <c>SET ISOLATION</c> is rejected by a
/// database created without logging, so a driver that always issued one would break every
/// unlogged-database connection. Leaving the keyword out sends nothing at all and the
/// server's own default stands.</para>
/// </summary>
public class IsolationLevelTests
{
    // ExecuteImmediate's reply: SQ_DONE then SQ_EOT (same shape TransactionEnlistmentTests uses).
    private static byte[] SetIsolationReply() =>
        SqliStatementKit.Join(SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame());

    private static string SentText(DuplexStream duplex) =>
        Encoding.Latin1.GetString(duplex.Output.ToArray());

    [Fact]
    public void Unspecified_by_default_sends_no_set_isolation()
    {
        // No reply scripted after bring-up: if Open sent a statement, the read would fail.
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);

        Assert.Equal(IsolationLevel.Unspecified, cn.Settings.IsolationLevel);
        Assert.DoesNotContain("SET ISOLATION", SentText(duplex), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ReadUncommitted", "SET ISOLATION TO DIRTY READ")]
    [InlineData("ReadCommitted", "SET ISOLATION TO COMMITTED READ")]
    [InlineData("RepeatableRead", "SET ISOLATION TO REPEATABLE READ")]
    [InlineData("Serializable", "SET ISOLATION TO REPEATABLE READ")] // Informix has no stricter level
    [InlineData("Snapshot", "SET ISOLATION TO COMMITTED READ LAST COMMITTED")]
    public void Specified_level_is_applied_at_open(string level, string expectedSql)
    {
        using var cn = SqliStatementKit.OpenConnection(
            SetIsolationReply(), out var duplex, $"Isolation Level={level}");

        Assert.Contains(expectedSql, SentText(duplex), StringComparison.Ordinal);
    }

    [Fact]
    public void Level_is_case_insensitive_and_accepts_the_unspaced_synonym()
    {
        using var cn = SqliStatementKit.OpenConnection(
            SetIsolationReply(), out var duplex, "IsolationLevel=repeatableread");

        Assert.Contains("SET ISOLATION TO REPEATABLE READ", SentText(duplex), StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_unspecified_still_sends_nothing()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex, "Isolation Level=Unspecified");

        Assert.DoesNotContain("SET ISOLATION", SentText(duplex), StringComparison.Ordinal);
    }

    [Fact]
    public void Unrecognized_level_throws_before_the_server_is_dialed()
    {
        // ValidateSettings reads the property first thing in Open, so the typo surfaces
        // without a socket ever being opened — note there is no transport factory here.
        var cn = new IfxConnection(
            "Host=h;Service=9088;Database=db;User ID=usr;Password=pwd;Isolation Level=RepatableRead");

        var ex = Assert.Throws<ArgumentException>(cn.Open);
        Assert.Contains("RepatableRead", ex.Message);
        Assert.Contains("RepeatableRead", ex.Message); // the message lists the valid names
    }

    [Theory]
    [InlineData(0, "Unspecified")]
    [InlineData(1, "ReadUncommitted")]
    [InlineData(2, "ReadCommitted")]
    [InlineData(3, "RepeatableRead")]
    [InlineData(4, "Serializable")]
    [InlineData(5, "Snapshot")]
    public void Numeric_ODBC_ISOLATIONLEVEL_codes_are_accepted(int code, string expectedName)
    {
        var b = new IfxConnectionStringBuilder($"Isolation Level={code}");

        Assert.Equal(Enum.Parse<IsolationLevel>(expectedName), b.IsolationLevel);
    }

    [Theory]
    [InlineData("6")]  // the Windows DSN dialog's "Transaction" — no ADO.NET counterpart
    [InlineData("7")]
    [InlineData("12345")]
    [InlineData("Chaos")] // a defined IsolationLevel member, but Informix has no equivalent
    public void Unsupported_values_are_rejected_rather_than_silently_accepted(string value)
    {
        // Enum.TryParse happily turns "12345" into an undefined IsolationLevel; the builder
        // must not let that through, or Open would emit no statement and silently do nothing.
        var b = new IfxConnectionStringBuilder($"Isolation Level={value}");

        Assert.Throws<ArgumentException>(() => b.IsolationLevel);
    }

    [Fact]
    public void Builder_round_trips_and_keeps_one_spelling()
    {
        var b = new IfxConnectionStringBuilder("IsolationLevel=ReadCommitted")
        {
            IsolationLevel = IsolationLevel.Snapshot,
        };

        Assert.Equal(IsolationLevel.Snapshot, b.IsolationLevel);
        // The setter drops the synonym so the two spellings can never disagree.
        Assert.False(b.ContainsKey("IsolationLevel"));
        Assert.Contains("Isolation Level=Snapshot", b.ConnectionString, StringComparison.Ordinal);
    }
}
