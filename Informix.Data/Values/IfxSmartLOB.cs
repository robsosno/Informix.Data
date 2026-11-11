using System.Data.SqlTypes;
using System.Text;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// A smart-large-object locator — the 72-byte <c>ifx_lo_t</c> handle the server
/// uses to reference CLOB/BLOB data in an sbspace.
/// </summary>
public class IfxSmartLOBLocator
{
    private readonly byte[] _bytes;

    public IfxSmartLOBLocator(byte[] locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _bytes = (byte[])locator.Clone();
    }

    public IfxSmartLOBLocator(string locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _bytes = Convert.FromHexString(locator);
    }

    public int Length => _bytes.Length;

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public override string ToString() => Convert.ToHexString(_bytes);

    internal byte[] Raw => _bytes;
}

/// <summary>
/// The base of the Informix smart large objects (<see cref="IfxBlob"/> and
/// <see cref="IfxClob"/>): a random-access handle over sbspace data, opened by
/// locator (an existing value) or created new. Wraps the FastPath
/// <c>ifx_lo_*</c> engine (open/seek/read/write/stat/truncate/close).
/// </summary>
public abstract class IfxSmartLOB : INullable
{
    private protected readonly IfxConnection? Connection;
    private protected byte[]? LocatorBytes;
    // Server-side file descriptor of the open LOB; -1 when closed. Pos tracks the client's idea of the current offset.
    private protected int Fd = -1;
    private protected long Pos;

    private protected IfxSmartLOB() { }

