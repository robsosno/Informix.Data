using System.Data.SqlTypes;
using System.Text;
using Informix.Data.Sqli;

namespace Informix.Data;

/// <summary>
/// A smart-large-object locator — the <see cref="Length"/>-byte <c>ifx_lo_t</c> handle the
/// server uses to reference CLOB/BLOB data in an sbspace. Immutable: the constructors copy
/// their input and <see cref="ToBytes"/> returns a copy.
/// </summary>
public class IfxSmartLOBLocator
{
    private readonly byte[] _bytes;

    /// <summary>A locator from its raw bytes, as <see cref="ToBytes"/> returns them.</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> is not exactly <see cref="Length"/> bytes.</exception>
    public IfxSmartLOBLocator(byte[] locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (locator.Length != Length)
            throw new ArgumentException(
                $"A smart-large-object locator is {Length} bytes; got {locator.Length}.", nameof(locator));
        _bytes = (byte[])locator.Clone();
    }

    /// <summary>A locator from its hex text, as <see cref="ToString"/> writes it.</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> is not <see cref="Length"/> × 2 hex digits.</exception>
    public IfxSmartLOBLocator(string locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (locator.Length != 2 * Length)
            throw new ArgumentException(
                $"A smart-large-object locator's hex text (IfxSmartLOBLocator.ToString()) is {2 * Length} hex digits; "
                + $"got {locator.Length} characters.", nameof(locator));
        try
        {
            _bytes = Convert.FromHexString(locator);
        }
        catch (FormatException e)
        {
            throw new ArgumentException(
                "A smart-large-object locator's hex text (IfxSmartLOBLocator.ToString()) may contain only hex digits.",
                nameof(locator), e);
        }
    }

    /// <summary>
    /// The size of every locator in bytes: 72, <c>sizeof(ifx_lo_t)</c>. A property of the type,
    /// not of an instance -- static as in Informix.Net.Core, and the single size the Informix JDBC
    /// driver knows too (<c>IfxLocator.IFX_LOCATOR_SIZE</c>; unlike the stat and spec blocks it has
    /// no smaller pre-long-ID variant). The constructors reject anything else.
    /// </summary>
    public static int Length => 72;

    /// <summary>The locator's raw bytes (a copy).</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>The locator as hex text, which the string constructor reads back.</summary>
    public override string ToString() => Convert.ToHexString(_bytes);

    // The bytes themselves, for the bind path: the instance never lets them out otherwise, so
    // reading them without a copy cannot change the locator.
    internal byte[] Raw => _bytes;
}

/// <summary>
/// The base of the Informix smart large objects (<see cref="IfxBlob"/> and
/// <see cref="IfxClob"/>): a random-access handle over sbspace data, opened by
/// locator (an existing value) or created new. Wraps the FastPath
/// <c>ifx_lo_*</c> engine (open/seek/read/write/stat/truncate/close).
/// </summary>
/// <remarks>
/// <para>Three kinds of object, as in Informix.Net.Core:</para>
/// <list type="bullet">
/// <item><description><b>Existing</b> -- built from a locator (a reader's
/// <see cref="IfxDataReader.GetIfxBlob"/>, or the <c>(connection, locator)</c> constructor):
/// <see cref="Open"/> opens it. Its create-spec properties report what the server holds for it
/// and cannot be set.</description></item>
/// <item><description><b>Not yet created</b> -- <see cref="IfxConnection.GetIfxBlob()"/> or the
/// <c>(connection)</c> / <c>(connection, table, column)</c> constructors: nothing exists on the
/// server and <see cref="IsNull"/> is true until <see cref="Open"/> creates it. That deferral is
/// what lets <see cref="SBSpace"/>, <see cref="ExtentSize"/>, <see cref="MaxBytes"/>,
/// <see cref="EstimatedSize"/>, <see cref="Flags"/> and the table/column take effect: they are
/// the create spec, and Open sends it.</description></item>
/// <item><description><b>SQL NULL</b> -- <see cref="IfxBlob.Null"/> / <see cref="IfxClob.Null"/>:
/// cannot be opened or created.</description></item>
/// </list>
/// </remarks>
public abstract class IfxSmartLOB : INullable
{
    private protected readonly IfxConnection? Connection;
    private protected byte[]? LocatorBytes;
    // Server-side file descriptor of the open LOB; -1 when closed. Pos tracks the client's idea of the current offset.
    private protected int Fd = -1;
    private protected long Pos;

