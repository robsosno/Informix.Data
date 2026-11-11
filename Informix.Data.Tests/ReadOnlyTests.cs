using System.Data;
using System.Text;
using Xunit;
using STx = System.Transactions;

namespace Informix.Data.Tests;

/// <summary>
/// The <c>Read Only</c> connection-string keyword (a plain bool, replacing the earlier
/// <c>AccessMode</c> enum keyword) and the matching
/// <see cref="IfxConnection.BeginTransaction(IsolationLevel, bool)"/>/<see cref="IfxConnection.BeginTransaction(bool)"/>
/// overloads: every transaction the driver begins — local or enlisted — starts <c>SET
/// TRANSACTION READ ONLY</c> when asked to, server-enforced (SQLCODE -878, live-verified).
/// Driven over the scripted in-memory transport; see <see cref="SelectOnlyTests"/> for the
/// independent client-side <see cref="IfxConnectionStringBuilder.SelectOnly"/> guard.
/// </summary>
public class ReadOnlyTests
{
    private static byte[] BeginReply() => SqliStatementKit.EotFrame();
    private static byte[] SetTransactionReply() =>
        SqliStatementKit.Join(SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame());
    private static byte[] EndReply() => SqliStatementKit.EotFrame();

    private static string Sent(DuplexStream duplex) => Encoding.Latin1.GetString(duplex.Output.ToArray());

    [Fact]
    public void Default_read_only_sends_nothing()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        using (var tx = cn.BeginTransaction())
        {
            Assert.False(tx.IsReadOnly);
            tx.Commit();
        }

        string sent = Encoding.Latin1.GetString(duplex.Output.ToArray()[(int)before..]);
        Assert.DoesNotContain("SET TRANSACTION", sent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unspecified", "SET TRANSACTION READ ONLY")]
    [InlineData("ReadUncommitted", "SET TRANSACTION READ ONLY, ISOLATION LEVEL READ UNCOMMITTED")]
    [InlineData("ReadCommitted", "SET TRANSACTION READ ONLY, ISOLATION LEVEL READ COMMITTED")]
    [InlineData("RepeatableRead", "SET TRANSACTION READ ONLY, ISOLATION LEVEL REPEATABLE READ")]
    [InlineData("Serializable", "SET TRANSACTION READ ONLY, ISOLATION LEVEL SERIALIZABLE")]
    public void Read_Only_true_sends_the_expected_SET_TRANSACTION_for_every_level(
        string level, string expectedSql)
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex, "Read Only=true");
        long before = duplex.Output.Length;

        var isolation = Enum.Parse<IsolationLevel>(level);
        using (var tx = cn.BeginTransaction(isolation))
        {
            Assert.True(tx.IsReadOnly);
            tx.Commit();
        }

        Assert.Contains(expectedSql, Sent(duplex)[(int)before..], StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_isReadOnly_true_overrides_the_default_false_keyword()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex); // Read Only default (false)

        using var tx = cn.BeginTransaction(IsolationLevel.ReadCommitted, isReadOnly: true);

