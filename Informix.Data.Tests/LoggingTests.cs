using Informix.Data.Asf;
using Informix.Data.Sqli;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Structured-logging behaviour: builds a data source over the in-memory transport with
/// a capturing <see cref="ILoggerFactory"/> and asserts which events were emitted (by
/// their <see cref="IfxEventId"/>) and that parameter values are logged only on opt-in.
/// </summary>
public class LoggingTests
{
    private const string BaseCs =
        "Host=h;Service=9088;Server=ol_test;Database=db;User ID=usr;Password=pwd";

    private static byte[] BringUp() =>
        AsfTestKit.BuildResponse(AsfProtocolMirror.Accept)
            .Concat(SqliTestKit.BringUpReply("9.59.FC5"))
            .ToArray();

    private static IfxDataSource Source(CapturingLoggerFactory factory, byte[] physicalScript,
        string extraCs = "", bool logParameters = false)
    {
        var builder = new IfxDataSourceBuilder(BaseCs + extraCs).UseLoggerFactory(factory);
        if (logParameters)
            builder.EnableParameterLogging();
        builder.AsfConnectionFactory = settings =>
            new AsfConnection(settings, new DuplexStream((byte[])physicalScript.Clone()));
        return builder.Build();
    }

    [Fact]
    public void Open_and_non_query_emit_connection_and_command_events()
    {
        var factory = new CapturingLoggerFactory();
        byte[] script = BringUp().Concat(SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Update, stmtId: 1, tupleSize: 0),
            SqliStatementKit.DoneFrame(rows: 2),
            SqliStatementKit.EotFrame())).ToArray();

        using (var source = Source(factory, script))
        using (var cn = source.OpenConnection())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "update t set x = 1";
            Assert.Equal(2, cmd.ExecuteNonQuery());
        }

        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.OpenedPhysicalConnection && e.Category == "Informix.Data.Connection");
        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.OpenedConnection && e.Category == "Informix.Data.Connection");
        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.CommandExecuting && e.Category == "Informix.Data.Command"
            && e.Message.Contains("update t set x = 1"));
        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.CommandCompleted && e.Message.Contains("2 rows"));
        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.ClosedConnection);
    }

    [Fact]
    public void A_server_error_emits_CommandFailed_with_the_sqlcode()
    {
        var factory = new CapturingLoggerFactory();
        byte[] script = BringUp()
            .Concat(SqliStatementKit.ErrorReply(-206, "The specified table (t) is not in the database."))
            .ToArray();

        using var source = Source(factory, script);
        using var cn = source.OpenConnection();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select * from t";

        Assert.Throws<IfxException>(() => cmd.ExecuteReader());

        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.CommandFailed
            && e.Category == "Informix.Data.Command"
            && e.Level == LogLevel.Error
            && e.Message.Contains("-206"));
    }

    [Fact]
    public void Pool_rent_timeout_emits_the_PoolRentTimeout_event()
    {
        var factory = new CapturingLoggerFactory();
        using var source = Source(factory, BringUp(), extraCs: ";Max Pool Size=1;Login Timeout=1");

        using var first = source.OpenConnection(); // takes the only slot
        var second = source.CreateConnection();
        Assert.Throws<IfxException>(second.Open);

        Assert.Contains(factory.Entries, e =>
            e.EventId == IfxEventId.PoolRentTimeout && e.Level == LogLevel.Error);
    }

    [Fact]
    public void Parameter_values_are_logged_only_when_enabled()
    {
        byte[] script = BringUp().Concat(SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame((short)SqliStatementType.Insert, stmtId: 9, tupleSize: 0),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(rows: 1),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame())).ToArray();

        // Enabled: the parameter values appear.
        var enabled = new CapturingLoggerFactory();
        using (var source = Source(enabled, script, logParameters: true))
        using (var cn = source.OpenConnection())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "insert into t values (?)";
            cmd.Parameters.Add("p", 42);
            cmd.ExecuteNonQuery();
        }
        Assert.Contains(enabled.Entries, e =>
            e.EventId == IfxEventId.ParameterValues && e.Message.Contains("p=42"));

        // Disabled (the default): no parameter-value event, even though the command ran.
        var disabled = new CapturingLoggerFactory();
        using (var source = Source(disabled, script))
        using (var cn = source.OpenConnection())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "insert into t values (?)";
            cmd.Parameters.Add("p", 42);
            cmd.ExecuteNonQuery();
        }
        Assert.DoesNotContain(disabled.Entries, e => e.EventId == IfxEventId.ParameterValues);
        Assert.Contains(disabled.Entries, e => e.EventId == IfxEventId.CommandExecuting);
    }

    [Fact]
    public void Standalone_connections_pick_up_the_globally_initialized_logger()
    {
        var factory = new CapturingLoggerFactory();
        IfxLoggingConfiguration.InitializeLogging(factory);
        try
        {
            byte[] script = BringUp();
            var cn = new IfxConnection(BaseCs + ";Pooling=false");
            cn.AsfConnectionFactory = settings => new AsfConnection(settings, new DuplexStream(script));
            cn.Open();
            cn.Close();

            Assert.Contains(factory.Entries, e => e.EventId == IfxEventId.OpenedConnection);
        }
        finally
        {
            // Restore the global default so other tests aren't affected.
            IfxLoggingConfiguration.InitializeLogging(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        }
    }
}

/// <summary>A minimal in-memory <see cref="ILoggerFactory"/> that records every log call.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public sealed record Entry(string Category, LogLevel Level, int EventId, string Message);

    public List<Entry> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger(string category, List<Entry> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink)
                sink.Add(new Entry(category, logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