    private protected IfxSmartLOB(IfxConnection connection) =>
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));

    private protected IfxSmartLOB(IfxConnection connection, IfxSmartLOBLocator locator) : this(connection)
    {
        ArgumentNullException.ThrowIfNull(locator);
        LocatorBytes = locator.ToBytes();
    }

    private SqliSession Session =>
        Connection?.SqliSession ?? throw new InvalidOperationException("The smart LOB's connection is not open.");

    /// <summary>True for a SQL NULL smart LOB (no locator).</summary>
    public bool IsNull => LocatorBytes is null;

    /// <summary>True while the smart LOB is open (an fd is held on the server).</summary>
    public bool IsOpen => Fd >= 0;

    // Create-time hints (fed into a future custom LO_CREATE spec; the current create
    // path uses the server-default sbspace). Kept for surface parity with the
    // official provider.
    public long EstimatedSize { get; set; }
    public int ExtentSize { get; set; }
    public int Flags { get; set; }
    public long MaxBytes { get; set; }
    public string SBSpace { get; set; } = "";

    /// <summary>The reference count (not tracked client-side; always throws).</summary>
    public int ReferenceCount => throw new NotSupportedException("ReferenceCount is not available from the client.");

    public int LastAccessTime => throw new NotSupportedException("LastAccessTime is not available from the client.");
    public int LastChangeTime => throw new NotSupportedException("LastChangeTime is not available from the client.");
    public int LastModificationTime => throw new NotSupportedException("LastModificationTime is not available from the client.");

    /// <summary>The total size of the open smart LOB in bytes.</summary>
    public long Size => Session.LobSize(RequireOpen());

    /// <summary>The current read/write cursor position.</summary>
    public long Position
    {
        get => Pos;
        set { Session.LobSeekTo(RequireOpen(), value, (int)IfxSmartLOBWhence.Begin); Pos = value; }
    }

    /// <summary>Opens the smart LOB for access.</summary>
    public void Open(IfxSmartLOBOpenMode mode)
    {
        if (LocatorBytes is null)
            throw new InvalidOperationException("Cannot open a NULL smart LOB.");
        if (IsOpen)
            return;
        Fd = Session.LobOpen(LocatorBytes, (int)mode);
        Pos = 0;
    }

    // Creates a new empty smart LOB (used by IfxConnection.GetIfxBlob/GetIfxClob).
    internal void CreateNew()
    {
        (int fd, byte[] locator) = Session.LobCreate();
        Fd = fd;
        LocatorBytes = locator;
        Pos = 0;
    }

    /// <summary>Positions the cursor and returns the new absolute position.</summary>
    public long Seek(long offset, IfxSmartLOBWhence whence)
    {
        int fd = RequireOpen();
        Session.LobSeekTo(fd, offset, (int)whence);
        Pos = whence switch
        {
            IfxSmartLOBWhence.Begin => offset,
            IfxSmartLOBWhence.Current => Pos + offset,
            IfxSmartLOBWhence.End => Session.LobSize(fd) + offset,
            _ => offset,
        };
        return Pos;
    }

    /// <summary>Truncates the smart LOB to <paramref name="offset"/> bytes.</summary>
    public void Truncate(long offset) => Session.LobTruncate(RequireOpen(), offset);

    /// <summary>Closes the smart LOB.</summary>
    public void Close()
    {
        if (IsOpen)
        {
            Session.LobClose(Fd);
            Fd = -1;
        }
    }

    /// <summary>Closes and releases the smart LOB.</summary>
    public void Release() => Close();

    /// <summary>The locator of this smart LOB.</summary>
    public IfxSmartLOBLocator GetLocator() =>
        LocatorBytes is null ? throw new InvalidOperationException("A NULL smart LOB has no locator.")
                             : new IfxSmartLOBLocator(LocatorBytes);

    // Shared raw byte read/write (IfxBlob uses directly; IfxClob transcodes chars).
    private protected byte[] ReadRaw(int count)
    {
        byte[] data = Session.LobRead(RequireOpen(), count);
        Pos += data.Length;
        return data;
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> raw bytes from the current position — the
    /// incremental read seam <see cref="IfxLobStream"/> drives one SQ_LODATA chunk at a
    /// time. The caller is expected to bound <paramref name="count"/> to the bytes that
    /// remain so the returned array is fully valid (no over-read past end of object).
    /// </summary>
    internal byte[] ReadBytes(int count) => ReadRaw(count);

    private protected int WriteRaw(byte[] data)
    {
        int n = Session.LobWrite(RequireOpen(), data);
        Pos += n;
        return n;
    }

    // Not implemented in this provider version — documented, throws.
    public void Lock(long smartLOBOffset, IfxSmartLOBWhence whence, long range, IfxSmartLOBLockMode lockMode) =>
        throw new NotSupportedException("Smart-LOB byte-range locking is not yet implemented.");

    public void Unlock(long smartLOBOffset, IfxSmartLOBWhence whence, long range) =>
        throw new NotSupportedException("Smart-LOB byte-range locking is not yet implemented.");

    /// <summary>
    /// Copies an OS file's bytes into the smart LOB — replacing its content, or
    /// appending when <paramref name="appendToSmartLOB"/> is true. Only
    /// <see cref="IfxSmartLOBFileLocation.Client"/> is supported: the file is read by
    /// this process and streamed up over the regular <c>ifx_lo_write</c> path (no
    /// server-side <c>ifx_lo_from_file</c>). A closed smart LOB is opened
    /// <see cref="IfxSmartLOBOpenMode.ReadWrite"/> for the copy and closed again; an
    /// already-open one is used as-is (its open mode must permit writing) and is left
    /// open, positioned after the written bytes. Bytes copy 1:1 here;
    /// <see cref="IfxClob"/> overrides this to transcode text.
    /// </summary>
    public virtual void FromFile(string filename, bool appendToSmartLOB, IfxSmartLOBFileLocation fileLocation) =>
        FromFileCore(filename, appendToSmartLOB, fileLocation, file =>
        {
            var buffer = new byte[FileTransferChunkSize];
            int n;
            while ((n = file.Read(buffer, 0, buffer.Length)) > 0)
                WriteRaw(n == buffer.Length ? buffer : buffer[..n]);
        });

    /// <summary>
    /// Copies the smart LOB's content into an OS file (opened with
    /// <paramref name="mode"/> — <see cref="FileMode.Create"/> to overwrite,
    /// <see cref="FileMode.Append"/> to extend, …) and returns the full path written.
    /// Only <see cref="IfxSmartLOBFileLocation.Client"/> is supported: the object
    /// streams down in SQ_LODATA chunks through <see cref="IfxLobStream"/> and is
    /// written by this process (no server-side <c>ifx_lo_to_file</c>). A closed smart
    /// LOB is opened read-only for the copy and closed again; an already-open one is
    /// used as-is (its open mode must permit reading) and is left open, positioned at
    /// the end of the object. Bytes copy 1:1 here; <see cref="IfxClob"/> overrides
    /// this to transcode text.
    /// </summary>
    public virtual string ToFile(string filename, FileMode mode, IfxSmartLOBFileLocation fileLocation) =>
        ToFileCore(filename, mode, fileLocation, (lob, file) => lob.CopyTo(file));

    // One ifx_lo_write round trip per chunk on the file-to-LOB path (R_W_BUFSIZE).
    private protected const int FileTransferChunkSize = 32000;

    // The shared file-to-LOB bookkeeping around a copy body: open (if the caller has
    // not), position at the start or the end, run the copy, then trim the old tail on
    // a replace. The LOB is left open only if it already was.
    private protected void FromFileCore(
        string filename, bool appendToSmartLOB, IfxSmartLOBFileLocation fileLocation, Action<FileStream> copy)
    {
        ArgumentNullException.ThrowIfNull(filename);
        RequireClientFile(fileLocation);
        using var file = File.OpenRead(filename);
        bool wasOpen = IsOpen;
        if (!wasOpen)
            Open(IfxSmartLOBOpenMode.ReadWrite);
        try
        {
            Seek(0, appendToSmartLOB ? IfxSmartLOBWhence.End : IfxSmartLOBWhence.Begin);
            copy(file);
            if (!appendToSmartLOB)
                Truncate(Pos); // replace semantics: drop any old tail past the file
        }
        finally
        {
            if (!wasOpen)
                Close();
        }
    }

    // The shared LOB-to-file bookkeeping around a copy body. The LO is opened before
    // the file so a bad locator fails without having clobbered an existing target.
    private protected string ToFileCore(
        string filename, FileMode mode, IfxSmartLOBFileLocation fileLocation, Action<IfxLobStream, FileStream> copy)
    {
        ArgumentNullException.ThrowIfNull(filename);
        RequireClientFile(fileLocation);
        using var lob = OpenReadStream();
        _ = lob.Length; // forces the open/stat round trips before the file is created
        using var file = new FileStream(filename, mode, FileAccess.Write);
        copy(lob, file);
        return Path.GetFullPath(filename);
    }

    // Client-side transfer is the only implemented location. Server/None would need
    // the ifx_lo_from_file / ifx_lo_to_file server routines and a server-visible
    // path — the JDBC IfxLoToFile(LO_SERVER_FILE) route.
    private protected static void RequireClientFile(IfxSmartLOBFileLocation fileLocation)
    {
        if (fileLocation != IfxSmartLOBFileLocation.Client)
            throw new NotSupportedException(
                $"Smart-LOB file transfer supports only IfxSmartLOBFileLocation.Client (got {fileLocation}); " +
                "the OS file is read/written by this process.");
    }

    // A read stream over this LOB positioned at byte 0. A closed LOB opens lazily
    // (read-only) and closes with the stream; an already-open one is rewound — a
    // file dump always covers the whole object — and left open for its owner.
    private protected IfxLobStream OpenReadStream()
    {
        if (IsNull)
            throw new InvalidOperationException("Cannot open a NULL smart LOB.");
        bool wasOpen = IsOpen;
        var stream = new IfxLobStream(this, leaveOpen: wasOpen);
        if (wasOpen)
            stream.Position = 0;
        return stream;
    }

    private int RequireOpen() => IsOpen ? Fd : throw new InvalidOperationException("The smart LOB is not open.");
}

