using System.Data;
using System.Linq;
using Informix.Data.Asf;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxConnection"/>'s <see cref="ICloneable"/> implementation: the clone is
/// always <see cref="ConnectionState.Closed"/> and never shares or copies live session state
/// (<see cref="IfxConnection.AsfSession"/>, the SQLI session, a rented physical connection,
/// an active transaction/enlistment) — only configuration (connection string incl. password,
/// <see cref="IfxConnection.PamHandler"/>, the <see cref="IfxConnection.AsfConnectionFactory"/>
/// test seam) travels. A connection bound to an <see cref="IfxDataSource"/> clones through the
/// bound constructor so the clone stays on the same pool.
/// </summary>
public class IfxConnectionCloneTests
{
    private const string Cs = "Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false";

    private static byte[] BringUp() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    [Fact]
    public void Connection_implements_the_non_generic_ICloneable()
    {
        Assert.IsAssignableFrom<ICloneable>(new IfxConnection());
    }

    [Fact]
    public void Clone_of_a_standalone_connection_copies_the_full_connection_string_including_the_password()
    {
        using var source = new IfxConnection(Cs);

        using var clone = source.Clone();

        Assert.Equal(source.ConnectionString, clone.ConnectionString);
        Assert.Contains("pwd", clone.ConnectionString); // the public getter never strips the password
        Assert.Equal(ConnectionState.Closed, clone.State);
    }

    [Fact]
    public void Clone_through_the_ICloneable_facade_returns_an_independent_IfxConnection()
    {
        using var source = new IfxConnection(Cs);

        object copy = ((ICloneable)source).Clone();

        var clone = Assert.IsType<IfxConnection>(copy);
        Assert.NotSame(source, clone);
        Assert.Equal(source.ConnectionString, clone.ConnectionString);
    }

    [Fact]
    public void Clone_copies_PamHandler_and_the_AsfConnectionFactory_test_seam()
    {
        using var source = new IfxConnection(Cs);
        var pam = new StubPamHandler();
        Func<AsfConnectionSettings, AsfConnection> factory = s => new AsfConnection(s, new DuplexStream(BringUp()));
        source.PamHandler = pam;
        source.AsfConnectionFactory = factory;

        using var clone = source.Clone();

        Assert.Same(pam, clone.PamHandler);
        Assert.Same(factory, clone.AsfConnectionFactory);
    }

    [Fact]
    public void Clone_of_an_open_connection_is_closed_and_can_be_opened_independently()
    {
        using var source = new IfxConnection(Cs)
        {
            AsfConnectionFactory = s => new AsfConnection(s, new DuplexStream(BringUp())),
        };
        source.Open();
        Assert.Equal(ConnectionState.Open, source.State);

        using var clone = source.Clone();
        Assert.Equal(ConnectionState.Closed, clone.State);
        Assert.Throws<InvalidOperationException>(() => clone.ServerVersion); // closed, and no live session state carried over

        clone.Open(); // the copied AsfConnectionFactory hands the clone its own fresh transport
        Assert.Equal(ConnectionState.Open, clone.State);
        Assert.Equal(ConnectionState.Open, source.State); // the original is unaffected
    }

    [Fact]
    public void Clone_of_a_data_source_bound_connection_rents_from_the_same_data_source_pool()
    {
        var builder = new IfxDataSourceBuilder(Cs.Replace("Pooling=false", "Pooling=true"));
        builder.AsfConnectionFactory = s => new AsfConnection(s, new DuplexStream(BringUp()));
        using var source = builder.Build();

        using var cn = source.CreateConnection(); // bound, unopened
        using var clone = cn.Clone();

        Assert.Equal((0, 0, 0), source.Statistics);
        clone.Open();
        Assert.Equal((1, 0, 1), source.Statistics); // rented from the SAME data source's pool
    }

    private sealed class StubPamHandler : IIfxPamHandler
    {
        public IfxPamResponse Respond(IfxPamChallenge challenge) => new("pw");
    }
}
