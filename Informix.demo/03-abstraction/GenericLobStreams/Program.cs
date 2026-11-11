// GenericLobStreams: large objects through System.Data.Common only.
//
// Reading is fully generic: DbDataReader.GetStream for a BLOB, GetTextReader for a CLOB, and
// GetBytes in pieces. Writing is tried with standard DbType values only: a byte[] goes into a
// BLOB column, a string does not go into a CLOB column (that one needs the provider's own
// IfxType.Clob). Streaming a LOB in pieces with IfxBlob.Write is in 02-native/LobStreaming.

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Demo;

Ui.Title("GenericLobStreams");
string dir = DemoEnv.WorkDir("generic-lob");

using DbConnection cn = Bootstrap.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. A BLOB as a Stream (GetStream)");
{
    using DbCommand cmd = cn.Command(
        "SELECT catalog_num, advert.picture FROM catalog WHERE advert.picture IS NOT NULL ORDER BY catalog_num");
    using DbDataReader r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read())
    {
        long num = r.GetInt64(0);
        using Stream s = r.GetStream(1);
        string path = Path.Combine(dir, $"cn_{num}.gif");
        using (var file = File.Create(path)) s.CopyTo(file);
        using var check = File.OpenRead(path);
        rows.Add([num, new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(check))[..12]]);
    }
    Ui.Table(["catalog_num", "bytes", "SHA-256 (start)"], rows);
}

Ui.Section("2. A CLOB as text (GetTextReader)");
{
    using DbCommand cmd = cn.Command("SELECT catalog_num, advert_descr FROM catalog WHERE advert_descr IS NOT NULL ORDER BY catalog_num");
    using DbDataReader r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read())
    {
        using TextReader text = r.GetTextReader(1);
        string all = text.ReadToEnd();
        rows.Add([r.GetInt64(0), all.Length, all.Replace("\n", " ")[..Math.Min(all.Length, 40)]]);
    }
    Ui.Table(["catalog_num", "chars", "starts with"], rows);
}

Ui.Section("3. The same BLOB in pieces with GetBytes");
{
    using DbCommand cmd = cn.Command("SELECT advert.picture FROM catalog WHERE catalog_num = 10001");
    using DbDataReader r = cmd.ExecuteReader(CommandBehavior.SequentialAccess);
    r.Read();
    var piece = new byte[4096];
    long offset = 0, got;
    int calls = 0;
    try
    {
        while ((got = r.GetBytes(0, offset, piece, 0, piece.Length)) > 0) { offset += got; calls++; }
        Ui.Kv("GetBytes", $"{offset:N0} bytes in {calls} calls of up to {piece.Length}");
    }
    catch (Exception e)
    {
        Ui.Kv("GetBytes", $"{e.GetType().Name}: {Ui.FirstLine(e.Message)}");
    }
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Writing: standard DbType values as the LOB parameters, one LOB at a time");
{
    var picture = File.ReadAllBytes(Path.Combine(dir, "cn_10001.gif"));

    void Attempt(string what, Action<DbCommand> parameters, string sql)
    {
        using DbTransaction tx = cn.BeginTransaction();
        try
        {
            using DbCommand cmd = cn.Command(sql, tx);
            parameters(cmd);
            cmd.ExecuteNonQuery();
            Ui.Kv(what, "succeeded");
        }
        catch (DbException e)
        {
            Ui.Kv(what, $"error {e.ErrorCode}: {Ui.FirstLine(e.Message)}");
        }
        tx.Rollback();
    }

    Attempt("BLOB as DbType.Binary", c => c.Add("@picture", picture, DbType.Binary),
        "INSERT INTO catalog VALUES (0, 1, 'HRO', 'case', ROW(@picture, 'caption'), NULL)");
    Attempt("CLOB as DbType.String", c => c.Add("@description", "a CLOB passed as DbType.String", DbType.String),
        "INSERT INTO catalog VALUES (0, 1, 'HRO', 'case', ROW(NULL, 'caption'), @description)");
    Ui.Note("a CLOB value has to be sent as IfxType.Clob (provider-specific); see 02-native/LobStreaming");
}

Ui.Line();
Ui.Line("Done.");
