using System.Data;
using System.Reflection;
using System.Text;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Structured ROW / collection decode (IfxComplexDecoder / IfxRow). The blobs are
/// real SENDRECV payloads captured live from IDS 9.59.FC5 (LiveProbe [25] and
/// [35]) — the self-describing bytes for row_t.a (addr_t), a NULL addr_t,
/// SET(int) / LIST(varchar) / MULTISET(decimal) columns, and nested ROW-in-ROW /
/// SET-of-ROW payloads (outer_t/inner_t) — embedded as base64 ground truth.
/// </summary>
public class SqliStructuredComplexTests
{
    private const string RowAddrB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAwAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAFAWACwAAAgbAAAABmFkZHJfdCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAABAAEAAQAGc3RyZWV0ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAANABQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAACAAEAAQADemlwICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAQAAAAUAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAAZAAAAAQdNYWluIFN0MTIzNDX/////AAAACAAAAAA=";
    private const string RowAddrNullB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAwAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAFAWAAMAAAgbAAAABmFkZHJfdCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAABAAEAAQAGc3RyZWV0ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAANABQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAACAAEAAQADemlwICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAQAAAAUAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAAAgAAAAA==";
    private const string SetIntB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAATAB8AAAAbAAQAFVNFVChpbnRlZ2VyIG5vdCBudWxsKSAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAhpbmZvcm1peCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAIAAAAAAABAAEAAQAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAECAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAAMAAAAAQAAAAoAAAAUAAAAHg==";
    private const string ListVarcharB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAAVACkAAAgcAAQAGkxJU1QodmFyY2hhcigxMikgbm90IG51bGwpICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAhpbmZvcm1peCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAIAAAAAAABAAEAAQAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAENAAwAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAAWAAAAAQVhbGljZQNib2L/////AAAABgAAAAA=";
    private const string MsetDecimalB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAAUAB0AAAgdAAQAH01VTFRJU0VUKGRlY2ltYWwoOCwyKSBub3QgbnVsbCkgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAhpbmZvcm1peCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAIAAAAAAABAAEAAQAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAEFCAIAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAAKAAAAAcEBGQAAwWNjAAA=";

    private static byte[] Blob(string b64) => Convert.FromBase64String(b64);

    [Fact]
    public void Row_decodes_named_fields_and_type_name()
    {
        var value = IfxComplexDecoder.Decode(Blob(RowAddrB64), SqliTypeCode.Row, null);

        var row = Assert.IsType<IfxRow>(value);
        Assert.Equal("addr_t", row.TypeName);
        Assert.Equal(2, row.FieldCount);
        Assert.Equal("street", row.GetName(0));
        Assert.Equal("zip", row.GetName(1));
        Assert.Equal("Main St", row.GetValue("street"));
        Assert.Equal("12345", row.GetValue("zip"));
        Assert.False(row.IsDBNull(0));
        Assert.Equal("ROW('Main St','12345')", row.ToString());
    }

    [Fact]
    public void Row_field_clr_types_are_reported()
    {
        var row = Assert.IsType<IfxRow>(IfxComplexDecoder.Decode(Blob(RowAddrB64), SqliTypeCode.Row, null));
        Assert.Equal(typeof(string), row.GetFieldType(0));
        Assert.Equal(typeof(string), row.GetFieldType(1));
        Assert.Equal("Main St", row.GetString(0));
    }

    [Fact]
    public void Null_row_surfaces_DBNull()
    {
        // The whole-value NULL marker (flags sign bit) must beat the non-empty
        // outer length — the limitation the structured decoder was meant to fix.
        var value = IfxComplexDecoder.Decode(Blob(RowAddrNullB64), SqliTypeCode.Row, null);
        Assert.Equal(DBNull.Value, value);
    }

    [Fact]
    public void Set_of_int_decodes_to_object_array()
    {
        var value = IfxComplexDecoder.Decode(Blob(SetIntB64), SqliTypeCode.Set, null);
        var arr = Assert.IsType<object[]>(value);
        Assert.Equal(new object[] { 10, 20, 30 }, arr);
    }

    [Fact]
    public void List_of_varchar_decodes_element_strings_in_order()
    {
        var value = IfxComplexDecoder.Decode(Blob(ListVarcharB64), SqliTypeCode.List, null);
        var arr = Assert.IsType<object[]>(value);
        Assert.Equal(new object[] { "alice", "bob" }, arr);
    }

    [Fact]
    public void Multiset_of_decimal_decodes_element_decimals()
    {
        var value = IfxComplexDecoder.Decode(Blob(MsetDecimalB64), SqliTypeCode.MultiSet, null);
        var arr = Assert.IsType<object[]>(value);
        Assert.Equal(2, arr.Length);
        Assert.Contains(1.25m, arr.Cast<decimal>());
        Assert.Contains(99.99m, arr.Cast<decimal>());
    }

