using System.Data;
using Informix.Data;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

public class ConnectionStringBuilderTests
{
    [Fact]
    public void Typed_properties_round_trip_through_the_connection_string()
    {
        var b = new IfxConnectionStringBuilder
        {
            Host = "ol_host",
            Service = "9088",
            Server = "ol_informix1210",
            Database = "stores_demo",
            UserId = "informix",
            Password = "secret",
            SslConnection = true,
            LoginTimeout = 15,
        };

        var reparsed = new IfxConnectionStringBuilder(b.ConnectionString);
        Assert.Equal("ol_host", reparsed.Host);
        Assert.Equal("9088", reparsed.Service);
        Assert.Equal("ol_informix1210", reparsed.Server);
        Assert.Equal("stores_demo", reparsed.Database);
        Assert.Equal("informix", reparsed.UserId);
        Assert.Equal("secret", reparsed.Password);
        Assert.True(reparsed.SslConnection);
        Assert.Equal(15, reparsed.LoginTimeout);
    }

    [Theory]
    [InlineData("UID=informix;PWD=secret")]
    [InlineData("uid=informix;pwd=secret")]
    [InlineData("User=informix;Password=secret")]
    [InlineData("UserId=informix;PWD=secret")]
    public void Uid_and_pwd_are_read_as_synonyms_of_user_id_and_password(string connectionString)
    {
        var b = new IfxConnectionStringBuilder(connectionString);
        Assert.Equal("informix", b.UserId);
        Assert.Equal("secret", b.Password);
    }

    [Fact]
    public void Setting_the_typed_credentials_removes_the_synonym_spellings()
    {
        var b = new IfxConnectionStringBuilder("Database=db;UID=old;PWD=old")
        {
            UserId = "informix",
            Password = "secret",
        };

        Assert.Equal("informix", b.UserId);
        Assert.Equal("secret", b.Password);
        Assert.False(b.ContainsKey("UID"));
        Assert.False(b.ContainsKey("PWD"));
    }

    [Fact]
    public void ToStringWithoutPassword_strips_the_pwd_synonym_too()
    {
        var stripped = new IfxConnectionStringBuilder("Database=db;UID=informix;PWD=secret")
            .ToStringWithoutPassword();

        Assert.DoesNotContain("secret", stripped);
        Assert.Contains("informix", stripped);
    }

    [Fact]
    public void PreparedStatementCacheSize_defaults_to_zero_and_round_trips_through_the_connection_string()
    {
        Assert.Equal(0, new IfxConnectionStringBuilder().PreparedStatementCacheSize); // off by default

        var b = new IfxConnectionStringBuilder { PreparedStatementCacheSize = 25 };

        Assert.Equal(25, new IfxConnectionStringBuilder(b.ConnectionString).PreparedStatementCacheSize);
    }

    [Fact]
    public void PreparedStatementCacheSize_accepts_the_JDBC_style_synonym()
    {
        var b = new IfxConnectionStringBuilder("Host=h;IFX_PREPAREDSTATEMENT_CACHE_SIZE=10");

        Assert.Equal(10, b.PreparedStatementCacheSize);
    }

    [Fact]
    public void PreparedStatementCacheSize_canonical_key_wins_over_the_synonym_when_both_are_present()
    {
        var b = new IfxConnectionStringBuilder(
            "Host=h;Prepared Statement Cache Size=5;IFX_PREPAREDSTATEMENT_CACHE_SIZE=99");

        Assert.Equal(5, b.PreparedStatementCacheSize);
    }
}

public class TypeCodeTests
{
    [Fact]
    public void BaseType_extracts_low_byte()
    {
        short word = (short)((short)IfxTypeFlags.NotNullable | (short)SqliTypeCode.Int);
        Assert.Equal(SqliTypeCode.Int, IfxTypeCodes.BaseType(word));
    }

    [Fact]
    public void NotNullable_flag_is_detected()
    {
        short word = (short)((short)IfxTypeFlags.NotNullable | (short)SqliTypeCode.VarChar);
        Assert.True(IfxTypeCodes.IsNotNullable(word));
        Assert.False(IfxTypeCodes.IsNotNullable((short)SqliTypeCode.VarChar));
    }
}

public class AdoSurfaceTests
{
    [Fact]
    public void Open_without_host_throws_IfxException()
    {
        using var cn = new IfxConnection("Database=stores_demo;Service=9088");
        var ex = Assert.Throws<IfxException>(cn.Open);
        Assert.Equal(-79758, ex.IfxSqlCode);
    }

    [Fact]
    public void Open_completes_the_asf_handshake_and_reports_the_server_version()
    {
        // Script: accept frame, the SQ_PROTOCOLS + SQ_VERSION bring-up replies, then the SQ_EXIT ack for Close.
        var script = new MemoryStream();
        script.Write(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept));
        script.Write(SqliTestKit.BringUpReply("9.28.FC4"));
        new Informix.Data.Wire.IfxWireWriter(script).WriteInt16(56);
        var duplex = new DuplexStream(script.ToArray());

