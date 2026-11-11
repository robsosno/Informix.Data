using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Informix.Data;

/// <summary>
/// Enumerates the visible Informix data sources. This first version returns a
/// correctly-shaped but empty table; a follow-up can parse the local
/// <c>sqlhosts</c> file (<c>%INFORMIXDIR%\etc\sqlhosts</c> / the
/// <c>INFORMIXSQLHOSTS</c> environment variable).
/// </summary>
public sealed class IfxDataSourceEnumerator : DbDataSourceEnumerator
{
    /// <summary>The singleton enumerator instance.</summary>
    public static readonly IfxDataSourceEnumerator Instance = new();

    private IfxDataSourceEnumerator() { }

    public override DataTable GetDataSources()
    {
        var table = new DataTable("DataSources") { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("ServerName", typeof(string));
        table.Columns.Add("InstanceName", typeof(string));
        table.Columns.Add("IsClustered", typeof(string));
        table.Columns.Add("Version", typeof(string));
        return table;
    }
}
