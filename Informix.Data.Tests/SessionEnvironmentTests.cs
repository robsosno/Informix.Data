using Informix.Data.Asf;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The session-environment connection-string keywords (<c>DB Date</c>, <c>GL Date</c>,
/// <c>DB Century</c>, <c>GL DateTime</c>, <c>Statement Cache</c>, <c>Long Identifiers</c>,
/// <c>Delimited Identifiers</c>, <c>Strict Thousands Separator</c>): what reaches the handshake,
/// what goes into the pool key, and that <see cref="IfxConnection.TextFormat"/> — not the process
/// environment — drives <see cref="IfxTextConvert"/>.
/// </summary>
public class SessionEnvironmentTests
{
    private const string Base =
        "Host=h;Service=9088;Server=s;Database=d;User ID=u;Password=p;Pooling=false;DB Locale Discovery=false";

    // Opens a connection whose ASF factory records the handshake inputs and then stops, so the
    // test sees exactly the environment block the server would receive.
    private static Dictionary<string, string> HandshakeEnvironment(string extra)
    {
        AsfConnectionSettings? captured = null;
        var cn = new IfxConnection(Base + (extra.Length > 0 ? ";" + extra : ""))
        {
            AsfConnectionFactory = settings =>
            {
                captured = settings;
                throw new InvalidOperationException("stop after capturing the handshake settings");
            },
        };
        Assert.ThrowsAny<Exception>(() => cn.Open());
        Assert.NotNull(captured);
        return new Dictionary<string, string>(captured!.EnvironmentVariables);
    }

    private static readonly string[] SessionVariables =
        ["DBDATE", "GL_DATE", "DBCENTURY", "GL_DATETIME", "STMT_CACHE", "IFX_LONGID", "DELIMIDENT"];

    [Fact]
    public void With_nothing_configured_no_session_variable_is_sent()
    {
        var env = HandshakeEnvironment("");
        foreach (string name in SessionVariables)
            Assert.DoesNotContain(name, env.Keys);
        // the standard block is exactly what it always was
        Assert.Equal("1", env["IFX_UPDDESC"]);
        Assert.Equal("1", env["CLNT_PAM_CAPABLE"]);
        Assert.Equal(".", env["DBPATH"]);
    }

    [Fact]
    public void Each_keyword_is_sent_as_its_informix_variable()
    {
        var env = HandshakeEnvironment(
            "DB Date=Y4MD-;GL Date=%d.%m.%Y;DB Century=C;GL DateTime=%Y/%m/%d;" +
            "Statement Cache=true;Long Identifiers=false;Delimited Identifiers=true");

        Assert.Equal("Y4MD-", env["DBDATE"]);
        Assert.Equal("%d.%m.%Y", env["GL_DATE"]);
        Assert.Equal("C", env["DBCENTURY"]);
        Assert.Equal("%Y/%m/%d", env["GL_DATETIME"]);
        Assert.Equal("1", env["STMT_CACHE"]);
        Assert.Equal("0", env["IFX_LONGID"]);
        Assert.Equal("y", env["DELIMIDENT"]);
    }

    [Fact]
    public void The_informix_variable_names_work_as_synonyms()
    {
        var env = HandshakeEnvironment("DBDATE=MDY4/;STMT_CACHE=0;IFX_LONGID=1;DELIMIDENT=y;DBCENTURY=P");

        Assert.Equal("MDY4/", env["DBDATE"]);
        Assert.Equal("0", env["STMT_CACHE"]);
        Assert.Equal("1", env["IFX_LONGID"]);
        Assert.Equal("y", env["DELIMIDENT"]);
        Assert.Equal("P", env["DBCENTURY"]);
    }

    [Theory]
    [InlineData("Delimited Identifiers=false")]
    [InlineData("DELIMIDENT=n")]
    [InlineData("DELIMIDENT=no")]
    [InlineData("DELIMIDENT=0")]
    public void A_false_Delimited_Identifiers_sends_nothing_because_the_server_only_checks_presence(string keyword)
    {
        // Sending DELIMIDENT=n would switch the feature ON: live-verified, the server tests only
        // for the variable's existence.
        Assert.DoesNotContain("DELIMIDENT", HandshakeEnvironment(keyword).Keys);
    }

    [Fact]
    public void The_environment_of_the_process_is_never_forwarded()
    {
        string? previous = Environment.GetEnvironmentVariable("DBDATE");
        try
        {
            Environment.SetEnvironmentVariable("DBDATE", "DMY4.");
            Assert.DoesNotContain("DBDATE", HandshakeEnvironment("").Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBDATE", previous);
        }
    }

    [Fact]
    public void Typed_properties_round_trip_and_drop_the_synonym_spelling()
    {
        var b = new IfxConnectionStringBuilder("DBDATE=MDY4/;STMT_CACHE=1;IFX_LONGID=0;DELIMIDENT=y")
        {
            DbDate = "Y4MD-",
            StatementCache = false,
            LongIdentifiers = true,
            DelimitedIdentifiers = false,
        };

        Assert.Equal("Y4MD-", b.DbDate);
        Assert.False(b.ContainsKey("DBDATE"));
        Assert.False(b.StatementCache);
        Assert.False(b.ContainsKey("STMT_CACHE"));
        Assert.True(b.LongIdentifiers);
        Assert.False(b.DelimitedIdentifiers);
        Assert.False(b.ContainsKey("DELIMIDENT"));      // false removes the key rather than storing "false"

        var reparsed = new IfxConnectionStringBuilder(b.ConnectionString);
        Assert.Equal("Y4MD-", reparsed.DbDate);
        Assert.False(reparsed.StatementCache);
        Assert.True(reparsed.LongIdentifiers);

        b.StatementCache = null;
        Assert.Null(b.StatementCache);
        Assert.False(b.ContainsKey("Statement Cache"));
    }

    [Fact]
    public void An_unrecognized_boolean_value_is_rejected_not_ignored()
    {
        var b = new IfxConnectionStringBuilder("Statement Cache=maybe");
        Assert.Throws<ArgumentException>(() => b.StatementCache);
    }

    [Fact]
    public void The_keywords_are_part_of_the_pool_key()
    {
        var plain = new IfxConnectionStringBuilder(Base);
        var withDate = new IfxConnectionStringBuilder(Base + ";DB Date=Y4MD-");
        var withOther = new IfxConnectionStringBuilder(Base + ";DB Date=MDY4/");

        Assert.NotEqual(IfxConnection.PoolKey(plain), IfxConnection.PoolKey(withDate));
        Assert.NotEqual(IfxConnection.PoolKey(withDate), IfxConnection.PoolKey(withOther));
    }

    // ------------------------------------------------------------------ text format

    [Fact]
    public void TextFormat_is_built_from_the_connection_string()
    {
        var b = new IfxConnectionStringBuilder(
            "DB Date=Y4MD-;GL Date=%d.%m.%Y;DB Century=F;GL DateTime=%Y;Strict Thousands Separator=false");
        var f = b.TextFormat;

        Assert.Equal("Y4MD-", f.DbDate);
        Assert.Equal("%d.%m.%Y", f.GlDate);
        Assert.Equal("F", f.DbCentury);
        Assert.Equal("%Y", f.GlDateTime);
        Assert.False(f.StrictThousandsSeparator);

        var none = new IfxConnectionStringBuilder("").TextFormat;
        Assert.Null(none.DbDate);
        Assert.Null(none.GlDate);
        Assert.Null(none.DbCentury);
        Assert.Null(none.GlDateTime);
        Assert.True(none.StrictThousandsSeparator);
    }

    [Fact]
    public void IfxTextConvert_follows_the_connection_not_the_environment()
    {
        string? previous = Environment.GetEnvironmentVariable("DBDATE");
        try
        {
            Environment.SetEnvironmentVariable("DBDATE", "DMY4-");     // must be ignored
            using var cn = new IfxConnection(Base + ";DB Date=Y4MD.");

            var date = new DateOnly(2008, 12, 31);
            Assert.Equal("2008.12.31", IfxTextConvert.FormatDate(date, cn.TextFormat));
            Assert.Equal(date, IfxTextConvert.ParseDate("2008.12.31", cn.TextFormat));

            // no format passed: the default, not DMY4- from the environment
            Assert.Equal("12/31/2008", IfxTextConvert.FormatDate(date));
            Assert.Equal(date, IfxTextConvert.ParseDate("12/31/2008"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBDATE", previous);
        }
    }

    [Fact]
    public void Strict_Thousands_Separator_false_loosens_the_grouping_rule()
    {
        using var strict = new IfxConnection(Base);
        using var loose = new IfxConnection(Base + ";Strict Thousands Separator=false");

        Assert.False(IfxTextConvert.TryParseBigInt("12,34", out _, strict.TextFormat));
        Assert.True(IfxTextConvert.TryParseBigInt("12,34", out var value, loose.TextFormat));
        Assert.Equal(1234, value);
    }
}
