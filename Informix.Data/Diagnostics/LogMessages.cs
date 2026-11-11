using Microsoft.Extensions.Logging;

namespace Informix.Data;

// ReSharper disable InconsistentNaming
#pragma warning disable SYSLIB1015 // Argument is not referenced from the logging message

/// <summary>
/// Source-generated (<see cref="LoggerMessageAttribute"/>) structured log messages
/// for Informix.Data. One strongly-typed,
/// allocation-free method per event, dispatched to the category-specific
/// <see cref="ILogger"/> from <see cref="IfxLoggingConfiguration"/>.
/// </summary>
internal static partial class LogMessages
{
    // ---- connection ----

    [LoggerMessage(
        EventId = IfxEventId.OpeningConnection,
        Level = LogLevel.Trace,
        Message = "Opening connection to {Server}/{Database}...")]
    internal static partial void OpeningConnection(ILogger logger, string Server, string Database, string ConnectionString);

    [LoggerMessage(
        EventId = IfxEventId.OpenedConnection,
        Level = LogLevel.Debug,
        Message = "Opened connection to {Server}/{Database}")]
    internal static partial void OpenedConnection(ILogger logger, string Server, string Database, string ConnectionString);

    [LoggerMessage(
        EventId = IfxEventId.ClosingConnection,
        Level = LogLevel.Trace,
        Message = "Closing connection to {Server}/{Database}...")]
    internal static partial void ClosingConnection(ILogger logger, string Server, string Database);

    [LoggerMessage(
        EventId = IfxEventId.ClosedConnection,
        Level = LogLevel.Debug,
        Message = "Closed connection to {Server}/{Database}")]
    internal static partial void ClosedConnection(ILogger logger, string Server, string Database);

    [LoggerMessage(
        EventId = IfxEventId.OpenedPhysicalConnection,
        Level = LogLevel.Debug,
        Message = "Opened physical connection to {Server}/{Database} (in {DurationMs}ms)")]
    internal static partial void OpenedPhysicalConnection(ILogger logger, string Server, string Database, long DurationMs);

    [LoggerMessage(
        EventId = IfxEventId.ClosedPhysicalConnection,
        Level = LogLevel.Debug,
        Message = "Closed physical connection")]
    internal static partial void ClosedPhysicalConnection(ILogger logger);

    [LoggerMessage(
        EventId = IfxEventId.PhysicalConnectionRetired,
        Level = LogLevel.Debug,
        Message = "Retiring physical connection (reason: {Reason})")]
    internal static partial void PhysicalConnectionRetired(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = IfxEventId.InfoMessageReceived,
        Level = LogLevel.Debug,
        Message = "Server info message received: {Message}")]
    internal static partial void InfoMessageReceived(ILogger logger, string Message);

    [LoggerMessage(
        EventId = IfxEventId.SentOutOfBandCancel,
        Level = LogLevel.Debug,
        Message = "Sent out-of-band cancel to interrupt the running statement")]
    internal static partial void SentOutOfBandCancel(ILogger logger);

    [LoggerMessage(
        EventId = IfxEventId.PoolWarmUpFailed,
        Level = LogLevel.Information,
        Message = "Failed to warm up a Min-Pool-Size connection; the pool will fill on demand")]
    internal static partial void PoolWarmUpFailed(ILogger logger, System.Exception exception);

    [LoggerMessage(
        EventId = IfxEventId.PoolRentTimeout,
        Level = LogLevel.Error,
        Message = "Timed out after {TimeoutSeconds}s waiting for a pooled connection (Max Pool Size reached)")]
    internal static partial void PoolRentTimeout(ILogger logger, int TimeoutSeconds);

    // ---- command ----

    [LoggerMessage(
        EventId = IfxEventId.CommandExecuting,
        Level = LogLevel.Trace,
        Message = "Executing command: {CommandText}")]
    internal static partial void CommandExecuting(ILogger logger, string CommandText);

    [LoggerMessage(
        EventId = IfxEventId.ParameterValues,
        Level = LogLevel.Trace,
        Message = "Command parameters: {Parameters}")]
    internal static partial void ParameterValues(ILogger logger, string Parameters);

    [LoggerMessage(
        EventId = IfxEventId.CommandCompleted,
        Level = LogLevel.Debug,
        Message = "Command completed ({Rows} rows) in {DurationMs}ms")]
    internal static partial void CommandCompleted(ILogger logger, int Rows, long DurationMs);

    [LoggerMessage(
        EventId = IfxEventId.CommandFailed,
        Level = LogLevel.Error,
        Message = "Command failed (sqlcode {SqlCode}): {Message}")]
    internal static partial void CommandFailed(ILogger logger, int SqlCode, string Message);

    // ---- transaction ----

    [LoggerMessage(
        EventId = IfxEventId.TransactionBegan,
        Level = LogLevel.Debug,
        Message = "Began transaction (isolation {IsolationLevel})")]
    internal static partial void TransactionBegan(ILogger logger, string IsolationLevel);

    [LoggerMessage(
        EventId = IfxEventId.TransactionCommitted,
        Level = LogLevel.Debug,
        Message = "Committed transaction")]
    internal static partial void TransactionCommitted(ILogger logger);

    [LoggerMessage(
        EventId = IfxEventId.TransactionRolledBack,
        Level = LogLevel.Debug,
        Message = "Rolled back transaction")]
    internal static partial void TransactionRolledBack(ILogger logger);
}
