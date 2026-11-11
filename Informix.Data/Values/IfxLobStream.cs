namespace Informix.Data;

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over an Informix smart large object
/// (CLOB/BLOB). It backs <see cref="IfxDataReader.GetStream"/> for a BLOB and,
/// wrapped in a <see cref="StreamReader"/>, <see cref="IfxDataReader.GetTextReader"/>
/// for a CLOB: the sbspace payload is pulled incrementally over the FastPath
/// <c>ifx_lo_*</c> engine in chunks of at most <see cref="ChunkSize"/> bytes
/// (SQ_LODATA reads) rather than materialized in one round trip.
/// </summary>
/// <remarks>
/// The stream holds the connection's SQLI session while open — the session is
/// message-at-a-time — so it must be consumed before the next
/// <see cref="IfxDataReader.Read"/> or any other command on the same connection.
/// The reader disposes (closes) any LO stream it handed out on the next row advance.
/// A closed backing object is opened read-only
/// (<see cref="IfxSmartLOBOpenMode.ReadOnly"/>); the stream never writes.
/// </remarks>
internal sealed class IfxLobStream : Stream
{
    /// <summary>The R_W_BUFSIZE the server transfers per SQ_LODATA read — the read cap.</summary>
    private const int ChunkSize = 32000;

    // An IfxBlob (raw-byte smart LOB). A CLOB column is read through the same raw-byte
    // engine here and decoded to text by the StreamReader layered on top.
    private readonly IfxSmartLOB _lob;
    private readonly bool _leaveOpen;
    private bool _opened;
    private bool _disposed;
    private long _length = -1;
    private long _position;

    /// <param name="leaveOpen">Do not close the smart LOB when the stream is disposed —
    /// used by <see cref="IfxSmartLOB.ToFile"/> over an object the caller opened (and
    /// still owns); <see cref="EnsureOpen"/>'s read-only open is then a no-op against
    /// the existing fd.</param>
    internal IfxLobStream(IfxSmartLOB lob, bool leaveOpen = false)
    {
        _lob = lob;
        _leaveOpen = leaveOpen;
    }

    // The large object is opened lazily on first use: getting Length or reading a byte
    // triggers IFX_LO_OPEN (positions at byte 0) and one IFX_LO_STAT for the size.
    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_opened)
            return;
        _lob.Open(IfxSmartLOBOpenMode.ReadOnly);
        _length = _lob.Size;
        _opened = true;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get { EnsureOpen(); return _length; }
    }

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        EnsureOpen();
        if (count == 0)
            return 0;
        long remaining = _length - _position;
        if (remaining <= 0)
            return 0; // at end of object
        // Bound to what remains so the underlying LO read returns a fully valid buffer,
        // and to a single SQ_LODATA chunk so large values stream instead of materializing.
        int want = (int)Math.Min(Math.Min(count, ChunkSize), remaining);
        byte[] data = _lob.ReadBytes(want);
        int n = Math.Min(data.Length, want);
        Array.Copy(data, 0, buffer, offset, n);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureOpen();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => offset,
        };
        if (target < 0)
            throw new IOException("Cannot seek before the start of the smart large object.");
        _lob.Seek(target, IfxSmartLOBWhence.Begin);
        _position = target;
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) =>
        throw new NotSupportedException("A smart-LOB read stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A smart-LOB read stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && _opened && !_leaveOpen)
                _lob.Close(); // release the server file descriptor (IFX_LO_CLOSE)
        }
        base.Dispose(disposing);
    }
}
