using System.Data.Common;
using Informix.Data;

namespace Demo;

public static class Bootstrap
{
    /// <summary>The provider invariant name the 03-abstraction programs ask <see cref="DbProviderFactories"/> for.</summary>
    public const string InvariantName = "Informix.Data";

    /// <summary>Registers the Informix factory with DbProviderFactories (what a config file or DI setup would do).</summary>
    public static void Register() => DbProviderFactories.RegisterFactory(InvariantName, InformixClientFactory.Instance);

    /// <summary>Registers the provider and returns its factory by name, as generic code would look it up.</summary>
    public static DbProviderFactory Factory()
    {
        Register();
        return DbProviderFactories.GetFactory(DemoEnv.Get("INFORMIX_DEMO_PROVIDER") ?? InvariantName);
    }

    /// <summary>
    /// An open connection to the demo database, made the provider-independent way:
    /// <see cref="DbProviderFactory.CreateConnection"/> and a connection string. Extra keywords are
    /// added to the connection string (they are the one place the provider's dialect shows).
    /// </summary>
    public static DbConnection Open(params (string Key, string Value)[] extra)
    {
        DbConnection cn = Factory().CreateConnection()!;
        cn.ConnectionString = DemoEnv.ConnectionString(null, extra);
        cn.Open();
        return cn;
    }
}
