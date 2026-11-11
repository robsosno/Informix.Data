using System.Text;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Random-access character reads on an open <see cref="IfxClob"/>
/// (<see cref="IfxSmartLOB.Read(char[])"/> and the seek+read overload): decoding the
/// bytes fetched over SQ_LODATA into the caller's <c>char[]</c> must never overflow
/// the destination, regardless of how many bytes <c>Encoding.GetMaxByteCount(1)</c>
/// says a single character might need.
/// </summary>
public class SmartLobCharReadTests
{
    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    private static byte[] Handle(int h) => SqliStatementKit.Join(
        SqliStatementKit.RoutineHandleFrame("db", h), SqliStatementKit.EotFrame());

    private static byte[] Fp(params object[] returns) => SqliStatementKit.Join(
        SqliStatementKit.FpReturnFrame(returns), SqliStatementKit.EotFrame());

    private static byte[] Open(int fd) => SqliStatementKit.Join(Handle(67), Fp(fd));

    // ifx_lo_seek: the routine handle is resolved once per session and reused after.
    private static byte[] SeekResolvingHandle() => SqliStatementKit.Join(Handle(73), Fp(0L, 0));
    private static byte[] SeekReusingHandle() => Fp(0L, 0);

    private static byte[] Read(byte[] data) => SqliStatementKit.Join(
        SqliStatementKit.LoDataReadFrame(data), SqliStatementKit.EotFrame());

    private static IfxConnection Connect(byte[] script, out DuplexStream duplex, string? dbLocale = null) =>
        SqliStatementKit.OpenConnection(
            SqliStatementKit.Join(script, SqliStatementKit.EotFrame()), out duplex,
            dbLocale is null ? null : $"DB Locale={dbLocale}");

    // The exact shape of the reported crash: a single-byte (Latin-1, the driver's
    // default DbEncoding) locale, buff.Length == numCharsToRead, reading from a
    // mid-object offset. Encoding.GetMaxByteCount(1) reports 2 bytes/char for
    // Latin-1 — not 1 — so sizing the fetch as numCharsToRead*GetMaxByteCount(1)
    // over-fetched, and Encoding.GetChars threw decoding the extra bytes back into
    // the 6-char destination ("output char buffer is too small").
    [Fact]
    public void Random_access_read_does_not_overflow_a_single_byte_destination_buffer()
    {
        byte[] script = SqliStatementKit.Join(
            Open(39),
            SeekResolvingHandle(),
            Read(Encoding.Latin1.GetBytes("target")));

        using var cn = Connect(script, out var duplex);
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));
        clob.Open(IfxSmartLOBOpenMode.ReadOnly);

        var window = new char[6];
        long read = clob.Read(window, 0, 6, 3000, IfxSmartLOBWhence.Begin);

        Assert.Equal(6, read);
        Assert.Equal("target", new string(window));

        // A single-byte code set must not double-fetch: exactly 6 bytes requested
        // over SQ_LODATA (opcode 97, LO_READ subCom 0, fd 39), not 12.
        byte[] sent = duplex.Output.ToArray();
        var loRead = new byte[] { 0, 97, 0, 0, 0, 39, 0, 0, 0, 6, 0x7D, 0x00 };
        Assert.Contains(BitConverter.ToString(loRead), BitConverter.ToString(sent));
    }

    // The parameterless overload (reads from the current position, no explicit seek)
    // shared the same over-fetch-then-GetChars bug; it must be fixed too.
    [Fact]
    public void Sequential_read_does_not_overflow_a_single_byte_destination_buffer()
    {
        byte[] script = SqliStatementKit.Join(Open(39), Read(Encoding.Latin1.GetBytes("abcdef")));

        using var cn = Connect(script, out _);
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));
        clob.Open(IfxSmartLOBOpenMode.ReadOnly);

        var buff = new char[6];
        long read = clob.Read(buff);

        Assert.Equal(6, read);
        Assert.Equal("abcdef", new string(buff));
    }

    // A multi-byte locale forces a real over-fetch (UTF-8's GetMaxByteCount(1) == 6,
    // six times what this all-ASCII content needs): the leftover bytes must be pushed
    // back onto the LOB position rather than silently dropped, or a second read
    // continuing where the first left off would skip the bytes the first over-fetched.
    [Fact]
    public void Sequential_reads_over_a_multibyte_locale_do_not_lose_or_duplicate_bytes()
    {
        const string full = "HELLOWORLD!"; // 11 ASCII bytes/chars, 1 byte per char in UTF-8
        byte[] script = SqliStatementKit.Join(
            Open(39),
            SeekResolvingHandle(),                       // Seek(0) before the 1st read
            Read(Encoding.ASCII.GetBytes(full[..5])),     // "HELLO"
            SeekReusingHandle(),                          // push-back after the 1st read
            SeekReusingHandle(),                          // Seek(5) before the 2nd read
            Read(Encoding.ASCII.GetBytes(full[5..])),     // "WORLD!"
            SeekReusingHandle());                         // push-back after the 2nd read

        using var cn = Connect(script, out _, dbLocale: "en_US.utf8");
        var clob = new IfxClob(cn, new IfxSmartLOBLocator(Locator(0x20)));
        clob.Open(IfxSmartLOBOpenMode.ReadOnly);

        var first = new char[5];
        Assert.Equal(5, clob.Read(first, 0, 5, 0, IfxSmartLOBWhence.Begin));
        Assert.Equal("HELLO", new string(first));

        var second = new char[6];
        Assert.Equal(6, clob.Read(second, 0, 6, 5, IfxSmartLOBWhence.Begin));
        Assert.Equal("WORLD!", new string(second));
    }
}
