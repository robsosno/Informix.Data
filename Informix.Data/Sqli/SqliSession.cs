using Informix.Data.Asf;
using Informix.Data.Types;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// Statement-level protocol engine for one logical session on a physical connection. It owns the state of the
/// "current" statement (id, type, columns, row buffer, cursor flags) and implements every request/reply exchange:
/// session bring-up, prepare/execute/open/fetch/close/release, batches and chains, the insert cursor, BYTE/TEXT and
/// smart-LOB transfer, FastPath calls and transactions.
///
/// Every exchange has the same shape: write messages, <see cref="Flip"/> (append SQ_EOT and flush), then
/// <see cref="ReceiveMessage"/> (dispatch replies until SQ_EOT). Public entry points take the link's gate for the whole
/// exchange so sibling sessions sharing the socket never interleave. Message layouts are described in docs/wire-protocol.md,
/// the round trips per API in docs/msg_flow.md.
/// </summary>
internal sealed class SqliSession
{
    // State shared with sibling sessions on the same socket (capabilities, caches, gate).
    private readonly SqliLink _link;
    private readonly AsfConnection _asf;
    private readonly IfxWireReader _in;
    private readonly IfxWireWriter _out;
    private readonly SqliTimeoutTimer _timeoutTimer;

    /// <summary>
    /// The client protocol-capability vector sent in the SQ_PROTOCOLS exchange,
    /// verbatim from <c>IfxSqliConnect.clientProtocols</c>. It advertises which
    /// wire features this client understands; the server replies with its own
    /// vector (including the PAM-required bit).
    // The extended form appends 0x06 to ask for the Informix 15 large-row bits (69 and 70).
    /// </summary>
    private static readonly byte[] ClientProtocols =
        [0xff, 0xfc, 0x7f, 0xfc, 0x3c, 0x8c, 0xaa, 0x97];

    /// <summary>
    /// The nine-byte vector client sends. The extra byte
    /// <c>0x06</c> = <c>0x04 | 0x02</c> asks for exactly the two Informix 15 wire
    /// formats decoded in <see cref="SqliCapabilities.IsLargeTupleSize"/> and
    /// <see cref="SqliCapabilities.IsLongRowId"/>; the server echoes back the subset
    /// it grants. Sent only when <see cref="RequestLargeRowFormats"/> is set.
    /// </summary>
    private static readonly byte[] ClientProtocolsExtended =
        [0xff, 0xfc, 0x7f, 0xfc, 0x3c, 0x8c, 0xaa, 0x97, 0x06];

    /// <summary>Creates the first (primary) session on a freshly-established physical
    /// connection — mints its own <see cref="SqliLink"/>.</summary>
    public SqliSession(AsfConnection asf) : this(new SqliLink(asf ?? throw new ArgumentNullException(nameof(asf))))
    {
    }

    /// <summary>Creates an additional session sharing <paramref name="link"/>'s socket and
    /// connection-wide state with every other session already on it.
    /// The new session starts with no current statement and no open cursor; everything
    /// per-statement (tuple buffer, statement id, blob cache, …) is its own.</summary>
    internal SqliSession(SqliLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        _asf = link.Asf;
        _in = link.Asf.Reader;
        _out = link.Asf.Writer;
        _timeoutTimer = new SqliTimeoutTimer(_asf);
        _link.Register(this);
    }

    /// <summary>The connection-wide state this session shares with any siblings on the same
    /// physical connection — lets <see cref="Informix.Data.IfxConnection"/> enumerate every
    /// session on the link (to acquire a free one, or to aggregate idleness for pooling).</summary>
    internal SqliLink Link => _link;

    /// <summary>
    /// The database code set (DB_LOCALE) this session encodes/decodes text with — the same
    /// encoding the wire reader/writer use, so higher layers (bind, batch, bulk load, CLOB)
    /// stay consistent with what travels on the wire.
    /// </summary>
    public System.Text.Encoding DbEncoding => _out.DbEncoding;

    /// <summary>The server's protocol-capability reply from the SQ_PROTOCOLS exchange.
    /// Link-wide: every session on the same connection negotiated the same handshake.</summary>
    public byte[] ServerProtocol => _link.ServerProtocol;

    /// <summary>Negotiated server capabilities; populated by <see cref="BringUp"/>. Link-wide.</summary>
    public SqliCapabilities Capabilities => _link.Capabilities;

    /// <summary>The SQLI-level server version string (distinct from the ASF handshake version).
    /// Link-wide.</summary>
    public string ServerVersion => _link.ServerVersion;

    // ---- reply state captured by the message handlers (mirrors IfxSqli fields) ----
    public short Warnings { get; private set; }
    public int RowsProcessed { get; private set; }
    public int SerialInserted { get; private set; }
    public short StatementType { get; private set; }
    public long Serial8Inserted { get; private set; }
    public long BigSerialInserted { get; private set; }

    // First SQ_ERR of the reply being read. It is thrown only after the reply has been drained to SQ_EOT, which is what
    // keeps the connection usable after a server error.
    private IfxException? _pending;
    private List<int>? _batchCounts; // set only while ExecuteBatch collects per-row DONE counts
    private List<IfxError>? _diagnostics; // MI warnings from the round trip being received

    /// <summary>
    /// The server-supplied diagnostics (SQ_ERR with sqlcode 0 — the MI/DataBlade warning
    /// form) collected during the last <see cref="ReceiveMessage"/>. Unlike
    /// <see cref="Warnings"/>, which is only the SQLWARN bit word, these carry real text
    /// and a SQLSTATE. Drained by <see cref="IfxCommand"/> into <c>InfoMessage</c>.
    /// </summary>
    public IReadOnlyList<IfxError> Diagnostics => (IReadOnlyList<IfxError>?)_diagnostics ?? [];

    private void AddDiagnostic(IfxError error) => (_diagnostics ??= []).Add(error);

    /// <summary>SQ_ERR's MI (DataBlade/UDR) diagnostic carrier — see <c>ReceiveError</c>.</summary>
    private const short MiDiagnosticSqlCode = -937;

    // ---- statement / cursor state (mirrors statementID, desc, the tuple buffer) ----
    public short StatementId { get; private set; } = -1;

    /// <summary>The described result columns of the current statement (empty for non-queries).</summary>
    public IReadOnlyList<IfxColumnMetadata> Columns => _columns;

    /// <summary>The fixed tuple byte size from the describe reply.</summary>
    /// <summary>
    /// Row width from the descriptor. An int rather than a short because with
    /// <see cref="SqliCapabilities.IsLargeTupleSize"/> the server sends it as a
    /// 4-byte value; without it the range is still that of the original short.
    /// </summary>
    public int TupleSize { get; private set; }

    private readonly List<IfxColumnMetadata> _columns = [];
    private bool _rowNeedsWalk;          // any var-length VARCHAR column present
    private bool _rowHasUnsupported;     // LVARCHAR/UDT/BLOB or DISTINCT column present
    // Cursor names are generated from the link's shared sequence (see SqliLink.NextCursorSequence),
    // not a per-session counter -- two sessions each counting from zero would collide.

    // Tuple buffer and row cursor (port of IfxRowColumn.tuple + offsetVector bookkeeping),
    // extracted to SqliTupleBuffer -- see its remarks for the ownership boundary.
    private readonly SqliTupleBuffer _tupleBuffer = new();
    // The per-statement fetch-buffer override (IfxCommand.FetchBufferSize /
    // Statement.fetchBufferSize) active for the open cursor; 0 = use the default
    // sizing. Set by OpenCursor/OpenScrollCursor and reused by every FetchMore.
    private int _activeFetchBufferSize;
    // The server auto-closed the cursor (SQ_CLOSE arrived inside a fetch reply because the last rows were delivered),
    // so no explicit SQ_CLOSE may be sent later.
    private bool _closeReceived;
    // Whether the currently-open cursor was actually opened WITH HOLD (see PrepareCursorOpen) --
    // live-verified against IDS 14.10 (2026-08-03, testlog): a hold cursor survives commit AND
    // rollback identically; a non-hold cursor dies on both. Read only by
    // MarkNonHoldCursorClosedByTransactionEnd, which the link broadcasts to every session on the
    // connection when a transaction ends (see SqliSession.ExecuteCommit/ExecuteRollback).
    private bool _cursorIsHold;
    // Set when a transaction end (not this session's own CloseCursor/end-of-rows) closed the
    // cursor out from under an in-progress read -- distinguishes "there is nothing further to
    // read" from "the server discarded this cursor while you weren't looking", so NextRow can
    // throw a clear diagnostic instead of just quietly reporting no more rows.
    private bool _closedByTransactionEnd;
    // True until a fetch has come back empty, i.e. until the end of the data has been seen. While set, an exhausted
    // buffer means "ask the server for more" (unless the server already auto-closed the cursor).
    private bool _first;

    // ---- simple-large-object (BYTE/TEXT) fetch state ----
    // The in-tuple blob descriptor is a fixed-size struct (IfxBlob() allocates
    // new byte[56], or new byte[68] once isLongRowId is granted); its data is
    // retrieved lazily with a separate SQ_FETCHBLOB round trip whose SQ_BLOB reply
    // frames land in _blobBuffer. Only the overall size and the tb_size offset move
    // between the two layouts — tb_start (8), tb_end (12) and tb_flags (38) do not.
    internal const int BlobDescriptorSize = 56;
    internal const int BlobDescriptorSizeLong = 68;
    internal const int BlobSizeOffset = 16;
    internal const int BlobSizeOffsetLong = 32;

    /// <summary>Width of the in-tuple BYTE/TEXT descriptor for this session's negotiated layout.</summary>
    internal int BlobDescriptorWidth =>
        Capabilities.IsLongRowId ? BlobDescriptorSizeLong : BlobDescriptorSize;
    private const short BlobIsNull = 1; // IfxBlob.BLOBISNULL (tb_flags value for a NULL blob)
    private byte[]? _blobBuffer;
    private int _blobWriteOffset;
    // Per-row cache of already-fetched blob columns so repeated GetValue/GetBytes
    // calls on one row do not re-issue the SQ_FETCHBLOB round trip (mirrors the
    // IfxBlob.isBlobLoaded() guard in IfxResultSet.blobCheck).
    private readonly Dictionary<int, object> _blobRowCache = [];
    private int _blobCacheRow = -1;

    /// <summary>True while a server-side cursor is open on this session.</summary>
    public bool CursorOpen { get; private set; }

    /// <summary>
    /// True from a successful <see cref="OpenCursor"/>/<see cref="OpenScrollCursor"/> until the
    /// reader that owns the result set calls <see cref="CloseCursor"/> — whether or not the
    /// server-side cursor still exists. It differs from <see cref="CursorOpen"/> in exactly one
    /// case: a commit or rollback closed a non-hold cursor under a reader that is still alive
    /// (<see cref="MarkNonHoldCursorClosedByTransactionEnd"/>). That reader keeps serving the
    /// rows already in this session's tuple buffer and will still end its statement when it is
    /// disposed, so until then the session must neither be handed to another statement (which
    /// would wipe the buffer and silently cut the reader's result set short) nor go back to
    /// the pool (where the stale reader's dispose would act on someone else's connection).
    /// </summary>
    public bool HasAttachedReader { get; private set; }

    /// <summary>True while a server-side insert cursor (SQ_PUT protocol) is open.</summary>
    public bool InsertCursorOpen { get; private set; }

    /// <summary>
    /// Whether the open result set produced at least one row. Settles once the
    /// first tuple buffer is primed by <see cref="OpenCursor"/> (an empty first
    /// fetch means the query matched no rows).
    /// </summary>
    public bool HasRows { get; private set; }

    // ---- fetch tuning (connection-level knobs + measurement counters) ----

    /// <summary>
    /// Connection-level default fetch-buffer size in bytes (Informix
    /// <c>FET_BUF_SIZE</c> / <c>conn.fetchBufSize</c>); 0 leaves the classic
    /// tuple-size-derived default (4 KB / 8 KB) in force. A per-statement
    /// override always wins over this.
    /// As in ESQL/C, <c>FET_BUF_SIZE</c> is not fetch-only: it also sizes the
    /// insert-cursor buffer (see <see cref="InsertBufferSize"/>). Link-wide — shared
    /// by every session on this connection, exactly as <c>IfxSqli</c> reads
    /// <c>this.conn.fetchBufSize</c> live rather than caching its own copy.
    /// </summary>
    public int ConnectionFetchBufferSize
    {
        get => _link.ConnectionFetchBufferSize;
        set => _link.ConnectionFetchBufferSize = value;
    }

    /// <summary>
    /// Enables OPTOFC (optimize-open-fetch-close): the first tuple buffer is
    /// requested in the same PDU as SQ_OPEN even for variable-length rows,
    /// saving the extra open-reply round trip. Applies to prepared SELECTs and
    /// EXECUTE PROCEDURE cursors.
    /// </summary>
    public bool Optofc
    {
        get => _link.Optofc;
        set => _link.Optofc = value;
    }

    /// <summary>
    /// Trims trailing ASCII space (0x20) characters from decoded CHAR/NCHAR values.
    /// Off by default: with it off, CHAR/NCHAR decode is byte-identical to before this
    /// property existed. VARCHAR/NVARCHAR/LVARCHAR are never trimmed. Link-wide.
    /// </summary>
    public bool TrimTrailingSpaces
    {
        get => _link.TrimTrailingSpaces;
        set => _link.TrimTrailingSpaces = value;
    }

    /// <summary>
    /// When set, <see cref="Prepare"/> rejects any statement whose SQ_DESCRIBE reply is
    /// not a plain SELECT (<see cref="SqliStatementType.Select"/>) — see
    /// <see cref="SelectOnlyViolationSqlCode"/>. Off by default. Link-wide: this is a
    /// client-side guard on top of whatever the connecting user's own database
    /// privileges already allow, not a substitute for them — a DSN that actually must
    /// not be able to write should connect as a SELECT-only Informix user regardless
    /// of this flag.
    /// </summary>
    public bool SelectOnly
    {
        get => _link.SelectOnly;
        set => _link.SelectOnly = value;
    }

    /// <summary>
    /// Asks the server, during the SQ_PROTOCOLS exchange, for the Informix 15
    /// large-row wire formats (4-byte tuple size, 8-byte row ids, 68-byte blob
    /// descriptor) by sending the ninth protocol byte. Off by default, so the
    /// handshake is byte-identical to a driver build without this property.
    ///
    /// <para>Must be set before <see cref="BringUp"/>. Enabling it is only a
    /// request: the negotiated formats follow the server's reply, so a server that
    /// does not grant the bits leaves every frame unchanged. The reply-side
    /// handling is always active and does not depend on this flag.</para>
    /// </summary>
    public bool RequestLargeRowFormats
    {
        get => _link.RequestLargeRowFormats;
        init => _link.RequestLargeRowFormats = value;
    }

    /// <summary>
    /// Count of SQ_NFETCH messages sent by this session (both the cursor-priming
    /// fetch and every <see cref="FetchMore"/>). A measurement aid: fewer fetches
    /// for the same scan means fewer server round trips. Reset with
    /// <see cref="ResetFetchStats"/>. Per-session — a sibling session's fetches
    /// don't count toward this one's.
    /// </summary>
    public long FetchMessagesSent { get; private set; }

    /// <summary>
    /// Count of request PDUs flushed to the server on this connection (each
    /// <see cref="Flip"/> — one client→server→client round trip), shared across every
    /// session on the link since they all ride the same socket. Reset with
    /// <see cref="ResetFetchStats"/>.
    /// </summary>
    public long RequestRoundTrips => _link.RequestRoundTrips;

    /// <summary>Zeroes <see cref="FetchMessagesSent"/> and the link's <see cref="RequestRoundTrips"/>
    /// (and, when the prepared-statement cache is on, <see cref="PreparedStatementCacheHits"/>/
    /// <see cref="PreparedStatementCacheMisses"/>).</summary>
    public void ResetFetchStats()
    {
        FetchMessagesSent = 0;
        _link.ResetRoundTrips();
        StatementCache?.ResetStats();
    }

    // ---- prepared-statement cache ----

    /// <summary>
    /// Size of the connection-wide prepared-statement cache (connection-string <c>Prepared
    /// Statement Cache Size</c>); 0 (the default) turns caching off. Link-wide — shared by
    /// every session on this connection.
    /// </summary>
    public int PreparedStatementCacheSize
    {
        get => _link.PreparedStatementCacheSize;
        set => _link.PreparedStatementCacheSize = value;
    }

    private SqliStatementCache? StatementCache => _link.StatementCache;

    /// <summary>Prepared-statement cache hits since the last <see cref="ResetFetchStats"/> —
    /// a checkout that found and reactivated an already-prepared statement.</summary>
    public long PreparedStatementCacheHits => StatementCache?.Hits ?? 0;

    /// <summary>Prepared-statement cache misses since the last <see cref="ResetFetchStats"/> —
    /// a checkout that found nothing and fell through to a real SQ_PREPARE.</summary>
    public long PreparedStatementCacheMisses => StatementCache?.Misses ?? 0;

