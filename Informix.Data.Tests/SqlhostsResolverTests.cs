using System.Data;
using Informix.Data.Asf;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// sqlhosts / services-database resolution (<see cref="SqlhostsResolver"/>) and its
/// wiring into <see cref="IfxConnection"/>. The file system, registry and services
/// database are injected in-memory so the tests never touch the machine.
/// </summary>
public class SqlhostsResolverTests
{
    private const string HostsPath = "/opt/informix/etc/sqlhosts";

    private static SqlhostsResolver Resolver(
        Dictionary<string, string[]> files,
        Dictionary<string, string?>? env = null,
        Func<string, string, string?>? registry = null) =>
        new(
            readLines: p => files.TryGetValue(p, out var l) ? l : null,
            // Without one the resolver reads the machine's real registry on Windows (an installed
            // ODBC driver adds its own SQLHOSTS entries), so default to an empty one.
            readRegistryValue: registry ?? ((_, _) => null),
            getEnv: v => (env ?? new()).TryGetValue(v, out var val) ? val : null);

    private static Dictionary<string, string?> EnvWith(string hostsPath) => new()
    {
        ["INFORMIXSQLHOSTS"] = hostsPath,
    };

    [Fact]
    public void Resolves_a_plain_tcp_server()
    {
        var r = Resolver(
            new() { [HostsPath] = ["ol_darkstar onsoctcp 192.168.88.11 9088"] },
            EnvWith(HostsPath));

        var e = r.ResolveServer("ol_darkstar");

        Assert.Equal("192.168.88.11", e.Host);
        Assert.Equal(9088, e.Port);
        Assert.False(e.UseSsl);
        Assert.False(e.IsGroup);
        Assert.Null(e.Authentication);
    }

    [Fact]
    public void Skips_comments_blank_lines_and_malformed_lines()
    {
        var r = Resolver(
            new()
            {
                [HostsPath] =
                [
                    "# this is a comment",
                    "",
                    "   ",
                    "bad_line onsoctcp",                       // only 2 tokens -> skipped
                    "ol_darkstar onsoctcp 192.168.88.11 9088   # trailing comment",
                ],
            },
            EnvWith(HostsPath));

        var e = r.ResolveServer("ol_darkstar");
        Assert.Equal("192.168.88.11", e.Host);
        Assert.Equal(9088, e.Port);
    }

    [Fact]
    public void Onsocssl_nettype_enables_tls()
    {
        var r = Resolver(
            new() { [HostsPath] = ["ol_ssl onsocssl 192.168.88.11 9089"] },
            EnvWith(HostsPath));

        var e = r.ResolveServer("ol_ssl");
        Assert.True(e.UseSsl);
        Assert.Equal(9089, e.Port);
    }

    [Fact]
    public void S4_option_implies_pam_authentication()
    {
        var r = Resolver(
            new() { [HostsPath] = ["pam_srv onsoctcp 192.168.88.11 9088 s=4,pam_serv=(login),pamauth=(password)"] },
            EnvWith(HostsPath));

        var e = r.ResolveServer("pam_srv");
        Assert.Equal(IfxAuthentication.Pam, e.Authentication);
    }

    [Fact]
    public void Group_entry_resolves_to_its_first_member()
    {
        var r = Resolver(
            new()
            {
                [HostsPath] =
                [
                    "mygroup group - -",
                    "member1 onsoctcp host1 9001 g=mygroup",
                    "member2 onsoctcp host2 9002 g=mygroup",
                ],
            },
            EnvWith(HostsPath));

        var e = r.ResolveServer("mygroup");
        Assert.True(e.IsGroup);
        Assert.Equal("host1", e.Host);
        Assert.Equal(9001, e.Port);
    }

    [Fact]
    public void Non_numeric_service_field_resolves_via_the_services_database()
    {
        var r = Resolver(
            new()
            {
                [HostsPath] = ["ol_named onsoctcp 192.168.88.11 turbo"],
                ["/etc/services"] = ["# services", "turbo    9101/tcp   # informix listener"],
            },
            EnvWith(HostsPath));

        var e = r.ResolveServer("ol_named");
        Assert.Equal(9101, e.Port);
    }

    [Fact]
    public void ResolveServicePort_resolves_a_name_and_passes_a_number_through()
    {
        var r = Resolver(new() { ["/etc/services"] = ["sqlexec 9088/tcp"] });
        Assert.Equal(9088, r.ResolveServicePort("sqlexec"));
        Assert.Equal(9200, r.ResolveServicePort("9200"));
    }

