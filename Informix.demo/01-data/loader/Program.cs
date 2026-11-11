using SuperstoreLoader;

try
{
    var options = Options.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(Options.Usage);
        return 0;
    }

    string root = FindRoot(options.Root);
    Console.WriteLine($"Scripts and data from {root}" + (options.DryRun ? "  (dry run)" : ""));

    using Target target = options.DryRun
        ? new DryTarget()
        : new LiveTarget(options.Connection!);
    var loader = new DemoLoader(target, options, root);
    loader.LoadSuperstore();

    Console.WriteLine();
    Console.WriteLine("Done.");
    return 0;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Failed: " + e.Message);
    return 1;
}

static string FindRoot(string? given)
{
    static bool IsRoot(string dir) =>
        Directory.Exists(Path.Combine(dir, "data")) && Directory.Exists(Path.Combine(dir, "sql"));

    if (given is not null)
        return IsRoot(given) ? Path.GetFullPath(given) : throw new ArgumentException($"'{given}' has no data/ and sql/ folders.");

    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        {
            foreach (string sub in new[] { "", "01-data", Path.Combine("Informix.demo", "01-data") })
            {
                string candidate = Path.Combine(d.FullName, sub);
                if (IsRoot(candidate)) return Path.GetFullPath(candidate);
            }
        }
    }
    throw new ArgumentException("Cannot find the data/ and sql/ folders; pass --root.");
}