    /// <summary>
    /// Prepares <paramref name="sql"/> for execution, consulting the prepared-statement
    /// cache first when <see cref="PreparedStatementCacheSize"/> is on: a cache hit
    /// reactivates the cached statement (<see cref="Activate"/>, no wire cost) instead of
    /// re-preparing. A miss, or caching being off, falls through to a genuine
    /// <see cref="Prepare"/>. <paramref name="scroll"/>/<paramref name="hold"/> join
    /// <paramref name="sql"/> in the cache key, since the same text opened as a scroll or
    /// hold cursor needs its own prepared statement.
    /// </summary>
    public SqliStatementHandle PrepareCached(string sql, int parameterCount, bool scroll, bool hold)
    {
        // Guards the whole checkout-then-activate-or-prepare sequence: the cache is shared by
        // every session on the link, so a sibling's concurrent checkout must not interleave
        // with this one's (Prepare/Activate re-enter the same lock harmlessly if this hits).
        using var gate = _link.Gate.EnterScope();
        if (StatementCache is { } cache)
        {
            var key = new SqliStatementCacheKey(sql, scroll, hold);
            if (cache.Checkout(key) is { } cached)
            {
                Activate(cached);
                return cached;
            }
        }
        return Prepare(sql, parameterCount);
    }

    /// <summary>
    /// Ends use of <paramref name="handle"/>: returns it to the prepared-statement cache
    /// instead of releasing it when caching is on (evicting and really releasing the LRU
    /// entry if the cache was full), or releases it for real when caching is off. Either
    /// way the session no longer considers it the current statement afterward.
    /// </summary>
    public void ReleaseOrCache(SqliStatementHandle handle, string sql, bool scroll, bool hold)
    {
        using var gate = _link.Gate.EnterScope();
        if (StatementCache is not { } cache || !_asf.IsConnected)
        {
            ReleaseStatementHandle(handle);
            return;
        }
        var key = new SqliStatementCacheKey(sql, scroll, hold);
        if (cache.Return(key, handle) is { } evicted)
            ReleaseStatementHandle(evicted);
        if (StatementId == handle.StatementId)
            DeactivateCurrent();
    }

    // Clears the session's current-statement bookkeeping without releasing anything on the
    // server -- the statement stays alive, parked in the cache; only the local "this is what
    // SQ_ID-driven ops target" pointer resets, so the session reads as idle (IsIdle) even
    // though nothing was released.
    private void DeactivateCurrent()
    {
        StatementId = -1;
        StatementType = 0;
        TupleSize = 0;
        _rowNeedsWalk = false;
        _rowHasUnsupported = false;
        _columns.Clear();
    }

    // getMaxFetchBufSize: unbounded with the 2GB-fetch/XPS capability, else the
    // classic 32767 ceiling (IfxSqliConnect.getMaxFetchBufSize).
    private int MaxFetchBufferSize =>
        Capabilities.IsXps840 || Capabilities.Is2GBFetchBufferSupported ? int.MaxValue : 32767;

    /// <summary>The classic insert-cursor buffer bound when no FET_BUF_SIZE is configured.</summary>
    internal const int DefaultInsertBufferSize = 32000;

    /// <summary>
    /// Effective insert-cursor (SQ_PUT) buffer size in bytes. Informix
    /// <c>FET_BUF_SIZE</c> is not fetch-only — ESQL/C uses the same buffer to send
    /// rows through an insert cursor, and a larger value packs more PUT rows per
    /// round trip exactly as it packs more fetched rows per reply. A configured
    /// connection-level size therefore replaces the classic ~32 KB guard here too
    /// (capped at the server's maximum buffer size).
    /// </summary>
    internal int InsertBufferSize =>
        ConnectionFetchBufferSize > 0
            ? Math.Min(ConnectionFetchBufferSize, MaxFetchBufferSize)
            : DefaultInsertBufferSize;

    /// <summary>
    /// True when the prepared statement produces a result set: a SELECT, or an
    /// EXECUTE PROCEDURE that describes output columns (port of <c>isResultSet</c>).
    /// </summary>
    public bool DescribesResultSet =>
        StatementType == (short)SqliStatementType.Select
        || (StatementType == (short)SqliStatementType.ExecProc && _columns.Count > 0);

    /// <summary>
    /// Brings the session to a usable state, in the order the live server
    /// requires for remote (PAM) connections: the SQ_PROTOCOLS capability
    /// exchange first, then PAM authentication (when a handler is supplied),
    /// then SQ_VERSION. Port of the <c>getServerVer</c> + <c>handlePAMAuthentication</c>
    /// sequence, verified against IDS 9.59 via a captured JDBC wire trace.
    /// </summary>
    public void BringUp(IIfxPamHandler? pamHandler = null)
    {
        ExecuteProtocols();
        if (pamHandler is not null)
            AuthenticatePam(pamHandler);
        ExecuteVersion();
        Capabilities.ApplyVersionString(ServerVersion);
        Capabilities.ApplyServerProtocol(ServerProtocol);
    }

    /// <summary>Exchanges the client/server protocol-capability vectors (port of <c>executeProtocols</c>).</summary>
    public void ExecuteProtocols()
    {
        using var gate = _link.Gate.EnterScope();
        byte[] vector = RequestLargeRowFormats ? ClientProtocolsExtended : ClientProtocols;
        _out.WriteInt16((short)SqliMessage.Protocols);
        _out.WriteInt16((short)vector.Length);
        _out.WritePadded(vector);
        Flip();
        ReceiveMessage();
    }

    /// <summary>Sends SQ_VERSION and reads the reply (port of <c>executeVersion</c>).</summary>
    public void ExecuteVersion()
    {
        using var gate = _link.Gate.EnterScope();
        _out.WriteInt16((short)SqliMessage.Version);
        Flip();
        ReceiveMessage();
    }

    // ===================================================================== //
    //  PAM authentication                                                   //
    // ===================================================================== //

    /// <summary>
    /// Runs the PAM challenge/response exchange on a PAM-protected port. The
    /// client opens with SQ_ACK, then answers each SQ_CHALLENGE via
    /// <paramref name="handler"/> until the server returns SQ_ACCEPT.
    /// Port of <c>IfxSqli.handlePAMAuthentication</c>.
    /// </summary>
    public void AuthenticatePam(IIfxPamHandler handler)
    {
        using var gate = _link.Gate.EnterScope();
        ArgumentNullException.ThrowIfNull(handler);

        _out.WriteInt16((short)SqliMessage.Ack);
        Flip();

        bool authorized = false;
        while (!authorized)
        {
            IfxPamChallenge? challenge = ReadPamChallenge(ref authorized);
            if (authorized || challenge is null)
                break;

            IfxPamResponse response = handler.Respond(challenge);
            if (response.TerminateConnection)
            {
                _out.WriteInt16((short)SqliMessage.Exit);
                Flip();
                break;
            }
            if (challenge.RequiresResponse)
                SendPamResponse(response);
        }

        if (!authorized)
            throw new IfxException("PAM authentication failed or was terminated by the server.", -79885);
    }

    // Port of readPAMChallengeMessage. Sets authorized=true on SQ_ACCEPT.
    private IfxPamChallenge? ReadPamChallenge(ref bool authorized)
    {
        var msg = (SqliMessage)_in.ReadInt16();
        switch (msg)
        {
            case SqliMessage.Exit:
                _out.WriteInt16((short)SqliMessage.Exit);
                Flip();
                return null;

            case SqliMessage.Accept:
                authorized = true;
                ExpectEot();
                return null;

            case SqliMessage.Challenge:
                short style = _in.ReadInt16();
                short length = _in.ReadInt16();
                byte[] raw = _in.ReadPadded(length);
                var challenge = new IfxPamChallenge(
                    IfxWireConverter.DecodeChar(raw, _in.DbEncoding), (PamMessageType)style);
                ExpectEot();
                return challenge;

            default:
                throw new IfxException($"Unexpected PAM message {(short)msg} (-408).", -408);
        }
    }

    // Port of sendPAMResponseMessage: SQ_RESPONSE + char-count + padded bytes + EOT.
    private void SendPamResponse(IfxPamResponse response)
    {
        string text = response.Response ?? "";
        if (text.Length > IfxPamResponse.MaxMessageSize)
            throw new IfxException("PAM response exceeds 512 characters.", -79886);

        // The length word counts bytes, not characters: with a multi-byte code set the two
        // differ, and a short count leaves the tail of the answer in the stream as garbage.
        byte[] encoded = IfxWireConverter.EncodeChar(text, _out.DbEncoding);
        _out.WriteInt16((short)SqliMessage.Response);
        _out.WriteInt16((short)encoded.Length);
        if (encoded.Length > 0)
            _out.WritePadded(encoded);
        Flip();
    }

    private void ExpectEot()
    {
        var msg = (SqliMessage)_in.ReadInt16();
        if (msg != SqliMessage.Eot)
            throw new IfxException($"Expected SQ_EOT but got {(short)msg} (-408).", -408);
    }

    // ===================================================================== //
    //  Statement pipeline (ports of executeCommand / executePrepare /       //
    //  sendStatementQuery / sendFetch / executeClose / executeRelease)      //
    // ===================================================================== //

    /// <summary>
    /// Executes a statement without parameters in a single round trip:
    /// SQ_COMMAND + SQ_NDESCRIBE + SQ_EXECUTE + SQ_RELEASE.
    /// The affected row count lands in <see cref="RowsProcessed"/>.
    /// </summary>
    public void ExecuteImmediate(string commandText)
    {
        using var gate = _link.Gate.EnterScope();
        ThrowIfCursorOpen();
        ClearStatementState();
        ResetInsertedSerials();
        _out.WriteInt16((short)SqliMessage.Command);
        _out.WriteInt16(0); // number of inline values
        WriteStatementText(commandText);
        _out.WriteInt16((short)SqliMessage.NDescribe);
        _out.WriteInt16((short)SqliMessage.Execute);
        _out.WriteInt16((short)SqliMessage.Release);
        Flip();
        ReceiveMessage();
        StatementId = -1; // SQ_RELEASE went with the command
    }

    /// <summary>
    /// Prepares a statement and reads its describe reply: SQ_PREPARE +
    /// SQ_NDESCRIBE + SQ_WANTDONE (port of <c>sendPrepare</c>). Populates
    /// <see cref="StatementId"/>, <see cref="StatementType"/> and
    /// <see cref="Columns"/> — which the returned <see cref="SqliStatementHandle"/>
    /// snapshots, so a caller that wants to reuse this statement later can hold
    /// onto the handle and reinstate it with <see cref="Activate"/> without
    /// re-preparing.
    ///
    /// <para>When <see cref="SelectOnly"/> is set, every statement type but
    /// <see cref="SqliStatementType.Select"/> is rejected here, before the caller ever
    /// gets a handle to execute — see <see cref="ThrowSelectOnlyViolation"/>.</para>
    /// </summary>
    public SqliStatementHandle Prepare(string commandText, int parameterCount = 0)
    {
        using var gate = _link.Gate.EnterScope();
        ThrowIfCursorOpen();
        ClearStatementState();
        _out.WriteInt16((short)SqliMessage.Prepare);
        _out.WriteInt16((short)parameterCount);
        WriteStatementText(commandText);
        _out.WriteInt16((short)SqliMessage.NDescribe);
        _out.WriteInt16((short)SqliMessage.WantDone);
        Flip();
        ReceiveMessage();
        if (SelectOnly && StatementType != (short)SqliStatementType.Select)
            ThrowSelectOnlyViolation();
        return CurrentHandle(parameterCount);
    }

    /// <summary>
    /// The SQLCODE this driver raises when <see cref="SelectOnly"/> rejects a prepared
    /// statement. Chosen to match Informix's own "invalid operation for a READ-ONLY
    /// transaction" family — and, per live testing, it is not merely a stylistic match:
    /// the server's own <c>SET TRANSACTION READ ONLY</c> (see <see cref="IfxConnectionStringBuilder.ReadOnly"/>)
    /// raises this exact SQLCODE for the same family of statements. This guard still runs
    /// purely client-side, off the statement type the server's own SQ_DESCRIBE reply
    /// already classified, independent of whether any server-side read-only transaction
    /// is active.
    /// </summary>
    internal const int SelectOnlyViolationSqlCode = -878;

    // Releases the statement Prepare just described but will never hand back to the
    // caller -- so a caller that catches this and keeps using the connection doesn't leak
    // a prepared statement on the server -- then raises the Select Only rejection. Only ever
    // called right after Prepare's ReceiveMessage, so StatementId/StatementType are still
    // the ones it just populated.
    private void ThrowSelectOnlyViolation()
    {
        string kind = Enum.IsDefined((SqliStatementType)StatementType)
            ? ((SqliStatementType)StatementType).ToString()
            : StatementType.ToString();
        ReleaseStatement();
        throw new IfxException(
            $"Statement rejected by 'Select Only': statement is {kind}, not SELECT (-878).",
            SelectOnlyViolationSqlCode);
    }

    // Snapshots the session's current-statement bookkeeping into a handle the
    // caller can stash and later reinstate via Activate.
    private SqliStatementHandle CurrentHandle(int parameterCount) => new()
    {
        StatementId = StatementId,
        StatementType = StatementType,
        Columns = _columns.ToArray(),
        TupleSize = TupleSize,
        RowNeedsWalk = _rowNeedsWalk,
        RowHasUnsupported = _rowHasUnsupported,
        ParameterCount = parameterCount,
    };

    /// <summary>
    /// Reinstates <paramref name="handle"/> as the session's current statement —
    /// pure local bookkeeping, no wire traffic: the server already tracks every
    /// prepared statement id independently of whichever one the client last had
    /// active, so switching which one <see cref="StatementId"/> etc. mirror costs
    /// nothing. Must run before any SQ_ID-driven operation (execute, open cursor)
    /// targeting a handle that is not already current. A no-op when it already is.
    /// </summary>
    public void Activate(SqliStatementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (StatementId == handle.StatementId)
            return;
        ThrowIfCursorOpen(); // a stashed handle can't preempt a live result set
        StatementId = handle.StatementId;
        StatementType = handle.StatementType;
        TupleSize = handle.TupleSize;
        _rowNeedsWalk = handle.RowNeedsWalk;
        _rowHasUnsupported = handle.RowHasUnsupported;
        _columns.Clear();
        _columns.AddRange(handle.Columns);
    }

    /// <summary>
    /// Executes a prepared non-query statement: SQ_ID + optional SQ_BIND +
    /// SQ_EXECUTE (port of <c>sendExecute</c> + <c>sendStatementExecute</c>).
    /// </summary>
    public void ExecuteStatement(IReadOnlyList<SqliBindValue>? parameters = null)
    {
        using var gate = _link.Gate.EnterScope();
        ResetInsertedSerials();
        SendStatementId();
        if (parameters is { Count: > 0 })
            SendBind(parameters);
        _out.WriteInt16((short)SqliMessage.Execute);
        Flip();
        ReceiveMessage();
    }

    /// <summary>
    /// Executes one prepared statement once per parameter set, chaining
    /// SQ_ID + (SQ_BIND + SQ_EXECUTE) for every row into a single PDU. Each row's
    /// affected count comes back as its own SQ_DONE. Mirrors the driver's ~32 KB
    /// PDU guard: a very large batch is split into several round trips.
    /// Returns the per-row affected counts.
    /// </summary>
    public int[] ExecuteBatch(IReadOnlyList<IReadOnlyList<SqliBindValue>> rows)
    {
        using var gate = _link.Gate.EnterScope();
        _batchCounts = new List<int>(rows.Count);
        try
        {
            SendStatementId();
            foreach (var row in rows)
            {
                if (row.Count > 0)
                    SendBind(row);
                _out.WriteInt16((short)SqliMessage.Execute);
                // The driver flushes and restarts the frame before it would exceed 32 KB.
                if (_out.BytesSentSinceLastEot >= 32000)
                {
                    Flip();
                    ReceiveMessage();
                    SendStatementId();
                }
            }
            Flip();
            ReceiveMessage();
            return [.. _batchCounts];
        }
        finally
        {
            _batchCounts = null;
        }
    }