    // The create spec (ifx_lo_spec, see SqliLobSpec) behind the create-time properties.
    // * An object not created yet: the spec Open() will send -- the server's defaults until the
    //   properties change it.
    // * An object that exists (it has a locator): the spec the server reports for it in
    //   IFX_LO_STAT, read on first use and kept (_specFromServer); the JDBC model, where
    //   IfxBblob.getLoStat().getLobDescriptor() describes the object as stored.
    private byte[]? _spec;
    private bool _specFromServer;

    // "table.column" whose storage clause (PUT ... IN (...), EXTENT SIZE, LOG ...) the server
    // folds into the create spec at Open() -- Informix.Net.Core's tableName/colName.
    private readonly string? _column;

    private protected IfxSmartLOB() { }

    private protected IfxSmartLOB(IfxConnection connection) =>
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));

    private protected IfxSmartLOB(IfxConnection connection, string? table, string? column) : this(connection) =>
        _column = QualifiedColumn(table, column);

    private protected IfxSmartLOB(IfxConnection connection, IfxSmartLOBLocator locator) : this(connection)
    {
        ArgumentNullException.ThrowIfNull(locator);
        LocatorBytes = locator.ToBytes();
    }

    // The column of an object that already exists cannot change where it was stored; kept so the
    // (connection, locator, table, column) shape behaves as in Informix.Net.Core, which stores it too.
    private protected IfxSmartLOB(IfxConnection connection, IfxSmartLOBLocator locator, string? table, string? column)
        : this(connection, locator) =>
        _column = QualifiedColumn(table, column);

    // Both or neither: half a column name would otherwise be dropped without a word, and the
    // object created in the default sbspace instead of the column's.
    private static string? QualifiedColumn(string? table, string? column)
    {
        bool hasTable = !string.IsNullOrEmpty(table), hasColumn = !string.IsNullOrEmpty(column);
        if (hasTable != hasColumn)
            throw new ArgumentException(
                "Give both the table and the column whose storage settings the smart LOB should take, or neither.");
        return hasTable ? table + "." + column : null;
    }

    private SqliSession Session =>
        Connection?.SqliSession ?? throw new InvalidOperationException("The smart LOB's connection is not open.");

    /// <summary>
    /// True when there is no locator: a SQL NULL, or an object <see cref="Open"/> has not created
    /// yet (as in Informix.Net.Core, where <c>GetIfxBlob()</c> also returns one with IsNull true).
    /// </summary>
    public bool IsNull => LocatorBytes is null;

    // Created by Open() on demand: has a connection to create it on, but no locator yet.
    internal bool IsPendingCreate => LocatorBytes is null && Connection is not null;

    /// <summary>True while the smart LOB is open (an fd is held on the server).</summary>
    public bool IsOpen => Fd >= 0;

    // The spec the getters read. The SQL NULL sentinel has none, as in Informix.Net.Core, whose
    // getters and setters throw InvalidOperationException for it.
    private byte[] Spec
    {
        get
        {
            RequireNotNullSentinel();
            if (LocatorBytes is null)
                return _spec ??= SqliSession.BuildDefaultLobDescriptor();
            if (!_specFromServer)
            {
                _spec = ReadSpecFromServer();
                _specFromServer = true;
            }
            return _spec!;
        }
    }

    // The spec the setters change: only while the object is still to be created. Afterwards the
    // server holds the spec, and changing a client-side copy would only make the getters wrong
    // (Informix.Net.Core accepts such a set and ignores it; JDBC's descriptor is a separate value
    // that only IfxLoAlter could apply).
    private byte[] SpecToChange
    {
        get
        {
            RequireNotNullSentinel();
            if (LocatorBytes is not null)
                throw new InvalidOperationException(
                    "This smart LOB already exists, and its create spec is fixed: SBSpace, ExtentSize, MaxBytes, "
                    + "EstimatedSize and Flags can only be set before Open() creates a new object "
                    + "(IfxConnection.GetIfxBlob()/GetIfxClob()). Reading them reports the object's actual settings.");
            return _spec ??= SqliSession.BuildDefaultLobDescriptor();
        }
    }

    private void RequireNotNullSentinel()
    {
        if (Connection is null)
            throw new InvalidOperationException("A SQL NULL smart LOB (IfxBlob.Null / IfxClob.Null) has no create spec.");
    }

    // The create spec the server reports for the object (the tail of its IFX_LO_STAT block, what
    // ESQL/C's ifx_lo_stat_cspec extracts). An object that is not open is opened read-only just
    // for this, as JDBC's IfxBblob.getLoStat() does.
    private byte[] ReadSpecFromServer()
    {
        SqliSession session = Session;
        if (IsOpen)
            return SpecOf(session.LobStat(Fd));
        int fd = session.LobOpen(LocatorBytes!, (int)IfxSmartLOBOpenMode.ReadOnly);
        try
        {
            return SpecOf(session.LobStat(fd));
        }
        finally
        {
            session.LobClose(fd);
        }

        static byte[] SpecOf(byte[] stat) => stat.AsSpan(SqliLobStat.SpecOffset, SqliLobSpec.Size).ToArray();
    }

    /// <summary>
    /// Expected size in bytes, a hint for the server's storage allocation; -1 (the default) lets
    /// the server decide. Part of the create spec: set it before <see cref="Open"/> creates the
    /// object.
    /// </summary>
    /// <remarks>
    /// <para>For an object that exists -- created by <see cref="Open"/>, or read from a row -- this
    /// and the other create-spec properties report what the server holds for it (from
    /// <c>ifx_lo_stat</c>, read once; the object is opened read-only for that if it is not open),
    /// as JDBC's <c>IfxBblob.getLoStat().getLobDescriptor()</c> does. Setting them then throws
    /// <see cref="InvalidOperationException"/>.</para>
    /// </remarks>
    public long EstimatedSize
    {
        get => SqliLobSpec.GetEstimatedBytes(Spec);
        set => SqliLobSpec.SetEstimatedBytes(SpecToChange, value);
    }

    /// <summary>
    /// Allocation extent in kilobytes; -1 (the default) uses the column's or the sbspace's own.
    /// Part of the create spec, like <see cref="EstimatedSize"/>.
    /// </summary>
    public int ExtentSize
    {
        get => SqliLobSpec.GetExtentSize(Spec);
        set => SqliLobSpec.SetExtentSize(SpecToChange, value);
    }

    /// <summary>
    /// Create-time flags, an <see cref="IfxSmartLOBCreateTimeFlags"/> combination (logging and
    /// last-access-time keeping); 0 (the default) takes the sbspace's settings. Part of the
    /// create spec, like <see cref="EstimatedSize"/>.
    /// </summary>
    public int Flags
    {
        get => SqliLobSpec.GetFlags(Spec);
        set => SqliLobSpec.SetFlags(SpecToChange, value);
    }

    /// <summary>
    /// Upper bound on the object's size in bytes; -1 (the default) means no limit beyond the
    /// sbspace's. Part of the create spec, like <see cref="EstimatedSize"/>.
    /// </summary>
    public long MaxBytes
    {
        get => SqliLobSpec.GetMaxBytes(Spec);
        set => SqliLobSpec.SetMaxBytes(SpecToChange, value);
    }

    /// <summary>
    /// The sbspace to create the object in; empty (the default) means the server's
    /// <c>SBSPACENAME</c>. Part of the create spec, like <see cref="EstimatedSize"/>; a name the
    /// server does not know fails <see cref="Open"/>. A table/column given to the constructor (or
    /// to <see cref="IfxConnection.GetIfxBlob(string, string)"/>) is applied on top of this, so the
    /// column's own <c>PUT ... IN (...)</c> wins.
    /// </summary>
    public string SBSpace
    {
        get => SqliLobSpec.GetSbspace(Spec);
        set => SqliLobSpec.SetSbspace(SpecToChange, value);
    }

    // ---- status, read from the server (IFX_LO_STAT) on every access; the object must be open ----

    /// <summary>
    /// How many rows reference the object. 0 for one no row points at yet -- an object created by
    /// <see cref="Open"/> and not yet stored, which is the orphan a rollback or a delete leaves.
    /// </summary>
    public int ReferenceCount => StatInt32(SqliLobStat.ReferenceCountOffset);

    /// <summary>
    /// Time of the last access, in Unix seconds, as the server records it. Measured on IDS 14.10:
    /// it moves on reads and writes and when rows come to reference the object -- with or without
    /// <see cref="IfxSmartLOBCreateTimeFlags.KeepAccessTime"/>.
    /// </summary>
    public int LastAccessTime => StatInt32(SqliLobStat.LastAccessTimeOffset);

    /// <summary>
    /// Time of the last change of the object's status, in Unix seconds: a write, or a change of
    /// <see cref="ReferenceCount"/> as rows come to point at it or stop doing so.
    /// </summary>
    public int LastChangeTime => StatInt32(SqliLobStat.LastStatusTimeOffset);

    /// <summary>Time of the last write to the object's contents, in Unix seconds.</summary>
    public int LastModificationTime => StatInt32(SqliLobStat.LastModificationTimeOffset);

    private int StatInt32(int offset) => SqliLobStat.GetInt32(Session.LobStat(RequireOpen()), offset);

    /// <summary>The total size of the open smart LOB in bytes.</summary>
    public long Size => Session.LobSize(RequireOpen());

    /// <summary>The current read/write cursor position.</summary>
    public long Position
    {
        get => Pos;
        set { Session.LobSeekTo(RequireOpen(), value, (int)IfxSmartLOBWhence.Begin); Pos = value; }
    }

    /// <summary>
    /// Opens the smart LOB for access -- and, for an object that does not exist yet (from
    /// <see cref="IfxConnection.GetIfxBlob()"/> or the creating constructors), first creates it
    /// on the server from the create spec: <see cref="SBSpace"/>, <see cref="ExtentSize"/>,
    /// <see cref="MaxBytes"/>, <see cref="EstimatedSize"/>, <see cref="Flags"/>, then the
    /// table/column's storage settings on top. Opening an object that is already open does nothing.
    /// </summary>
    public void Open(IfxSmartLOBOpenMode mode)
    {
        if (LocatorBytes is null)
        {
            if (Connection is null)
                throw new InvalidOperationException("Cannot open a NULL smart LOB.");
            Create(mode);
            return;
        }
        if (IsOpen)
            return;
        Fd = Session.LobOpen(LocatorBytes, (int)mode);
        Pos = 0;
    }

    // ifx_lo_create with this object's spec; the server leaves the new object open in `mode`.
    // A table/column is resolved last, with the spec as the template, which is the order
    // Informix.Net.Core's CreateSmartLOB uses (GetDefaultLOSpec, then GetColumnSpecificLOSpec).
    private void Create(IfxSmartLOBOpenMode mode)
    {
        SqliSession session = Session;
        byte[] spec = (byte[])Spec.Clone();
        if (_column is not null)
            spec = session.ResolveColumnLobSpec(_column, spec);
        (int fd, byte[] locator) = session.LobCreate(spec, (int)mode);
        _spec = null;              // the object exists: the getters now ask the server what it holds
        _specFromServer = false;
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
        LocatorBytes is not null ? new IfxSmartLOBLocator(LocatorBytes)
        : IsPendingCreate ? throw new InvalidOperationException(NotCreatedYet)
        : throw new InvalidOperationException("A NULL smart LOB has no locator.");

    internal const string NotCreatedYet =
        "This smart LOB has not been created yet: Open() creates it on the server "
        + "(GetIfxBlob()/GetIfxClob() and the (connection) and (connection, table, column) constructors only prepare it).";

    // What IfxParameterBinder binds for a smart LOB passed as a parameter value: the raw
    // locator, or null for a NULL object (which binds a typed SQL NULL). Unlike GetLocator()
    // this does not throw for a NULL object and does not copy, since the binder only reads it.
    internal byte[]? LocatorForBind => LocatorBytes;

    // Which Informix opaque type the locator belongs to, for the SENDRECV bind's extended
    // type name ("clob"/"blob"). Set by the concrete class, not guessed from the locator:
    // the 72-byte ifx_lo_t does not say which it is.
    internal abstract bool IsClobObject { get; }

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

    private int RequireOpen() =>
        IsOpen ? Fd
        : IsPendingCreate ? throw new InvalidOperationException(NotCreatedYet)
        : throw new InvalidOperationException("The smart LOB is not open.");
}

