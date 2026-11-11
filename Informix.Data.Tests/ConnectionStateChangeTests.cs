using System.Data;
using Informix.Data.Asf;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxConnection"/> raises <see cref="System.Data.Common.DbConnection.StateChange"/>
/// like every ADO.NET connection: Closed to Open when Open() succeeds, Open to Closed when the
/// connection is closed or disposed, and nothing when an Open() fails.
/// </summary>
public class ConnectionStateChangeTests
{
    private static IfxConnection Unopened(out DuplexStream duplex)
    {
        byte[] script = AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();
        duplex = new DuplexStream(script);
        var local = duplex;
        var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = s => new AsfConnection(s, local);
        return cn;
    }

    [Fact]
    public void Open_then_Close_raise_one_event_each()
    {
        using var cn = Unopened(out _);
        var seen = new List<(ConnectionState From, ConnectionState To)>();
        cn.StateChange += (_, e) => seen.Add((e.OriginalState, e.CurrentState));

        cn.Open();
        Assert.Equal([(ConnectionState.Closed, ConnectionState.Open)], seen);

        cn.Close();
        Assert.Equal(
            [(ConnectionState.Closed, ConnectionState.Open), (ConnectionState.Open, ConnectionState.Closed)], seen);
    }

    [Fact]
    public void Closing_a_closed_connection_raises_nothing()
    {
        using var cn = Unopened(out _);
        int events = 0;
        cn.StateChange += (_, _) => events++;

        cn.Close();                 // never opened
        cn.Open();
        cn.Close();
        cn.Close();                 // already closed

        Assert.Equal(2, events);
    }

    [Fact]
    public void Dispose_of_an_open_connection_raises_Open_to_Closed()
    {
        var cn = Unopened(out _);
        cn.Open();
        var seen = new List<ConnectionState>();
        cn.StateChange += (_, e) => seen.Add(e.CurrentState);

        cn.Dispose();

        Assert.Equal([ConnectionState.Closed], seen);
    }

    [Fact]
    public void The_state_is_already_final_when_the_event_is_raised()
    {
        using var cn = Unopened(out _);
        var states = new List<ConnectionState>();
        cn.StateChange += (sender, _) => states.Add(((IfxConnection)sender!).State);

        cn.Open();
        cn.Close();

        Assert.Equal([ConnectionState.Open, ConnectionState.Closed], states);
    }

    [Fact]
    public void A_failed_Open_raises_nothing_and_leaves_the_connection_closed()
    {
        using var cn = new IfxConnection("Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd;Pooling=false");
        cn.AsfConnectionFactory = _ => throw new IfxException("no server", -1);
        int events = 0;
        cn.StateChange += (_, _) => events++;

        Assert.Throws<IfxException>(cn.Open);

        Assert.Equal(0, events);
        Assert.Equal(ConnectionState.Closed, cn.State);
    }
}
