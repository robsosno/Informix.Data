using Informix.Data.Asf;

namespace Informix.Data.Sqli;

// ===================================================================== //
//  CommandTimeout enforcement                                           //
// ===================================================================== //

/// <summary>
/// The one-shot cancel timer backing <see cref="SqliSession.BeginCommandTimeout"/>,
/// extracted out of <see cref="SqliSession"/> — it touches nothing on the session besides
/// <see cref="AsfConnection"/> (<c>SendOutOfBand</c> / <c>SetReceiveTimeout</c> /
/// <c>RestoreReceiveTimeout</c>), taken in the constructor.
/// </summary>
internal sealed class SqliTimeoutTimer
{
    private readonly AsfConnection _asf;
    private readonly object _timeoutGate = new();
    private Timer? _cancelTimer;
    private bool _timeoutArmed;
    // How far past CommandTimeout a single blocking receive may run before the
    // socket-level backstop trips (a server that never even answers the cancel).
    private const int TimeoutBackstopSeconds = 5;

    internal SqliTimeoutTimer(AsfConnection asf) => _asf = asf;

    /// <summary>
    /// Arms a one-shot cancel timer covering a command's execution, mirroring the
    /// <c>cq.startCancel</c>/<c>cq.stopCancel</c> bracket around
    /// <c>IfxStatement.executeQueryImpl</c>/<c>executeImpl</c>. On expiry the timer
    /// sends the ASF out-of-band cancel byte (the port of
    /// <c>IfxCancelQueryImpl.run</c> → <c>IfxStatement.cancel</c> →
    /// <c>Connection.sendOutOfBand</c>); the server aborts the running statement and
    /// replies SQ_ERR <c>-213</c> ("Statement interrupted by user"), which the
    /// in-progress <see cref="SqliSession.ReceiveMessage"/> surfaces as an
    /// <see cref="IfxException"/>. <paramref name="seconds"/> == 0 disables the timer
    /// (the ADO.NET "no timeout" convention). The returned scope disarms the timer
    /// on dispose; dispose it around the whole execution, not each round trip.
    /// </summary>
    internal IDisposable Begin(int seconds)
    {
        if (seconds <= 0)
            return NoTimeoutScope.Instance;

        lock (_timeoutGate)
        {
            _timeoutArmed = true;
            _cancelTimer = new Timer(_ => FireCancel(), null, (long)seconds * 1000, Timeout.Infinite);
        }
        // Backstop: cap a single blocking receive so a server that never answers the
        // cancel can't hang the caller past CommandTimeout + margin.
        _asf.SetReceiveTimeout((seconds + TimeoutBackstopSeconds) * 1000);
        return new TimeoutScope(this);
    }

    // Timer callback: send the out-of-band cancel exactly once. Best-effort — if the
    // OOB send itself fails, the pending receive still surfaces the real outcome
    // (either the server's reply or the socket-timeout backstop).
    // Thread safety: this runs on a ThreadPool timer thread, but SendOutOfBand() writes
    // to a separate urgent-data channel (TCP OOB byte) which is atomic at the OS level
    // and does not conflict with the main session's synchronous read/write path.
    // The send happens INSIDE _timeoutGate (a one-byte non-blocking send, so the hold is
    // brief): End() takes the same gate, which makes it join an in-flight send instead of
    // returning — and letting the connection serve its next statement — while the byte
    // is still on its way. The byte is not tied to a statement, so one that arrives after
    // End() would interrupt that next statement (SQLCODE -213). SendOutOfBand also marks
    // the physical connection (AsfConnection.OutOfBandSent) so the pool retires it.
    private void FireCancel()
    {
        lock (_timeoutGate)
        {
            if (!_timeoutArmed)
                return;
            _timeoutArmed = false;
            try { _asf.SendOutOfBand(); }
            catch { /* swallow — the read in flight determines the outcome */ }
        }
    }

    private void End()
    {
        lock (_timeoutGate)
        {
            _timeoutArmed = false;
            _cancelTimer?.Dispose();
            _cancelTimer = null;
        }
        _asf.RestoreReceiveTimeout();
    }

    private sealed class TimeoutScope(SqliTimeoutTimer owner) : IDisposable
    {
        public void Dispose() => owner.End();
    }

    private sealed class NoTimeoutScope : IDisposable
    {
        public static readonly NoTimeoutScope Instance = new();
        public void Dispose() { }
    }
}