    /// <summary>
    /// Executes a heterogeneous sequence of already-resolved statements — each either a
    /// parameterless immediate command (<see cref="SqliChainStepKind.Immediate"/>, the
    /// SQ_COMMAND wire shape) or a bind+execute against an already-prepared statement
    /// (<see cref="SqliChainStepKind.Execute"/>, the SQ_ID+BIND+EXECUTE wire shape) —
    /// chained into as few PDUs as the ~32 KB guard allows, instead of one round trip
    /// per statement. The <see cref="IfxBatch"/> pipelined executor.
    ///
    /// <para>Port of the chaining behavior InformixDataDbBatchPrompt.md §2's probes P1/P2/P4
    /// proved live against IDS 9.59: heterogeneous SQ_COMMAND segments and heterogeneous
    /// SQ_ID segments both chain into one PDU behind a single flush (each still gets its
    /// own SQ_DESCRIBE/SQ_DONE or SQ_DONE reply, in send order, plus an incidental trailing
    /// SQ_COST after a successful SQ_DONE that this method discards like any other
    /// diagnostic-only frame); the server does NOT need an explicit SQ_ID before the
    /// SQ_BIND/SQ_EXECUTE that immediately follows a chained SQ_PREPARE (P3) — but this
    /// method does not exploit that: <see cref="IfxBatch"/> always prepares a cold (cache-miss)
    /// statement with its own separate <see cref="Prepare"/> round trip first, so it can
    /// inspect <see cref="SqliStatementHandle.DescribesResultSet"/> before deciding how to
    /// run it — chaining a speculative prepare+bind+execute for a statement that turns out to
    /// describe a result set (a RETURNING procedure or a stray SELECT) is not wire-safe
    /// without confirming what SQ_EXECUTE against such a statement does, which P3 did not
    /// test. This still pipelines the dominant case: every already-prepared/cached statement's
    /// execute chains regardless, and a cold miss costs exactly one prepare round trip (the
    /// mission's own accepted fallback).</para>
    ///
    /// <para>On the first error (P4): the server aborts the rest of that PDU's queued
    /// statements fail-fast — nothing else arrives for them, not even an EOT-terminated empty
    /// reply — so this method stops sending further segments too. <see cref="SqliChainOutcome.FailedIndex"/>
    /// names the failing step; every step from there on (including ones never even sent) gets
    /// a null result in <see cref="SqliChainOutcome.Results"/>.</para>
    /// </summary>
    internal SqliChainOutcome ExecuteChain(IReadOnlyList<SqliChainStep> steps)
    {
        using var gate = _link.Gate.EnterScope();
        ThrowIfCursorOpen();
        var results = new SqliChainStepResult?[steps.Count];
        int failedIndex = -1;
        IfxException? error = null;
        ResetInsertedSerials();

        int i = 0;
        while (i < steps.Count && error is null)
        {
            int segmentStart = i;
            do
            {
                WriteChainStep(steps[i]);
                i++;
            }
            while (i < steps.Count && _out.BytesSentSinceLastEot < 32000);
            Flip();

            // Read every reply message for this PDU up to its closing EOT, attributing
            // each Done/Err to the next step in send order. A successful Done can (and,
            // empirically, always does) have a trailing SQ_COST that carries no
            // information this method needs -- that includes after the LAST step's Done,
            // so this cannot stop as soon as it has seen one terminal per step: it must
            // keep dispatching (Cost, InsertDone, whatever else) until EOT actually
            // arrives. Getting this wrong desyncs every read for the rest of the
            // connection, not just this call.
            int j = segmentStart;
            while (true)
            {
                var msg = (SqliMessage)_in.ReadInt16();
                bool keepGoing = Dispatch(msg);
                // Remember only the first error and the step it belongs to.
                if (_pending is not null && error is null)
                {
                    error = _pending;
                    failedIndex = j;
                }
                _pending = null;
                // Each step ends with exactly one SQ_DONE (success) or SQ_ERR (failure); count them to attribute results to steps.
                if (msg is SqliMessage.Done or SqliMessage.Err)
                {
                    // Fail-fast (P4): once one statement in this PDU has failed, the
                    // server sends nothing for any statement queued after it -- so a
                    // Done/Err seen after `error` is already set cannot belong to a
                    // later step (there won't be one); it would only be possible if
                    // this Err is the very failure being recorded, already handled above.
                    if (error is null)
                    {
                        results[j] = new SqliChainStepResult
                        {
                            RowsProcessed = RowsProcessed,
                            SerialInserted = SerialInserted,
                            Serial8Inserted = Serial8Inserted,
                            BigSerialInserted = BigSerialInserted,
                        };
                        ResetInsertedSerials();
                    }
                    j++;
                }
                if (!keepGoing || msg == SqliMessage.Eot)
                    break;
            }
        }

        DeactivateCurrent(); // no single statement is "current" after a heterogeneous chain
        return new SqliChainOutcome { Results = results, FailedIndex = failedIndex, Error = error };
    }

    // Inline statement text for parameterless steps, SQ_ID + bind + execute against a prepared id otherwise.
    private void WriteChainStep(SqliChainStep step)
    {
        if (step.Kind == SqliChainStepKind.Immediate)
        {
            _out.WriteInt16((short)SqliMessage.Command);
            _out.WriteInt16(0); // number of inline values
            WriteStatementText(step.Sql!);
            _out.WriteInt16((short)SqliMessage.NDescribe);
            _out.WriteInt16((short)SqliMessage.Execute);
            _out.WriteInt16((short)SqliMessage.Release);
        }
        else
        {
            SendStatementId(step.StatementId);
            if (step.Binds.Count > 0)
                SendBind(step.Binds);
            _out.WriteInt16((short)SqliMessage.Execute);
        }
    }

    // ===================================================================== //
    //  Insert cursor (the real SQ_PUT protocol — port of the ESQL/C         //
    //  DECLARE ... INSERT CURSOR / PUT / CLOSE sequence, reverse-engineered //
    //  from a dbaccess LOAD SQLIDEBUG trace on IDS 9.59)                    //
    // ===================================================================== //

    /// <summary>
    /// Declares and opens an insert cursor over the prepared INSERT: SQ_ID +
    /// SQ_CURNAME + [SQ_HOLD] + SQ_OPEN. Unlike a fetch cursor the open reply is a
    /// bare SQ_EOT (no SQ_DONE). Opened WITH HOLD by default so the cursor survives
    /// the per-batch COMMIT WORK a bulk load issues between flushes.
    /// </summary>
    public void OpenInsertCursor(bool hold = true)
    {
        using var gate = _link.Gate.EnterScope();
        if (StatementType != (short)SqliStatementType.Insert)
            throw new InvalidOperationException("An insert cursor requires a prepared INSERT statement.");
        if (InsertCursorOpen)
            throw new InvalidOperationException("An insert cursor is already open on this connection.");
        ThrowIfCursorOpen();

        SendStatementId();
        _out.WriteInt16((short)SqliMessage.CurName);
        _out.WriteChar($"ifxnet_i{_link.NextCursorSequence()}");
        if (hold)
            _out.WriteInt16((short)SqliMessage.Hold);
        _out.WriteInt16((short)SqliMessage.Open);
        Flip();
        ReceiveMessage();
        InsertCursorOpen = true;
    }

    /// <summary>
    /// Sends buffered rows through the insert cursor: SQ_ID + SQ_PUT +
    /// rowSize:int32 + nrows:int16 + each row image (WritePadded to even), split
    /// into several SQ_PUT PDUs if the batch would exceed the insert buffer —
    /// <see cref="InsertBufferSize"/>, i.e. FET_BUF_SIZE when configured, else the
    /// classic ~32 KB guard. Each row image is the fixed-width tuple format the
    /// server describes for the INSERT (see <see cref="SqliInsertTuple"/>). Returns
    /// the total rows the server acknowledged across the chunks.
    /// </summary>
    public int PutRows(IReadOnlyList<byte[]> tuples, int rowSize)
    {
        using var gate = _link.Gate.EnterScope();
        if (!InsertCursorOpen)
            throw new InvalidOperationException("No insert cursor is open on this connection.");
        if (tuples.Count == 0)
            return 0;

        int bufferSize = InsertBufferSize;
        int total = 0;
        int i = 0;
        while (i < tuples.Count)
        {
            // Fill one PDU up to the insert buffer (always at least one row). The
            // per-PDU row count is also bounded by the SQ_PUT nrows word being int16.
            int start = i;
            int bytes = 0;
            do
            {
                bytes += (tuples[i].Length + 1) & ~1; // even-padded footprint
                i++;
            }
            while (i < tuples.Count && i - start < short.MaxValue
                   && bytes + ((tuples[i].Length + 1) & ~1) <= bufferSize);

            SendStatementId();
            _out.WriteInt16((short)SqliMessage.Put);
            _out.WriteInt32(rowSize);
            _out.WriteInt16((short)(i - start));
            for (int j = start; j < i; j++)
                _out.WritePadded(tuples[j]);
            Flip();
            ReceiveMessage();       // SQ_INSERTDONE + SQ_DONE(#rows) + SQ_COST + EOT
            total += RowsProcessed; // the DONE reports this PUT's row count
        }
        return total;
    }

    /// <summary>Closes the open insert cursor: SQ_ID + SQ_CLOSE. The SQ_ID prefix is
    /// required — after a SQ_PUTERR the server no longer tracks a "current cursor", so a
    /// bare SQ_CLOSE blocks forever; the ESQL/C reference always identifies the cursor.</summary>
    public void CloseInsertCursor()
    {
        using var gate = _link.Gate.EnterScope();
        if (!InsertCursorOpen)
            return;
        SendStatementId();
        _out.WriteInt16((short)SqliMessage.Close);
        Flip();
        ReceiveMessage();
        InsertCursorOpen = false;
    }

    /// <summary>Synchronizes the tracked cursor state after the <i>server</i> has already
    /// discarded a non-HOLD insert cursor as a side effect of COMMIT or ROLLBACK — no wire
    /// traffic, since sending SQ_CLOSE for a cursor the server already dropped would itself
    /// fail. After this, <see cref="CloseInsertCursor"/> correctly no-ops.</summary>
    internal void MarkInsertCursorClosed() => InsertCursorOpen = false;

    // Shared preamble for OpenCursor/OpenScrollCursor: validates state, resets tuples,
    // emits SQ_ID + SQ_CURNAME + optional SQ_BIND + optional SQ_SCROLL + optional SQ_HOLD + SQ_OPEN.
    private void PrepareCursorOpen(IReadOnlyList<SqliBindValue>? parameters, bool hold, int fetchBufferSize, bool scroll)
    {
        if (!DescribesResultSet)
            throw new IfxException("The prepared statement does not return rows (-79750).", -79750);
        if (_rowHasUnsupported)
            throw new NotSupportedException(
                scroll
                    ? "The result set contains LVARCHAR/UDT/BLOB or DISTINCT columns; scrolling those is not ported yet."
                    : "The result set contains LVARCHAR/UDT/BLOB or DISTINCT columns; fetching those is not ported yet.");
        ThrowIfCursorOpen();

        ResetTuples();
        _closeReceived = false;
        _closedByTransactionEnd = false;
        _first = true;
        _activeFetchBufferSize = fetchBufferSize;
        // Mirrors the actual SQ_HOLD condition just below, not the caller's raw request: a
        // hold=true request on an ExecProc statement never sends SQ_HOLD, so the server does
        // not treat it as a hold cursor either -- MarkNonHoldCursorClosedByTransactionEnd must
        // agree with what was really negotiated on the wire.
        _cursorIsHold = hold && StatementType != (short)SqliStatementType.ExecProc;

        SendStatementId();
        _out.WriteInt16((short)SqliMessage.CurName);
        _out.WriteChar(scroll ? $"ifxnet_s{_link.NextCursorSequence()}" : $"ifxnet_c{_link.NextCursorSequence()}");
        if (parameters is { Count: > 0 })
            SendBind(parameters);
        if (scroll)
            _out.WriteInt16((short)SqliMessage.Scroll);
        if (_cursorIsHold)
            _out.WriteInt16((short)SqliMessage.Hold);
        _out.WriteInt16((short)SqliMessage.Open);
    }

    /// <summary>
    /// Opens a forward-only cursor over the prepared SELECT and primes the
    /// first tuple buffer: SQ_ID + SQ_CURNAME + optional SQ_BIND + SQ_OPEN +
    /// SQ_NFETCH. When the row has var-length columns the
    /// open reply is consumed first and SQ_RET_TYPE precedes the fetch.
    /// </summary>
    /// <param name="singleRow">Set for <see cref="System.Data.CommandBehavior.SingleRow"/>: nothing
    /// past the first tuple of this buffer can ever be surfaced, so SQ_CLOSE rides along in the
    /// same PDU as the fetch and closing the reader costs no round trip. Ignored when a column's
    /// value is only retrieved after the tuple arrives (see <see cref="RowHasDeferredData"/>).</param>
    public void OpenCursor(IReadOnlyList<SqliBindValue>? parameters = null, bool hold = false, int fetchBufferSize = 0,
        bool singleRow = false)
    {
        using var gate = _link.Gate.EnterScope();
        PrepareCursorOpen(parameters, hold, fetchBufferSize, scroll: false);

        // OPTOFC (optimize-open-fetch-close): the driver piggybacks
        // SQ_ID + [SQ_RET_TYPE] + SQ_NFETCH in the very same PDU as SQ_OPEN, so the
        // first tuple buffer rides back with the open reply — one round trip instead
        // of two. EXECUTE PROCEDURE(56)
        // is included here as well because IDS 9.61 accepts the merged PDU for it verbatim
        // (live-verified : `execute procedure xxxx(?,?,?,?)` returning a
        // VARCHAR — i.e. a walked row — drops from 2 round trips to 1 with identical results).
        bool optofc = Optofc && StatementType is (short)SqliStatementType.Select or (short)SqliStatementType.ExecProc;
        if (_rowNeedsWalk && !optofc)
        {
            // Var-length rows without OPTOFC: the server answers the open before the
            // typed fetch, so consume that reply first.
            Flip();
            ReceiveMessage();
        }
        SendFetch(sendRetType: _rowNeedsWalk);

        // SingleRow: nothing after the first tuple of this buffer can ever be read, so
        // SQ_CLOSE rides along in the same PDU as the fetch. The server executes one
        // request's opcodes in order and answers them all in a single reply stream
        // terminated by one SQ_EOT — exactly how SQ_OPEN and SQ_NFETCH already share a
        // PDU here — so the close costs no round trip of its own and CloseCursor() has
        // nothing left to send when the reader is disposed.
        bool closeWithFetch = singleRow && !RowHasDeferredData;
        if (closeWithFetch)
        {
            SendStatementId();
            _out.WriteInt16((short)SqliMessage.Close);
        }

        Flip();
        ReceiveMessage();
        if (closeWithFetch)
        {
            // The cursor stays "open" as far as the session is concerned: it still owns
            // the tuple buffer the reader is decoding from, so ThrowIfCursorOpen must
            // keep rejecting a second command and IsIdle must keep the connection out of
            // the pool until the reader is disposed. What the flag below records is that
            // the server side is already gone -- the same state an end-of-rows auto-close
            // leaves behind -- which is what makes CloseCursor() and NextRow() wire-free.
            // (The reply normally carries SQ_CLOSE, which sets this too; we asked for the
            // close, so we do not depend on the acknowledgement coming back.)
            _closeReceived = true;
        }
        CursorOpen = true;
        HasAttachedReader = true;
        HasRows = _tupleBuffer.Count > 0; // empty first fetch => the query matched no rows
    }

    // True when some column's value only arrives after the tuple does: BYTE/TEXT
    // descriptors are resolved by a follow-up SQ_FETCHBLOB, smart large objects by
    // opening their locator. Both need the cursor to outlive the fetch, so the
    // fetch+close piggyback above cannot be used. Mirrors the deferred branches of
    // DecodeColumn -- keep the two type lists in step.
    private bool RowHasDeferredData
    {
        get
        {
            foreach (var col in _columns)
            {
                if (col.SqliTypeCode is SqliTypeCode.Byte or SqliTypeCode.Text
                    or SqliTypeCode.Clob or SqliTypeCode.Blob)
                    return true;
            }
            return false;
        }
    }

    // ---- scroll cursors ----

    /// <summary>SQ_SFETCH orientation: fetch the next row (index = target row id).</summary>
    public const short ScrollNext = 1;
    /// <summary>SQ_SFETCH orientation: fetch the last row (index ignored).</summary>
    public const short ScrollLast = 4;
    /// <summary>SQ_SFETCH orientation: fetch an absolute row by 1-based id.</summary>
    public const short ScrollAbsolute = 6;

    /// <summary>
    /// The absolute (1-based) result-set position of the current row. Meaningful after a
    /// <see cref="ScrollFetch"/> positions on a row. See <see cref="SqliTupleBuffer.CurrentTupleId"/>.
    /// </summary>
    public int CurrentTupleId => _tupleBuffer.CurrentTupleId;

    /// <summary>The absolute row id of the last tuple the most recent scroll fetch delivered (0 when it delivered none).</summary>
    public int LastBufferedTupleId => _tupleBuffer.Count > 0 ? _tupleBuffer.FirstTupleId + _tupleBuffer.Count - 1 : 0;

    /// <summary>
    /// Positions on absolute row <paramref name="rowId"/> when the tuple buffer of the last scroll
    /// fetch already holds it (a NEXT fetch returns a whole buffer of rows, not one); no round trip.
    /// Like <see cref="NextRow"/> it touches only this session's own buffer, so it needs no gate.
    /// </summary>
    public bool TryPositionInBuffer(int rowId)
    {
        if (!_tupleBuffer.Contains(rowId))
            return false;
        _tupleBuffer.MoveTo(rowId);
        if (_rowNeedsWalk)
            _tupleBuffer.Walk(_columns, Capabilities);
        return true;
    }

    /// <summary>
    /// Opens a scrollable (optionally hold) cursor over the prepared SELECT.
    /// Emits SQ_SCROLL(24) — and SQ_HOLD(43) when <paramref name="hold"/> is set and
    /// the statement is not an EXECUTE PROCEDURE(56) — before SQ_OPEN(6), the scroll
    /// branch of <c>sendQuery</c>. Unlike <see cref="OpenCursor"/> it does not fetch;
    /// rows are retrieved on demand by <see cref="ScrollFetch"/>.
    /// </summary>
    public void OpenScrollCursor(bool hold = false, IReadOnlyList<SqliBindValue>? parameters = null, int fetchBufferSize = 0)
    {
        using var gate = _link.Gate.EnterScope();
        PrepareCursorOpen(parameters, hold, fetchBufferSize, scroll: true);
        Flip();
        ReceiveMessage();
        CursorOpen = true;
        HasAttachedReader = true;
    }

