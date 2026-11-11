using System.Threading.Tasks;
using Informix.Data.Asf;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// A duplex transport that serves <c>phase1</c> immediately, then blocks reads
/// once it is drained until the out-of-band cancel is signalled (or the test
/// releases it manually), after which it serves <c>phase2</c>. Models a server
/// that has stopped replying mid-statement — the exact situation
/// <see cref="Informix.Data.IfxCommand.CommandTimeout"/> must break out of.
/// </summary>
internal sealed class CancelScriptStream : Stream, IOutOfBandSink
{
    private readonly MemoryStream _phase1;
    private readonly MemoryStream _phase2;
    private readonly ManualResetEventSlim _gate = new(false);
    private const int SafetyTimeoutMs = 30_000; // keep a driver bug from hanging the suite

    public CancelScriptStream(byte[] phase1, byte[] phase2)
    {
        _phase1 = new MemoryStream(phase1);
        _phase2 = new MemoryStream(phase2);
    }

    /// <summary>Everything the client wrote (requests), for assertions.</summary>
    public MemoryStream Output { get; } = new();

    /// <summary>How many times the driver's cancel path signalled out-of-band.</summary>
    public int OutOfBandCount { get; private set; }

    // The driver's cancel-on-timeout path (AsfConnection.SendOutOfBand) lands here.
    public void SignalOutOfBand()
    {
        OutOfBandCount++;
        _gate.Set();
    }

    /// <summary>Test-only: release the block without counting an out-of-band cancel.</summary>
    public void Release() => _gate.Set();

    /// <summary>Set once a read has drained phase 1 and is parked waiting for the cancel —
    /// i.e. the "server" is mid-statement. Lets a test cancel at a deterministic moment.</summary>
    public ManualResetEventSlim ReadBlocked { get; } = new(false);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _phase1.Read(buffer, offset, count);
        if (n > 0)
            return n;
        // phase1 drained: the "server" has gone quiet. Wait for the cancel signal,
        // then deliver phase2 (the interrupted-statement reply and what follows).
        ReadBlocked.Set();
        if (!_gate.Wait(SafetyTimeoutMs))
            throw new TimeoutException("CancelScriptStream: cancel was never signalled.");
        return _phase2.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _gate.Dispose();
        base.Dispose(disposing);
    }
}

public class CommandTimeoutTests
{
    private static byte[] IntRow(int v)
    {
        var r = new byte[4];
        IfxWireConverter.WriteInt32(r.AsSpan(0, 4), v);
        return r;
    }

    private static IfxConnection Open(byte[] phase1Statement, byte[] phase2, out CancelScriptStream stream)
    {
        byte[] phase1 = AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .Concat(phase1Statement)
            .ToArray();
        var s = new CancelScriptStream(phase1, phase2);
        stream = s;
        var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = settings => new AsfConnection(settings, s);
        cn.Open();
        return cn;
    }

    // A prepare reply describing a one-INT-column SELECT, ready in phase1. The
    // open+fetch reply is deliberately withheld to phase2 so the open blocks.
    private static byte[] SelectPrepareReply(short stmtId) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId, tupleSize: 4,
            new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame());

    // A complete reply script for one scalar SELECT returning `value`.
    private static byte[] ScalarSelectReply(short stmtId, int value) => SqliStatementKit.Join(
        SelectPrepareReply(stmtId),
        SqliStatementKit.DoneFrame(),                 // open reply
        SqliStatementKit.TupleFrame(IntRow(value)),
        SqliStatementKit.CloseFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());                 // release reply

    [Fact]
    public void CommandTimeout_cancels_a_hung_statement_and_leaves_the_connection_usable()
    {
        // phase2, served after the cancel out-of-band byte lands: the server's
        // "statement interrupted by user" (-213), the release reply for the
        // interrupted statement, then a full reply for a follow-up scalar select.
        byte[] phase2 = SqliStatementKit.Join(
            SqliStatementKit.ErrorReply(-213, "SQL statement interrupted by user."),
            SqliStatementKit.EotFrame(),              // release reply for the aborted statement
            ScalarSelectReply(stmtId: 2, value: 99));

        using var cn = Open(SelectPrepareReply(stmtId: 1), phase2, out var stream);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from spin";
        cmd.CommandTimeout = 1;

        var ex = Assert.Throws<IfxException>(() => cmd.ExecuteReader());
        Assert.Equal(-213, ex.IfxSqlCode);
        Assert.Equal(1, stream.OutOfBandCount); // the timer sent exactly one cancel

        // The connection survived the cancel: a follow-up command runs on it.
        using var cmd2 = cn.CreateCommand();
        cmd2.CommandText = "select 99 from t";
        Assert.Equal(99, cmd2.ExecuteScalar());
    }

    [Fact]
    public async Task CommandTimeout_zero_arms_no_timer_and_never_cancels()
    {
        // With no timer armed, the pending open stays blocked; the test releases it
        // by hand once it has confirmed nothing auto-cancelled it.
        byte[] phase2 = SqliStatementKit.Join(
            SqliStatementKit.DoneFrame(),             // open reply
            SqliStatementKit.TupleFrame(IntRow(7)),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());             // release reply

        using var cn = Open(SelectPrepareReply(stmtId: 1), phase2, out var stream);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from t";
        cmd.CommandTimeout = 0;

        var task = Task.Run(() =>
        {
            using var r = cmd.ExecuteReader();
            return r.Read() ? r.GetInt32(0) : -1;
        });

        // No timer: the open is never auto-cancelled even well past a nominal timeout.
        Task finished = await Task.WhenAny(task, Task.Delay(1200));
        Assert.NotSame(task, finished); // the delay won: the open is still blocked
        Assert.Equal(0, stream.OutOfBandCount);

        // Release by hand and let the reader finish normally.
        stream.Release();
        int value = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.OutOfBandCount);
        Assert.Equal(7, value);
    }
}
