using System.Transactions;
using Xunit;
using STx = System.Transactions;

namespace Informix.Data.Tests;

/// <summary>
/// System.Transactions (promotable single-phase) enlistment: an IfxConnection that joins an
/// ambient <see cref="TransactionScope"/> drives the local SQLI transaction (SQ_BEGIN /
/// SQ_CMMTWORK / SQ_RBWORK) under the transaction manager, with no MSDTC/XA. Driven over the
/// scripted in-memory transport, so the wire opcodes are asserted without a live server.
///
/// <para>System.Transactions resolves an unspecified isolation level to Serializable, so an
/// enlisted transaction always issues a SET TRANSACTION after BEGIN (Serializable -> REPEATABLE
/// READ) — the same default-Serializable behaviour as SqlClient. Every script below therefore
/// includes the SET TRANSACTION reply between BEGIN and COMMIT/ROLLBACK.</para>
/// </summary>
public class TransactionEnlistmentTests
{
    // BEGIN(35)+EOT and CMMTWORK(19)+EOT / RBWORK(20)+svpt0+EOT as raw wire bytes.
    private static readonly byte[] Begin = { 0, 35, 0, 12 };
    private static readonly byte[] Commit = { 0, 19, 0, 12 };
    private static readonly byte[] Rollback = { 0, 20, 0, 0, 0, 12 };

    // Reply frames the server would send for BEGIN, then the SET TRANSACTION statement, then
    // the final COMMIT or ROLLBACK.
    private static byte[] BeginReply() => SqliStatementKit.EotFrame();
    private static byte[] SetTransactionReply() =>
        SqliStatementKit.Join(SqliStatementKit.DoneFrame(), SqliStatementKit.EotFrame());
    private static byte[] EndReply() => SqliStatementKit.EotFrame();

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);

    [Fact]
    public void Manual_enlist_then_commit_sends_begin_and_commit()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        long before = duplex.Output.Length;

        using var tx = new CommittableTransaction();
        cn.EnlistTransaction(tx);   // SQ_BEGIN + SET TRANSACTION, synchronously (Initialize)
        tx.Commit();                // SQ_CMMTWORK, synchronously (SinglePhaseCommit)

        string hex = Hex(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains(Hex(Begin), hex);
        Assert.Contains(Hex(Commit), hex);
        Assert.True(hex.IndexOf(Hex(Begin), StringComparison.Ordinal)
                  < hex.IndexOf(Hex(Commit), StringComparison.Ordinal), "BEGIN must precede COMMIT");
    }

    [Fact]
    public void Ambient_scope_commit_drives_begin_and_commit_even_though_connection_closed_first()
    {
        // The canonical using-pattern: the connection is disposed (Closed) INSIDE the scope,
        // before the scope commits. The physical connection must stay alive under the
        // enlistment so the transaction manager can still COMMIT WORK on it afterwards.
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        DuplexStream duplex;
        using (var scope = new TransactionScope())
        {
            using (var cn = SqliStatementKit.OpenConnection(script, out duplex))
            {
                // work happens here; auto-enlisted at Open()
            } // cn.Close() — enlisted + live: physical handed to the enlistment, NOT rolled back
            scope.Complete();
        } // scope.Dispose() -> SinglePhaseCommit -> SQ_CMMTWORK on the held physical

        string hex = Hex(duplex.Output.ToArray());
        Assert.Contains(Hex(Begin), hex);
        Assert.Contains(Hex(Commit), hex);
        Assert.True(hex.IndexOf(Hex(Begin), StringComparison.Ordinal)
                  < hex.IndexOf(Hex(Commit), StringComparison.Ordinal), "BEGIN must precede COMMIT");
    }

    [Fact]
    public void Ambient_scope_without_complete_rolls_back()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        DuplexStream duplex;
        using (new TransactionScope())
        using (var cn = SqliStatementKit.OpenConnection(script, out duplex))
        {
            // no scope.Complete() -> the scope aborts on dispose
        }

        string hex = Hex(duplex.Output.ToArray());
        Assert.Contains(Hex(Begin), hex);
        Assert.Contains(Hex(Rollback), hex);
    }

    [Fact]
    public void Default_scope_isolation_is_serializable_set_transaction()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        DuplexStream duplex;
        using (var scope = new TransactionScope())
        {
            using (var cn = SqliStatementKit.OpenConnection(script, out duplex)) { }
            scope.Complete();
        }

        string sent = System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray());
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sent);
    }

    [Fact]
    public void Ambient_scope_isolation_level_issues_set_transaction()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        DuplexStream duplex;
        using (var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = STx.IsolationLevel.ReadCommitted }))
        {
            using (var cn = SqliStatementKit.OpenConnection(script, out duplex)) { }
            scope.Complete();
        }

        string sent = System.Text.Encoding.Latin1.GetString(duplex.Output.ToArray());
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", sent);
    }

    [Fact]
    public void Enlist_false_skips_auto_enlistment()
    {
        // No transaction opcodes should be sent: with Enlist=false the connection ignores
        // the ambient scope. Only the handshake (and the disconnect) travel the wire.
        byte[] script = Array.Empty<byte>();

        DuplexStream duplex;
        using (new TransactionScope())
        using (var cn = SqliStatementKit.OpenConnection(script, out duplex, extraSettings: "Enlist=false"))
        {
        }

        string hex = Hex(duplex.Output.ToArray());
        Assert.DoesNotContain(Hex(Begin), hex);
    }

    [Fact]
    public void Begin_local_transaction_while_enlisted_throws()
    {
        byte[] script = SqliStatementKit.Join(BeginReply(), SetTransactionReply(), EndReply());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var tx = new CommittableTransaction();
        cn.EnlistTransaction(tx);

        Assert.Throws<InvalidOperationException>(() => cn.BeginTransaction());

        tx.Rollback(); // resolve the transaction so the connection cleans up
    }

    [Fact]
    public void Enlist_is_reflected_by_the_connection_string_builder()
    {
        Assert.True(new IfxConnectionStringBuilder("Database=d").Enlist);       // default
        var off = new IfxConnectionStringBuilder("Database=d;Enlist=false");
        Assert.False(off.Enlist);
        Assert.False(new IfxConnectionStringBuilder(off.ConnectionString).Enlist); // round-trips
    }
}
