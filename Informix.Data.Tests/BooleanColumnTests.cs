using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// BOOLEAN columns.
///
/// A BOOLEAN reaches this client as the opaque built-in UDTVAR(40)/UDTFIXED(41)
/// descriptor carrying extended-type id BOOLEAN(5), framed <c>[flag:1][len:int32][value:1]</c>
/// like the rest of the opaque family. <see cref="SqliSession"/> collapses that to
/// <see cref="SqliTypeCode.Bool"/> so the column reads as a CLR <see cref="bool"/>
/// instead of a one-byte <c>byte[]</c>.
///
/// Two properties have to hold at once, and they pull in opposite directions:
/// the column must decode as a bool, *and* it must stay variable-length — the same
/// <c>IsVariableLengthColumn</c> predicate gates the SQ_RET_TYPE fetch negotiation,
/// where a BOOLEAN has to keep announcing 45 + owner/name or the server desynchronizes
/// mid-fetch and drops the connection.
/// </summary>
public class BooleanColumnTests
{
    private static SqliStatementKit.Col BoolCol(
        string name, int startPos = 0, SqliTypeCode baseType = SqliTypeCode.UdtFixed) =>
        new(name, (short)baseType, 0, startPos,
            ExtendedId: (int)IfxExtendedType.Boolean, ExtOwner: "informix", ExtName: "boolean");

    /// <summary>An in-tuple BOOLEAN field: <c>[flag:1][len:int32][value:1]</c>.
    /// NULL is flag 1 + length 0, a 5-byte footprint with no value byte.</summary>
    private static byte[] BoolField(bool? value) =>
        value is null ? [1, 0, 0, 0, 0] : [0, 0, 0, 0, 1, (byte)(value.Value ? 1 : 0)];

    /// <summary>A length-prefixed VARCHAR field: <c>[len:byte][data]</c>.</summary>
    private static byte[] VarcharField(string s) =>
        [(byte)s.Length, .. IfxWireConverter.EncodeChar(s)];

    // Two-phase select script (open reply, then the typed-fetch reply) — required
    // because BOOLEAN is a walked, variable-length column.
    private static byte[] SelectScript(SqliStatementKit.Col[] cols, params byte[][] rows) => SqliStatementKit.Join(
        SqliStatementKit.DescribeFrame((short)SqliStatementType.Select, stmtId: 1, tupleSize: 0, cols),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.DoneFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.Join(rows.Select(SqliStatementKit.TupleFrame).ToArray()),
        SqliStatementKit.CloseFrame(),
        SqliStatementKit.EotFrame(),
        SqliStatementKit.EotFrame());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Boolean_decodes_to_the_clr_bool(bool value)
    {
        SqliStatementKit.Col[] cols = [BoolCol("flag")];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, BoolField(value)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select flag from t";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        // The bug: the value used to come back as the raw opaque payload (a byte[]),
        // so GetValue(0) was not a bool at all and every ORM mapping to bool blew up.
        Assert.Equal(value, Assert.IsType<bool>(reader.GetValue(0)));
        Assert.Equal(value, reader.GetBoolean(0));
        Assert.Equal(value, reader.GetFieldValue<bool>(0));
        Assert.Equal(value, reader[0]);
        Assert.Equal(value, reader["flag"]);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Boolean_reports_bool_as_its_field_type_and_Bool_as_its_type_name()
    {
        SqliStatementKit.Col[] cols = [BoolCol("flag")];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, BoolField(true)), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select flag from t";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(typeof(bool), reader.GetFieldType(0));
        // Not "boolean": the collapse to Bool takes the column out of the opaque-complex
        // family, so DataTypeName reports the built-in type name rather than the
        // extended one. (Compare LLVC, which collapses to LVarChar the same way.)
        Assert.Equal("Bool", reader.GetDataTypeName(0));

        var col = reader.GetColumnSchema()[0];
        Assert.Equal(typeof(bool), col.DataType);
        Assert.False((bool)col["IsOpaqueComplex"]!);
    }

