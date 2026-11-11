using System.Text;

namespace Informix.Data.Wire;

/// <summary>
/// A pass-through <see cref="Stream"/> that appends a hex dump of every byte
/// read and written to a trace file. Enabled by setting the environment
/// variable <c>IFX_WIRE_TRACE</c> to a file path; used only for protocol
/// bring-up/debugging and has no effect otherwise.
/// </summary>
internal sealed class WireTraceStream(Stream inner, string tracePath) : Stream
{
    // Serializes appends to the trace file, which is shared by the read and write paths (and by other connections tracing to it).
    private readonly object _lock = new();

    /// <summary>Wraps <paramref name="inner"/> when IFX_WIRE_TRACE is set; otherwise returns it unchanged.</summary>
    public static Stream MaybeWrap(Stream inner)
    {
        string? path = Environment.GetEnvironmentVariable("IFX_WIRE_TRACE");
        return string.IsNullOrEmpty(path) ? inner : new WireTraceStream(inner, path);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        if (n > 0) Dump("S->C", buffer.AsSpan(offset, n));
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Dump("C->S", buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    // Format: "C->S" / "S->C", byte count, hex bytes, then a printable-ASCII column between '|' marks.
    private void Dump(string dir, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        sb.Append(dir).Append(' ').Append(data.Length).Append(": ");
        foreach (byte b in data)
            sb.Append(b.ToString("x2")).Append(' ');
        sb.Append("  |");
        foreach (byte b in data)
            sb.Append(b is >= 32 and < 127 ? (char)b : '.');
        sb.Append('|');
        lock (_lock)
            File.AppendAllText(tracePath, sb.AppendLine().ToString());
    }

    public override bool CanRead => inner.CanRead;
    // The wrapped socket stream is not seekable; the remaining members just delegate or throw.
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
