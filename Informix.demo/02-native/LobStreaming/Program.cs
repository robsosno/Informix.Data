// LobStreaming: reading and writing BLOBs and CLOBs a chunk at a time.
//
// Reading: IfxDataReader.GetStream / GetTextReader pull the object from the sbspace in pieces
// (32,000 bytes per round trip), and the stream can seek. Writing: an IfxBlob / IfxClob opened on
// a row's object is written chunk by chunk, so a large value never has to exist in memory -
// a parameter, in contrast, carries the whole value.
//
// Everything that is written happens in a transaction that is rolled back at the end, so the
// catalog table is left as it was (and the sbspace space is returned).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Demo;
using Informix.Data;

Ui.Title("LobStreaming - BLOB and CLOB as streams");
string dir = DemoEnv.WorkDir("lob");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Reading a BLOB as a stream (the picture inside catalog.advert)");
{
    using var cmd = new IfxCommand(
        "SELECT catalog_num, advert.picture FROM catalog WHERE advert.picture IS NOT NULL ORDER BY catalog_num", cn);
    using var reader = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (reader.Read())
    {
        long num = reader.GetInt64(0);
        using Stream stream = reader.GetStream(1);      // must be consumed before the next Read()
        string path = Path.Combine(dir, $"cn_{num}.gif");

        var buffer = new byte[8192];
        int reads = 0, n;
        using (var file = File.Create(path))
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) { file.Write(buffer, 0, n); reads++; }

        // The stream is seekable: jump to the start and look at the header.
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[6];
        stream.ReadExactly(head);
        rows.Add([num, stream.Length, reads, Encoding.ASCII.GetString(head)]);
    }
    Ui.Table(["catalog_num", "bytes", "8 KB reads", "header"], rows);
    Ui.Note($"copied to {dir}");
}

Ui.Section("2. Reading a CLOB as text (GetTextReader)");
{
    using var cmd = new IfxCommand(
        "SELECT catalog_num, advert_descr FROM catalog WHERE advert_descr IS NOT NULL ORDER BY catalog_num", cn);
    using var reader = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (reader.Read())
    {
        using TextReader text = reader.GetTextReader(1);
        string all = text.ReadToEnd();
        rows.Add([reader.GetInt64(0), all.Length, all.Replace("\n", " ")[..Math.Min(all.Length, 50)]]);
    }
    Ui.Table(["catalog_num", "chars", "starts with"], rows);
}

// ---------------------------------------------------------------------------------------------
const int Megabytes = 8;
const int Chunk = 32000;
int chunks = Megabytes * 1024 * 1024 / Chunk;

byte[] Chunk_(int index)
{
    var data = new byte[Chunk];
    new Random(index).NextBytes(data);     // reproducible, so the read-back can be verified
    return data;
}

using var tx = cn.BeginTransaction();

long InsertCatalogRow(byte[] smallPicture, string smallText)
{
    using var cmd = new IfxCommand("INSERT INTO catalog VALUES (?, ?, ?, ?, ROW(?, ?), ?)", cn) { Transaction = tx };
    cmd.Parameters.Add(new IfxParameter("a", "0"));      // 0 = let the server generate the SERIAL8
    cmd.Parameters.Add(new IfxParameter("b", "1"));
    cmd.Parameters.Add(new IfxParameter("c", "HRO"));
    cmd.Parameters.Add(new IfxParameter("d", "case"));
    cmd.Parameters.Add(new IfxParameter("e", smallPicture) { IfxType = IfxType.Blob });
    cmd.Parameters.Add(new IfxParameter("f", "streamed demo row"));
    cmd.Parameters.Add(new IfxParameter("g", smallText) { IfxType = IfxType.Clob });
    try { cmd.ExecuteNonQuery(); }
    catch (IfxException e) { throw new InvalidOperationException($"INSERT INTO catalog failed: SQLCODE {e.IfxSqlCode}, ISAM {e.IsamCode}, {e.Message}", e); }
    return cmd.LastSerial8;
}