/// <summary>An Informix BLOB (binary smart large object).</summary>
public sealed class IfxBlob : IfxSmartLOB
{
    /// <summary>A SQL NULL BLOB.</summary>
    public static readonly IfxBlob Null = new();

    private IfxBlob() { }

    public IfxBlob(IfxConnection connection) : base(connection) { }

    public IfxBlob(IfxConnection connection, IfxSmartLOBLocator locator) : base(connection, locator) { }

    /// <param name="table">Reserved for future use (not currently used).</param>
    /// <param name="column">Reserved for future use (not currently used).</param>
    public IfxBlob(IfxConnection connection, string table, string column) : base(connection) { }

    /// <param name="table">Reserved for future use (not currently used).</param>
    /// <param name="column">Reserved for future use (not currently used).</param>
    public IfxBlob(IfxConnection connection, IfxSmartLOBLocator locator, string table, string column)
        : base(connection, locator) { }

    /// <summary>Reads up to <c>buff.Length</c> bytes from the current position.</summary>
    public long Read(byte[] buff)
    {
        ArgumentNullException.ThrowIfNull(buff);
        byte[] data = ReadRaw(buff.Length);
        Array.Copy(data, buff, data.Length);
        return data.Length;
    }

    /// <summary>Reads a slice: seeks to <paramref name="smartLOBOffset"/> then reads into <paramref name="buff"/>.</summary>
    public long Read(byte[] buff, long buffOffset, long numBytesToRead, long smartLOBOffset, IfxSmartLOBWhence whence)
    {
        ArgumentNullException.ThrowIfNull(buff);
        Seek(smartLOBOffset, whence);
        byte[] data = ReadRaw((int)numBytesToRead);
        Array.Copy(data, 0, buff, buffOffset, data.Length);
        return data.Length;
    }

