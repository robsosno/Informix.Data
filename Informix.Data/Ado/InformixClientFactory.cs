using System.Data.Common;

namespace Informix.Data;

/// <summary>
/// The <see cref="DbProviderFactory"/> for Informix. Registering
/// <see cref="Instance"/> with <see cref="DbProviderFactories"/> lets
/// provider-agnostic ADO.NET code (and DbProviderFactory-based frameworks) create
/// Informix objects without a compile-time reference.
/// </summary>
/// <remarks>
/// Register with the invariant name <c>"Informix.Data"</c>:
/// <c>DbProviderFactories.RegisterFactory("Informix.Data", InformixClientFactory.Instance)</c>.
/// The ADO.NET provider factory — the standard way to register a .NET
/// database provider with the <c>DbProviderFactories</c> infrastructure.
/// </remarks>
public sealed class InformixClientFactory : DbProviderFactory
{
    public static readonly InformixClientFactory Instance = new();

    private InformixClientFactory() { }

    public override DbConnection CreateConnection() => new IfxConnection();

    public override DbCommand CreateCommand() => new IfxCommand();

    public override DbParameter CreateParameter() => new IfxParameter();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new IfxConnectionStringBuilder();

    public override DbCommandBuilder CreateCommandBuilder() => new IfxCommandBuilder();

    public override DbDataAdapter CreateDataAdapter() => new IfxDataAdapter();

    public override DbDataSourceEnumerator CreateDataSourceEnumerator() => IfxDataSourceEnumerator.Instance;

    public override bool CanCreateDataSourceEnumerator => true;

    public override bool CanCreateBatch => true;

    public override DbBatch CreateBatch() => new IfxBatch();

    public override DbBatchCommand CreateBatchCommand() => new IfxBatchCommand();

    /// <summary>
    /// Creates the real pooled <see cref="IfxDataSource"/> for <paramref name="connectionString"/>
    /// (via <see cref="IfxDataSource.Create(string)"/>), overriding the .NET base class's generic,
    /// non-pooled <see cref="DbDataSource"/> wrapper so provider-agnostic code that goes through
    /// <see cref="DbProviderFactory"/> gets the same pooling/logging/tracing as code that
    /// constructs <see cref="IfxDataSource"/> directly.
    /// </summary>
    public override DbDataSource CreateDataSource(string connectionString) =>
        IfxDataSource.Create(connectionString);
}