/// <summary>An Informix BLOB (binary smart large object).</summary>
public sealed class IfxBlob : IfxSmartLOB
{
    /// <summary>A SQL NULL BLOB.</summary>
    public static readonly IfxBlob Null = new();

    private IfxBlob() { }

    internal override bool IsClobObject => false;

    public IfxBlob(IfxConnection connection) : base(connection) { }

    public IfxBlob(IfxConnection connection, IfxSmartLOBLocator locator) : base(connection, locator) { }

    /// <summary>
    /// A new object to be created by <see cref="IfxSmartLOB.Open"/> with the storage settings of
    /// <paramref name="table"/>.<paramref name="column"/> (its sbspace and other column-level
    /// attributes, which the server looks up at that point).
    /// </summary>
    /// <param name="connection">The connection to create the object on.</param>
    /// <param name="table">The table holding the column; give both or neither.</param>
    /// <param name="column">The CLOB/BLOB column whose storage clause the new object should follow.</param>
    public IfxBlob(IfxConnection connection, string table, string column) : base(connection, table, column) { }

    /// <summary>An existing object, by its locator; the table and column are recorded but cannot
    /// change where an object that already exists is stored.</summary>
    /// <param name="connection">The connection the object is read and written through.</param>
    /// <param name="locator">The existing object's locator.</param>
    /// <param name="table">The table holding the column; give both or neither.</param>
    /// <param name="column">The CLOB/BLOB column the object belongs to.</param>
    public IfxBlob(IfxConnection connection, IfxSmartLOBLocator locator, string table, string column)
        : base(connection, locator, table, column) { }

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

