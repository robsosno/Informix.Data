using System.Globalization;
using System.Text;
using Informix.Data;

namespace SuperstoreLoader;

internal sealed class DemoLoader
{
    private readonly Target _target;
    private readonly Options _options;
    private readonly string _dataDir;
    private readonly string _sqlDir;
    private readonly Dictionary<string, int> _expected = new();

    public DemoLoader(Target target, Options options, string root)
    {
        _target = target;
        _options = options;
        _dataDir = Path.Combine(root, "data");
        _sqlDir = Path.Combine(root, "sql");
    }

    public void LoadSuperstore()
    {
        string db = _options.Database;
        Start(db, Plan.SuperstoreDrop);

        foreach (string script in Plan.SuperstoreSchema)
            RunScript(script, tolerateErrors: script == "c_percent");
        if (!_options.NoBson) RunScript(Plan.BsonSchema);

        foreach (var table in Plan.SuperstoreTables) LoadTable(table);

        if (!_options.NoBson)
        {
            RunScript(Plan.BsonData);
            _expected[Plan.BsonTable] = 4;
        }
        if (!_options.NoExtras)
            foreach (string script in Plan.SuperstoreExtras) RunScript(script);
        if (_options.ExternalTable) RunScript(Plan.ExternalTableSchema);

        Verify();
    }

    private void Start(string database, string dropScript)
    {
        Console.WriteLine();
        Console.WriteLine($"== {database} " + new string('=', Math.Max(0, 60 - database.Length)));
        _expected.Clear();
        _target.Use(database);

        if (!_target.IsLogged)
            throw new InvalidOperationException(
                $"Database '{database}' has no transaction logging. Create it WITH LOG (sql/create_databases.sql).");

        if (_options.Recreate)
        {
            RunScript(dropScript, tolerateErrors: true, quiet: true);
        }
        else
        {
            long tables = _target.Scalar("SELECT COUNT(*) FROM systables WHERE tabid > 99 AND tabtype = 'T'");
            if (tables > 0)
                throw new InvalidOperationException(
                    $"Database '{database}' already has {tables} table(s); use --recreate to drop the demo objects first.");
        }
    }

    private void RunScript(string name, bool tolerateErrors = false, bool quiet = false)
    {
        string path = Path.Combine(_sqlDir, name + ".sql");
        var statements = SqlScript.Split(File.ReadAllText(path));
        int done = 0, ignored = 0;
        foreach (string statement in statements)
        {
            try
            {
                _target.Execute(statement);
                done++;
            }
            catch (Exception e) when (tolerateErrors)
            {
                ignored++;
                if (!quiet) Console.WriteLine($"  {name}.sql: ignored: {Summary(e)}");
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"{name}.sql, statement {done + 1}: {Summary(e)}\n{Head(statement)}", e);
            }
        }
        Console.WriteLine($"  {name + ".sql",-22} {done} statement(s)" + (ignored > 0 ? $", {ignored} skipped (object not there)" : ""));
    }

    private void LoadTable(TableLoad table)
    {
        var rows = UnlFile.Read(Path.Combine(_dataDir, table.File));
        _target.Begin();
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                try
                {
                    if (table.Table == "catalog") InsertCatalog(rows[i]);
                    else InsertRow(table, rows[i]);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"{table.File}, line {i + 1}: {Summary(e)}", e);
                }
            }
            _target.Commit();
        }
        catch
        {
            _target.Rollback();
            throw;
        }
        _expected[table.Table] = rows.Count;
        Console.WriteLine($"  {table.Table,-22} {rows.Count,5} rows  <- {table.File}");
    }

    /// <summary>
    /// Scalar fields travel as text parameters and the server converts them to the column's type
    /// (dates by the session's DBDATE). ROW and collection values are written into the statement,
    /// because the driver reads them but has no parameter type for them.
    /// </summary>
    private void InsertRow(TableLoad table, string[] fields)
    {
        var sql = new StringBuilder("INSERT INTO ").Append(table.Table).Append(" VALUES (");
        var parameters = new List<Param>();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            string field = fields[i];
            if (field.Length == 0)
                sql.Append("NULL");
            else if (Literal.IsComplex(field))
            {
                sql.Append(Literal.ToSql(Literal.Parse(field)));
                if (table.Casts is not null && table.Casts.TryGetValue(i, out string? type))
                    sql.Append("::").Append(type);
            }
            else
            {
                sql.Append('?');
                parameters.Add(new Param(field));
            }
        }
        sql.Append(')');
        _target.Execute(sql.ToString(), parameters);
    }

    /// <summary>
    /// catalog.unl names client files: the picture inside ROW(path, caption), and the description
    /// as hexadecimal offset,length,path of a slice of catalog.des. Both are uploaded as smart
    /// large objects.
    /// </summary>
    private void InsertCatalog(string[] f)
    {
        var advert = (ContainerNode)Literal.Parse(f[4]);
        var picture = (ScalarNode)advert.Items[0];
        var caption = (ScalarNode)advert.Items[1];

        var pictureParam = IsNull(picture.Text)
            ? new Param(null, IfxType.Blob)
            : new Param(File.ReadAllBytes(Local(picture.Text)), IfxType.Blob);
        var captionParam = new Param(IsNull(caption.Text) ? null : caption.Text);
        var descriptionParam = new Param(f[5].Length == 0 ? null : ReadSlice(f[5]), IfxType.Clob);

        _target.Execute("INSERT INTO catalog VALUES (?, ?, ?, ?, ROW(?, ?), ?)",
        [
            new Param(f[0]), new Param(f[1]), new Param(f[2]), new Param(f[3]),
            pictureParam, captionParam, descriptionParam,
        ]);
    }

    private static bool IsNull(string s) => s.Length == 0 || s.Equals("NULL", StringComparison.OrdinalIgnoreCase);

    // The unload file points into the server's /tmp; the copies live in data/.
    private string Local(string path)
    {
        string local = Path.Combine(_dataDir, Path.GetFileName(path));
        return File.Exists(local) ? local : throw new FileNotFoundException($"{path} is not in {_dataDir}.");
    }

    private string ReadSlice(string descriptor)
    {
        // Offset and length are hexadecimal, as dbaccess writes them.
        string[] part = descriptor.Split(',', 3);
        if (part.Length != 3
            || !int.TryParse(part[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset)
            || !int.TryParse(part[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int length))
            throw new FormatException($"Not an offset,length,file descriptor: {descriptor}");
        byte[] bytes = File.ReadAllBytes(Local(part[2]));
        return Encoding.UTF8.GetString(bytes, offset, Math.Min(length, bytes.Length - offset));
    }

    private void Verify()
    {
        foreach (var (table, expected) in _expected)
        {
            long actual = _target.Count(table);
            if (actual >= 0 && actual != expected)
                Console.WriteLine($"  !! {table}: {actual} rows in the database, {expected} expected");
        }
    }

    private static string Summary(Exception e)
    {
        string message = e.Message.ReplaceLineEndings(" ").Trim();
        for (var inner = e; inner is not null; inner = inner.InnerException)
            if (inner is IfxException ix)
                return $"{message} [SQLCODE {ix.IfxSqlCode}, ISAM {ix.IsamCode}]";
        return message;
    }

    private static string Head(string statement)
    {
        string flat = statement.ReplaceLineEndings(" ");
        return "   " + (flat.Length > 200 ? flat[..200] + "..." : flat);
    }
}
