using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Informix.Data.Sqli;
using Informix.Data.Wire;

namespace Informix.Data.Asf;

/// <summary>
/// The ASF transport: opens the TCP session to the Informix listener, performs
/// the connection handshake and then hands its <see cref="Reader"/>/<see cref="Writer"/>
/// to the SQLI layer.
///
/// <para>TLS (<c>SSLCONNECTION</c>) is supported: with <see cref="AsfConnectionSettings.UseSsl"/>
/// the socket is wrapped in an <see cref="SslStream"/> before the handshake, the
/// client side of an Informix <c>onsocssl</c> listener.</para>
///
/// <para>Not supported yet:
/// CSM/password encryption (<c>SECURITY=...</c>), the HTTP proxy transport,
/// sqlhosts/LDAP server lookup and automatic redirect-following — a redirect
/// currently surfaces as <see cref="AsfRedirectException"/>.</para>
///
/// <para>For tests, the internal constructor accepts any duplex
/// <see cref="Stream"/> in place of the socket.</para>
/// </summary>
internal sealed class AsfConnection : IDisposable
{
    private const int StreamBufferSize = 4096; // STREAM_BUF_SIZE

    private readonly AsfConnectionSettings _settings;
    private TcpClient? _tcpClient;
    // Read and write are buffered *separately* over the socket. A single duplex
    // BufferedStream cannot straddle a non-seekable stream — a Write while its read
    // buffer still holds read-ahead bytes throws NotSupportedException (and, when it
    // does not throw, the leftover bytes desync the exchange and the server drops the
    // connection). Two one-directional buffers never interleave, mirroring the JDBC
    // driver's BufferedInputStream + BufferedOutputStream pair. On the injected test
    // transport _stream carries both directions and _writeStream/_transport stay null.
    private Stream? _stream;         // read side (live) or the injected duplex (tests)
    private Stream? _writeStream;    // write side (live only)
    private Stream? _transport;      // the shared underlying socket/TLS/trace stream (live only)
    private IfxWireReader? _reader;
    private IfxWireWriter? _writer;
    private bool _connected;