    /// <summary>
    /// Positioned fetch on an open scroll cursor: SQ_ID + [SQ_RETTYPE for var-length
    /// rows] + SQ_SFETCH(23) + <paramref name="direction"/>:int16 +
    /// <paramref name="index"/>:int32 + tuple-buffer-size (int32 with the 2GB-fetch/
    /// XPS capability, else int16). Positions on the first tuple of the reply and returns
    /// whether a row was returned. The server precedes the tuples with SQ_TUPID, so
    /// <see cref="CurrentTupleId"/> reports the fetched row's absolute position.
    /// </summary>
    public bool ScrollFetch(short direction, int index)
    {
        using var gate = _link.Gate.EnterScope();
        if (!CursorOpen)
            throw new InvalidOperationException("No scroll cursor is open on this connection.");

        ResetTuples();
        SendStatementId();
        if (_rowNeedsWalk)
            SendRetType();
        _out.WriteInt16((short)SqliMessage.SFetch);
        _out.WriteInt16(direction);
        _out.WriteInt32(index);
        int size = GetTupleBufferSize(_activeFetchBufferSize);
        if (Capabilities.IsXps840 || Capabilities.Is2GBFetchBufferSupported)
            _out.WriteInt32(size);
        else
            _out.WriteInt16((short)size);
        // sendScrollFetch, unlike sendFetch, does not append the fetch-array size word.
        Flip();
        ReceiveMessage();

        if (_tupleBuffer.Count <= 0)
            return false;
        _tupleBuffer.Advance(); // ResetTuples() above left CurrentRow at -1; this positions on the first (only) returned tuple
        if (_rowNeedsWalk)
            _tupleBuffer.Walk(_columns, Capabilities);
        return true;
    }

    /// <summary>
    /// Advances to the next buffered row, fetching the next tuple buffer from
    /// the server when the current one is drained (port of the forward-only
    /// branch of <c>getaRow</c>).
    ///
    /// <para>Deliberately does not take <see cref="SqliLink.Gate"/> itself: serving a row
    /// already sitting in this session's own buffer touches nothing shared, so a reader
    /// being iterated needs no lock at all — only <see cref="FetchMore"/>, the branch that
    /// actually goes back to the wire, re-enters the gate. One accepted, benign race falls
    /// out of this: a sibling session's commit/rollback (see
    /// <see cref="MarkNonHoldCursorClosedByTransactionEnd"/>) writes <see cref="CursorOpen"/>/
    /// the transaction-end flag from another thread without this method observing it under a
    /// lock. Worst case, this thread's stale read sends one real <see cref="FetchMore"/> to a
    /// cursor the server already discarded — which still surfaces as an exception from the
    /// server's own error reply, just not this class's friendlier diagnostic. Not a
    /// memory-safety issue (a <c>bool</c> read/write is atomic), just a marginally plainer
    /// error message in an already-narrow window.</para>
    /// </summary>
    public bool NextRow()
    {
        int remaining = _tupleBuffer.Count - (_tupleBuffer.CurrentRow + 1);
        if (!_closeReceived && !_first && remaining == 0)
            return false;
        if (!_closeReceived && _first && remaining == 0)
        {
            // Rows already sitting in this session's local buffer stay readable regardless --
            // only reaching past them (needing a fresh SQ_NFETCH the server can no longer
            // answer, since it discarded the cursor when the transaction ended) is refused.
            if (_closedByTransactionEnd)
                throw new InvalidOperationException(
                    "This cursor was closed because the connection's transaction ended (commit " +
                    "or rollback); open it WITH HOLD to survive that. Any rows beyond what was " +
                    "already buffered can no longer be fetched from the server.");
            FetchMore();
            remaining = _tupleBuffer.Count - (_tupleBuffer.CurrentRow + 1);
        }
        if (remaining == 0)
        {
            _first = false;
            return false;
        }
        _tupleBuffer.Advance();
        if (_rowNeedsWalk)
            _tupleBuffer.Walk(_columns, Capabilities);
        return true;
    }

    /// <summary>Decodes the current row's column <paramref name="ordinal"/> (0-based) to a CLR value.</summary>
    public object DecodeColumn(int ordinal)
    {
        if (_tupleBuffer.CurrentRow < 0)
            throw new InvalidOperationException("No current row; call NextRow() first.");
        var col = _columns[ordinal];
        var (offset, length) = ColumnExtent(ordinal, col);
        if (col.SqliTypeCode is SqliTypeCode.Byte or SqliTypeCode.Text)
            return DecodeBlobColumn(ordinal, col, offset);
        if (col.SqliTypeCode is SqliTypeCode.Clob or SqliTypeCode.Blob)
            return DecodeSmartBlobColumn(ordinal, col, offset);
        return DecodeValue(col, offset, length);
    }

    // Where a column's bytes sit in the current row: the walked layout for a
    // variable-width row, otherwise the described fixed position/width.
    private (int Offset, int Length) ColumnExtent(int ordinal, IfxColumnMetadata col) =>
        _tupleBuffer.Extent(ordinal, col, _rowNeedsWalk);

    /// <summary>
    /// The current row's DECIMAL/MONEY column as a <see cref="double"/>, converted from the
    /// packed digits without passing through <see cref="decimal"/> — the accessor that still
    /// works when the value's magnitude is outside the decimal range, where
    /// <see cref="DecodeColumn"/> can only throw (see
    /// <see cref="Types.PackedValue.DecimalRangeError"/>). Approximate by nature: a double
    /// holds ~15-17 significant digits against DECIMAL's 32.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the fast path does not apply — the column is not a DECIMAL/MONEY, or
    /// the cell is SQL NULL — leaving both cases to the caller's ordinary decode-then-convert
    /// path so its NULL and type-mismatch behaviour is unchanged.
    /// </returns>
    internal double? DecodeColumnAsDouble(int ordinal)
    {
        if (_tupleBuffer.CurrentRow < 0)
            throw new InvalidOperationException("No current row; call NextRow() first.");
        var col = _columns[ordinal];
        if (col.SqliTypeCode is not (SqliTypeCode.Decimal or SqliTypeCode.Money))
            return null;
        var (offset, length) = ColumnExtent(ordinal, col);
        var d = Types.PackedValue.FromWire(_tupleBuffer.Slice(offset, length), col.PrecisionScale);
        return d.IsNull ? null : d.ToDouble();
    }

    /// <summary>
    /// The raw undecoded payload of the current row's column, for the types that decode to
    /// something other than a plain <c>byte[]</c>: the SENDRECV <c>[len:int32][data]</c> body
    /// of a ROW/collection column, or the inline <c>[flag:1][len:int32][data]</c> body of a
    /// JSON/BSON or opaque-UDT column (the raw BSON bytes / JSON UTF-8 text / UDT payload).
    /// Returns <c>null</c> for any other column (or a NULL cell). Lets
    /// <see cref="Informix.Data.IfxDataReader.GetBytes"/> and
    /// <c>GetFieldValue&lt;byte[]&gt;</c> reach the undecoded bytes without running the
    /// structured/text decode.
    /// </summary>
    internal byte[]? GetRawColumnBytes(int ordinal)
    {
        if (_tupleBuffer.CurrentRow < 0)
            throw new InvalidOperationException("No current row; call NextRow() first.");
        var col = _columns[ordinal];
        int offset = _tupleBuffer.Offset(ordinal, col, _rowNeedsWalk);

        // JSON/BSON and the opaque UDTs: inline [flag:1][len:int32][data]; flag 1 => SQL NULL.
        if (col.SqliTypeCode is SqliTypeCode.Json or SqliTypeCode.Bson
            or SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed)
        {
            if (_tupleBuffer.Slice(offset, 1)[0] != 0)
                return null; // SQL NULL
            int n = IfxWireConverter.ReadInt32(_tupleBuffer.Slice(offset + 1, 4));
            return n <= 0 ? [] : _tupleBuffer.Slice(offset + 5, n).ToArray();
        }

        // ROW/collection SENDRECV: [len:int32][data], no flag byte.
        if (col.SqliTypeCode is not (SqliTypeCode.Row or SqliTypeCode.Set or SqliTypeCode.MultiSet or SqliTypeCode.List
            or SqliTypeCode.Collection))
            return null;
        int len = IfxWireConverter.ReadInt32(_tupleBuffer.Slice(offset, 4));
        return len <= 0 ? [] : _tupleBuffer.Slice(offset + 4, len).ToArray();
    }

    /// <summary>
    /// The raw 72-byte smart-LOB locator of a CLOB/BLOB column in the current row,
    /// without materializing the data (the lazy <see cref="Informix.Data.IfxBlob"/>/
    /// <see cref="Informix.Data.IfxClob"/> path). Returns <c>null</c> for a non-LOB
    /// column or SQL NULL.
    /// </summary>
    internal byte[]? GetSmartLobLocator(int ordinal)
    {
        if (_tupleBuffer.CurrentRow < 0)
            throw new InvalidOperationException("No current row; call NextRow() first.");
        var col = _columns[ordinal];
        if (col.SqliTypeCode is not (SqliTypeCode.Clob or SqliTypeCode.Blob))
            return null;
        int offset = _tupleBuffer.Offset(ordinal, col, _rowNeedsWalk);
        if (_tupleBuffer.Slice(offset, 1)[0] != 0)
            return null; // flag byte 1 => SQL NULL
        int len = IfxWireConverter.ReadInt32(_tupleBuffer.Slice(offset + 1, 4));
        return _tupleBuffer.Slice(offset + 5, len).ToArray();
    }

    // A CLOB/BLOB column carries only [flag:1][len:int32][72-byte ifx_lo_t locator]
    // in the tuple (flag 1 = SQL NULL); the data lives in an sbspace and is
    // retrieved lazily (and cached per row) through the FastPath IFX_LO_* calls +
    // SQ_LODATA. CLOB -> decoded string, BLOB -> byte[].
    private object DecodeSmartBlobColumn(int ordinal, IfxColumnMetadata col, int offset)
    {
        if (_blobCacheRow != _tupleBuffer.CurrentRow)
        {
            _blobRowCache.Clear();
            _blobCacheRow = _tupleBuffer.CurrentRow;
        }
        if (_blobRowCache.TryGetValue(ordinal, out var cached))
            return cached;

        object value;
        if (_tupleBuffer.Slice(offset, 1)[0] != 0)
        {
            value = DBNull.Value; // flag byte 1 => SQL NULL, no round trip
        }
        else
        {
            int len = IfxWireConverter.ReadInt32(_tupleBuffer.Slice(offset + 1, 4));
            byte[] locator = _tupleBuffer.Slice(offset + 5, len).ToArray();
            byte[] data = ReadSmartBlob(locator);
            value = col.SqliTypeCode == SqliTypeCode.Clob
                ? IfxWireConverter.DecodeChar(data, _in.DbEncoding)
                : data;
        }
        _blobRowCache[ordinal] = value;
        return value;
    }

    // A BYTE/TEXT column carries only a descriptor in the tuple; the data
    // is fetched lazily (and cached per row) via SQ_FETCHBLOB when first accessed.
    // BYTE -> byte[], TEXT -> decoded string. Port of IfxResultSet.blobCheck +
    // IfxBlob.toObject (TEXT -> toString, BYTE -> toBytes).
    private object DecodeBlobColumn(int ordinal, IfxColumnMetadata col, int offset)
    {
        if (_blobCacheRow != _tupleBuffer.CurrentRow)
        {
            _blobRowCache.Clear();
            _blobCacheRow = _tupleBuffer.CurrentRow;
        }
        if (_blobRowCache.TryGetValue(ordinal, out var cached))
            return cached;

        byte[]? data = FetchBlob(_tupleBuffer.Slice(offset, BlobDescriptorWidth));
        object value = data is null
            ? DBNull.Value
            : col.SqliTypeCode == SqliTypeCode.Text
                ? IfxWireConverter.DecodeChar(data, _in.DbEncoding)
                : data;
        _blobRowCache[ordinal] = value;
        return value;
    }

    /// <summary>
    /// Fetches one simple large object (BYTE/TEXT) by echoing its in-tuple
    /// descriptor back to the server in SQ_FETCHBLOB and collecting the SQ_BLOB
    /// data frames into a buffer. Returns <c>null</c> when the
    /// descriptor's <c>tb_flags</c> is <see cref="BlobIsNull"/> (SQL NULL) — no
    /// round trip is made in that case.
    /// </summary>
    private byte[]? FetchBlob(ReadOnlySpan<byte> descriptor)
    {
        using var gate = _link.Gate.EnterScope();
        // IfxBlob.fromIfx layout: tb_start = int32 @8, tb_end = int32 @12,
        // tb_size = int32 @16, tb_flags = int16 @38. A NULL blob is signalled by
        // tb_flags == BLOBISNULL (the server sets bytes[38..40) to 00 01).
        short tbFlags = (short)((descriptor[38] << 8) | descriptor[39]);
        if (tbFlags == BlobIsNull)
            return null;

        int tbStart = IfxWireConverter.ReadInt32(descriptor.Slice(8, 4));
        int tbEnd = IfxWireConverter.ReadInt32(descriptor.Slice(12, 4));
        int blobLength = Math.Max(0, tbEnd - tbStart + 1); // IfxBlob.getBlobLength()

        // Echo the descriptor verbatim (IfxBlob.toIfx only rewrites tb_size, which
        // for non-XPS already equals bytes[16..20)). Copy it out of the tuple
        // before any socket I/O so the fetch reply cannot disturb it.
        byte[] echo = descriptor[..BlobDescriptorWidth].ToArray();

        _blobBuffer = new byte[blobLength];
        _blobWriteOffset = 0;

        SendStatementId();
        _out.WriteInt16((short)SqliMessage.FetchBlob);
        _out.WritePadded(echo);
        Flip();
        ReceiveMessage();

        byte[] result = _blobBuffer;
        _blobBuffer = null;
        return result;
    }

    /// <summary>Closes the open cursor (SQ_ID + SQ_CLOSE).</summary>
    public void CloseCursor()
    {
        // The lock guards the CursorOpen read itself, not just the wire send below: a
        // sibling session's commit/rollback (see MarkNonHoldCursorClosedByTransactionEnd)
        // can flip this session's CursorOpen from a different thread at any time.
        using var gate = _link.Gate.EnterScope();
        HasAttachedReader = false; // the reader is done with this session either way
        if (!CursorOpen)
            return;
        CursorOpen = false;
        if (_closeReceived)
            return; // the server already auto-closed at end of rows
        if (!_asf.IsConnected)
            return; // the connection was closed under the reader: nothing is left to close
        SendStatementId();
        _out.WriteInt16((short)SqliMessage.Close);
        Flip();
        ReceiveMessage();
    }

    /// <summary>
    /// True when the session carries no open cursor or prepared statement — the clean
    /// state a physical connection must be in to be safely returned to the pool.
    /// </summary>
    public bool IsIdle => !CursorOpen && !HasAttachedReader && StatementId == -1;

    /// <summary>
    /// Marks this session's open cursor closed because the connection's transaction just ended
    /// (commit or rollback) — a no-op unless <see cref="CursorOpen"/> is true and it was not
    /// opened WITH HOLD. Called on every session of the link by <see cref="ExecuteCommit"/> and
    /// <see cref="ExecuteRollback"/>, since either one can end a cursor open on a <em>sibling</em>
    /// session, not just this one's own.
    ///
    /// <para>Purely local bookkeeping, like the end-of-rows auto-close this mirrors: the server
    /// has already discarded the cursor as part of ending the transaction, so nothing is sent.
    /// <see cref="CloseCursor"/> afterward is then correctly a no-op, and <see cref="NextRow"/>
    /// throws instead of silently reporting no more rows, so a caller mid-read finds out its
    /// cursor died out from under it rather than mistaking transaction-end for end-of-data.</para>
    ///
    /// <para>Live-verified against IDS 14.10 (2026-08-03, database <c>testlog</c> — a logged
    /// database; the driver's own <c>stores_demo</c> probe is unlogged and rejects transactions
    /// outright): with a result far too large for any client fetch buffer, a WITH HOLD cursor
    /// survives both COMMIT and ROLLBACK and keeps yielding rows; a non-hold cursor dies on
    /// both, identically — there is no commit/rollback asymmetry to model here.</para>
    /// </summary>
    internal void MarkNonHoldCursorClosedByTransactionEnd()
    {
        if (!CursorOpen || _cursorIsHold)
            return;
        CursorOpen = false;
        _closedByTransactionEnd = true;
    }

    /// <summary>Frees the prepared statement on the server (SQ_ID + SQ_RELEASE; port of <c>executeRelease</c>).</summary>
    public void ReleaseStatement()
    {
        using var gate = _link.Gate.EnterScope();
        if (StatementId == -1)
            return;
        if (!_asf.IsConnected)
        {
            ClearStatementState(); // the server side went with the connection
            return;
        }
        SendStatementId();
        _out.WriteInt16((short)SqliMessage.Release);
        StatementId = -1;
        Flip();
        ReceiveMessage();
        ClearStatementState();
    }

