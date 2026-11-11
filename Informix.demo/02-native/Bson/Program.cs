// Bson: documents in a BSON column (stock_spec.spec, added for these demos; needs IDS 12.10+).
//
// A BSON column reads as JSON text by default; the raw BSON bytes and a System.Text.Json
// document are one call away. A BSON parameter takes JSON text (the driver encodes it) or
// already-encoded bytes. The server's own functions (bson_value_*, bson_get) look inside.

using System.Data;
using System.Text.Json;
using Demo;
using Informix.Data;

Ui.Title("Bson");

using var cn = new IfxConnection(DemoEnv.ConnectionString());
cn.Open();

// ---------------------------------------------------------------------------------------------
Ui.Section("1. Reading: JSON text, a JsonDocument, raw BSON");
{
    using var cmd = new IfxCommand("SELECT spec_id, stock_num, manu_code, spec FROM stock_spec ORDER BY spec_id", cn);
    using var r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read())
    {
        string json = r.GetString(3);                                    // the default
        using JsonDocument doc = r.GetFieldValue<JsonDocument>(3);       // parsed
        byte[] bson = r.GetFieldValue<byte[]>(3);                        // as stored
        string keys = string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name));
        rows.Add([r.GetInt64(0), r.GetFieldType(3).Name, json.Length, bson.Length, keys]);
    }
    Ui.Table(["spec_id", "GetFieldType", "JSON chars", "BSON bytes", "top-level fields"], rows);
}

Ui.Section("2. Navigating a document on the client");
{
    using var cmd = new IfxCommand("SELECT spec FROM stock_spec WHERE spec_id = 1", cn);
    using var r = cmd.ExecuteReader();
    r.Read();
    JsonElement root = r.GetFieldValue<JsonElement>(0);
    Ui.Kv("material", root.GetProperty("material").GetString());
    Ui.Kv("colors", string.Join(", ", root.GetProperty("colors").EnumerateArray().Select(c => c.GetString())));
    Ui.Kv("dims.width_mm", root.GetProperty("dims").GetProperty("width_mm").GetInt32());
    Ui.Kv("waterproof", root.GetProperty("waterproof").GetBoolean());
}

// ---------------------------------------------------------------------------------------------
Ui.Section("3. Looking inside on the server");
{
    using var cmd = new IfxCommand(
        "SELECT s.description, s.manu_code, " +
        "       BSON_VALUE_LVARCHAR(p.spec, 'material') AS material, " +
        "       BSON_VALUE_INT(p.spec, 'weight_g') AS weight_g, " +
        "       BSON_VALUE_INT(p.spec, 'dims.width_mm') AS width_mm " +
        "FROM stock_spec p, stock s " +
        "WHERE s.stock_num = p.stock_num AND s.manu_code = p.manu_code AND s.unit = p.unit " +
        "ORDER BY p.spec_id", cn);
    using var r = cmd.ExecuteReader();
    var rows = new List<object?[]>();
    while (r.Read()) rows.Add([r.GetValue(0), r.GetValue(1), r.GetValue(2), r.GetValue(3), r.GetValue(4)]);
    Ui.Table(["description", "manu", "material", "weight_g", "width_mm"], rows);
    Ui.Note("a field a document does not have comes back NULL");
}
{
    using var cmd = new IfxCommand(
        "SELECT spec_id, BSON_GET(spec, 'lens')::JSON FROM stock_spec WHERE BSON_VALUE_INT(spec, 'weight_g') > ? ORDER BY spec_id", cn);
    cmd.Parameters.Add(new IfxParameter("w", 40));
    using var r = cmd.ExecuteReader();
    Ui.Line();
    Ui.Line("   documents heavier than 40 g, and their 'lens' sub-document ({} = the document has none):");
    while (r.Read()) Ui.Line($"      spec {r.GetValue(0)}: {Ui.Show(r.GetValue(1))}");
}

// ---------------------------------------------------------------------------------------------
Ui.Section("4. Writing: a BSON parameter");
{
    using var tx = cn.BeginTransaction();

    const string Json = "{\"material\":\"carbon\",\"weight_g\":31,\"dims\":{\"width_mm\":120,\"height_mm\":60},\"tags\":[\"light\",\"new\"]}";
    long id;
    using (var ins = new IfxCommand("INSERT INTO stock_spec (stock_num, manu_code, unit, spec) VALUES (313, 'ANZ', 'box', ?)", cn) { Transaction = tx })
    {
        ins.Parameters.Add(new IfxParameter("spec", Json) { IfxType = IfxType.Bson });      // JSON text in, BSON stored
        ins.ExecuteNonQuery();
        id = ins.LastSerial8;
        Ui.Kv("inserted spec_id", id);
    }

    byte[] raw;
    using (var sel = new IfxCommand("SELECT spec FROM stock_spec WHERE spec_id = ?", cn) { Transaction = tx })
    {
        sel.Parameters.Add(new IfxParameter("id", id));
        using var r = sel.ExecuteReader();
        r.Read();
        raw = r.GetFieldValue<byte[]>(0);
        Ui.Kv("stored as", $"{raw.Length} bytes of BSON");
        Ui.Kv("read as JSON", r.GetString(0));
    }

    using (var ins = new IfxCommand("INSERT INTO stock_spec (stock_num, manu_code, unit, spec) VALUES (313, 'ANZ', 'box', ?)", cn) { Transaction = tx })
    {
        ins.Parameters.Add(new IfxParameter("spec", raw) { IfxType = IfxType.Bson });         // already-encoded bytes in
        ins.ExecuteNonQuery();
        Ui.Kv("same document from its bytes", $"spec_id {ins.LastSerial8}");
    }

    using (var cnt = new IfxCommand("SELECT COUNT(*) FROM stock_spec WHERE BSON_VALUE_LVARCHAR(spec, 'material') = 'carbon'", cn) { Transaction = tx })
        Ui.Kv("documents with material 'carbon'", cnt.ExecuteScalar());

    tx.Rollback();
    using var after = new IfxCommand("SELECT COUNT(*) FROM stock_spec", cn);
    Ui.Kv("stock_spec rows after the rollback", after.ExecuteScalar());
}

Ui.Line();
Ui.Line("Done.");