    public AsfConnection(AsfConnectionSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Test seam: runs the handshake over a supplied duplex stream instead of a socket.</summary>
    internal AsfConnection(AsfConnectionSettings settings, Stream duplexStream)
        : this(settings)
    {
        _stream = duplexStream;
    }

    /// <summary>The server identity from the accept frame; set after <see cref="Connect"/>.</summary>
    public IfxServerInfo? ServerInfo { get; private set; }

    /// <summary>The resolved host this transport dials (for trace <c>server.address</c>).</summary>
    public string Host => _settings.Host;

    /// <summary>The resolved TCP port this transport dials (for trace <c>server.port</c>).</summary>
    public int Port => _settings.Port;

    public bool IsConnected => _connected;

    // Set once a cancel byte went out. Such a connection may still have a statement in flight, so the pool
    // discards it instead of reusing it.
    private volatile bool _oobSent;

    /// <summary>
    /// True once an out-of-band cancel byte has been sent (or attempted) on this physical
    /// connection. Such a connection is <i>tainted</i>: the byte is not tied to the statement
    /// it was aimed at, so one that arrives after that statement finished can interrupt the
    /// next one (SQLCODE -213), and no protocol barrier proves otherwise. The pool therefore
    /// retires a tainted connection instead of reusing it. Never cleared.
    /// </summary>
    internal bool OutOfBandSent => _oobSent;

    /// <summary>
    /// Cheap liveness probe for connection pooling: reports whether the socket is still
    /// usable (peer has not closed it). Uses a non-blocking <see cref="Socket.Poll"/> —
    /// a readable socket with zero bytes available means the peer sent FIN. For the
    /// injected test transport (no socket) it falls back to <see cref="IsConnected"/>.
    /// </summary>
    public bool IsSocketConnected()
    {
        if (!_connected)
            return false;
        var socket = _tcpClient?.Client;
        if (socket is null)
            return _connected; // injected test stream: trust the connected flag
        try
        {
            return socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>The wire reader the SQLI layer consumes.</summary>
    public IfxWireReader Reader =>
        _reader ?? throw new InvalidOperationException("The ASF connection is not established.");

    /// <summary>The wire writer the SQLI layer consumes.</summary>
    public IfxWireWriter Writer =>
        _writer ?? throw new InvalidOperationException("The ASF connection is not established.");

    /// <summary>
    /// Opens the socket (unless one was injected), sends the connection request
    /// and parses the response (port of the constructor tail:
    /// <c>openSocket</c> + <c>getIfxDataStreams</c> + <c>establishConnection</c>).
    /// </summary>
    public IfxServerInfo Connect()
    {
        if (_connected)
            throw new InvalidOperationException("The ASF connection is already established.");

        if (_stream is null)
            OpenSocket();

        _reader = new IfxWireReader(_stream!) { DbEncoding = _settings.DbEncoding };
        _writer = new IfxWireWriter(_writeStream ?? _stream!) { DbEncoding = _settings.DbEncoding };

        try
        {
            AsfFrameCodec.WriteConnectionRequest(_writer, _settings);
            _writer.Flush();
            ServerInfo = AsfFrameCodec.ReadConnectionResponse(_reader);
        }
        catch (IOException e)
        {
            Dispose();
            throw new IfxException(
                $"Lost connection to Informix server '{_settings.ServerName}' during the handshake (-908).",
                -908, inner: e);
        }
        // Exception filter used for its side effect: Cleanup() disposes the connection and returns false, so the
        // original exception propagates unchanged (with its stack) after the cleanup.
        catch (Exception) when (Cleanup())
        {
            throw; // Cleanup() always returns false; this never handles.
        }

        _connected = true;
        return ServerInfo;
    }

    private bool Cleanup()
    {
        Dispose();
        return false;
    }

    // Port of openSocket()'s plain-TCP branch, including the retry loop.
    private void OpenSocket()
    {
        for (int retryNumber = 0; ; retryNumber++)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                if (_settings.LoginTimeoutSeconds > 0)
                {
                    if (!client.ConnectAsync(_settings.Host, _settings.Port)
                            .Wait(TimeSpan.FromSeconds(_settings.LoginTimeoutSeconds)))
                    {
                        throw new TimeoutException(
                            $"Connecting to {_settings.Host}:{_settings.Port} timed out after {_settings.LoginTimeoutSeconds}s.");
                    }
                }
                else
                {
                    client.Connect(_settings.Host, _settings.Port);
                }

                // SQLI is request/reply with small PDUs; Nagle's algorithm would only add latency.
                client.NoDelay = true;
                if (_settings.SocketKeepAlive)
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                var network = client.GetStream();
                if (_settings.SocketTimeoutSeconds > 0)
                {
                    network.ReadTimeout = _settings.SocketTimeoutSeconds * 1000;
                    network.WriteTimeout = _settings.SocketTimeoutSeconds * 1000;
                }

                // TLS: an Informix onsocssl listener speaks implicit TLS — the handshake
                // runs right after connect, before any SQLI byte. Wrap the socket in an
                // SslStream so the ASF handshake and the whole SQLI conversation are
                // encrypted.
                Stream transport = _settings.UseSsl ? OpenTls(network) : network;

                _tcpClient = client;
                // Trace at the wire (below buffering) so the dump reflects real socket
                // I/O, then buffer each direction independently over that one transport.
                _transport = WireTraceStream.MaybeWrap(transport);
                _stream = new BufferedStream(_transport, StreamBufferSize);       // reads
                _writeStream = new BufferedStream(_transport, StreamBufferSize);  // writes
                return;
            }
            // Exception filter used for its side effect: a failed attempt (connect, socket options,
            // TLS) closes its own TcpClient before the retry or the rethrow; ReleaseAttempt returns false.
            catch (Exception) when (_tcpClient is null && ReleaseAttempt(client))
            {
                throw; // never reached
            }
            catch (Exception e) when (e is SocketException or TimeoutException or AggregateException)
            {
                if (retryNumber >= _settings.RetryCount)
                {
                    throw new IfxException(
                        $"Cannot connect to Informix server '{_settings.ServerName}' at {_settings.Host}:{_settings.Port} (-908).",
                        -908, inner: e);
                }
                if (_settings.ConnectTimeSeconds > 0 && _settings.RetryCount > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(
                        (double)_settings.ConnectTimeSeconds / _settings.RetryCount));
                }
            }
        }
    }

    private static bool ReleaseAttempt(TcpClient? client)
    {
        client?.Dispose();
        return false;
    }

