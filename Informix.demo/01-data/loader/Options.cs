namespace SuperstoreLoader;

internal sealed class Options
{
    public const string ConnectionVariable = "INFORMIX_DEMO_CONNECTION";

    public string? Connection { get; private set; }
    public string Database { get; private set; } = "superstore_demo2";
    public string? Root { get; private set; }
    public bool Recreate { get; private set; }
    public bool NoBson { get; private set; }
    public bool NoExtras { get; private set; }
    public bool ExternalTable { get; private set; }
    public bool DryRun { get; private set; }
    public bool Help { get; private set; }

    public const string Usage = """
        SuperstoreLoader - fills the Informix demo database.

          --connection <string>   Connection string; its Database key is ignored. Without it the
                                  INFORMIX_DEMO_CONNECTION from Informix.demo/.env (or the
                                  environment variable of that name) is used.
          --database <name>       Demo database (default superstore_demo2).
                                  It must exist already: run sql/create_databases.sql on the server.
          --recreate              Drop what an earlier load created (tables, routines, types) first.
                                  Without it the loader stops when a database already has tables.
          --no-bson               Skip the stock_spec table (BSON needs IDS 12.10 or newer).
          --no-extras             Skip indexes, SPL routines and triggers.
          --external-table        Also create ext_customer; its file must already be in the
                                  server's /tmp (see sql/c_extcustom.sql).
          --root <dir>            Folder holding data/ and sql/ (default: found automatically).
          --dry-run               Connect to nothing; print what would be executed.
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--connection": o.Connection = Next(); break;
                case "--database": o.Database = Next(); break;
                case "--root": o.Root = Next(); break;
                case "--recreate": o.Recreate = true; break;
                case "--no-bson": o.NoBson = true; break;
                case "--no-extras": o.NoExtras = true; break;
                case "--external-table": o.ExternalTable = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--help" or "-h" or "-?": o.Help = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }
        o.Connection ??= Demo.DemoEnv.Get(ConnectionVariable);
        if (!o.Help && !o.DryRun && string.IsNullOrWhiteSpace(o.Connection))
            throw new ArgumentException($"No connection string: pass --connection or set {ConnectionVariable} in Informix.demo/.env.");
        return o;
    }
}