Ui.Section($"3. Writing an {Megabytes} MB BLOB in {Chunk:N0}-byte pieces into the row's object");
long blobRow = InsertCatalogRow([0], "placeholder");
Ui.Kv("new catalog row", blobRow);
{
    using var select = new IfxCommand("SELECT advert.picture FROM catalog WHERE catalog_num = ?", cn) { Transaction = tx };
    select.Parameters.Add(new IfxParameter("n", blobRow));
    using var reader = select.ExecuteReader();
    reader.Read();
    IfxBlob blob = reader.GetIfxBlob(0);

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var sw = Stopwatch.StartNew();
    blob.Open(IfxSmartLOBOpenMode.ReadWrite);
    blob.Seek(0, IfxSmartLOBWhence.Begin);
    for (int i = 0; i < chunks; i++)
    {
        byte[] piece = Chunk_(i);
        hash.AppendData(piece);
        blob.Write(piece);
    }
    blob.Truncate(blob.Position);          // drop the old 1-byte tail, if any
    long size = blob.Size;
    blob.Close();
    sw.Stop();
    Ui.Kv("written", $"{size:N0} bytes in {Ui.Ms(sw.Elapsed)} ({size / 1024.0 / 1024 / sw.Elapsed.TotalSeconds:N1} MB/s)");
    Ui.Kv("client memory held", $"one {Chunk:N0}-byte buffer");

    string expected = Convert.ToHexString(hash.GetHashAndReset());
    reader.Close();

    // Read it back as a stream and hash it again.
    using var back = new IfxCommand("SELECT advert.picture FROM catalog WHERE catalog_num = ?", cn) { Transaction = tx };
    back.Parameters.Add(new IfxParameter("n", blobRow));
    using var r2 = back.ExecuteReader();
    r2.Read();
    sw.Restart();
    using Stream s = r2.GetStream(0);
    var buf = new byte[Chunk];
    int n;
    while ((n = s.Read(buf, 0, buf.Length)) > 0) hash.AppendData(buf.AsSpan(0, n));
    sw.Stop();
    string actual = Convert.ToHexString(hash.GetHashAndReset());
    Ui.Kv("read back", $"{s.Length:N0} bytes in {Ui.Ms(sw.Elapsed)}");
    Ui.Kv("SHA-256 equal", expected == actual ? "yes" : $"NO ({expected[..8]} / {actual[..8]})");
}

Ui.Section("4. For comparison: the same BLOB as one parameter");
{
    var whole = new byte[chunks * Chunk];
    for (int i = 0; i < chunks; i++) Chunk_(i).CopyTo(whole, i * Chunk);
    var sw = Stopwatch.StartNew();
    long row = InsertCatalogRow(whole, "parameter upload");
    sw.Stop();
    Ui.Kv("INSERT with a byte[] parameter", $"{whole.Length:N0} bytes in {Ui.Ms(sw.Elapsed)}, row {row}");
    Ui.Note("the array above had to exist in memory first - the streamed write did not");
}

Ui.Section("5. Writing a CLOB in pieces");
{
    long row = InsertCatalogRow([0], "placeholder");
    using var select = new IfxCommand("SELECT advert_descr FROM catalog WHERE catalog_num = ?", cn) { Transaction = tx };
    select.Parameters.Add(new IfxParameter("n", row));
    using var reader = select.ExecuteReader();
    reader.Read();
    IfxClob clob = reader.GetIfxClob(0);

    int lines = 20000;
    var written = new StringBuilder();
    clob.Open(IfxSmartLOBOpenMode.ReadWrite);
    clob.Seek(0, IfxSmartLOBWhence.Begin);
    var batch = new StringBuilder();
    for (int i = 1; i <= lines; i++)
    {
        batch.Append("line ").Append(i).Append(" of the streamed CLOB\n");
        if (i % 500 == 0)
        {
            written.Append(batch);
            clob.Write(batch.ToString().ToCharArray());
            batch.Clear();
        }
    }
    clob.Truncate(clob.Position);
    clob.Close();
    reader.Close();

    using var back = new IfxCommand("SELECT advert_descr FROM catalog WHERE catalog_num = ?", cn) { Transaction = tx };
    back.Parameters.Add(new IfxParameter("n", row));
    using var r2 = back.ExecuteReader();
    r2.Read();
    using TextReader text = r2.GetTextReader(0);
    int count = 0;
    string? last = null;
    while (text.ReadLine() is { } line) { count++; last = line; }
    Ui.Kv("lines written / read back", $"{lines:N0} / {count:N0}");
    Ui.Kv("last line", last);
    Ui.Kv("text equal", count == lines && last == $"line {lines} of the streamed CLOB" ? "yes" : "NO");
}

tx.Rollback();
Ui.Section("6. Rolled back");
{
    using var cmd = new IfxCommand("SELECT COUNT(*) FROM catalog", cn);
    Ui.Kv("catalog rows now", cmd.ExecuteScalar());
    Ui.Note("74 rows, as loaded; the objects written above went back with the rollback");
}