    internal override bool IsClobObject => true;

    public IfxClob(IfxConnection connection) : base(connection) { }

    public IfxClob(IfxConnection connection, IfxSmartLOBLocator locator) : base(connection, locator) { }

    /// <summary>
    /// A new object to be created by <see cref="IfxSmartLOB.Open"/> with the storage settings of
    /// <paramref name="table"/>.<paramref name="column"/> (its sbspace and other column-level
    /// attributes, which the server looks up at that point).
    /// </summary>
    /// <param name="connection">The connection to create the object on.</param>
    /// <param name="table">The table holding the column; give both or neither.</param>
    /// <param name="column">The CLOB/BLOB column whose storage clause the new object should follow.</param>
    public IfxClob(IfxConnection connection, string table, string column) : base(connection, table, column) { }

    /// <summary>An existing object, by its locator; the table and column are recorded but cannot
    /// change where an object that already exists is stored.</summary>
    /// <param name="connection">The connection the object is read and written through.</param>
    /// <param name="locator">The existing object's locator.</param>
    /// <param name="table">The table holding the column; give both or neither.</param>
    /// <param name="column">The CLOB/BLOB column the object belongs to.</param>
    public IfxClob(IfxConnection connection, IfxSmartLOBLocator locator, string table, string column)
        : base(connection, locator, table, column) { }

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
