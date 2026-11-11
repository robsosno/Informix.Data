using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// A transport whose out-of-band send can be held mid-flight, so a test can park the
/// cancel timer's thread inside <see cref="AsfConnection.SendOutOfBand"/> and observe
/// what <c>End()</c> (the end of the command's timeout scope) does about it.
/// </summary>
internal sealed class SlowOobStream : Stream, IOutOfBandSink
{
    public ManualResetEventSlim SendEntered { get; } = new(false);
    public ManualResetEventSlim SendRelease { get; } = new(false);
    public int OutOfBandCount;

    public void SignalOutOfBand()
    {
        SendEntered.Set();
        SendRelease.Wait(TimeSpan.FromSeconds(30));
        Interlocked.Increment(ref OutOfBandCount);
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override void Write(byte[] buffer, int offset, int count) { }
    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The out-of-band cancel byte is not synchronized with the statement it targets: one that
/// lands after its statement finished can interrupt the NEXT statement on the same
/// (pooled) physical connection (SQLCODE -213). These tests pin the three guards:
/// the timeout scope's end joins an in-flight send, <c>Cancel()</c> is a no-op while
/// nothing is executing, and a physical connection that was ever sent an OOB byte is
/// never returned to the pool.
/// </summary>
public class OutOfBandCancelTests
{
    private static byte[] IntRow(int v)
    {
        var r = new byte[4];
        IfxWireConverter.WriteInt32(r.AsSpan(0, 4), v);
        return r;
    }

    private static byte[] SelectPrepareReply(short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] ScalarSelectReply(short stmtId, int value) => SqliStatementKit.Join(
        SelectPrepareReply(stmtId),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.TupleFrame(IntRow(value)),
        SqliStatementKit.CloseFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    private static byte[] BringUp() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    private static IfxConnection Open(byte[] statementScript, byte[] phase2, out CancelScriptStream stream)
    {
        var s = new CancelScriptStream(BringUp().Concat(statementScript).ToArray(), phase2);
        stream = s;
        var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = settings => new AsfConnection(settings, s);
        cn.Open();
        return cn;
    }

    // ---- 1. End() joins an in-flight FireCancel --------------------------------------

    [Fact]
    public async Task Ending_the_timeout_scope_waits_for_an_in_flight_cancel_send()
    {
        var stream = new SlowOobStream();
        var asf = new AsfConnection(AsfTestKit.Settings(), stream);
        var timer = new SqliTimeoutTimer(asf);

        IDisposable scope = timer.Begin(1);

        // The timer thread fires and parks inside the out-of-band send.
        Assert.True(stream.SendEntered.Wait(TimeSpan.FromSeconds(10)), "the cancel timer never fired");

        // The command "completes" now and ends its scope. If End() does not join the
        // in-flight send, the scope ends — and the connection is handed to the next
        // statement — while the OOB byte is still on its way.
        Task end = Task.Run(scope.Dispose);
        Task first = await Task.WhenAny(end, Task.Delay(500));
        Assert.NotSame(end, first); // End() must still be waiting on the send

        stream.SendRelease.Set();
        await end.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, stream.OutOfBandCount);
        Assert.True(asf.OutOfBandSent); // and the physical connection is marked tainted
    }

    // ---- 2. Cancel() is a no-op while idle -------------------------------------------

    [Fact]
    public void Cancel_on_an_idle_connection_sends_nothing()
    {
        using var cn = Open(SelectPrepareReply(1), [], out var stream);
        using var cmd = cn.CreateCommand();

        cmd.Cancel(); // nothing is executing

        Assert.Equal(0, stream.OutOfBandCount);
        Assert.False(cn.AsfSession!.OutOfBandSent);
    }

    [Fact]
    public void Cancel_after_the_statement_completed_sends_nothing()
    {
        using var cn = Open(ScalarSelectReply(1, 5), [], out var stream);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from t";
        Assert.Equal(5, cmd.ExecuteScalar());

        cmd.Cancel(); // a late Cancel must not poison whatever runs next

        Assert.Equal(0, stream.OutOfBandCount);
        Assert.False(cn.AsfSession!.OutOfBandSent);
        stream.Release(); // teardown's statement-release exchange reads past the script
    }

    [Fact]
    public async Task Cancel_while_a_statement_is_executing_interrupts_it_and_taints_the_connection()
    {
        byte[] phase2 = SqliStatementKit.Join(
            SqliStatementKit.ErrorReply(-213, "SQL statement interrupted by user."),
            SqliStatementKit.EotFrame());

        using var cn = Open(SelectPrepareReply(1), phase2, out var stream);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from spin";

        var run = Task.Factory.StartNew(() => Assert.Throws<IfxException>(() => cmd.ExecuteReader()),
            TaskCreationOptions.LongRunning); // own thread: immune to pool starvation from sibling tests
        Assert.True(stream.ReadBlocked.Wait(TimeSpan.FromSeconds(30)), "the statement never blocked");

        cmd.Cancel();

        var ex = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(-213, ex.IfxSqlCode);
        Assert.Equal(1, stream.OutOfBandCount);
        Assert.True(cn.AsfSession!.OutOfBandSent);
    }

    // ---- 3. A tainted physical connection is never pooled ----------------------------

    [Fact]
    public void A_connection_that_was_sent_an_out_of_band_byte_is_discarded_not_pooled()
    {
        int calls = 0;
        string cs = "Host=h;Service=9088;Server=ol_test;Database=oob_taint;User ID=usr;Password=pwd";
        IfxConnection NewConn()
        {
            var cn = new IfxConnection(cs);
            cn.AsfConnectionFactory = settings =>
            {
                Interlocked.Increment(ref calls);
                return new AsfConnection(settings, new DuplexStream(BringUp()));
            };
            return cn;
        }

        using (var cn = NewConn())
        {
            cn.Open();
            cn.AsfSession!.SendOutOfBand(); // what a timeout that fired late would have done
            cn.Close();
            cn.Open();                      // must NOT get the interrupted physical connection back
            Assert.False(cn.AsfSession!.OutOfBandSent);
            cn.Close();
        }

        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_timed_out_command_does_not_leave_its_physical_connection_in_the_pool()
    {
        int calls = 0;
        string cs = "Host=h;Service=9088;Server=ol_test;Database=oob_timeout;User ID=usr;Password=pwd";
        byte[] phase2 = SqliStatementKit.Join(
            SqliStatementKit.ErrorReply(-213, "SQL statement interrupted by user."),
            SqliStatementKit.EotFrame());

        IfxConnection NewConn()
        {
            var cn = new IfxConnection(cs);
            cn.AsfConnectionFactory = settings =>
            {
                // First physical connection hangs the statement until the timer cancels it;
                // any later one is a plain bring-up-only transport.
                Stream s = Interlocked.Increment(ref calls) == 1
                    ? new CancelScriptStream(BringUp().Concat(SelectPrepareReply(1)).ToArray(), phase2)
                    : new DuplexStream(BringUp());
                return new AsfConnection(settings, s);
            };
            return cn;
        }

        using (var cn = NewConn())
        {
            cn.Open();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "select n from spin";
                cmd.CommandTimeout = 1;
                var ex = Assert.Throws<IfxException>(() => cmd.ExecuteReader());
                Assert.Equal(-213, ex.IfxSqlCode);
            }
            cn.Close();
            cn.Open();
            cn.Close();
        }

        Assert.Equal(2, calls); // the interrupted connection was retired, not reused
    }
}