    /// <summary>Writes all of <paramref name="buff"/> at the current position.</summary>
    public long Write(byte[] buff)
    {
        ArgumentNullException.ThrowIfNull(buff);
        return WriteRaw(buff);
    }

    /// <summary>Writes a slice at <paramref name="smartLOBOffset"/>.</summary>
    public long Write(byte[] buff, long buffOffset, long numBytesToWrite, long smartLOBOffset, IfxSmartLOBWhence whence)
    {
        ArgumentNullException.ThrowIfNull(buff);
        Seek(smartLOBOffset, whence);
        return WriteRaw(buff[(int)buffOffset..(int)(buffOffset + numBytesToWrite)]);
    }
}

/// <summary>An Informix CLOB (character smart large object).</summary>
public sealed class IfxClob : IfxSmartLOB
{
    /// <summary>A SQL NULL CLOB.</summary>
    public static readonly IfxClob Null = new();

    /// <summary>The code set the CLOB's bytes are stored in on the server (DB_LOCALE).</summary>
    private Encoding DbEncoding => Connection?.DbEncoding ?? Wire.IfxWireConverter.DefaultEncoding;

    private IfxClob() { }

    public IfxClob(IfxConnection connection) : base(connection) { }

    public IfxClob(IfxConnection connection, IfxSmartLOBLocator locator) : base(connection, locator) { }

    /// <param name="table">Reserved for future use (not currently used).</param>
    /// <param name="column">Reserved for future use (not currently used).</param>
    public IfxClob(IfxConnection connection, string table, string column) : base(connection) { }

    /// <param name="table">Reserved for future use (not currently used).</param>
    /// <param name="column">Reserved for future use (not currently used).</param>
    public IfxClob(IfxConnection connection, IfxSmartLOBLocator locator, string table, string column)
        : base(connection, locator) { }

    /// <summary>Reads characters from the current position into <paramref name="buff"/>.</summary>
    /// <remarks>
    /// Reads enough bytes to decode up to <c>buff.Length</c> characters. In multi-byte
    /// encodings a character may span multiple bytes, so the number of bytes read from
    /// the LOB may exceed <c>buff.Length</c>.
    /// </remarks>
    public long Read(char[] buff)
    {
        ArgumentNullException.ThrowIfNull(buff);
        return ReadChars(buff, 0, buff.Length);
    }

    /// <summary>Reads a slice: seeks then reads characters.</summary>
    /// <remarks>
    /// Reads enough bytes to decode up to <paramref name="numCharsToRead"/> characters.
    /// </remarks>
    public long Read(char[] buff, long buffOffset, long numCharsToRead, long smartLOBOffset, IfxSmartLOBWhence whence)
    {
        ArgumentNullException.ThrowIfNull(buff);
        Seek(smartLOBOffset, whence);
        return ReadChars(buff, (int)buffOffset, (int)numCharsToRead);
    }

    // Reads up to `count` characters into buff starting at offset. The byte count a
    // multi-byte code set needs for `count` characters isn't known up front — and
    // Encoding.GetMaxByteCount pads even a 1-char estimate (e.g. it reports 2
    // bytes/char for the single-byte Latin-1 code set, not 1) — so the fetched bytes
    // can decode to more characters than the destination holds. Decoder.Convert bounds
    // the decode to the destination instead of throwing the way Encoding.GetChars
    // does, and any fetched bytes it leaves unconsumed are pushed back onto the LOB
    // position so a later read doesn't skip them.
    private long ReadChars(char[] buff, int offset, int count)
    {
        count = Math.Min(count, buff.Length - offset);
        var enc = DbEncoding;
        int maxBytesPerChar = enc.IsSingleByte ? 1 : enc.GetMaxByteCount(1);
        byte[] raw = ReadRaw(count * maxBytesPerChar);
        enc.GetDecoder().Convert(raw, 0, raw.Length, buff, offset, count, flush: false,
            out int bytesUsed, out int charsUsed, out _);
        if (bytesUsed < raw.Length)
            Seek(Pos - (raw.Length - bytesUsed), IfxSmartLOBWhence.Begin);
        return charsUsed;
    }

    /// <summary>Writes all of <paramref name="buff"/> at the current position.</summary>
    public long Write(char[] buff)
    {
        ArgumentNullException.ThrowIfNull(buff);
        return WriteRaw(DbEncoding.GetBytes(buff));
    }