    [Fact]
    public void Truncated_value_block_raises_a_clean_IfxException()
    {
        // Chop the tail of the value block: the descriptor still parses, but an
        // element read runs off the end. It must surface as an IfxException, never
        // leak a raw IndexOutOfRange/ArgumentOutOfRange.
        byte[] full = Blob(SetIntB64);
        byte[] truncated = full[..^2];
        var ex = Assert.Throws<IfxException>(() => IfxComplexDecoder.Decode(truncated, SqliTypeCode.Set, null));
        Assert.Equal(-79716, ex.IfxSqlCode);
    }

    // Nested complex payloads captured live from IDS 9.59.FC5 (LiveProbe [35]):
    // outer_t(id int, inner inner_t, tail int) — a ROW nested inside a ROW, with a
    // scalar sibling declared *after* the nested member (the sibling-recovery
    // proof) — and SET(inner_t not null), a collection whose elements are ROWs.
    private const string NestedOuterB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAABgAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAABAWAEsAAAh2AAQAB291dGVyX3QgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAABAAEAAQACaWQgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAACAAEAAQAFaW5uZXIgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAARAWAAgAAAh1AAQAB2lubmVyX3QgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAADAAIAAwABeCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAQACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAEAAIAAwABeSAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAFAAEAAQAEdGFpbCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAA4AAAAAQAAAAEAHgAAAAAAAQAAAAEAAAACAAAAA/////8AAAAEAAAAAAAAAAT/////AAAAJAAAAAQAAAAA";
    private const string NestedOuterNullInnerB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAABgAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAABAWAC4AAAh2AAQAB291dGVyX3QgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAABAAEAAQACaWQgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAACAAEAAQAFaW5uZXIgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAARAWAAgAAAh1AAQAB2lubmVyX3QgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAADAAIAAwABeCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAQACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAEAAIAAwABeSAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAFAAEAAQAEdGFpbCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAgACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAAAbAAAAAQAAAAIAAQEAAAAF/////wAAAAcAAAAEAAAAAA==";
    private const string NestedSetB64 =
        "AAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAABAAAAAAAAAAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAATAGMAAAh3AAQAFVNFVChpbm5lcl90IG5vdCBudWxsKSAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAhpbmZvcm1peCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAIAAAAAAABAAEAAQAAICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAABEWAAgAAAh1AAQAB2lubmVyX3QgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAARyb290ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAEAAAAAAACAAIAAgABeCAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAAACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAADAAIAAgABeSAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAAAQACAAQAAAAAAAAAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgAAAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIAAAAAAAAAAAAAAAAABQAAAAAQAiAAAAAAAAAAAAAQAAAAEAAAAKAAAAC/////8AAAAEAAAAAAAeAAAAAAABAAAAAQAAABQAAAAV/////wAAAAQAAAAA/////wAAACQAAAAA";

    [Fact]
    public void Nested_row_decodes_and_recovers_sibling_declared_after_it()
    {
        // The regression this mission fixes: readNestedComplex's original port
        // (the 'default:' arm) consumed the rest of the value block on a nested
        // ROW/collection member, silently losing every sibling declared after it —
        // here, outer_t's 'tail' field. This is the headline proof it no longer does.
        var outer = Assert.IsType<IfxRow>(IfxComplexDecoder.Decode(Blob(NestedOuterB64), SqliTypeCode.Row, null));
        Assert.Equal(1, outer.GetValue("id"));
        Assert.Equal(4, outer.GetValue("tail"));
        Assert.Equal(typeof(IfxRow), outer.GetFieldType(outer.GetOrdinal("inner")));

        var inner = Assert.IsType<IfxRow>(outer.GetValue("inner"));
        Assert.Equal(2, inner.GetValue("x"));
        Assert.Equal(3, inner.GetValue("y"));
    }

    [Fact]
    public void Nested_null_row_surfaces_DBNull_and_still_recovers_the_following_sibling()
    {
        // The 3-byte NULL form ([size:int16]=1, then a single 0x01 marker byte)
        // must not disturb the cursor: 'tail' still has to decode correctly.
        var outer = Assert.IsType<IfxRow>(IfxComplexDecoder.Decode(Blob(NestedOuterNullInnerB64), SqliTypeCode.Row, null));
        Assert.Equal(2, outer.GetValue("id"));
        Assert.True(outer.IsDBNull(outer.GetOrdinal("inner")));
        Assert.Equal(5, outer.GetValue("tail"));
    }

    [Fact]
    public void Collection_of_nested_rows_decodes_each_element_as_IfxRow()
    {
        // SET(inner_t not null): exercises DecodeCollection's offset-table branch
        // (Row is one of the HasElementOffsetTable types) with a nested-complex
        // element type, not just a nested-complex ROW *member*.
        var value = IfxComplexDecoder.Decode(Blob(NestedSetB64), SqliTypeCode.Set, null);
        var arr = Assert.IsType<object[]>(value);
        Assert.Equal(2, arr.Length);
        var rows = arr.Cast<IfxRow>().ToArray();
        Assert.Contains(rows, row => (int)row.GetValue("x") == 10 && (int)row.GetValue("y") == 11);
        Assert.Contains(rows, row => (int)row.GetValue("x") == 20 && (int)row.GetValue("y") == 21);
    }