        Assert.True(tx.IsReadOnly);
        Assert.Contains("SET TRANSACTION READ ONLY, ISOLATION LEVEL READ COMMITTED", Sent(duplex), StringComparison.Ordinal);
        tx.Commit();
    }

    [Fact]
    public void Explicit_isReadOnly_false_overrides_the_Read_Only_true_keyword()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex, "Read Only=true");

        using var tx = cn.BeginTransaction(IsolationLevel.ReadCommitted, isReadOnly: false);

        Assert.False(tx.IsReadOnly);
        Assert.DoesNotContain("READ ONLY", Sent(duplex), StringComparison.Ordinal);
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", Sent(duplex), StringComparison.Ordinal);
        tx.Commit();
    }

    [Fact]
    public void BeginTransaction_bool_overload_is_shorthand_for_Unspecified_plus_the_flag()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);

        using var tx = cn.BeginTransaction(isReadOnly: true);

        Assert.Equal(IsolationLevel.Unspecified, tx.IsolationLevel);
        Assert.True(tx.IsReadOnly);
        Assert.Contains("SET TRANSACTION READ ONLY", Sent(duplex), StringComparison.Ordinal);
        tx.Commit();
    }

    [Fact]
    public void Read_Only_is_a_plain_bool_and_round_trips_through_the_property_setter()
    {
        var b = new IfxConnectionStringBuilder { ReadOnly = true };
        Assert.True(b.ReadOnly);
        Assert.Contains("Read Only=True", b.ConnectionString, StringComparison.Ordinal);

        b.ReadOnly = false;
        Assert.False(b.ReadOnly);
    }

    [Fact]
    public void Chaos_throws_without_sending_SQ_BEGIN()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        long before = duplex.Output.Length;

        Assert.Throws<ArgumentOutOfRangeException>(() => cn.BeginTransaction(IsolationLevel.Chaos));

        // 0,35,0,12 is SQ_BEGIN + SQ_EOT -- must never have been written.
        Assert.DoesNotContain(BitConverter.ToString([0, 35, 0, 12]),
            BitConverter.ToString(duplex.Output.ToArray()[(int)before..]));
    }

    // Snapshot + read-only sends BOTH statements -- SET ISOLATION (Snapshot's usual recipe,
    // which has no SET TRANSACTION equivalent) then SET TRANSACTION READ ONLY -- rather than
    // throwing: live-tested 2026-09-14 as sound (SELECT keeps working, every write is
    // rejected with -878) even though LAST COMMITTED's own lock-avoidance benefit needs a
    // server new enough to have the feature at all (introduced in IDS 11.10) plus ROW-level
    // locking on the target table (see IfxConnection.BeginTransaction's doc comment).
    [Fact]
    public void Snapshot_plus_isReadOnly_sends_isolation_then_read_only()
    {
        byte[] script = SqliStatementKit.Join(
            BeginReply(), SetTransactionReply() /* SET ISOLATION reply */,
            SetTransactionReply() /* SET TRANSACTION READ ONLY reply */, EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);

        using var tx = cn.BeginTransaction(IsolationLevel.Snapshot, isReadOnly: true);

        Assert.True(tx.IsReadOnly);
        string sent = Sent(duplex);
        int isolationAt = sent.IndexOf("SET ISOLATION TO COMMITTED READ LAST COMMITTED", StringComparison.Ordinal);
        int readOnlyAt = sent.IndexOf("SET TRANSACTION READ ONLY", StringComparison.Ordinal);
        Assert.True(isolationAt >= 0 && readOnlyAt >= 0 && isolationAt < readOnlyAt,
            "expected SET ISOLATION before SET TRANSACTION READ ONLY");
        tx.Commit();
    }

    [Fact]
    public void Read_Only_true_plus_Snapshot_via_BeginTransaction_sends_both_statements()
    {
        byte[] script = SqliStatementKit.Join(
            BeginReply(), SetTransactionReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex, "Read Only=true");

        // No explicit isReadOnly -- the keyword alone must drive the same two statements.
        using var tx = cn.BeginTransaction(IsolationLevel.Snapshot);

        Assert.True(tx.IsReadOnly);
        string sent = Sent(duplex);
        Assert.Contains("SET ISOLATION TO COMMITTED READ LAST COMMITTED", sent, StringComparison.Ordinal);
        Assert.Contains("SET TRANSACTION READ ONLY", sent, StringComparison.Ordinal);
        tx.Commit();
    }

    // readOnly: false is still one round trip -- no SET TRANSACTION at all for Snapshot.
    [Fact]
    public void Snapshot_without_isReadOnly_sends_only_the_isolation_statement()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);

        using var tx = cn.BeginTransaction(IsolationLevel.Snapshot);

        Assert.False(tx.IsReadOnly);
        string sent = Sent(duplex);
        Assert.Contains("SET ISOLATION TO COMMITTED READ LAST COMMITTED", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("SET TRANSACTION", sent, StringComparison.Ordinal);
        tx.Commit();
    }

    [Fact]
    public void After_a_rejected_BeginTransaction_the_connection_has_no_active_transaction()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());
        using var cn = SqliStatementKit.OpenConnection(script, out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => cn.BeginTransaction(IsolationLevel.Chaos));

        // A real transaction can still be started afterward -- Chaos never registered one.
        using var tx = cn.BeginTransaction(IsolationLevel.ReadCommitted);
        tx.Commit();
    }

    [Fact]
    public void Enlisted_TransactionScope_picks_up_the_Read_Only_keyword()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        DuplexStream duplex;
        using (var scope = new STx.TransactionScope())
        {
            using (var cn = SqliStatementKit.OpenConnection(script, out duplex, "Read Only=true")) { }
            scope.Complete();
        }

        // System.Transactions resolves an unspecified scope isolation to Serializable.
        Assert.Contains("SET TRANSACTION READ ONLY, ISOLATION LEVEL SERIALIZABLE",
            Encoding.Latin1.GetString(duplex.Output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Enlisted_TransactionScope_with_Chaos_isolation_throws_and_does_not_enlist()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        long before = duplex.Output.Length;

        using var scope = new STx.TransactionScope(STx.TransactionScopeOption.Required,
            new STx.TransactionOptions { IsolationLevel = STx.IsolationLevel.Chaos });

        Assert.Throws<ArgumentOutOfRangeException>(() => cn.EnlistTransaction(STx.Transaction.Current));

        Assert.DoesNotContain(BitConverter.ToString([0, 35, 0, 12]),
            BitConverter.ToString(duplex.Output.ToArray()[(int)before..]));
    }
}