    [Fact]
    public void Unknown_server_reports_minus_25555_with_searched_locations()
    {
        // IBM JDBC: "Server %s is not listed as a dbserver name in sqlhosts" (live-verified).
        var r = Resolver(
            new() { [HostsPath] = ["other onsoctcp h 9088"] },
            EnvWith(HostsPath));

        var ex = Assert.Throws<IfxException>(() => r.ResolveServer("ghost"));
        Assert.Equal(-25555, ex.IfxSqlCode);
        Assert.Contains(HostsPath, ex.Message);      // the searched location is reported
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void A_missing_sqlhosts_file_reports_minus_25553()
    {
        // IBM JDBC: "Sqlhosts file not found or cannot be opened" (live-verified).
        var r = Resolver(new(), EnvWith(HostsPath));

        var ex = Assert.Throws<IfxException>(() => r.ResolveServer("ghost"));
        Assert.Equal(-25553, ex.IfxSqlCode);
        Assert.Contains(HostsPath, ex.Message);
    }

    [Fact]
    public void No_sqlhosts_location_at_all_reports_minus_25553()
    {
        var r = Resolver(new());

        Assert.Equal(-25553, Assert.Throws<IfxException>(() => r.ResolveServer("ghost")).IfxSqlCode);
    }

    [Fact]
    public void A_malformed_line_for_the_requested_server_reports_minus_25556()
    {
        // The malformed line is still skipped for other lookups (see Skips_comments_...), but
        // when it is the requested server's own line, "not listed" would mislead.
        var r = Resolver(
            new() { [HostsPath] = ["ol_darkstar", "other onsoctcp h 9088"] },
            EnvWith(HostsPath));

        var ex = Assert.Throws<IfxException>(() => r.ResolveServer("ol_darkstar"));
        Assert.Equal(-25556, ex.IfxSqlCode);
        Assert.Equal("h", r.ResolveServer("other").Host);
    }

    [Fact]
    public void An_unresolvable_service_name_reports_minus_931()
    {
        // The server's own "Cannot locate %s service/tcp service", as IBM JDBC reports it.
        var r = Resolver(
            new() { [HostsPath] = ["ol_darkstar onsoctcp h nosuchservice"], ["/etc/services"] = ["sqlexec 9088/tcp"] },
            EnvWith(HostsPath));

        Assert.Equal(-931, Assert.Throws<IfxException>(() => r.ResolveServer("ol_darkstar")).IfxSqlCode);
        Assert.Equal(-931, Assert.Throws<IfxException>(() => r.ResolveServicePort("nosuchservice")).IfxSqlCode);
    }

    [Fact]
    public void Shared_memory_and_drda_nettypes_are_rejected()
    {
        var r = Resolver(
            new() { [HostsPath] = ["shm onipcshm host servername", "drda drsoctcp host 9089"] },
            EnvWith(HostsPath));

        Assert.Equal(-25563, Assert.Throws<IfxException>(() => r.ResolveServer("shm")).IfxSqlCode);
        Assert.Equal(-25563, Assert.Throws<IfxException>(() => r.ResolveServer("drda")).IfxSqlCode);
    }

    // The resolver builds $INFORMIXDIR/etc/sqlhosts with Path.Combine, so the test
    // key must be built the same way to match on both Windows and Unix.
    private static string DirHostsPath(string informixDir) => Path.Combine(informixDir, "etc", "sqlhosts");

    [Fact]
    public void Informixsqlhosts_takes_precedence_over_informixdir()
    {
        // The env-named file is consulted exclusively; a server only in
        // $INFORMIXDIR/etc/sqlhosts is not found while $INFORMIXSQLHOSTS exists.
        var files = new Dictionary<string, string[]>
        {
            ["/custom/sqlhosts"] = ["envsrv onsoctcp eh 9088"],
            [DirHostsPath("/opt/ifx")] = ["dirsrv onsoctcp dh 9088"],
        };
        var env = new Dictionary<string, string?>
        {
            ["INFORMIXSQLHOSTS"] = "/custom/sqlhosts",
            ["INFORMIXDIR"] = "/opt/ifx",
        };
        var r = Resolver(files, env);

        Assert.Equal("eh", r.ResolveServer("envsrv").Host);
        Assert.Equal(-25555, Assert.Throws<IfxException>(() => r.ResolveServer("dirsrv")).IfxSqlCode);
    }

    [Fact]
    public void Falls_back_to_informixdir_when_the_env_file_is_absent()
    {
        var files = new Dictionary<string, string[]>
        {
            [DirHostsPath("/opt/ifx")] = ["dirsrv onsoctcp dh 9088"],
        };
        var env = new Dictionary<string, string?> { ["INFORMIXDIR"] = "/opt/ifx" };
        var r = Resolver(files, env);

        Assert.Equal("dh", r.ResolveServer("dirsrv").Host);
    }

    [Fact]
    public void Registry_entry_is_used_when_no_file_matches()
    {
        var reg = new Dictionary<(string, string), string?>
        {
            [("ol_reg", "HOST")] = "10.0.0.5",
            [("ol_reg", "SERVICE")] = "9088",
            [("ol_reg", "PROTOCOL")] = "onsoctcp",
            [("ol_reg", "OPTIONS")] = "s=4",
        };
        var r = Resolver(
            new(), // no sqlhosts files
            new(),
            registry: (server, value) => reg.TryGetValue((server, value), out var v) ? v : null);

        var e = r.ResolveServer("ol_reg");
        Assert.Equal("10.0.0.5", e.Host);
        Assert.Equal(9088, e.Port);
        Assert.Equal(IfxAuthentication.Pam, e.Authentication);
    }

    // ---- connection-level wiring ----

    [Fact]
    public void Server_only_connection_resolves_the_endpoint_through_sqlhosts()
    {
        byte[] script = AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();
        var duplex = new DuplexStream(script);

        AsfConnectionSettings? captured = null;
        using var cn = new IfxConnection("Server=probe_srv;Database=testlog;User ID=root;Password=pw;Pooling=false");
        cn.SqlhostsResolverFactory = () => Resolver(
            new() { [HostsPath] = ["probe_srv onsoctcp 192.168.88.11 9088"] },
            EnvWith(HostsPath));
        cn.AsfConnectionFactory = s => { captured = s; return new AsfConnection(s, duplex); };

        cn.Open();

        Assert.NotNull(captured);
        Assert.Equal("192.168.88.11", captured!.Host);
        Assert.Equal(9088, captured.Port);
        Assert.False(captured.UseSsl);
        Assert.Equal(ConnectionState.Open, cn.State);
    }
}
