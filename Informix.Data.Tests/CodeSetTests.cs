using System.Text;
using Informix.Data;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

public class CodeSetAndLocaleDiscoveryRegressionTests
{
    // DB locale discovery opens a helper connection first. When that helper cannot reach the server
    // at all (-908), Open fails right there instead of swallowing it and dialling the same host and
    // port a second time (which doubled Login Timeout against an unreachable server).
    [Fact]
    public async Task Open_does_not_dial_twice_when_locale_discovery_cannot_reach_the_server()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int accepts = 0;
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref accepts);
                    client.Client.Close(); // drop it before the ASF handshake completes
                }
            }
            catch
            {
                // listener stopped
            }
        });
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            using var cn = new IfxConnection(
                $"Host=127.0.0.1;Service={port};Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
            Assert.True(cn.IsDbLocaleDiscoveryNeeded);

            var ex = Assert.Throws<IfxException>(() => cn.Open());
            Assert.Equal(-908, ex.IfxSqlCode);
            await Task.Delay(500); // let a (wrong) second connection reach the accept loop
            Assert.Equal(1, Volatile.Read(ref accepts));
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(acceptLoop, Task.Delay(2000));
        }
    }

    // UTF-7 is a name .NET knows but refuses (Encoding.GetEncoding throws NotSupportedException,
    // not ArgumentException): it must resolve as an unknown code set (Latin-1), not escape.
    [Theory]
    [InlineData("en_us.65000")]
    [InlineData("en_us.utf-7")]
    [InlineData("utf-7")]
    [InlineData("en_us.unicode-1-1-utf-7")]
    public void Utf7_is_treated_as_an_unknown_code_set(string locale)
    {
        Assert.Null(IfxCodeSet.TryResolve(locale));
        Assert.Equal(Encoding.Latin1.CodePage, IfxCodeSet.Resolve(locale).CodePage);
    }
}

