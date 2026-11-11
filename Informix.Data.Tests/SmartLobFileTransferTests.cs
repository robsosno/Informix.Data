using System.Text;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Client-side smart-LOB file transfer (<see cref="IfxSmartLOB.ToFile"/> /
/// <see cref="IfxSmartLOB.FromFile"/>): the FastPath call sequence it drives, the
/// SQ_LODATA streaming, and the code-set handling — a BLOB moves bytes 1:1 while a
/// CLOB transcodes between the database code set and the file's encoding (UTF-8 by
/// default, as for any .NET string).
/// </summary>
public class SmartLobFileTransferTests : IDisposable
{
    // The driver registers this from IfxCodeSet's static constructor, but these tests
    // build ISO-8859-2/windows-1250 encodings of their own before touching it.
    static SmartLobFileTransferTests() =>
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    private readonly string _dir = Directory.CreateTempSubdirectory("ifxlobfile").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    // FastPath handles are resolved once per signature per session, so each distinct
    // ifx_lo_* routine contributes a SQ_GETROUTINE reply the first time it is called.
    private static byte[] Handle(int h) => SqliStatementKit.Join(
        SqliStatementKit.RoutineHandleFrame("db", h), SqliStatementKit.EotFrame());

    private static byte[] Fp(params object[] returns) => SqliStatementKit.Join(
        SqliStatementKit.FpReturnFrame(returns), SqliStatementKit.EotFrame());

    private static byte[] Open(int fd) => SqliStatementKit.Join(Handle(67), Fp(fd));

    private static byte[] Stat(long size) => SqliStatementKit.Join(
        Handle(71), Fp(SqliStatementKit.LoStatBlock(size), 0));

    private static byte[] Seek() => SqliStatementKit.Join(Handle(73), Fp(0L, 0));

    private static byte[] Truncate() => SqliStatementKit.Join(Handle(75), Fp(0));

    private static byte[] Close() => SqliStatementKit.Join(Handle(68), Fp(0));

    private static byte[] Read(byte[] data) => SqliStatementKit.Join(
        SqliStatementKit.LoDataReadFrame(data), SqliStatementKit.EotFrame());

    private static byte[] WriteAck(int written) => SqliStatementKit.Join(
        SqliStatementKit.LoDataWriteAckFrame(written), SqliStatementKit.EotFrame());

    /// <summary>The reply script for a ToFile dump: open (read-only) + stat + one
    /// SQ_LODATA read per 32000-byte chunk + close.</summary>
    private static byte[] ToFileScript(params byte[][] chunks) => SqliStatementKit.Join(
        [Open(39), Stat(chunks.Sum(c => c.Length)), .. chunks.Select(Read), Close()]);

    /// <summary>The reply script for a FromFile load: open + seek to 0 + one write ack
    /// per uploaded chunk + truncate (replace semantics) + close.</summary>
    private static byte[] FromFileScript(params int[] writes) => SqliStatementKit.Join(
        [Open(39), Seek(), .. writes.Select(WriteAck), Truncate(), Close()]);

    private static IfxConnection Connect(byte[] script, out DuplexStream duplex, string? dbLocale = null) =>
        SqliStatementKit.OpenConnection(
            SqliStatementKit.Join(script, SqliStatementKit.EotFrame()), out duplex,
            dbLocale is null ? null : $"DB Locale={dbLocale}");

    // ---- BLOB: bytes move verbatim ----

    [Fact]
    public void Blob_ToFile_writes_the_object_bytes_verbatim_and_returns_the_full_path()
    {
        byte[] payload = [0x00, 0xFF, 0x41, 0x80, 0x0A, 0x0D, 0x1A];
        using var cn = Connect(ToFileScript(payload), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        string file = Path("dump.bin");
        string returned = blob.ToFile(file, FileMode.Create, IfxSmartLOBFileLocation.Client);

        Assert.Equal(System.IO.Path.GetFullPath(file), returned);
        Assert.Equal(payload, File.ReadAllBytes(file));
        Assert.False(blob.IsOpen); // the transfer opened it and closed it again
    }

    [Fact]
    public void Blob_FromFile_streams_the_file_up_then_truncates_to_its_length()
    {
        byte[] payload = [0x00, 0xFF, 0x41, 0x80, 0x0A];
        string file = Path("upload.bin");
        File.WriteAllBytes(file, payload);

        using var cn = Connect(FromFileScript(payload.Length), out var duplex);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        long before = duplex.Output.Length;

        blob.FromFile(file, appendToSmartLOB: false, IfxSmartLOBFileLocation.Client);

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        // SQ_LODATA(97) + subCom 2 (write) + fd 39, then the payload verbatim.
        Assert.Contains(BitConverter.ToString(new byte[] { 0, 97, 0, 2, 0, 39 }), hex);
        Assert.Contains(BitConverter.ToString(payload), hex);
        // A replace drops any old tail: ifx_lo_truncate went out.
        Assert.Contains("informix.ifx_lo_truncate", Encoding.Latin1.GetString(duplex.Output.ToArray()));
        Assert.False(blob.IsOpen);
    }

    [Fact]
    public void ToFile_streams_in_32000_byte_chunks_rather_than_one_read()
    {
        // 32000 is the server's R_W_BUFSIZE; a larger object must take several
        // SQ_LODATA round trips instead of being materialized in one buffer.
        byte[] first = Enumerable.Range(0, 32000).Select(i => (byte)i).ToArray();
        byte[] second = Enumerable.Range(0, 5000).Select(i => (byte)(i + 7)).ToArray();

        using var cn = Connect(ToFileScript(first, second), out var duplex);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        string file = Path("big.bin");
        blob.ToFile(file, FileMode.Create, IfxSmartLOBFileLocation.Client);

        Assert.Equal(37000, new FileInfo(file).Length);
        Assert.Equal(first.Concat(second), File.ReadAllBytes(file));

        // Two LO_READ requests went out: the first capped at 32000, the second for the rest.
        byte[] sent = duplex.Output.ToArray();
        Assert.Contains(BitConverter.ToString([0, 97, 0, 0, 0, 39, 0, 0, 0x7D, 0x00]), BitConverter.ToString(sent));
        Assert.Contains(BitConverter.ToString([0, 97, 0, 0, 0, 39, 0, 0, 0x13, 0x88]), BitConverter.ToString(sent));
    }

    // ---- CLOB: text is transcoded between DB_LOCALE and the file encoding ----

    private const string Polish = "zażółć gęślą jaźń";

    [Fact]
    public void Clob_ToFile_transcodes_the_db_code_set_to_utf8_without_a_bom()
    {
        // The object is stored in ISO-8859-2 (DB_LOCALE pl_pl.8859-2); the file must
        // come out as UTF-8, the .NET default for text.
        byte[] latin2 = Encoding.GetEncoding(28592).GetBytes(Polish);
        using var cn = Connect(ToFileScript(latin2), out _, dbLocale: "pl_pl.8859-2");
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));

        string file = Path("text.txt");
        clob.ToFile(file, FileMode.Create, IfxSmartLOBFileLocation.Client);

        Assert.Equal(Polish, File.ReadAllText(file));                       // decodes as UTF-8
        Assert.Equal(Encoding.UTF8.GetBytes(Polish), File.ReadAllBytes(file)); // no BOM, no Latin-2 bytes
    }

    [Fact]
    public void Clob_FromFile_transcodes_utf8_to_the_db_code_set()
    {
        string file = Path("text.txt");
        File.WriteAllText(file, Polish); // UTF-8, no BOM
        byte[] latin2 = Encoding.GetEncoding(28592).GetBytes(Polish);

        using var cn = Connect(FromFileScript(latin2.Length), out var duplex, dbLocale: "pl_pl.8859-2");
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));
        long before = duplex.Output.Length;

        clob.FromFile(file, appendToSmartLOB: false, IfxSmartLOBFileLocation.Client);

        // The wire carries the Latin-2 bytes, not the file's UTF-8 ones.
        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains(BitConverter.ToString(latin2), hex);
        Assert.DoesNotContain(BitConverter.ToString(Encoding.UTF8.GetBytes(Polish)), hex);
    }

    [Fact]
    public void Clob_FromFile_strips_a_utf8_byte_order_mark()
    {
        string file = Path("bom.txt");
        File.WriteAllText(file, Polish, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        byte[] latin2 = Encoding.GetEncoding(28592).GetBytes(Polish);

        using var cn = Connect(FromFileScript(latin2.Length), out var duplex, dbLocale: "pl_pl.8859-2");
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));
        long before = duplex.Output.Length;

        clob.FromFile(file, appendToSmartLOB: false, IfxSmartLOBFileLocation.Client);

        // The BOM is a file-format artefact, not content: it must not reach the LOB.
        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains(BitConverter.ToString(latin2), hex);
        Assert.DoesNotContain(BitConverter.ToString(new byte[] { 0xEF, 0xBB, 0xBF }), hex);
    }

    [Fact]
    public void Clob_file_transfer_honours_an_explicit_file_encoding()
    {
        byte[] latin2 = Encoding.GetEncoding(28592).GetBytes(Polish);
        var cp1250 = Encoding.GetEncoding(1250);

        using var cn = Connect(ToFileScript(latin2), out _, dbLocale: "pl_pl.8859-2");
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));

        string file = Path("cp1250.txt");
        clob.ToFile(file, FileMode.Create, IfxSmartLOBFileLocation.Client, cp1250);

        Assert.Equal(cp1250.GetBytes(Polish), File.ReadAllBytes(file));
        Assert.Equal(Polish, File.ReadAllText(file, cp1250));
    }

    [Fact]
    public void Clob_FromFile_handles_a_surrogate_pair_split_across_read_buffers()
    {
        // The transfer reads 8192 chars at a time; a surrogate pair straddling that
        // boundary must survive (a stateful Encoder), not become two replacements.
        // 8193 chars of text, so the pair splits across the first and second read.
        string text = new string('a', 8191) + "\U0001F600" + new string('b', 10);
        string file = Path("emoji.txt");
        File.WriteAllText(file, text);

        // UTF-8 DB_LOCALE, so the astral character survives the database code set too.
        // Two reads -> two uploads: 8191 'a' + the pair's lead half deferred, then the rest.
        using var cn = Connect(FromFileScript(8191, 15), out var duplex, dbLocale: "en_US.utf8");
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));
        long before = duplex.Output.Length;

        clob.FromFile(file, appendToSmartLOB: false, IfxSmartLOBFileLocation.Client);

        string hex = BitConverter.ToString(duplex.Output.ToArray()[(int)before..]);
        Assert.Contains(BitConverter.ToString(Encoding.UTF8.GetBytes("\U0001F600")), hex);
        Assert.DoesNotContain(BitConverter.ToString(Encoding.UTF8.GetBytes("�")), hex);
    }

    // ---- open-state and argument handling ----

    [Fact]
    public void ToFile_on_an_already_open_lob_leaves_it_open()
    {
        byte[] payload = [1, 2, 3];
        // No trailing ifx_lo_close in the script: closing the caller's LOB would
        // desynchronize the reply stream and fail the test.
        using var cn = Connect(SqliStatementKit.Join(Open(39), Stat(payload.Length), Seek(), Read(payload)), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        blob.Open(IfxSmartLOBOpenMode.ReadOnly);

        blob.ToFile(Path("open.bin"), FileMode.Create, IfxSmartLOBFileLocation.Client);

        Assert.True(blob.IsOpen);
        Assert.Equal(payload, File.ReadAllBytes(Path("open.bin")));
    }

    [Theory]
    [InlineData(IfxSmartLOBFileLocation.Server)]
    [InlineData(IfxSmartLOBFileLocation.None)]
    public void File_transfer_supports_only_client_side_files(IfxSmartLOBFileLocation location)
    {
        using var cn = Connect(SqliStatementKit.EotFrame(), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));

        // Server-side transfer would need the ifx_lo_to_file/ifx_lo_from_file routines;
        // it must fail loudly rather than silently write a file on the wrong machine.
        Assert.Throws<NotSupportedException>(() => blob.ToFile(Path("x.bin"), FileMode.Create, location));
        Assert.Throws<NotSupportedException>(() => blob.FromFile(Path("x.bin"), false, location));
        Assert.Throws<NotSupportedException>(() => clob.ToFile(Path("x.txt"), FileMode.Create, location));
        Assert.Throws<NotSupportedException>(() => clob.FromFile(Path("x.txt"), false, location));
        Assert.False(File.Exists(Path("x.bin")));
    }

    [Fact]
    public void ToFile_does_not_touch_the_target_when_the_object_cannot_be_opened()
    {
        string file = Path("keep.bin");
        File.WriteAllBytes(file, [0xDE, 0xAD]);

        byte[] script = SqliStatementKit.Join(
            Handle(67), SqliStatementKit.ErrorReply(-9810, "Smart-large-object error."));
        using var cn = Connect(script, out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        Assert.Throws<IfxException>(() => blob.ToFile(file, FileMode.Create, IfxSmartLOBFileLocation.Client));
        Assert.Equal(new byte[] { 0xDE, 0xAD }, File.ReadAllBytes(file)); // not clobbered
    }

    [Fact]
    public void File_transfer_rejects_a_null_filename()
    {
        using var cn = Connect(SqliStatementKit.EotFrame(), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        Assert.Throws<ArgumentNullException>(() =>
            blob.ToFile(null!, FileMode.Create, IfxSmartLOBFileLocation.Client));
        Assert.Throws<ArgumentNullException>(() =>
            blob.FromFile(null!, false, IfxSmartLOBFileLocation.Client));
    }
}
