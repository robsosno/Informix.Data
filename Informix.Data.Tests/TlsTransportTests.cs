using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Informix.Data;
using Informix.Data.Asf;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// TLS transport (Informix <c>onsocssl</c> / <c>SSLCONNECTION</c>). The connection-string
/// surface is covered directly; the <see cref="AsfConnection"/> SSL path is exercised over
/// a real loopback <see cref="SslStream"/> so the handshake, certificate-trust decision and
/// the ASF response decode over the encrypted channel are all tested without an external server.
/// </summary>
public class TlsTransportTests
{
    // ---- connection-string surface ----

    [Fact]
    public void Ssl_and_trust_options_parse_from_the_connection_string()
    {
        var b = new IfxConnectionStringBuilder(
            "Host=h;Service=9089;Database=db;SSL Connection=true;Trust Server Certificate=true");
        Assert.True(b.SslConnection);
        Assert.True(b.TrustServerCertificate);
    }

    [Fact]
    public void Encrypt_is_a_synonym_for_ssl_connection()
    {
        Assert.True(new IfxConnectionStringBuilder("Host=h;Service=9089;Database=db;Encrypt=true").SslConnection);
    }

    [Fact]
    public void Ssl_options_default_to_off()
    {
        var b = new IfxConnectionStringBuilder("Host=h;Service=9088;Database=db");
        Assert.False(b.SslConnection);
        Assert.False(b.TrustServerCertificate);
    }

    // ---- loopback TLS transport ----

    [Fact]
    public async Task Connect_completes_the_asf_handshake_over_tls_when_the_cert_is_trusted()
    {
        using var cert = CreateSelfSignedServerCert();
        byte[] accept = AsfTestKit.BuildResponse(AsfProtocolMirror.Accept, version: "9.59.FC5");
        var (listener, serverTask) = StartTlsAcceptServer(cert, accept);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var conn = new AsfConnection(TlsSettings(port, trust: true));

            var info = conn.Connect(); // TCP + TLS handshake + ASF accept decode

            Assert.True(conn.IsConnected);
            Assert.Equal("9.59.FC5", info.VersionNumber);
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(2000)); // server task races the client teardown
        }
    }

    [Fact]
    public async Task Connect_rejects_an_untrusted_self_signed_cert()
    {
        using var cert = CreateSelfSignedServerCert();
        byte[] accept = AsfTestKit.BuildResponse(AsfProtocolMirror.Accept);
        var (listener, serverTask) = StartTlsAcceptServer(cert, accept);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var conn = new AsfConnection(TlsSettings(port, trust: false));

            // The self-signed cert cannot chain to a trusted root and TrustServerCertificate
            // is off, so the client aborts the handshake — surfaced as a -908 IfxException.
            var ex = Assert.Throws<IfxException>(() => conn.Connect());
            Assert.Equal(-908, ex.IfxSqlCode);
            Assert.False(conn.IsConnected);
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(2000));
        }
    }

    private static AsfConnectionSettings TlsSettings(int port, bool trust) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        UserName = "usr",
        Password = "pwd",
        ServerName = "ol_ssl",
        DatabaseName = "db",
        UseSsl = true,
        TrustServerCertificate = trust,
    };

    private static X509Certificate2 CreateSelfSignedServerCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Reload through PKCS#12 so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    // A one-shot loopback TLS server: completes the server handshake, pushes the canned ASF
    // accept response, then drains whatever the client sends until it disconnects.
    private static (TcpListener listener, Task serverTask) StartTlsAcceptServer(
        X509Certificate2 cert, byte[] responseBytes)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var net = client.GetStream();
                await using var ssl = new SslStream(net, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(cert);
                await ssl.WriteAsync(responseBytes);
                await ssl.FlushAsync();
                var buf = new byte[1024];
                while (await ssl.ReadAsync(buf) > 0) { }
            }
            catch
            {
                // A rejected client (untrusted-cert test) aborts the handshake; the server
                // side then throws — expected, nothing to assert here.
            }
        });
        return (listener, serverTask);
    }

    // ---- read/write interleave (regression for the single duplex BufferedStream bug) ----

    // Reproduces the transport bug that broke every live statement: the read side buffers
    // trailing bytes via read-ahead (a BufferedStream pulls a whole chunk off the socket),
    // so after Connect() the client's read buffer is NOT empty. When the reader and writer
    // shared one BufferedStream, the very next write threw
    //   NotSupportedException: Cannot write to a BufferedStream while the read buffer is not
    //   empty if the underlying stream is not seekable.
    // (that surfaced live as the PAM-handshake failure), and when it happened not to throw
    // the leftover bytes desynced the exchange and the server dropped the connection. The
    // fix buffers each direction separately, so a write no longer depends on the read buffer
    // being drained. Run over a real socket (plain + TLS) because both transports are
    // non-seekable — the injected in-memory test stream is seekable and cannot expose it.

    [Fact]
    public Task Interleaved_write_after_a_buffered_read_succeeds_over_a_plain_socket()
        => AssertInterleavedWriteSucceeds(useTls: false);

    [Fact]
    public Task Interleaved_write_after_a_buffered_read_succeeds_over_tls()
        => AssertInterleavedWriteSucceeds(useTls: true);

    private static async Task AssertInterleavedWriteSucceeds(bool useTls)
    {
        const short trailer = 0x2A2A; // a SQLI-ish word the client's read-ahead will buffer
        byte[] responseBytes =
        [
            .. AsfTestKit.BuildResponse(AsfProtocolMirror.Accept, version: "9.59.FC5"),
            (byte)(trailer >> 8), (byte)(trailer & 0xFF), // sent in the SAME write as the accept frame
        ];

        using var cert = useTls ? CreateSelfSignedServerCert() : null;
        var (listener, serverTask) = useTls
            ? StartTlsAcceptServer(cert!, responseBytes)
            : StartPlainAcceptServer(responseBytes);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var conn = new AsfConnection(InterleaveSettings(port, useTls));

            conn.Connect(); // reads the accept frame; read-ahead leaves the trailer buffered
            Assert.True(conn.IsConnected);

            // The regression: with the old shared BufferedStream this threw NotSupportedException.
            conn.Writer.WriteInt16(0x1234);
            conn.Writer.Flush();

            // The buffered trailer is still intact and readable from the independent read side.
            Assert.Equal(trailer, conn.Reader.ReadInt16());
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(2000));
        }
    }

    private static AsfConnectionSettings InterleaveSettings(int port, bool useTls) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        UserName = "usr",
        Password = "pwd",
        ServerName = useTls ? "ol_ssl" : "ol_test",
        DatabaseName = "db",
        UseSsl = useTls,
        TrustServerCertificate = useTls,
    };

    // Plaintext twin of StartTlsAcceptServer: reads the connection request, sends the scripted
    // bytes in a single write (so the client's read-ahead buffers everything past the accept
    // frame), then drains the client's interleaved write until it disconnects.
    private static (TcpListener listener, Task serverTask) StartPlainAcceptServer(byte[] responseBytes)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var net = client.GetStream();
                await net.WriteAsync(responseBytes); // accept frame + trailer, one segment
                await net.FlushAsync();
                var buf = new byte[4096];
                // Drains the connection request and the client's interleaved write until close.
                while (await net.ReadAsync(buf) > 0) { }
            }
            catch
            {
                // client teardown races the drain; nothing to assert here
            }
        });
        return (listener, serverTask);
    }
}