    /// <summary>
    /// Frees a specific prepared statement on the server (SQ_ID(handle) + SQ_RELEASE),
    /// regardless of whether it is the session's currently active statement — e.g. a
    /// prepared-statement cache's LRU evictee, or a stale handle
    /// <see cref="Informix.Data.IfxCommand"/> is dropping because its CommandText
    /// changed. Unlike <see cref="ReleaseStatement"/>, this never disturbs whichever
    /// statement (if any) is currently active — it only clears the session's current-
    /// statement bookkeeping when <paramref name="handle"/> happens to be that statement.
    /// </summary>
    public void ReleaseStatementHandle(SqliStatementHandle handle)
    {
        using var gate = _link.Gate.EnterScope();
        ArgumentNullException.ThrowIfNull(handle);
        bool wasActive = StatementId == handle.StatementId;
        if (!_asf.IsConnected)
        {
            // Reached from a reader or command disposed after its connection was closed: the
            // statement died with the connection, so only the local bookkeeping is left.
            if (wasActive)
                ClearStatementState();
            return;
        }
        SendStatementId(handle.StatementId);
        _out.WriteInt16((short)SqliMessage.Release);
        if (wasActive)
            StatementId = -1;
        Flip();
        ReceiveMessage();
        if (wasActive)
            ClearStatementState();
    }

    // ---- transactions ----

    /// <summary>Sends SQ_BEGIN (BEGIN WORK).</summary>
    public void ExecuteBegin()
    {
        using var gate = _link.Gate.EnterScope();
        _out.WriteInt16((short)SqliMessage.Begin);
        Flip();
        ReceiveMessage();
    }

    /// <summary>Sends SQ_CMMTWORK (COMMIT WORK). When <see cref="StatementOwner"/> implements
    /// <see cref="ISqliTransactionAwareStatement"/> (currently only <see cref="IfxBulkLoader"/>),
    /// its <see cref="ISqliTransactionAwareStatement.OnBeforeCommit"/> runs first — so buffered
    /// rows join the transaction that is about to commit instead of silently landing in a later
    /// one — and <see cref="ISqliTransactionAwareStatement.OnAfterCommit"/> runs after: live probes
    /// against IDS 9.59 (2026-07-23) show COMMIT closes a non-HOLD cursor (server error on the next
    /// fetch/PUT), so the loader needs to know. Afterward, every session on the link — not just
    /// this one, since a sibling can hold a read cursor commit just as easily discards — gets
    /// <see cref="MarkNonHoldCursorClosedByTransactionEnd"/> called on it.</summary>
    public void ExecuteCommit()
    {
        // Held across the whole method, including the broadcast loop below: the same gate
        // guards CloseCursor's CursorOpen read on every session on this link, so the broadcast
        // and any concurrent CloseCursor genuinely serialize against each other, not just
        // benignly race (contrast NextRow, which deliberately stays lock-free -- see its doc).
        using var gate = _link.Gate.EnterScope();
        (StatementOwner as ISqliTransactionAwareStatement)?.OnBeforeCommit();
        _out.WriteInt16((short)SqliMessage.CommitWork);
        Flip();
        ReceiveMessage();
        (StatementOwner as ISqliTransactionAwareStatement)?.OnAfterCommit();
        foreach (var session in _link.Sessions)
            session.MarkNonHoldCursorClosedByTransactionEnd();
    }

    /// <summary>Sends SQ_RBWORK (ROLLBACK WORK). Unlike SQ_CMMTWORK, the rollback opcode
    /// carries a trailing savepoint word (0 = whole transaction) — <c>sendRollback</c>
    /// writes it unconditionally, and the server blocks waiting for it if omitted
    /// (live-diagnosed against IDS 9.59: a bare SQ_RBWORK after an insert-cursor
    /// SQ_PUTERR hangs; ESQL/C sends the extra word too). Same <see cref="ISqliTransactionAwareStatement"/>
    /// hooks as <see cref="ExecuteCommit"/> — live probes (2026-07-23) show ROLLBACK closes a
    /// non-HOLD cursor exactly like COMMIT does (symmetric, not special-cased), reconfirmed
    /// 2026-08-03 against IDS 14.10 for ordinary read cursors specifically (not just inserts):
    /// a ~8836-row WITH HOLD cursor survives both COMMIT and ROLLBACK identically; non-hold dies
    /// on both. Same link-wide <see cref="MarkNonHoldCursorClosedByTransactionEnd"/> broadcast as
    /// <see cref="ExecuteCommit"/>.</summary>
    public void ExecuteRollback()
    {
        using var gate = _link.Gate.EnterScope(); // see ExecuteCommit for why this spans the broadcast too
        (StatementOwner as ISqliTransactionAwareStatement)?.OnBeforeRollback();
        _out.WriteInt16((short)SqliMessage.RollbackWork);
        _out.WriteInt16(0); // savepoint indicator: roll back the whole transaction
        Flip();
        ReceiveMessage();
        (StatementOwner as ISqliTransactionAwareStatement)?.OnAfterRollback();
        foreach (var session in _link.Sessions)
            session.MarkNonHoldCursorClosedByTransactionEnd();
    }

    /// <summary>
    /// Sets a named savepoint: SQ_SQLISETSVPT(137) + name + uniq flag (port of
    /// <c>IfxSqli.sendSavepoint</c>).
    /// </summary>
    public void ExecuteSavepoint(string name, bool unique = false)
    {
        using var gate = _link.Gate.EnterScope();
        _out.WriteInt16((short)SqliMessage.SqliSetSvpt);
        _out.WriteChar(name);
        _out.WriteInt16((short)(unique ? 1 : 0));
        Flip();
        ReceiveMessage();
    }

    /// <summary>Releases a named savepoint: SQ_SQLIRELSVPT(138) + name (port of <c>sendReleaseSavepoint</c>).</summary>
    public void ExecuteReleaseSavepoint(string name)
    {
        using var gate = _link.Gate.EnterScope();
        _out.WriteInt16((short)SqliMessage.SqliRelSvpt);
        _out.WriteChar(name);
        Flip();
        ReceiveMessage();
    }

    /// <summary>Rolls back to a named savepoint: SQ_SQLIRBACKSVPT(139) + name (port of <c>sendRollbackSavepoint</c>).</summary>
    public void ExecuteRollbackSavepoint(string name)
    {
        using var gate = _link.Gate.EnterScope();
        _out.WriteInt16((short)SqliMessage.SqliRbackSvpt);
        _out.WriteChar(name);
        Flip();
        ReceiveMessage();
    }

    // ---- pipeline internals ----

    // The server allows one open cursor per session; nested readers run on a sibling session instead.
    // A reader whose cursor a commit/rollback already closed (HasAttachedReader without CursorOpen) still
    // owns this session's tuple buffer and statement, so it blocks a new statement just the same.
    private void ThrowIfCursorOpen()
    {
        if (CursorOpen || HasAttachedReader)
            throw new InvalidOperationException(
                "A data reader is already open on this connection; close it before executing another statement.");
    }

    // ---- single-statement pin (IfxBulkLoader) ----
    // A bulk loader holds the session's one prepared-statement slot for the whole
    // load; an interleaved IfxCommand/IfxRowBatch would re-Prepare and silently clobber
    // it. While a loader owns the pin, those execution paths refuse to run.

    /// <summary>The construct (an <see cref="IfxBulkLoader"/>) that currently owns the
    /// session's prepared statement, or <see langword="null"/> when the session is free.</summary>
    internal object? StatementOwner { get; private set; }

    /// <summary>Claims the single-statement pin for <paramref name="owner"/>. Throws if
    /// another owner already holds it (a second concurrent loader on the connection).</summary>
    internal void PinStatement(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (StatementOwner is not null && !ReferenceEquals(StatementOwner, owner))
            throw new InvalidOperationException(
                "A bulk loader is already active on this connection; complete or dispose it before opening another.");
        StatementOwner = owner;
    }

    /// <summary>Releases the pin if <paramref name="owner"/> holds it; a no-op otherwise.</summary>
    internal void UnpinStatement(object owner)
    {
        if (ReferenceEquals(StatementOwner, owner))
            StatementOwner = null;
    }

    /// <summary>Guards ordinary command/batch execution while a loader owns the session.</summary>
    internal void ThrowIfStatementOwned()
    {
        if (StatementOwner is not null)
            throw new InvalidOperationException(
                "A bulk loader is active on this connection; complete or dispose it before running other commands.");
    }

    // ---- session reservation (IfxConnection.AcquireSession) ----
    //
    // Live-verified 2026-08-03: without this, two threads' AcquireSession calls can both see
    // the SAME session as free and both get handed it -- CursorOpen/StatementId alone are not
    // enough to make "free" trustworthy, because neither is set until well after AcquireSession
    // has already returned its answer (building binds, etc. happen in between with no lock
    // held). This flag closes that gap: AcquireSession sets it, atomically with its scan, the
    // instant a session is chosen; the caller clears it once its own top-level call
    // (ExecuteReader/ExecuteNonQuery/ExecuteScrollReader/Prepare) finishes -- successfully or
    // not -- by which point CursorOpen (if a cursor was opened) or StatementOwner (if pinned)
    // has taken over marking the session busy for as long as that actually matters.

    /// <summary>True from the moment <see cref="Informix.Data.IfxConnection.AcquireSession"/>
    /// selects this session until the caller's top-level call finishes.</summary>
    internal bool IsReservedForAcquire { get; private set; }

    internal void MarkReservedForAcquire() => IsReservedForAcquire = true;

    internal void ClearReservedForAcquire() => IsReservedForAcquire = false;

    // Forgets everything about the current statement and its row buffer (local state only).
    private void ClearStatementState()
    {
        _columns.Clear();
        _rowNeedsWalk = false;
        _rowHasUnsupported = false;
        TupleSize = 0;
        _tupleBuffer.ResetCapacity();
        _activeFetchBufferSize = 0;
        StatementId = -1;
        StatementType = 0;
        _closeReceived = false;
        _first = false;
        HasRows = false;
        InsertCursorOpen = false;
        HasAttachedReader = false;
        ResetTuples();
    }

    // SerialInserted is always refreshed by the next SQ_DONE, but Serial8Inserted/
    // BigSerialInserted only arrive via SQ_INSERTDONE, which the server sends solely
    // for an INSERT into a SERIAL8/BIGSERIAL column — never for e.g. an UPDATE. Without
    // this reset, IfxCommand.LastSerial8/LastBigSerial would keep surfacing a prior
    // execution's value on a statement that generated none.
    private void ResetInsertedSerials()
    {
        SerialInserted = 0;
        Serial8Inserted = 0;
        BigSerialInserted = 0;
    }

    private void ResetTuples()
    {
        _tupleBuffer.Reset();
        // Row indices restart with each tuple buffer, so the per-row blob cache
        // must be dropped to avoid a stale hit on the reused index 0.
        _blobRowCache.Clear();
        _blobCacheRow = -1;
    }

    // With the remove-64K-limit capability the
    // statement text travels with a 4-byte length prefix,
    // otherwise the classic 2-byte one. Cursor names always use the 2-byte form.
    private void WriteStatementText(string text)
    {
        byte[] data = IfxWireConverter.EncodeChar(text, _out.DbEncoding);
        if (Capabilities.IsRemove64KLimitSupported)
            _out.WriteInt32(data.Length);
        else
            _out.WriteInt16((short)data.Length);
        _out.WritePadded(data);
    }

    // Port of sendStatementID: a no-op until a statement is prepared.
    private void SendStatementId()
    {
        if (StatementType == 0 && StatementId == -1)
            return;
        SendStatementId(StatementId);
    }

    // As above, but for an explicit (not necessarily current) statement id — used
    // to target a handle that Activate hasn't (and, for ReleaseStatementHandle,
    // deliberately shouldn't) make the session's current statement.
    private void SendStatementId(short statementId)
    {
        _out.WriteInt16((short)SqliMessage.Id);
        _out.WriteInt16(statementId);
    }

    // Port of sendBind. An opaque/UDT parameter (type word > 18, excluding the
    // BIGINT pair 52/53) carries its extended owner and type-name strings right
    // after the type word — before the indicator, so a typed NULL still names its
    // type (sendBind writes the extended block ahead of the isNull branch). A
    // BYTE/TEXT parameter sends its 56-byte descriptor inline here and its
    // payload afterwards in SQ_BBIND.
    private void SendBind(IReadOnlyList<SqliBindValue> parameters)
    {
        _out.WriteInt16((short)SqliMessage.Bind);
        _out.WriteInt16((short)parameters.Count);
        int blobCount = 0;
        foreach (var p in parameters)
        {
            _out.WriteInt16((short)p.Type);
            if (p.UseDefault)
            {
                // -2 tells the server to apply the argument's DEFAULT (IfxSqli.sendBind); no
                // extended type name travels, whatever the type.
                _out.WriteInt16(-2);
                _out.WriteInt16(0);
                continue;
            }
            if ((short)p.Type > 18 && (short)p.Type != 52 && (short)p.Type != 53)
            {
                // A null owner is a bare zero length word — WriteChar("") emits.
                _out.WriteChar(p.ExtendedOwner);
                _out.WriteChar(p.ExtendedTypeName);
            }
            if (p.Data is null)
            {
                _out.WriteInt16(-1); // NULL indicator
                _out.WriteInt16(0);
                continue;
            }
            _out.WriteInt16(0);
            _out.WriteInt16(p.Precision);
            _out.WritePadded(p.Data);
            if (p.IsBlob && p.BlobData is not null)
                blobCount++;
        }
        if (blobCount > 0)
            SendBlobData(parameters, blobCount);
    }

    // Port of sendBlob + sendBytesBlob (IfxSqli ~3506/3655): after the descriptors
    // go out in SQ_BIND, each non-null BYTE/TEXT payload is streamed as SQ_BLOB
    // frames (chunked at 1024 bytes) closed by a zero-length SQ_BLOB, all under a
    // single SQ_BBIND header. Blobs are sent in parameter order.
    private void SendBlobData(IReadOnlyList<SqliBindValue> parameters, int blobCount)
    {
        _out.WriteInt16((short)SqliMessage.BBind);
        _out.WriteInt16((short)blobCount);
        foreach (var p in parameters)
        {
            if (!p.IsBlob || p.BlobData is null)
                continue;
            SendOneBlob(p.BlobData);
        }
    }