        using var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = s => new Informix.Data.Asf.AsfConnection(s, duplex);

        cn.Open();
        Assert.Equal(ConnectionState.Open, cn.State);
        Assert.Equal("9.28.FC4", cn.ServerVersion);
        Assert.NotNull(cn.ServerInfo);

        cn.Close();
        Assert.Equal(ConnectionState.Closed, cn.State);
        Assert.Throws<InvalidOperationException>(() => cn.ServerVersion);   // a closed connection has no server to report
    }

    [Fact]
    public void ServerType_splits_the_product_name_off_the_handshake_version_string()
    {
        // Live-captured shape (aaa.md): "Informix Dynamic Server Version 9.59.FC5".
        var script = new MemoryStream();
        script.Write(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept, version: "Informix Dynamic Server Version 9.59.FC5"));
        script.Write(SqliTestKit.BringUpReply("9.59.FC5"));
        new Informix.Data.Wire.IfxWireWriter(script).WriteInt16(56);
        var duplex = new DuplexStream(script.ToArray());

        using var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = s => new Informix.Data.Asf.AsfConnection(s, duplex);

        cn.Open();
        Assert.Equal("Informix Dynamic Server", cn.ServerType);
    }

    [Fact]
    public void ServerType_falls_back_when_the_handshake_string_has_no_Version_token()
    {
        var script = new MemoryStream();
        script.Write(AsfTestKit.BuildResponse(AsfProtocolMirror.Accept, version: "9.28.FC4"));
        script.Write(SqliTestKit.BringUpReply("9.28.FC4"));
        new Informix.Data.Wire.IfxWireWriter(script).WriteInt16(56);
        var duplex = new DuplexStream(script.ToArray());

        using var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = s => new Informix.Data.Asf.AsfConnection(s, duplex);

        cn.Open();
        Assert.Equal("Informix Dynamic Server", cn.ServerType);
    }

    [Fact]
    public void ServerType_throws_when_the_connection_is_closed()
    {
        using var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd");
        Assert.Throws<InvalidOperationException>(() => cn.ServerType);
    }

    [Fact]
    public void Open_with_rejected_login_stays_closed()
    {
        var frame = AsfTestKit.BuildResponse(
            AsfProtocolMirror.Reject, tailSegment: AsfProtocolMirror.AscInitResponse,
            svcError: -951, osError: 13, errorMessage: "Incorrect password");
        var duplex = new DuplexStream(frame);

        using var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=bad");
        cn.AsfConnectionFactory = s => new Informix.Data.Asf.AsfConnection(s, duplex);

        var ex = Assert.Throws<Informix.Data.Asf.AsfRemoteException>(cn.Open);
        Assert.Equal(-951, ex.IfxSqlCode);
        Assert.Equal(ConnectionState.Closed, cn.State);
    }

    [Fact]
    public void Open_with_non_numeric_service_reports_missing_sqlhosts_support()
    {
        using var cn = new IfxConnection("Host=h;Service=sqlexec;Database=db");
        var ex = Assert.Throws<IfxException>(cn.Open);
        Assert.Equal(-931, ex.IfxSqlCode);
    }

    [Fact]
    public void Factory_creates_provider_objects()
    {
        var f = InformixClientFactory.Instance;
        Assert.IsType<IfxConnection>(f.CreateConnection());
        Assert.IsType<IfxCommand>(f.CreateCommand());
        Assert.IsType<IfxParameter>(f.CreateParameter());
        Assert.IsType<IfxConnectionStringBuilder>(f.CreateConnectionStringBuilder());
        Assert.IsType<IfxCommandBuilder>(f.CreateCommandBuilder());
        Assert.IsType<IfxDataAdapter>(f.CreateDataAdapter());
        Assert.IsType<IfxDataSourceEnumerator>(f.CreateDataSourceEnumerator());
        Assert.True(f.CanCreateDataSourceEnumerator);
    }

    [Fact]
    public void Factory_CreateDataSource_returns_the_real_pooled_IfxDataSource()
    {
        var f = InformixClientFactory.Instance;

        using var source = f.CreateDataSource("Host=h;Service=9088;Database=db;Pooling=false");

        Assert.IsType<IfxDataSource>(source);
        // The base DbProviderFactory lazily instantiates and caches these -- no override needed.
        Assert.True(f.CanCreateDataAdapter);
        Assert.True(f.CanCreateCommandBuilder);
        // IfxBatch is the standard ADO.NET DbBatch; IfxRowBatch (one statement, many rows)
        // remains the separate provider-specific construct.
        Assert.True(f.CanCreateBatch);
    }

    [Fact]
    public void Parameter_collection_supports_named_and_positional_access()
    {
        var cmd = new IfxCommand("select * from t where a = ? and b = ?");
        cmd.Parameters.Add("a", 1);
        cmd.Parameters.Add("b", "x");

        Assert.Equal(2, cmd.Parameters.Count);
        Assert.Equal(0, cmd.Parameters.IndexOf("a"));
        Assert.Equal("x", cmd.Parameters[1].Value);
    }
}