    [Fact]
    public void Null_boolean_decodes_to_DBNull_without_shifting_the_rest_of_the_row()
    {
        SqliStatementKit.Col[] cols =
        [
            BoolCol("flag"),
            new("m", (short)SqliTypeCode.VarChar, 20, 0),
        ];
        // flag 1 + len 0 => SQL NULL, a 5-byte footprint (no trailing value byte).
        byte[] row = [.. BoolField(null), .. VarcharField("ok")];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, row), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select flag, m from t";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.Equal("ok", reader.GetString(1));
    }

    [Fact]
    public void Boolean_column_does_not_desynchronize_the_columns_behind_it()
    {
        // Why the three parts of the fix are interdependent, and cannot be split up.
        //
        // The walk classification keys off the *raw* wire base type, so a BOOLEAN always
        // forced a walk — while it described as UdtVar/UdtFixed the walk read its
        // footprint through the opaque branch and got 5 + len right by accident. The
        // moment the collapse rewrites SqliTypeCode to Bool, that no longer holds: Bool
        // matches neither the opaque branch nor the ROW/collection branch, so the walk
        // falls through to the fixed-width `len = col.ByteLength` and desynchronizes
        // every column behind the bool. Adding Bool to IsVariableLengthColumn and to the
        // walk's opaque branch is what keeps the collapse safe. Verified by reverting
        // just those two lists: this test, the two NULL/mixed-row tests and the
        // SQ_RET_TYPE test below all fail together.
        SqliStatementKit.Col[] cols =
        [
            BoolCol("a"),
            BoolCol("b"),
            new("m", (short)SqliTypeCode.VarChar, 20, 0),
            new("n", (short)SqliTypeCode.Int, 4, 0),
        ];
        byte[] row =
        [
            .. BoolField(true),
            .. BoolField(false),
            .. VarcharField("marker"),
            0x00, 0x00, 0x30, 0x39, // int 12345
        ];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, row), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select a, b, m, n from t";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Equal("marker", reader.GetString(2));
        Assert.Equal(12345, reader.GetInt32(3));
    }

    [Fact]
    public void Boolean_walks_correctly_when_mixed_with_nulls_across_rows()
    {
        // A NULL bool is 5 bytes and a non-NULL one is 6, so the row layout genuinely
        // varies per row — the case a fixed-offset reader cannot represent at all.
        SqliStatementKit.Col[] cols =
        [
            BoolCol("flag"),
            new("m", (short)SqliTypeCode.VarChar, 20, 0),
        ];
        byte[] row1 = [.. BoolField(true), .. VarcharField("first")];
        byte[] row2 = [.. BoolField(null), .. VarcharField("second")];
        byte[] row3 = [.. BoolField(false), .. VarcharField("third")];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, row1, row2, row3), out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select flag, m from t";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("first", reader.GetString(1));

        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("second", reader.GetString(1));

        Assert.True(reader.Read());
        Assert.False(reader.GetBoolean(0));
        Assert.Equal("third", reader.GetString(1));

        Assert.False(reader.Read());
    }

    [Fact]
    public void Boolean_collapses_from_both_UdtVar_and_UdtFixed_descriptors()
    {
        // The server has been seen announcing BOOLEAN on either opaque base type;
        // the extended id is what identifies it, so both must collapse to Bool.
        foreach (var baseType in new[] { SqliTypeCode.UdtVar, SqliTypeCode.UdtFixed })
        {
            SqliStatementKit.Col[] cols = [BoolCol("flag", baseType: baseType)];

            using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, BoolField(true)), out _);
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "select flag from t";
            using var reader = cmd.ExecuteReader();

            Assert.Equal(typeof(bool), reader.GetFieldType(0));
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
        }
    }

    [Fact]
    public void Boolean_still_announces_type_45_with_owner_and_name_in_SQ_RET_TYPE()
    {
        // The constraint that keeps the collapse safe. SendRetType only remaps a column
        // that IsVariableLengthColumn accepts, and BOOLEAN's extended id (5) maps to
        // wire type 45 followed by the extended owner/name. Drop Bool out of the
        // var-length set to "simplify" and this frame silently loses its owner/name
        // strings, which desynchronizes the server mid-fetch.
        SqliStatementKit.Col[] cols = [BoolCol("flag")];

        using var cn = SqliStatementKit.OpenConnection(SelectScript(cols, BoolField(true)), out var duplex);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select flag from t";
        using (var reader = cmd.ExecuteReader())
            Assert.True(reader.Read());

        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.RetType);
        w.WriteInt16(1);  // direction: 1 = FETCH
        w.WriteInt16(1);  // one column
        w.WriteInt16(45); // BOOL — from extended id BOOLEAN(5), not the base UDTFIXED(41)
        w.WriteChar("informix");
        w.WriteChar("boolean");
        w.WriteInt32(0);  // column length

        Assert.Contains(
            BitConverter.ToString(ms.ToArray()),
            BitConverter.ToString(duplex.Output.ToArray()));
    }
}