    // Content is chunked at 1024 bytes per SQ_BLOB frame and ended by a zero-length frame.
    private void SendOneBlob(byte[] data)
    {
        int offset = 0, remaining = data.Length;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, 1024); // sendBytesBlob caps each frame at 1024
            _out.WriteInt16((short)SqliMessage.Blob);
            _out.WriteInt16((short)chunk);
            _out.WritePadded(data.AsSpan(offset, chunk));
            offset += chunk;
            remaining -= chunk;
        }
        _out.WriteInt16((short)SqliMessage.Blob);
        _out.WriteInt16(0); // zero-length frame terminates the object
    }

    // SQ_ID [+ SQ_RETTYPE] + SQ_NFETCH + buffer size (int32 with the 2 GB capability, else int16) + the fetch-array word when supported.
    private void SendFetch(bool sendRetType)
    {
        SendStatementId();
        if (sendRetType)
            SendRetType();
        _out.WriteInt16((short)SqliMessage.NFetch);
        int size = GetTupleBufferSize(_activeFetchBufferSize);
        if (Capabilities.IsXps840 || Capabilities.Is2GBFetchBufferSupported)
            _out.WriteInt32(size);
        else
            _out.WriteInt16((short)size);
        if (Capabilities.IsFetchArray)
            _out.WriteInt16(0);
        FetchMessagesSent++;
    }

    // SQ_RETTYPE announces the fetch column types. For an
    // extended/opaque type (code > 18, excluding BIGINT/BIGSERIAL) the type word is
    // followed by the owner and type-name strings, mirroring sendBind's extended
    // branch — verified on the wire for LVARCHAR (type 43 + "informix"/"lvarchar").
    // A smart large object column announces as SENDRECV(44) + "informix"/"clob"|
    // "blob" + length 72 (IfxTypes.FromIfxTypeToSendRecv maps UDT/CLOB/BLOB codes
    // to 44; captured verbatim from the JDBC reference run).
    private void SendRetType()
    {
        _out.WriteInt16((short)SqliMessage.RetType);
        _out.WriteInt16(1); // direction: 1 = FETCH
        _out.WriteInt16((short)_columns.Count);
        foreach (var col in _columns)
        {
            short sqltype = (short)col.SqliTypeCode;
            // A variable-length column that is not a plain
            // VARCHAR/NVARCHAR is announced by the CLIENT's chosen receive representation,
            // keyed off the extended-type id — LVARCHAR(1)→43, IMPEXP(3)→46, IMPEXPBIN(4)→47,
            // BOOL(5)→45, everything else (ROW, SET/LIST/MULTISET, CLOB/BLOB, opaque UDT)→
            // SENDRECV(44). The server then streams that column in the requested form.
            bool isVarLen = IsVariableLengthColumn(col);
            bool remap = isVarLen && col.SqliTypeCode is not (SqliTypeCode.VarChar or SqliTypeCode.NVarChar);
            if (remap)
            {
                int xid = col.IsDistinct ? col.SourceType : col.ExtendedTypeId;
                // Extended id -> announced type: 3 -> 46 (import/export text), 4 -> 47 (binary), 1 -> 43 (LVARCHAR), 5 -> 45 (BOOLEAN), otherwise 44.
                sqltype = xid switch { 3 => 46, 4 => 47, 1 => 43, 5 => 45, _ => 44 };
            }
            _out.WriteInt16(sqltype);
            if (remap)
            {
                _out.WriteChar(col.ExtendedOwner);
                _out.WriteChar(col.ExtendedTypeName);
            }
            _out.WriteInt32(col.Length);
        }
    }

    // A column whose in-tuple footprint varies per row (walked), matching
    // IfxColumnInfo.isVariableLengthType: VARCHAR/NVARCHAR, LVARCHAR, smart blobs, the
    // complex/opaque family (ROW, SET/LIST/MULTISET/COLLECTION, opaque UDTs), and BOOLEAN
    // (the opaque-xid form collapsed to BOOL). This also gates the SQ_RET_TYPE remap in
    // SendRetType, so BOOL must stay here to keep announcing 45 + owner/name to the server.
    private static bool IsVariableLengthColumn(IfxColumnMetadata col) =>
        col.SqliTypeCode is SqliTypeCode.VarChar or SqliTypeCode.NVarChar or SqliTypeCode.LVarChar
            or SqliTypeCode.Clob or SqliTypeCode.Blob or SqliTypeCode.Json or SqliTypeCode.Bson
            or SqliTypeCode.Row or SqliTypeCode.Set or SqliTypeCode.MultiSet
            or SqliTypeCode.List or SqliTypeCode.Collection or SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
            or SqliTypeCode.Bool;

    // Next buffer of an open forward cursor: SQ_NFETCH only (the receive types were announced when the cursor was opened).
    private void FetchMore()
    {
        using var gate = _link.Gate.EnterScope();
        ResetTuples();
        SendFetch(sendRetType: false);
        Flip();
        ReceiveMessage();
    }

    // Requested buffer size in bytes: see SqliTupleBuffer.Capacity.
    private int GetTupleBufferSize(int fetchBufferSize = 0) =>
        _tupleBuffer.Capacity(TupleSize, ConnectionFetchBufferSize, fetchBufferSize, MaxFetchBufferSize);

    // Decodes one field in place from the row buffer; the context carries the code set and the column name for error messages.
    private object DecodeValue(IfxColumnMetadata col, int offset, int length)
    {
        var ctx = new SqliDecodeContext(
            _in.DbEncoding, TrimTrailingSpaces, col.PrecisionScale,
            IsColumn: true, SubjectName: col.Name,
            col.NumericPrecision ?? 0, col.NumericScale ?? 0);
        return SqliValueDecoder.Decode(col.SqliTypeCode, _tupleBuffer.Slice(offset, length), ctx);
    }

    // ========================================================================= //
    //  FastPath (SQ_GETROUTINE / SQ_EXFPROUTINE) + smart large objects          //
    //  wire layouts verified against a captured JDBC reference run on IDS 9.59) //
    // ========================================================================= //

    // Routine signatures, verbatim from IfxSmartBlob (including the stray
    // spaces — the server resolves the signature textually).
    private const string LoCreateSignature = "function informix.ifx_lo_create( ifx_lo_spec,integer,blob)";
    private const string LoOpenSignature = "function informix.ifx_lo_open(blob ,integer)";
    private const string LoCloseSignature = "function informix.ifx_lo_close(integer)";
    private const string LoColSbinfoSignature = "function informix.ifx_lo_col_sbinfo(lvarchar, ifx_lo_spec)";
    private const string LoSeekSignature = "function informix.ifx_lo_seek(integer,int8,integer,int8)";
    private const string LoStatSignature = "function informix.ifx_lo_stat(int, ifx_lo_stat)";
    private const string LoTruncateSignature = "function informix.ifx_lo_truncate(integer,int8)";

    private const int LoStatSize = 644;      // IfxSmartBlob.LO_STAT_SIZE (isLongID)
    private const int LoRdOnly = 4;          // IfxSmartBlob.LO_RDONLY
    private const int LoRdWr = 8;            // IfxSmartBlob.LO_RDWR
    private const short LoSeekSet = 0;       // IfxSmartBlob.LO_SEEK_SET
    private const short LoBufferSize = 32000; // R_W_BUFSIZE in sendLoData

    // Resolved FastPath routine handles: link-wide (SqliLink.FpHandleCache), mirroring
    // conn.getFPCacheRoutineId -- a routine handle is a connection-scoped server resource,
    // the same category as a prepared-statement id, so every session on this connection
    // shares one cache instead of each re-resolving the same routine. The reply's dbName
    // rides along because SQ_EXFPROUTINE opens with it.
    private string _fpReplyDb = "";
    private int _fpReplyHandle;
    private List<object?>? _fpReturns;

    // In-flight SQ_LODATA state (mirrors BlobBuffer/BlobRequestSize/amountRW).
    private byte[]? _loReadBuffer;
    private int _loReadOffset;
    private int _loAmount;

    /// <summary>
    /// Resolves a server routine handle by signature (SQ_GETROUTINE, port of
    /// <c>IfxSqli.getHandle</c>): a one-byte isRoutineById flag, the signature in
    /// the statement-text encoding (4-byte length with the remove-64K capability),
    /// and the fparam request flag. The reply carries the routine's database name
    /// and its integer handle; both are cached for the session.
    /// </summary>
    private (int Handle, string Db) GetRoutineHandle(string signature)
    {
        using var gate = _link.Gate.EnterScope(); // guards the shared FpHandleCache too
        if (_link.FpHandleCache.TryGetValue(signature, out var cached))
            return cached;

        _out.WriteInt16((short)SqliMessage.GetRoutine);
        _out.Write(0); // isRoutineById = false (byte)
        WriteStatementText(signature);
        _out.WriteInt16(0); // no SQ_FPARAM follow-up requested
        Flip();
        ReceiveMessage();

        var entry = (_fpReplyHandle, _fpReplyDb);
        _link.FpHandleCache[signature] = entry;
        return entry;
    }

    /// <summary>
    /// Invokes a server function over the FastPath channel: SQ_EXFPROUTINE +
    /// routine db name + handle + parameter count + fparam flag, followed by an
    /// ordinary SQ_BIND with the argument vector (port of
    /// <c>IfxSqli.executeFastPath</c>/<c>sendFastPath</c>). The reply is
    /// SQ_FPROUTINE carrying the routine's return values.
    /// </summary>
    internal IReadOnlyList<object?> ExecuteFastPath(string signature, params SqliBindValue[] args)
    {
        using var gate = _link.Gate.EnterScope();
        var (handle, db) = GetRoutineHandle(signature);
        _fpReturns = null;
        _out.WriteInt16((short)SqliMessage.ExFpRoutine);
        _out.WriteChar(db);
        _out.WriteInt32(handle);
        _out.WriteInt16((short)args.Length);
        _out.WriteInt16(0); // no fparam
        if (args.Length > 0)
            SendBind(args);
        Flip();
        ReceiveMessage();
        return _fpReturns
            ?? throw new IfxException($"FastPath call '{signature}' returned no SQ_FPROUTINE reply (-408).", -408);
    }

    // ===================================================================== //
    //  SPL routines with OUT / INOUT parameters                             //
    //  (the -9752 prepare rejection, SQ_GETROUTINE-by-id + SQ_FPARAM, then  //
    //  SQ_EXFPROUTINE + SQ_BIND + SQ_FPARAM)                                //
    // ===================================================================== //

    /// <summary>
    /// "Routine has OUT parameters": the server's answer to an SQ_PREPARE of an
    /// <c>EXECUTE PROCEDURE</c> naming a routine that declares <c>OUT</c>/<c>INOUT</c>
    /// arguments. Such a routine cannot run through prepare/execute at all — the reply
    /// exists only to hand back the routine's FastPath identity (see
    /// <see cref="OutParameterRoutine"/>) so the call can be re-driven over SQ_EXFPROUTINE.
    /// </summary>
    internal const short RoutineHasOutParametersSqlCode = -9752;

    /// <summary>
    /// The routine identity carried by the most recent <see cref="RoutineHasOutParametersSqlCode"/>
    /// error, or <see langword="null"/> when the last exchange did not raise one. Reset at
    /// the start of every <see cref="ReceiveMessage"/>.
    /// </summary>
    internal SqliOutParameterRoutine? OutParameterRoutine { get; private set; }

    // "db:handle:unique" -- e.g. "testlog:571:1". Anything that does not parse leaves the
    // routine unidentified, so the caller simply surfaces -9752 as the error it is.
    private static SqliOutParameterRoutine? ParseOutParameterRoutine(string message)
    {
        string[] parts = message.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[^2], out int handle))
            return null;
        return new SqliOutParameterRoutine(
            string.Join(':', parts[..^2]), handle, parts[^1].Trim() == "1");
    }

    // The fparam descriptor of the routine currently being resolved/executed. Non-null
    // only while a fparam-requesting exchange is in flight, since ReceiveFParam fills it.
    private SqliFParam? _fparam;

    /// <summary>
    /// Resolves the routine identified by <paramref name="routine"/> and asks the server to
    /// describe it (SQ_GETROUTINE with the "resolve by id" flag plus the fparam request;
    /// port of <c>IfxSqli.getHandle</c> with <c>request_fparam</c>). The reply pair —
    /// SQ_GETROUTINE's handle and SQ_FPARAM's descriptor — tells us which arguments are
    /// OUT/INOUT and how many values the routine returns.
    /// </summary>
    internal SqliFParam DescribeOutParameterRoutine(SqliOutParameterRoutine routine)
    {
        using var gate = _link.Gate.EnterScope();
        var fparam = new SqliFParam();
        _fparam = fparam;
        try
        {
            _out.WriteInt16((short)SqliMessage.GetRoutine);
            if (Capabilities.IsNamedParametersSupported)
            {
                _out.Write((byte)1); // isRoutineById
                WriteStatementText(routine.Database);
                _out.WriteInt32(routine.Handle);
            }
            else
            {
                _out.Write((byte)0);
                WriteStatementText(routine.Database);
            }
            _out.WriteInt16(1); // request the SQ_FPARAM descriptor
            Flip();
            ReceiveMessage();
        }
        finally
        {
            _fparam = null;
        }
        return fparam;
    }

    /// <summary>
    /// Executes a routine that has OUT/INOUT arguments over the FastPath channel:
    /// SQ_EXFPROUTINE + SQ_BIND of every declared argument (an OUT-only argument binds
    /// as a typed NULL placeholder) + the SQ_FPARAM echo. The SQ_FPROUTINE reply
    /// carries the OUT/INOUT argument values first — in
    /// <see cref="SqliFParam.OutParameterSequence"/> order — then the RETURNed values.
    /// </summary>
    internal IReadOnlyList<object?> ExecuteOutParameterRoutine(
        SqliOutParameterRoutine routine, SqliFParam fparam, IReadOnlyList<SqliBindValue> args)
    {
        using var gate = _link.Gate.EnterScope();
        _fparam = fparam;
        _fpReturns = null;
        try
        {
            _out.WriteInt16((short)SqliMessage.ExFpRoutine);
            if (Capabilities.IsNamedParametersSupported)
                _out.WriteChar(routine.Database);
            _out.WriteInt32(routine.Handle);
            _out.WriteInt16((short)args.Count);
            _out.WriteInt16(1); // fparam segment follows the binds
            if (args.Count > 0)
                SendBind(args);
            SendFParam(fparam);
            Flip();
            ReceiveMessage();
        }
        finally
        {
            _fparam = null;
        }
        return _fpReturns
            ?? throw new IfxException(
                $"Routine handle {routine.Handle} returned no SQ_FPROUTINE reply (-408).", -408);
    }

    // Port of sendFParam: the descriptor's opaque state echoed back, with isDone inverted
    // (the server sends "am I done", the client answers "keep going").
    private void SendFParam(SqliFParam fparam)
    {
        _out.WriteInt16((short)SqliMessage.FParam);
        _out.WriteInt8(fparam.FuncState);
        _out.WriteInt32(fparam.Request);
        _out.Write(fparam.IsDone ? (byte)0 : (byte)1);
    }

    // Port of receiveFParam: the routine descriptor. Return-value types come first, then
    // the argument types -- and only argument entries carry the trailing mode word, which
    // is what identifies an OUT/INOUT argument.
    private void ReceiveFParam()
    {
        var fparam = _fparam
            ?? throw new IfxException("SQ_FPARAM arrived without a pending routine descriptor (-408).", -408);

        fparam.FuncState = _in.ReadInt8();
        fparam.Request = _in.ReadInt32();
        fparam.IsDone = _in.ReadByte() != 0;
        if (fparam.IsDescribed)
            return; // a refresh of the opaque state only -- the descriptor does not repeat

        _ = _in.ReadInt32();  // id
        _ = _in.ReadInt8();   // row
        _ = _in.ReadInt32();  // colid

        fparam.ReturnCount = _in.ReadInt16();
        if (fparam.ReturnCount > 0)
            ReadFParamTypes(fparam.ReturnCount, hasParameterNames: false, modes: null);

        fparam.ArgumentCount = _in.ReadInt16();
        bool hasParameterNames = false;
        if (Capabilities.IsNamedParametersSupported)
        {
            short namedFlags = _in.ReadInt16();
            hasParameterNames = (namedFlags & 1) != 0;
        }

        var modes = new short[fparam.ArgumentCount];
        var flags = new byte[fparam.ArgumentCount];
        var types = new short[fparam.ArgumentCount];
        if (fparam.ArgumentCount > 0)
            ReadFParamTypes(fparam.ArgumentCount, hasParameterNames, modes, flags, types);
        fparam.ArgumentModes = modes;
        fparam.ArgumentFlags = flags;
        fparam.ArgumentTypes = types;

        var sequence = new List<int>();
        for (int i = 0; i < modes.Length; i++)
            if (modes[i] == SqliFParam.ModeOut)
                sequence.Add(i);
        fparam.OutParameterSequence = [.. sequence];
        fparam.IsDescribed = true;
    }

    // Port of readFPTypeInfo. Argument entries (modes != null) end with the mode word;
    // return-value entries do not.
    private void ReadFParamTypes(
        int count, bool hasParameterNames, short[]? modes, byte[]? flags = null, short[]? types = null)
    {
        for (int i = 0; i < count; i++)
        {
            if (hasParameterNames)
            {
                byte argFlags = _in.ReadByte();
                if (flags is not null)
                    flags[i] = argFlags;
                _ = _in.ReadChar();  // argument name
            }
            _ = _in.ReadByte();   // argNull
            short type = _in.ReadInt16();
            if (types is not null)
                types[i] = type;
            _ = _in.ReadInt32();  // extended type id
            _ = _in.ReadInt32();  // length
            _ = _in.ReadInt16();  // precision
            _ = _in.ReadInt16();  // scale
            if (modes is not null)
                modes[i] = _in.ReadInt16();
        }
    }

    /// <summary>
    /// Creates a smart large object in the default sbspace, writes
    /// <paramref name="data"/> into it and closes it, returning the 72-byte
    /// locator to bind into an INSERT/UPDATE — the exact insert path
    /// (IfxLoCreate + IfxLoWrite + IfxLoClose in <c>IfxSmartBlob</c>).
    /// </summary>
    internal byte[] CreateSmartBlob(byte[] data, string? column = null)
    {
        // Spans all three FastPath calls (create+write+close) as one unit: each is already
        // independently safe against a sibling session interleaving (reentrant re-acquire is
        // cheap), but keeping the whole create-then-populate-then-close sequence atomic matches
        // intuition better for a single logical "create this LOB" operation.
        using var gate = _link.Gate.EnterScope();
        byte[] spec = column is null ? BuildDefaultLobDescriptor() : ResolveColumnLobSpec(column);
        var created = ExecuteFastPath(LoCreateSignature,
            SqliBindValue.Udt("ifx_lo_spec", spec),
            SqliBindValue.From(LoRdWr, _out.DbEncoding),
            SqliBindValue.Udt("blob", new byte[IfxSmartLOBLocator.Length]));
        byte[] locator = created[0] as byte[]
            ?? throw new IfxException("ifx_lo_create did not return a locator (-408).", -408);
        int fd = Convert.ToInt32(created[1]);
        try
        {
            LoWrite(fd, data);
        }
        finally
        {
            ExecuteFastPath(LoCloseSignature, SqliBindValue.From(fd, _out.DbEncoding));
        }
        return locator;
    }

    // The create-time spec for a LOB that is going into "table.column": the template spec (the
    // default one, unless the caller already set attributes on it) with the column's own storage
    // attributes filled in by the server (ifx_lo_col_sbinfo) - its sbspace from PUT ... IN (...),
    // and the rest of the column-level settings. Live-verified: the same INSERT lands the LOB in
    // the default sbspace with the default spec and in the column's sbspace with this one.
    internal byte[] ResolveColumnLobSpec(string column, byte[]? template = null)
    {
        var resolved = ExecuteFastPath(LoColSbinfoSignature,
            SqliBindValue.LVarCharArgument(column, _out.DbEncoding),
            SqliBindValue.Udt("ifx_lo_spec", template ?? BuildDefaultLobDescriptor()));
        return resolved.Count > 0 && resolved[0] is byte[] spec
            ? spec
            : throw new IfxException($"ifx_lo_col_sbinfo returned no spec for '{column}' (-408).", -408);
    }

    // ---- incremental smart-LOB operations (the public IfxSmartLOB surface) ----

    /// <summary>Opens an existing smart LOB by its locator; returns the server file descriptor.</summary>
    internal int LobOpen(byte[] locator, int mode)
    {
        var opened = ExecuteFastPath(LoOpenSignature,
            SqliBindValue.Udt("blob", locator),
            SqliBindValue.From(mode == 0 ? LoRdWr : mode, _out.DbEncoding));
        return Convert.ToInt32(opened[0]);
    }

    /// <summary>
    /// Creates a new empty smart LOB from <paramref name="spec"/> (a 596-byte
    /// <c>ifx_lo_spec</c>, see <see cref="SqliLobSpec"/>) and leaves it open in
    /// <paramref name="mode"/>; returns its file descriptor and 72-byte locator.
    /// </summary>
    internal (int Fd, byte[] Locator) LobCreate(byte[] spec, int mode)
    {
        var created = ExecuteFastPath(LoCreateSignature,
            SqliBindValue.Udt("ifx_lo_spec", spec),
            SqliBindValue.From(mode == 0 ? LoRdWr : mode, _out.DbEncoding),
            SqliBindValue.Udt("blob", new byte[IfxSmartLOBLocator.Length]));
        byte[] locator = created[0] as byte[]
            ?? throw new IfxException("ifx_lo_create did not return a locator (-408).", -408);
        return (Convert.ToInt32(created[1]), locator);
    }

    /// <summary>The size in bytes of the open smart LOB (from IFX_LO_STAT).</summary>
    internal long LobSize(int fd) => SqliLobStat.GetSize(LobStat(fd));

    /// <summary>
    /// The raw IFX_LO_STAT block of the open smart LOB (JDBC <c>IfxLoStat</c>, 644 bytes on a
    /// long-ID server): size, times, reference count, and the create spec the server actually
    /// used, as a full <c>ifx_lo_spec</c> at its tail.
    /// </summary>
    internal byte[] LobStat(int fd)
    {
        var stat = ExecuteFastPath(LoStatSignature,
            SqliBindValue.From(fd, _out.DbEncoding),
            SqliBindValue.Udt("ifx_lo_stat", new byte[LoStatSize]));
        return stat[0] as byte[]
            ?? throw new IfxException("ifx_lo_stat did not return a stat block (-408).", -408);
    }

    /// <summary>Positions the server cursor of the open smart LOB (IFX_LO_SEEK).</summary>
    internal void LobSeekTo(int fd, long offset, int whence) =>
        ExecuteFastPath(LoSeekSignature,
            SqliBindValue.From(fd, _out.DbEncoding),
            SqliBindValue.From(offset, _out.DbEncoding),
            SqliBindValue.From(whence, _out.DbEncoding),
            SqliBindValue.From(0L, _out.DbEncoding));

    /// <summary>Reads up to <paramref name="nbytes"/> from the current cursor position.</summary>
    internal byte[] LobRead(int fd, int nbytes) => LoRead(fd, nbytes);

    /// <summary>Writes <paramref name="data"/> at the current cursor position; returns bytes written.</summary>
    internal int LobWrite(int fd, byte[] data) => LoWrite(fd, data);

    /// <summary>Truncates the open smart LOB to <paramref name="offset"/> bytes (IFX_LO_TRUNCATE).</summary>
    internal void LobTruncate(int fd, long offset) =>
        ExecuteFastPath(LoTruncateSignature,
            SqliBindValue.From(fd, _out.DbEncoding),
            SqliBindValue.From(offset, _out.DbEncoding));

    /// <summary>Closes an open smart LOB file descriptor (IFX_LO_CLOSE).</summary>
    internal void LobClose(int fd) =>
        ExecuteFastPath(LoCloseSignature, SqliBindValue.From(fd, _out.DbEncoding));

    /// <summary>
    /// Materializes a smart large object from its locator: IFX_LO_OPEN (read
    /// only) → IFX_LO_STAT for the size → IFX_LO_SEEK to the start → bulk
    /// SQ_LODATA read → IFX_LO_CLOSE.
    /// </summary>
    private byte[] ReadSmartBlob(byte[] locator)
    {
        var opened = ExecuteFastPath(LoOpenSignature,
            SqliBindValue.Udt("blob", locator),
            SqliBindValue.From(LoRdOnly, _out.DbEncoding));
        int fd = Convert.ToInt32(opened[0]);
        try
        {
            var stat = ExecuteFastPath(LoStatSignature,
                SqliBindValue.From(fd, _out.DbEncoding),
                SqliBindValue.Udt("ifx_lo_stat", new byte[LoStatSize]));
            byte[] statBytes = stat[0] as byte[]
                ?? throw new IfxException("ifx_lo_stat did not return a stat block (-408).", -408);
            // IfxLoSize: skip the int32 magic, then the size as an IfxUDTInput
            // long — [low32][high32][sign:int16] (sign 0 = null, -1 = negative).
            long size = ReadUdtLong(statBytes.AsSpan(4));
            if (size <= 0)
                return [];
            ExecuteFastPath(LoSeekSignature,
                SqliBindValue.From(fd, _out.DbEncoding),
                SqliBindValue.From(0L, _out.DbEncoding),
                SqliBindValue.From((int)LoSeekSet, _out.DbEncoding),
                SqliBindValue.From(0L, _out.DbEncoding));
            return LoRead(fd, checked((int)size));
        }
        finally
        {
            ExecuteFastPath(LoCloseSignature, SqliBindValue.From(fd, _out.DbEncoding));
        }
    }

    // SQ_LODATA subcommand 0 (read): sendLoData(subCom 0 = LO_READ):
    // opcode + subCom + loFd:int16 + nbytes:int32 + R_W_BUFSIZE(32000):int16.
    // The reply chunks land in _loReadBuffer via ReceiveLoData.
    private byte[] LoRead(int fd, int nbytes)
    {
        if (nbytes <= 0)
            return [];
        using var gate = _link.Gate.EnterScope();
        _loReadBuffer = new byte[nbytes];
        _loReadOffset = 0;
        _loAmount = 0;
        _out.WriteInt16((short)SqliMessage.LoData);
        _out.WriteInt16(0); // LO_READ
        _out.WriteInt16((short)fd);
        _out.WriteInt32(nbytes);
        _out.WriteInt16(LoBufferSize);
        Flip();
        ReceiveMessage();
        byte[] result = _loReadBuffer;
        _loReadBuffer = null;
        // The server sends min(requested, bytes left in the object). Hand back exactly what
        // arrived: callers take the array length as the count read, so a zero-padded tail would
        // turn a read at (or near) the end of the object into phantom data and never report 0.
        if (_loReadOffset < result.Length)
            Array.Resize(ref result, _loReadOffset);
        return result;
    }

    // SQ_LODATA subcommand 2 (write): sendLoData(subCom 2 = LO_WRITE): the header,
    // then the payload streamed as [size:int16][padded bytes] chunks capped at
    // 32000. The ack is an SQ_LODATA reply with opType 2 + the written count.
    private int LoWrite(int fd, byte[] data)
    {
        using var gate = _link.Gate.EnterScope();
        _out.WriteInt16((short)SqliMessage.LoData);
        _out.WriteInt16(2); // LO_WRITE
        _out.WriteInt16((short)fd);
        _out.WriteInt32(data.Length);
        _out.WriteInt16(LoBufferSize);
        int offset = 0, remaining = data.Length;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, LoBufferSize);
            _out.WriteInt16((short)chunk);
            _out.WritePadded(data.AsSpan(offset, chunk));
            offset += chunk;
            remaining -= chunk;
        }
        Flip();
        ReceiveMessage();
        return _loAmount;
    }

    /// <summary>
    /// The default create-time LO spec (<c>ifx_lo_spec</c>) for a long-ID server:
    /// 596 bytes, magic 0xDADAFEED, estimated size / size limit / extent size all
    /// -1, empty sbspace name, and an all-defaults column descriptor whose only
    /// non-zero words are two -1 ints.
    /// </summary>
    internal static byte[] BuildDefaultLobDescriptor()
    {
        var b = new byte[SqliLobSpec.Size];
        IfxWireConverter.WriteInt32(b.AsSpan(0, 4), unchecked((int)0xDADAFEED)); // MagicNumber
        WriteUdtLong(b.AsSpan(12), -1);           // estimatedNoBytes (+ pad short at 22)
        WriteUdtLong(b.AsSpan(24), -1);           // sizeLimit (+ pad short at 34)
        IfxWireConverter.WriteInt32(b.AsSpan(36, 4), -1); // extentSize
        // [40..168): sbspace name, 128 NUL bytes (server default sbspace).
        // [168..596): rawColDescriptor — all zero except two -1 ints at [572..580).
        IfxWireConverter.WriteInt32(b.AsSpan(572, 4), -1);
        IfxWireConverter.WriteInt32(b.AsSpan(576, 4), -1);
        return b;
    }

    // IfxUDTOutput.writeLong / IfxUDTInput.readLong: a long inside an opaque UDT
    // travels as [|v| low word:int32][|v| high word:int32][sign:int16] where the
    // sign is 1/-1 and 0 marks null.
    internal static void WriteUdtLong(Span<byte> target, long value)
    {
        ulong magnitude = value < 0 ? (ulong)(-value) : (ulong)value;
        IfxWireConverter.WriteInt32(target[..4], unchecked((int)(magnitude & 0xFFFFFFFF)));
        IfxWireConverter.WriteInt32(target.Slice(4, 4), unchecked((int)(magnitude >> 32)));
        short sign = value < 0 ? (short)-1 : value > 0 ? (short)1 : (short)0;
        target[8] = (byte)(sign >> 8);
        target[9] = (byte)sign;
    }

    internal static long ReadUdtLong(ReadOnlySpan<byte> source)
    {
        uint low = (uint)IfxWireConverter.ReadInt32(source[..4]);
        uint high = (uint)IfxWireConverter.ReadInt32(source.Slice(4, 4));
        short sign = (short)((source[8] << 8) | source[9]);
        long magnitude = (long)(((ulong)high << 32) | low);
        return sign < 0 ? -magnitude : sign == 0 ? 0 : magnitude;
    }

    // Port of receiveHandle: the SQ_GETROUTINE reply — the routine's database
    // name (named-parameters servers) and its FastPath handle.
    private void ReceiveRoutineHandle()
    {
        _fpReplyDb = _in.ReadChar();
        _fpReplyHandle = _in.ReadInt32();
    }

    // Port of receiveFastPath: the SQ_FPROUTINE reply carrying the routine's
    // return values. Each value is a type word (owner/name strings follow for
    // codes >= 18 except the BIGINT pair), an indicator, a precision, then the
    // payload: UDT values as [len:int32][padded bytes], integers bare.
    private void ReceiveFastPath()
    {
        short count = _in.ReadInt16();
        var values = new List<object?>(count);
        for (int i = 0; i < count; i++)
        {
            short typeWord = _in.ReadInt16();
            bool isDistinct = (typeWord & 0x800) != 0;
            short baseType = (short)(typeWord & 255);
            if ((baseType >= 18 && baseType != 52 && baseType != 53) || isDistinct)
            {
                _ = _in.ReadChar(); // extended owner
                _ = _in.ReadChar(); // extended type name
            }
            short indicator = _in.ReadInt16();
            short precision = _in.ReadInt16();
            if (indicator == -1)
            {
                values.Add(null);
                continue;
            }
            values.Add(ReadFastPathValue(baseType, precision));
        }
        _fpReturns = values;
    }

    // One SQ_FPROUTINE value payload, keyed by the reply's base type word (port of
    // receiveFastPath's switch). BOOLEAN(45) and LVARCHAR(43) never appear here: the
    // server reports them as SENDRECV(44) with the length-prefixed opaque framing
    // (cleanUpFPTypeInfo), so they are decoded by the caller from the extended type name.
    private object? ReadFastPathValue(short baseType, short precision) =>
        (SqliTypeCode)baseType switch
        {
            SqliTypeCode.Char or SqliTypeCode.VarChar
                or SqliTypeCode.NChar or SqliTypeCode.NVarChar => _in.ReadChar(),
            SqliTypeCode.SmallInt => _in.ReadInt16(),
            SqliTypeCode.Int or SqliTypeCode.Serial => _in.ReadInt32(),
            SqliTypeCode.Int8 or SqliTypeCode.Serial8 => _in.ReadInt8(),
            SqliTypeCode.BigInt or SqliTypeCode.BigSerial => _in.ReadBigInt(),
            SqliTypeCode.SmFloat => _in.ReadReal(),
            SqliTypeCode.Float => _in.ReadDouble(),
            SqliTypeCode.Decimal or SqliTypeCode.Money => _in.ReadDecimal(precision),
            SqliTypeCode.Date => _in.ReadDate() is { } date ? DateOnly.FromDateTime(date) : null,
            // Same CLR shapes DecodeValue gives a result-set column of the type: a
            // time-only DATETIME is a TimeOnly, a day-time INTERVAL a TimeSpan.
            SqliTypeCode.DateTime => IfxQualifier.GetStartCode(precision) >= 6
                ? _in.ReadPackedValue(precision, isDateTime: true).ToTimeOnly()
                : _in.ReadPackedValue(precision, isDateTime: true).ToDateTime(),
            SqliTypeCode.Interval => _in.ReadPackedValue(precision).ToInterval() switch
            {
                IfxIntervalDayFraction df => (object?)df.ToTimeSpan(),
                IfxIntervalYearMonth ym => new IfxMonthSpan(ym),
                _ => null,
            },
            SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed or SqliTypeCode.SendRecv =>
                ReadFastPathOpaque(),
            _ => throw new IfxException(
                $"FastPath return type {baseType} is not ported yet (-408).", -408),
        };

    // An opaque/UDT FastPath value: [len:int32][padded bytes].
    private byte[] ReadFastPathOpaque()
    {
        int length = _in.ReadInt32();
        return length <= 0 ? [] : _in.ReadPadded(length);
    }

    // SQ_LODATA reply: opType 2 acknowledges a write with the byte count
    // (negative = ISAM error); any other opType is a read reply whose data
    // arrives as [size:int16][padded bytes] chunks totalling min(requested, size).
    private void ReceiveLoData()
    {
        short opType = _in.ReadInt16();
        int fileSize = _in.ReadInt32();
        if (opType == 2)
        {
            if (fileSize < 0)
                _pending = new IfxException($"Smart-blob write failed (isam {fileSize}).", -9810, isamCode: (short)fileSize);
            else
                _loAmount = fileSize;
            return;
        }
        if (fileSize < 1)
        {
            _ = _in.ReadInt16();
            _loAmount = 0;
            return;
        }
        int remaining = Math.Min(_loReadBuffer?.Length - _loReadOffset ?? 0, fileSize);
        while (remaining > 0)
        {
            short size = _in.ReadInt16();
            if (size < 0)
                return;
            byte[] chunk = _in.ReadPadded(size);
            if (_loReadBuffer is not null)
            {
                int n = Math.Min(size, _loReadBuffer.Length - _loReadOffset);
                Array.Copy(chunk, 0, _loReadBuffer, _loReadOffset, n);
                _loReadOffset += n;
                _loAmount += n;
            }
            remaining -= size;
        }
    }

    // ===================================================================== //
    //  CommandTimeout enforcement                                           //
    //  -- extracted to SqliTimeoutTimer; this is a one-line forward so      //
    //  IfxCommand, IfxBatch, IfxBulkLoader and CommandTimeoutTests are      //
    //  unaffected.                                                          //
    // ===================================================================== //

    /// <inheritdoc cref="SqliTimeoutTimer.Begin"/>
    public IDisposable BeginCommandTimeout(int seconds) => _timeoutTimer.Begin(seconds);

    // ===================================================================== //
    //  Framing                                                              //
    // ===================================================================== //

    /// <summary>Terminates the outbound request with SQ_EOT and flushes (port of <c>flip</c>/<c>sendEOT</c>).</summary>
    private void Flip()
    {
        _out.WriteInt16((short)SqliMessage.Eot);
        _out.Flush();
        _out.SetByteCountAtEot();
        _link.IncrementRoundTrips();
    }

    /// <summary>
    /// Reads reply messages and dispatches each until end-of-transmission,
    /// then rethrows any server error collected along the way (port of
    /// <c>receiveMessage</c> + <c>dispatchMsg</c>).
    /// </summary>
    public void ReceiveMessage()
    {
        _pending = null;
        _diagnostics = null;
        OutParameterRoutine = null;
        while (true)
        {
            var msg = (SqliMessage)_in.ReadInt16();
            bool keepGoing = Dispatch(msg);
            if (!keepGoing || msg == SqliMessage.Eot || msg == SqliMessage.Exit)
                break;
        }
        if (_pending is not null)
            throw _pending;
    }

    // One handler per reply message the driver understands. A message with an unknown code cannot be skipped (its body
    // length is unknown), so it becomes a protocol error (-408) and stops the loop.
    private bool Dispatch(SqliMessage msg)
    {
        switch (msg)
        {
            case SqliMessage.Version:
                ReceiveVersion();
                return true;
            case SqliMessage.Protocols:
                ReceiveProtocols();
                return true;
            case SqliMessage.Done:
                ReceiveDone();
                return true;
            case SqliMessage.Describe:
                ReceiveDescribe();
                return true;
            case SqliMessage.Tuple:
                ReceiveTuple();
                return true;
            case SqliMessage.TupId:
                ReceiveTupleId();
                return true;
            case SqliMessage.Blob:
                ReceiveBlob();
                return true;
            case SqliMessage.Close:
                // The server auto-closed the cursor at end of rows.
                _closeReceived = true;
                return true;
            // Optimizer estimates (rows, I/O); not used.
            case SqliMessage.Cost:
                _ = _in.ReadInt32(); // estimated rows (sqlerrd[0])
                _ = _in.ReadInt32(); // estimated I/O (sqlerrd[3])
                return true;
            case SqliMessage.InsertDone:
                ReceiveInsertDone();
                return true;
            case SqliMessage.PutErr:
                ReceivePutError();
                return true; // keep reading to EOT so the connection stays usable
            case SqliMessage.GetRoutine:
                ReceiveRoutineHandle();
                return true;
            case SqliMessage.FpRoutine:
                ReceiveFastPath();
                return true;
            case SqliMessage.FParam:
                ReceiveFParam();
                return true;
            case SqliMessage.LoData:
                ReceiveLoData();
                return true;
            case SqliMessage.XactStat:
                ReceiveXactStat();
                return true;
            case SqliMessage.Err:
                ReceiveError();
                return true;
            case SqliMessage.Eot:
                return true;
            case SqliMessage.Exit:
                return true;
            default:
                // Unimplemented reply code: fail loudly rather than mis-read the stream.
                _pending = new IfxException(
                    $"Unhandled SQLI reply message {(short)msg} ({msg}); the codec for it is not ported yet (-408).",
                    -408);
                return false;
        }
    }

    // ===================================================================== //
    //  Reply handlers                                                       //
    // ===================================================================== //

    // Port of receiveVersion.
    private void ReceiveVersion() => _link.ServerVersion = _in.ReadChar();

    // A row count / row id: 8 bytes once the server has granted isLongRowId
    // (IfxDataInputStream.readLongBigint is a plain big-endian int64), else 4.
    private long ReadRowCount() =>
        Capabilities.IsLongRowId ? _in.ReadBigInt() : _in.ReadInt32();

    // Port of receiveProtocols: a 2-byte length then the server's capability vector.
    private void ReceiveProtocols()
    {
        short len = _in.ReadInt16();
        _link.ServerProtocol = _in.ReadPadded(len);
    }

    // SQ_DONE: warnings + the sqlerrd row-count/rowid/serial values.
    // With isLongRowId the row count and the row id widen to 8 bytes, but the
    // serial (sqlerrd[1]) stays a 4-byte read — do not widen it.
    private void ReceiveDone()
    {
        Warnings = _in.ReadInt16();
        RowsProcessed = (int)ReadRowCount();  // narrowed like IfxSqli.getNumberOfRowsProcessed
        _ = ReadRowCount();                   // rowid (sqlerrd[5])
        SerialInserted = _in.ReadInt32();
        _batchCounts?.Add(RowsProcessed); // one SQ_DONE per row while a batch is running
    }

    // Port of receiveDescribe: the statement/cursor descriptor with one entry
    // per result column. Wire widths are capability-gated
    // (is4ByteOffsetSupported for the string table and field indexes, isUSVER
    // for start positions, the extended block and int-sized lengths).
    private void ReceiveDescribe()
    {
        StatementType = _in.ReadInt16();
        StatementId = _in.ReadInt16();
        _ = _in.ReadInt32(); // estimated cost (sqlerrd[3])
        TupleSize = Capabilities.IsLargeTupleSize ? _in.ReadInt32() : _in.ReadInt16();
        short fieldCount = _in.ReadInt16();
        int stringTableSize = Capabilities.Is4ByteOffsetSupported ? _in.ReadInt32() : _in.ReadInt16();

        var startPositions = new int[fieldCount];
        var typeWords = new short[fieldCount];
        var extendedIds = new int[fieldCount];
        var extendedOwners = new string[fieldCount];
        var extendedNames = new string[fieldCount];
        var sourceTypes = new int[fieldCount];
        var lengths = new int[fieldCount];

        for (int i = 0; i < fieldCount; i++)
        {
            extendedOwners[i] = "";
            extendedNames[i] = "";
            _ = Capabilities.Is4ByteOffsetSupported ? _in.ReadInt32() : _in.ReadInt16(); // field index
            startPositions[i] = Capabilities.IsExtendedDescriptor ? _in.ReadInt32() : _in.ReadInt16();
            typeWords[i] = _in.ReadInt16();
            if (Capabilities.IsExtendedDescriptor)
            {
                extendedIds[i] = _in.ReadInt32();
                extendedOwners[i] = _in.ReadChar();   // extended owner name (e.g. "informix")
                extendedNames[i] = _in.ReadChar();    // extended type name (e.g. "lvarchar")
                _ = _in.ReadInt16();  // reference
                _ = _in.ReadInt16();  // alignment
                sourceTypes[i] = _in.ReadInt32(); // DISTINCT base (source) type id
                lengths[i] = _in.ReadInt32();
            }
            else
            {
                lengths[i] = _in.ReadInt16();
            }
        }

        string[] names = new string[fieldCount];
        if (stringTableSize > 0)
        {
            byte[] table = _in.ReadPadded(stringTableSize);
            string[] tokens = IfxWireConverter.DecodeChar(table, _in.DbEncoding)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < fieldCount; i++)
                names[i] = i < tokens.Length ? tokens[i] : "";
        }

        _columns.Clear();
        _rowNeedsWalk = false;
        _rowHasUnsupported = false;
        for (int i = 0; i < fieldCount; i++)
        {
            var baseType = IfxTypeCodes.BaseType(typeWords[i]);

            // LVARCHAR reaches the client as the opaque built-in UdtVar(40)/UdtFixed(41)
            // carrying extended-type id LVARCHAR(1) — or, on some servers, directly as
            // base type 43. Collapse both to LVarChar so decode/walk/ClrType key off it.
            // LLVC(24) — "informix"."longlvarchar", the >32 KB lvarchar variant — is the
            // same thing with a wider maximum: same [flag:1][len:int32][data] framing,
            // same inline character payload (live-verified against IDS 14.10 with a
            // ::informix.longlvarchar cast), so it collapses to LVarChar too and reads
            // as a string instead of an opaque byte[].
            bool isLvarchar =
                baseType is SqliTypeCode.LVarChar
                || (baseType is SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
                    && extendedIds[i] is (int)IfxExtendedType.LVarChar or (int)IfxExtendedType.Llvc);
            // A smart large object (CLOB/BLOB) likewise arrives as UdtFixed(41) with
            // extended id CLOB(11)/BLOB(10) and owner/name "informix"/"clob"|"blob". Collapse
            // to the driver-level Clob/Blob codes; the in-tuple field is a variable
            // [flag:1][len:int32][72-byte locator] like any opaque UDT.
            bool isSmartBlob =
                baseType is SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
                && extendedIds[i] is (int)IfxExtendedType.Clob or (int)IfxExtendedType.Blob;
            // JSON/BSON are opaque built-ins like LVARCHAR — they reach the client as
            // UdtVar(40)/UdtFixed(41) with extended id JSON(25)/BSON(26), and travel
            // inline with the SAME [flag:1][len:int32][data] framing as LVARCHAR (no
            // locator, unlike CLOB/BLOB). JSON decodes as text; BSON stays raw bytes —
            // decode it further with BsonMediaTypeFormatter.
            bool isJson =
                baseType is SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
                && extendedIds[i] == (int)IfxExtendedType.Json;
            bool isBson =
                baseType is SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
                && extendedIds[i] == (int)IfxExtendedType.Bson;
            // BOOLEAN reaches this client as the opaque built-in UdtVar(40)/UdtFixed(41)
            // carrying extended-type id BOOLEAN(5) — the [flag:1][len:int32][1-byte value]
            // opaque frame. Collapse it to BOOL so DecodeValue/WalkCurrentRow key off it and
            // it reads as a CLR bool (ClrType already reports typeof(bool)) instead of an
            // opaque byte[]. It stays var-length (IsVariableLengthColumn) so the SQ_RET_TYPE
            // fetch negotiation still announces it as 45 + owner/name exactly as it did while
            // the column was UdtFixed — otherwise the server desyncs and drops the connection
            // mid-fetch. (Bare base type BOOL(45) without the xid is left to the existing
            // unsupported-column guard; not observed from a live server here.)
            bool isBoolean =
                baseType is SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed
                && extendedIds[i] == (int)IfxExtendedType.Boolean;
            var effectiveType = isLvarchar
                ? SqliTypeCode.LVarChar
                : isSmartBlob
                    ? (extendedIds[i] == (int)IfxExtendedType.Clob ? SqliTypeCode.Clob : SqliTypeCode.Blob)
                    : isJson
                        ? SqliTypeCode.Json
                        : isBson
                            ? SqliTypeCode.Bson
                            : isBoolean
                                ? SqliTypeCode.Bool
                                : baseType;

            // A DISTINCT type (descriptor flag 0x800) carries its base type in the low byte and
            // names the distinct type in the extended owner/name (e.g. 0x0805 = DISTINCT over
            // DECIMAL, "root"/"percent"). Its tuple payload is exactly the base type's, so we
            // decode it as the base type and only remember the distinct name for the metadata.
            bool isDistinct = (typeWords[i] & (short)IfxTypeFlags.Distinct) != 0;

            _columns.Add(new IfxColumnMetadata
            {
                Name = names[i] ?? "",
                SqliTypeCode = effectiveType,
                TypeWord = typeWords[i],
                Length = lengths[i],
                PrecisionScale = (short)lengths[i],
                ExtendedTypeId = extendedIds[i],
                IsDistinct = isDistinct,
                ExtendedOwner = extendedOwners[i],
                ExtendedTypeName = extendedNames[i],
                SourceType = sourceTypes[i],
                StartPosition = startPositions[i],
            });

            // Classification port of IfxColumnInfo.isVariableLengthType. A DISTINCT over a
            // supported base decodes as that base (fixed or walked per the base type). The
            // complex/opaque types (ROW, SET/MULTISET/LIST/COLLECTION, and opaque UDTs) are
            // all variable-length, so they all walk — but they do NOT share one framing:
            // opaque UDTs carry a leading flag byte and ROW/collections do not. See
            // WalkCurrentRow for the split.
            bool isBigInt = baseType is SqliTypeCode.BigInt or SqliTypeCode.BigSerial;
            bool isComplex = baseType is SqliTypeCode.Row or SqliTypeCode.Set or SqliTypeCode.MultiSet
                or SqliTypeCode.List or SqliTypeCode.Collection or SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed;
            // Decides how rows are read: opaque/complex/LVARCHAR/LOB values and, on servers with variable-length VARCHAR, VARCHAR
            // make the row "walked"; any other type above 18 (except BIGINT/BIGSERIAL) is unsupported.
            if (isLvarchar || isSmartBlob || isComplex)
                _rowNeedsWalk = true; // [flag:1][len:int32][data] — variable per row
            else if ((short)baseType > 18 && !isBigInt)
                _rowHasUnsupported = true;
            else if (baseType is SqliTypeCode.VarChar or SqliTypeCode.NVarChar && Capabilities.IsVarcharVarLenSupported)
                _rowNeedsWalk = true;
        }
    }

    // SQ_TUPLE: reads the PDU's warnings word and size (the frame header,
    // whose width depends on Capabilities -- not buffer state), then hands the row's
    // bytes to SqliTupleBuffer.Append, which records its cumulative end offset.
    private void ReceiveTuple()
    {
        _ = _in.ReadInt16(); // warnings
        int size = Capabilities.IsExtendedDescriptor ? _in.ReadInt32() : _in.ReadInt16();
        if (size <= 0)
            return;
        _tupleBuffer.Append(_in, size, GetTupleBufferSize());
    }

    // ReceiveBlob/receiveText: appends one SQ_BLOB data frame to the
    // in-flight blob buffer. Large objects arrive as several frames; the receive
    // loop dispatches each until SQ_DONE/SQ_EOT. BYTE and TEXT both travel as
    // SQ_BLOB(39). After the fetch (fetchCodesetConvertedTextBlob) DecodeBlobColumn
    // decodes a TEXT buffer straight from DB_LOCALE to a string.
    // The DB_LOCALE conversion happens — one layer up, not here.
    private void ReceiveBlob()
    {
        int datalen = _in.ReadInt16() & 0xFFFF; // frame chunk size (server caps well under 32K)
        if (datalen == 0)
            return;
        byte[] chunk = _in.ReadPadded(datalen);
        if (_blobBuffer is null)
            return; // no active fetch: drop (defensive)
        int n = Math.Min(datalen, _blobBuffer.Length - _blobWriteOffset);
        if (n > 0)
        {
            Array.Copy(chunk, 0, _blobBuffer, _blobWriteOffset, n);
            _blobWriteOffset += n;
        }
    }

    // SQ_TUPID: the absolute row id of the first tuple
    // in the buffer. Scroll cursors send it ahead of the tuples so buffer offsets
    // can be mapped back to result-set positions (see CurrentTupleId).
    private void ReceiveTupleId() => _tupleBuffer.SetTupleId(_in.ReadInt32());

    // Port of receiveInsertDone: SERIAL8 (and BIGSERIAL when supported) values.
    private void ReceiveInsertDone()
    {
        Serial8Inserted = _in.ReadInt8();
        if (Capabilities.IsBigIntSupported)
            BigSerialInserted = _in.ReadBigInt();
    }

    // SQ_PUTERR (18): an insert-cursor SQ_PUT failed on a row. Layout captured from an
    // IDS 9.59 dbaccess LOAD trace — a rows-inserted count, then exactly the SQ_ERR
    // body. The count is how many rows of the batch the server committed before the
    // failing one (it stops at the first bad row). Delegating the tail to ReceiveError
    // is what receivePutErr does, and it means the MI/SQLSTATE variant below is handled
    // here too rather than desyncing the stream.
    private void ReceivePutError()
    {
        RowsProcessed = (int)ReadRowCount();
        ReceiveError();
    }

    // Port of receiveXactstat: a transaction-state notification (3 shorts of detail).
    private void ReceiveXactStat()
    {
        _in.ReadInt16();
        _in.ReadInt16();
        _in.ReadInt16();
    }

    // SQ_ERR: One opcode, three body shapes -- and the third one is why
    // this method must read past the first message string:
    //
    //   * the ordinary error: [sqlcode][isam][offset][message]
    //   * sqlcode 100 -- SQLSTATE 02000 "no rows", a warning rather than a failure
    //   * the MI (DataBlade/UDR) diagnostic, sqlcode -937 for its error form and 0 for
    //     its warning form. Its body carries TWO fields beyond the common tail: a raw
    //     5-byte SQLSTATE and the actual diagnostic text -- and the string in the usual
    //     message slot is not the message at all, it is the name of the routine that
    //     raised it.
    //
    // Stopping after that first string (as this did before) leaves the SQLSTATE and the
    // text unread, so the next ReceiveMessage takes their bytes for a message id. Live
    // against IDS 9.59.FC5, a failing binaryvar cast replied
    //     [13][-937][0][offset]["binaryvarout"]["UBD01"]["memory allocation failed"]
    // and the driver surfaced "Unhandled SQLI reply message 17456" -- 0x4430, the "D0"
    // of "UBD01" -- with the real diagnostic lost and the connection unusable.
    //
    // (-619 gets a dedicated resync path. It is not reproduced here because the
    // byte counts already agree: gobbleSmallInts(2) + the conditional gobbleSmallInts(1)
    // consume exactly the isam word plus the 2- or 4-byte offset read below.)
    private void ReceiveError()
    {
        short sqlcode = _in.ReadInt16();
        short isamcode = _in.ReadInt16();
        int offset = Capabilities.IsRemove64KLimitSupported ? _in.ReadInt32() : _in.ReadInt16();
        _ = offset;

        string message = sqlcode == -368 ? "" : _in.ReadChar().Trim();

        // Keep the routine identity so the caller can re-run the statement through FastPath.
        if (sqlcode == RoutineHasOutParametersSqlCode)
        {
            // Not really a failure: the server is telling us the routine we tried to
            // PREPARE declares OUT/INOUT arguments, which prepare/execute cannot carry.
            // The message is the "db:handle:unique" triple identifying the routine for
            // the FastPath channel (IfxCallableStatement's -9752 handler). It is still
            // raised as an exception -- the caller decides whether to re-drive the call.
            OutParameterRoutine = ParseOutParameterRoutine(message);
        }

        if (sqlcode == 100)
        {
            // SQLSTATE 02000 "no rows" — a warning, not an error.
            return;
        }
        if (sqlcode != 0 && sqlcode != MiDiagnosticSqlCode)
        {
            _pending ??= new IfxException(
                message.Length > 0 ? message : IfxException.TextForEmptyMessage(sqlcode),
                sqlCode: sqlcode, isamCode: isamcode);
            return;
        }

        // The MI variant. `message` above was the routine name, not the diagnostic.
        string routine = message;
        string sqlState = IfxWireConverter.DecodeChar(_in.ReadPadded(5), _in.DbEncoding).Trim();
        string diagnostic = _in.ReadChar().Trim();

        if (sqlcode == MiDiagnosticSqlCode)
        {
            // The routine name is appended rather than dropped:
            // "memory allocation failed" alone does not say which UDR failed, and for a
            // DataBlade error that is the single most useful thing the reply carries.
            string text = diagnostic.Length > 0 ? diagnostic : IfxException.TextForEmptyMessage(sqlcode);
            _pending ??= new IfxException(
                routine.Length > 0 ? $"{text} ({routine})" : text,
                sqlCode: sqlcode, isamCode: isamcode, sqlState: sqlState);
            return;
        }

        // sqlcode 0: the warning form forces the isam code to 0 here, since the
        // word belongs to the diagnostic and not to a failure. Surfaced through
        // InfoMessage instead of being swallowed like the sqlcode-100 case above.
        AddDiagnostic(new IfxError(
            routine.Length > 0 ? $"{diagnostic} ({routine})" : diagnostic,
            nativeError: 0, source: "Informix.Data", sqlState: sqlState));
    }
}