    /// <summary>Writes a character slice at <paramref name="smartLOBOffset"/>.</summary>
    public long Write(char[] buff, long buffOffset, long numCharsToWrite, long smartLOBOffset, IfxSmartLOBWhence whence)
    {
        ArgumentNullException.ThrowIfNull(buff);
        Seek(smartLOBOffset, whence);
        return WriteRaw(DbEncoding.GetBytes(buff, (int)buffOffset, (int)numCharsToWrite));
    }

    /// <summary>
    /// The file encoding used by <see cref="ToFile(string, FileMode, IfxSmartLOBFileLocation)"/>
    /// and <see cref="FromFile(string, bool, IfxSmartLOBFileLocation)"/> when none is
    /// given: UTF-8 without a byte-order mark, matching <see cref="File.WriteAllText(string, string)"/>.
    /// </summary>
    public static readonly Encoding DefaultFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc cref="FromFile(string, bool, IfxSmartLOBFileLocation, Encoding)"/>
    public override void FromFile(string filename, bool appendToSmartLOB, IfxSmartLOBFileLocation fileLocation) =>
        FromFile(filename, appendToSmartLOB, fileLocation, DefaultFileEncoding);

    /// <summary>
    /// Loads an OS text file into the CLOB, transcoding it from
    /// <paramref name="fileEncoding"/> (UTF-8 without BOM by default, as for any .NET
    /// string; a leading BOM in the file is detected and dropped) into the database
    /// code set the CLOB is stored in. Otherwise identical to
    /// <see cref="IfxSmartLOB.FromFile(string, bool, IfxSmartLOBFileLocation)"/> —
    /// client-side only.
    /// </summary>
    /// <remarks>
    /// Appending re-positions at the end of the existing <em>bytes</em>. In a
    /// multi-byte code set that is only a character boundary if the CLOB was written
    /// whole; appending to a LOB truncated mid-character will corrupt the seam.
    /// </remarks>
    public void FromFile(string filename, bool appendToSmartLOB, IfxSmartLOBFileLocation fileLocation,
                         Encoding fileEncoding)
    {
        ArgumentNullException.ThrowIfNull(fileEncoding);
        FromFileCore(filename, appendToSmartLOB, fileLocation, file =>
        {
            using var reader = new StreamReader(file, fileEncoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            // Encode through a stateful Encoder: a surrogate pair split across two
            // reads must not turn into two replacement characters.
            var encoder = DbEncoding.GetEncoder();
            var chars = new char[TextTransferCharCount];
            var bytes = new byte[DbEncoding.GetMaxByteCount(chars.Length)];
            int n;
            while ((n = reader.Read(chars, 0, chars.Length)) > 0)
                WriteEncoded(encoder, chars, n, bytes, flush: false);
            WriteEncoded(encoder, chars, 0, bytes, flush: true);
        });
    }

    /// <inheritdoc cref="ToFile(string, FileMode, IfxSmartLOBFileLocation, Encoding)"/>
    public override string ToFile(string filename, FileMode mode, IfxSmartLOBFileLocation fileLocation) =>
        ToFile(filename, mode, fileLocation, DefaultFileEncoding);

    /// <summary>
    /// Dumps the CLOB into an OS text file, transcoding it from the database code set
    /// into <paramref name="fileEncoding"/> (UTF-8 without BOM by default, as for any
    /// .NET string). Otherwise identical to
    /// <see cref="IfxSmartLOB.ToFile(string, FileMode, IfxSmartLOBFileLocation)"/> —
    /// client-side only, streamed in SQ_LODATA chunks.
    /// </summary>
    public string ToFile(string filename, FileMode mode, IfxSmartLOBFileLocation fileLocation, Encoding fileEncoding)
    {
        ArgumentNullException.ThrowIfNull(fileEncoding);
        return ToFileCore(filename, mode, fileLocation, (lob, file) =>
        {
            using var reader = new StreamReader(lob, DbEncoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(file, fileEncoding, leaveOpen: true);
            var chars = new char[TextTransferCharCount];
            int n;
            while ((n = reader.Read(chars, 0, chars.Length)) > 0)
                writer.Write(chars, 0, n);
        });
    }

    private const int TextTransferCharCount = 8192;

    private void WriteEncoded(Encoder encoder, char[] chars, int charCount, byte[] bytes, bool flush)
    {
        int n = encoder.GetBytes(chars, 0, charCount, bytes, 0, flush);
        if (n > 0)
            WriteRaw(n == bytes.Length ? bytes : bytes[..n]);
    }
}