    // Runs the client TLS handshake over the raw socket stream. With
    // TrustServerCertificate the platform's chain/host-name errors are tolerated so a
    // self-signed server cert (typical for an internal onsocssl listener) is accepted;
    // otherwise the default chain validation applies. Certificate name checking is by
    // TargetHost (SNI).
    private SslStream OpenTls(NetworkStream network)
    {
        var ssl = new SslStream(network, leaveInnerStreamOpen: false, ValidateServerCertificate);
        try
        {
            ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = _settings.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
        }
        // A non-TLS reply is an AuthenticationException; a peer that drops the connection or
        // stalls past the receive timeout mid-handshake is an IOException. Both are the same
        // configuration/transport failure (e.g. UseSsl against a plain listener) and surface as -908.
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            ssl.Dispose();
            throw new IfxException(
                $"TLS handshake with Informix server '{_settings.ServerName}' failed (-908). {e.Message}",
                -908, inner: e);
        }
        return ssl;
    }

    private bool ValidateServerCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        => sslPolicyErrors == SslPolicyErrors.None || _settings.TrustServerCertificate;

    /// <summary>
    /// Interrupts a running statement by sending the out-of-band cancel byte
    /// (port of <c>sOOBSocket</c>; used by <c>IfxCancelQueryImpl</c>).
    /// </summary>
    public void SendOutOfBand()
    {
        _oobSent = true; // set before the send: even a failed/partial attempt taints
        if (_tcpClient is null)
        {
            // Test transport (injected stream, no socket): signal the stream's
            // out-of-band sink if it provides one, so cancel-on-timeout is
            // observable without a real socket.
            (_stream as IOutOfBandSink)?.SignalOutOfBand();
            return;
        }
        try
        {
            _tcpClient.Client.Send([AsfProtocol.OutOfBandByte], SocketFlags.OutOfBand);
        }
        catch (SocketException e)
        {
            throw new IfxException("Failed to send the out-of-band cancel (-908).", -908, inner: e);
        }
    }

    /// <summary>
    /// Bounds a single blocking receive at <paramref name="milliseconds"/> — the
    /// backstop that keeps an unresponsive server (one that never answers a cancel)
    /// from hanging the caller forever. No-op for the injected test transport.
    /// </summary>
    public void SetReceiveTimeout(int milliseconds)
    {
        if (_tcpClient is not null)
            _tcpClient.Client.ReceiveTimeout = milliseconds;
    }

    /// <summary>Restores the socket receive timeout to the configured value (0 = infinite).</summary>
    public void RestoreReceiveTimeout()
    {
        if (_tcpClient is not null)
            _tcpClient.Client.ReceiveTimeout = _settings.SocketTimeoutSeconds * 1000;
    }

    /// <summary>
    /// Sends SQ_EXIT and waits for the server's acknowledgement, draining any
    /// pending SQ_XACTSTAT frames (port of <c>disconnectOrderly</c>).
    /// </summary>
    public void DisconnectOrderly()
    {
        if (_writer is null || _reader is null)
        {
            Dispose();
            return;
        }

        try
        {
            _writer.WriteInt16((short)SqliMessage.Exit);
            _writer.Flush();
        }
        catch (IOException e)
        {
            Dispose();
            throw new IfxException("Failed to send SQ_EXIT (-908).", -908, inner: e);
        }

        try
        {
            short lastMessage = _reader.ReadInt16();
            while (lastMessage == (short)SqliMessage.XactStat)
            {
                _reader.ReadInt16();
                _reader.ReadInt16();
                _reader.ReadInt16();
                lastMessage = _reader.ReadInt16();
            }
            if (lastMessage != (short)SqliMessage.Exit && lastMessage != (short)SqliMessage.Eot)
                throw new IfxException($"Unexpected reply {lastMessage} to SQ_EXIT (-408).", -408);
        }
        // The server may close the socket without acknowledging; at this point that is not an error.
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {

        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Closes without the SQ_EXIT exchange (used for connections already in an unknown state).</summary>
    public void DisconnectAbortive()
    {
        try
        {
            _writer?.Flush();
        }
        catch (IOException)
        {
            // best effort
        }
        finally
        {
            Dispose();
        }
    }

    // Flush the write buffer and release the transport and socket; every step is best effort so cleanup never throws.
    public void Dispose()
    {
        _connected = false;
        _reader = null;
        _writer = null;
        // Live path: both buffers share _transport, so dispose the transport once
        // (flushing any pending write first). Test path: _transport is null and _stream
        // is the injected duplex we own directly.
        try { _writeStream?.Flush(); } catch { /* best effort */ }
        try { (_transport ?? _stream)?.Dispose(); } catch { }
        try { _tcpClient?.Dispose(); } catch { }
        _stream = null;
        _writeStream = null;
        _transport = null;
        _tcpClient = null;
    }
}

/// <summary>
/// Implemented by a test transport stream so <see cref="AsfConnection.SendOutOfBand"/>
/// can deliver the cancel out-of-band signal in the absence of a real socket. A
/// live connection uses the socket's <see cref="SocketFlags.OutOfBand"/> path
/// instead and never touches this seam.
/// </summary>
internal interface IOutOfBandSink
{
    void SignalOutOfBand();
}