/// <summary>
/// Covers the Informix locale / code-set → .NET <see cref="Encoding"/> mapping and its
/// wiring into <see cref="IfxConnection"/>, so a non-default DB_LOCALE (e.g.
/// <c>pl_pl.8859-2</c>) encodes/decodes CHAR/NCHAR text in the right code set instead of
/// the historical ISO-8859-1 default (which lossily folded Polish/Cyrillic/… characters).
/// </summary>
public class CodeSetTests
{
    [Theory]
    [InlineData("pl_pl.8859-2", 28592)]
    [InlineData("pl_PL.ISO8859-2", 28592)]
    [InlineData("cs_cz.8859-2", 28592)]
    [InlineData("en_us.8859-1", 28591)]
    [InlineData("en_US.819", 28591)]        // IBM CCSID 819 == ISO-8859-1
    [InlineData("ru_ru.8859-5", 28595)]
    [InlineData("de_de.8859-15@euro", 28605)] // '@modifier' is stripped
    [InlineData("en_US.utf8", 65001)]
    [InlineData("en_us.CP1250", 1250)]
    [InlineData("pl_pl.1250", 1250)]         // bare numeric code set == .NET code page
    [InlineData("ja_jp.sjis", 932)]
    [InlineData("8859-2", 28592)]            // bare code set, no language_territory
    public void Resolve_maps_common_informix_code_sets(string locale, int expectedCodePage)
    {
        Encoding enc = IfxCodeSet.Resolve(locale);
        Assert.Equal(expectedCodePage, enc.CodePage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pl_pl.no-such-codeset")]
    public void Resolve_falls_back_to_latin1_when_unknown_or_absent(string? locale)
    {
        Assert.Equal(Encoding.Latin1.CodePage, IfxCodeSet.Resolve(locale).CodePage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pl_pl.no-such-codeset")]
    public void TryResolve_returns_null_when_unknown_or_absent(string? locale)
    {
        Assert.Null(IfxCodeSet.TryResolve(locale));
    }

    [Fact]
    public void Resolve_polish_code_set_round_trips_polish_characters()
    {
        Encoding enc = IfxCodeSet.Resolve("pl_pl.8859-2");
        const string polish = "zażółć gęślą jaźń ĄĆĘŁŃÓŚŹŻ";
        byte[] bytes = enc.GetBytes(polish);

        // Every character is a single byte in Latin-2 and survives the round trip —
        // unlike the ISO-8859-1 default, which folds ż/ł/ć/ę/… to ASCII.
        Assert.Equal(polish.Length, bytes.Length);
        Assert.Equal(polish, enc.GetString(bytes));
        Assert.Equal(0xBF, bytes[2]); // 'ż' == 0xBF in ISO-8859-2
    }

    [Fact]
    public void Connection_encoding_comes_from_db_locale_only()
    {
        // DB_LOCALE is the single source of the conversion code set.
        using var db = new IfxConnection(
            "Host=h;Service=9088;Server=s;Database=d;DB Locale=pl_pl.8859-2");
        Assert.Equal(28592, db.DbEncoding.CodePage);

        // The driver has no Client Locale keyword: CLIENT_LOCALE is a non-Unicode-client
        // legacy concept it never sends. A leftover key carried over from an old JDBC or
        // ODBC connection string is an unrecognized token and must stay inert — neither
        // overriding DB Locale nor selecting an encoding on its own.
        using var staleKeyWithDbLocale = new IfxConnection(
            "Host=h;Service=9088;Server=s;Database=d;Client Locale=en_us.8859-1;DB Locale=pl_pl.8859-2");
        Assert.Equal(28592, staleKeyWithDbLocale.DbEncoding.CodePage);

        using var staleKeyAlone = new IfxConnection(
            "Host=h;Service=9088;Server=s;Database=d;Client Locale=pl_pl.8859-2");
        Assert.Equal(Encoding.Latin1.CodePage, staleKeyAlone.DbEncoding.CodePage);

        // No DB Locale → the ISO-8859-1 "NOENCODING" default.
        using var neither = new IfxConnection("Host=h;Service=9088;Server=s;Database=d");
        Assert.Equal(Encoding.Latin1.CodePage, neither.DbEncoding.CodePage);
    }

    [Fact]
    public void Db_locale_discovery_defaults_on_and_is_opt_out()
    {
        // Default: on (used at Open to look the code set up from sysmaster:sysdbslocale).
        Assert.True(new IfxConnectionStringBuilder("Host=h;Service=9088;Server=s;Database=d").DbLocaleDiscovery);

        // Explicit opt-out and opt-in round-trip through the connection string.
        var off = new IfxConnectionStringBuilder("Host=h;Service=9088;Server=s;Database=d;DB Locale Discovery=false");
        Assert.False(off.DbLocaleDiscovery);
        Assert.False(new IfxConnectionStringBuilder(off.ConnectionString).DbLocaleDiscovery);

        var on = new IfxConnectionStringBuilder { DbLocaleDiscovery = true };
        Assert.True(new IfxConnectionStringBuilder(on.ConnectionString).DbLocaleDiscovery);
    }

    [Fact]
    public void Db_locale_accepts_the_informix_native_DB_LOCALE_spelling()
    {
        // Existing ESQL/C, JDBC and ODBC connection strings write DB_LOCALE; it has to mean
        // the same as the ADO.NET-style "DB Locale" both for the typed property...
        var native = new IfxConnectionStringBuilder("Host=h;Service=9088;Database=d;DB_LOCALE=pl_pl.8859-2");
        Assert.Equal("pl_pl.8859-2", native.DbLocale);

        // ...and for the encoding the connection converts all text with.
        using var cn = new IfxConnection("Host=h;Service=9088;Server=s;Database=d;DB_LOCALE=pl_pl.8859-2");
        Assert.Equal(28592, cn.DbEncoding.CodePage);

        // The setter persists the canonical keyword and drops the synonym, so the two
        // spellings can never disagree.
        native.DbLocale = "en_us.utf8";
        Assert.Equal("en_us.utf8", new IfxConnectionStringBuilder(native.ConnectionString).DbLocale);
        Assert.False(native.ContainsKey("DB_LOCALE"));

        // Both spellings present: the canonical keyword wins.
        var both = new IfxConnectionStringBuilder("Database=d;DB_LOCALE=en_us.utf8;DB Locale=pl_pl.8859-2");
        Assert.Equal("pl_pl.8859-2", both.DbLocale);
    }

    [Fact]
    public void A_locale_in_the_connection_string_skips_the_sysmaster_lookup()
    {
        // Nothing given → Open asks sysmaster:sysdbslocale for the database's code set.
        using var zeroConfig = new IfxConnection("Host=h;Service=9088;Server=s;Database=d");
        Assert.True(zeroConfig.IsDbLocaleDiscoveryNeeded);

        // A locale the caller spelled out is authoritative — in either spelling — so the
        // lookup (and the extra connection it costs) is skipped.
        using var spaced = new IfxConnection("Host=h;Service=9088;Server=s;Database=d;DB Locale=pl_pl.8859-2");
        Assert.False(spaced.IsDbLocaleDiscoveryNeeded);

        using var native = new IfxConnection("Host=h;Service=9088;Server=s;Database=d;DB_LOCALE=pl_pl.8859-2");
        Assert.False(native.IsDbLocaleDiscoveryNeeded);

        // Explicit opt-out, and no database to look up, skip it too.
        using var optedOut = new IfxConnection("Host=h;Service=9088;Server=s;Database=d;DB Locale Discovery=false");
        Assert.False(optedOut.IsDbLocaleDiscoveryNeeded);

        using var noDatabase = new IfxConnection("Host=h;Service=9088;Server=s");
        Assert.False(noDatabase.IsDbLocaleDiscoveryNeeded);
    }

    [Fact]
    public void Connection_encoding_tracks_connection_string_reassignment()
    {
        var cn = new IfxConnection("Host=h;Service=9088;Server=s;Database=d;DB Locale=pl_pl.8859-2");
        Assert.Equal(28592, cn.DbEncoding.CodePage);

        cn.ConnectionString = "Host=h;Service=9088;Server=s;Database=d"; // no locale
        Assert.Equal(Encoding.Latin1.CodePage, cn.DbEncoding.CodePage);
        cn.Dispose();
    }
}
