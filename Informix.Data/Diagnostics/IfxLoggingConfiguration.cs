using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Informix.Data;

/// <summary>
/// Holds the <see cref="ILogger"/> instances Informix.Data logs through, one per
/// category (<c>Informix.Data.Connection</c>, <c>Informix.Data.Command</c>,
/// <c>Informix.Data.Transaction</c>), plus whether parameter values may be logged.
///
/// <para>Applications configure logging by building an
/// <see cref="IfxDataSource"/> via <see cref="IfxDataSourceBuilder.UseLoggerFactory"/>.
/// Code that uses a standalone <see cref="IfxConnection"/> (no data source) can opt
/// in globally with <see cref="InitializeLogging"/>.</para>
/// </summary>
/// <remarks>The type is public
/// so <see cref="InitializeLogging"/> is reachable, but its instance members are
/// internal — logging is an implementation concern, not part of the data surface.</remarks>
public sealed class IfxLoggingConfiguration
{
    /// <summary>The do-nothing configuration used until a logger factory is supplied.</summary>
    internal static readonly IfxLoggingConfiguration NullConfiguration =
        new(NullLoggerFactory.Instance, isParameterLoggingEnabled: false);

    // Process-wide defaults for standalone connections; set by InitializeLogging.
    private static ILoggerFactory _globalLoggerFactory = NullLoggerFactory.Instance;
    private static bool _globalIsParameterLoggingEnabled;
    private static IfxLoggingConfiguration? _globalDefault;

    internal IfxLoggingConfiguration(ILoggerFactory loggerFactory, bool isParameterLoggingEnabled)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ConnectionLogger = loggerFactory.CreateLogger("Informix.Data.Connection");
        CommandLogger = loggerFactory.CreateLogger("Informix.Data.Command");
        TransactionLogger = loggerFactory.CreateLogger("Informix.Data.Transaction");
        IsParameterLoggingEnabled = isParameterLoggingEnabled;
    }

    internal ILogger ConnectionLogger { get; }
    internal ILogger CommandLogger { get; }
    internal ILogger TransactionLogger { get; }

    /// <summary>
    /// Whether bound parameter values are written alongside the SQL text (this can
    /// reveal sensitive data — off by default). Enabled per data source via
    /// <see cref="IfxDataSourceBuilder.EnableParameterLogging"/>.
    /// </summary>
    internal bool IsParameterLoggingEnabled { get; }

    /// <summary>
    /// The configuration standalone <see cref="IfxConnection"/> instances use, derived
    /// from the last <see cref="InitializeLogging"/> call (the null configuration until
    /// then). Cached so repeated <c>Open()</c> calls don't rebuild loggers.
    /// </summary>
    internal static IfxLoggingConfiguration GlobalDefault
    {
        get
        {
            if (ReferenceEquals(Volatile.Read(ref _globalLoggerFactory), NullLoggerFactory.Instance))
                return NullConfiguration;
            return LazyInitializer.EnsureInitialized(
                ref _globalDefault,
                () => new IfxLoggingConfiguration(_globalLoggerFactory, _globalIsParameterLoggingEnabled))!;
        }
    }

    /// <summary>
    /// Globally routes logging for standalone <see cref="IfxConnection"/> use through
    /// <paramref name="loggerFactory"/>. Must be called before any other Informix.Data
    /// API is used. Applications that build an <see cref="IfxDataSource"/> should prefer
    /// <see cref="IfxDataSourceBuilder.UseLoggerFactory"/> instead — this is the
    /// legacy/global escape hatch.
    /// </summary>
    /// <param name="loggerFactory">The factory to create Informix.Data's loggers from.</param>
    /// <param name="parameterLoggingEnabled">
    /// Whether parameter values may be logged alongside SQL statements; may reveal
    /// sensitive information. Defaults to <see langword="false"/>.
    /// </param>
    public static void InitializeLogging(ILoggerFactory loggerFactory, bool parameterLoggingEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _globalLoggerFactory = loggerFactory;
        _globalIsParameterLoggingEnabled = parameterLoggingEnabled;
        _globalDefault = null; // rebuilt lazily against the new factory
    }
}