    [Fact]
    public void Nested_size_zero_raises_a_clean_IfxException()
    {
        // Byte offset 2636 is the 'inner' member's nested-complex size word within
        // NestedOuterB64 (located by instrumenting IfxComplexDecoder against this
        // exact capture). size<=0 is the malformed-payload case (JDBC's -79716).
        byte[] blob = Blob(NestedOuterB64);
        blob[2636] = 0;
        blob[2637] = 0;
        var ex = Assert.Throws<IfxException>(() => IfxComplexDecoder.Decode(blob, SqliTypeCode.Row, null));
        Assert.Equal(-79716, ex.IfxSqlCode);
    }

    [Fact]
    public void Nested_negative_size_raises_a_clean_IfxException()
    {
        byte[] blob = Blob(NestedOuterB64);
        blob[2636] = 0xFF;
        blob[2637] = 0xFF; // int16 -1, regardless of endianness
        var ex = Assert.Throws<IfxException>(() => IfxComplexDecoder.Decode(blob, SqliTypeCode.Row, null));
        Assert.Equal(-79716, ex.IfxSqlCode);
    }

    [Fact]
    public void Nested_depth_cap_breach_raises_a_clean_IfxException()
    {
        // A depth-cap breach is only reachable with 16+ levels of genuinely nested
        // ROW/collection descriptors, impractical to hand-build as a wire blob —
        // so this drives the private recursion guard directly via reflection.
        var method = typeof(IfxComplexDecoder).GetMethod("ReadNestedComplex",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        int maxDepth = (int)typeof(IfxComplexDecoder)
            .GetField("MaxNestingDepth", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        var t = new IfxComplexTypeInfo { Type = SqliTypeCode.Row };
        object?[] args = { t, new byte[4], 0, 0, null, false, maxDepth };
        var thrown = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, args));
        var ex = Assert.IsType<IfxException>(thrown.InnerException);
        Assert.Equal(-79716, ex.IfxSqlCode);
    }

    [Fact]
    public void Undecodable_short_blob_falls_back_to_raw_bytes()
    {
        // A blob too short to even hold the descriptor must not throw — it returns
        // the raw bytes so the prior opaque behavior never regresses.
        byte[] junk = new byte[16];
        var value = IfxComplexDecoder.Decode(junk, SqliTypeCode.Row, null);
        Assert.Same(junk, value);
    }

    [Fact]
    public void Reader_materializes_a_row_column_and_GetBytes_yields_the_raw_blob()
    {
        byte[] blob = Blob(RowAddrB64);
        byte[] rowBytes = new byte[4 + blob.Length];
        IfxWireConverter.WriteInt32(rowBytes.AsSpan(0, 4), blob.Length);
        blob.CopyTo(rowBytes.AsSpan(4));

        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 5, tupleSize: 0,
                new SqliStatementKit.Col("a", (short)SqliTypeCode.Row, blob.Length, 0,
                    ExtendedId: 0, ExtOwner: "root", ExtName: "addr_t")),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(rowBytes),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a from row_t";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(typeof(IfxRow), reader.GetFieldType(0));
        Assert.Equal("addr_t", reader.GetDataTypeName(0));
        Assert.True(reader.Read());

        var row = Assert.IsType<IfxRow>(reader.GetValue(0));
        Assert.Equal("Main St", row.GetValue("street"));
        Assert.Equal("12345", row.GetValue("zip"));

        // The escape hatch: GetBytes still yields the raw self-describing payload.
        long n = reader.GetBytes(0, 0, null, 0, 0);
        Assert.Equal(blob.Length, n);
        byte[] raw = new byte[n];
        reader.GetBytes(0, 0, raw, 0, (int)n);
        Assert.Equal(blob, raw);

        Assert.False(reader.Read());
    }

    [Fact]
    public void Reader_materializes_a_set_column_as_object_array()
    {
        byte[] blob = Blob(SetIntB64);
        byte[] rowBytes = new byte[4 + blob.Length];
        IfxWireConverter.WriteInt32(rowBytes.AsSpan(0, 4), blob.Length);
        blob.CopyTo(rowBytes.AsSpan(4));

        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, stmtId: 6, tupleSize: 0,
                new SqliStatementKit.Col("tags", (short)SqliTypeCode.Set, blob.Length, 0,
                    ExtendedId: 0, ExtOwner: "informix", ExtName: "SET(integer not null)")),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.TupleFrame(rowBytes),
            SqliStatementKit.CloseFrame(),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select tags from coll_t";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(typeof(object[]), reader.GetFieldType(0));
        Assert.True(reader.Read());
        Assert.Equal(new object[] { 10, 20, 30 }, Assert.IsType<object[]>(reader.GetValue(0)));
    }
}
