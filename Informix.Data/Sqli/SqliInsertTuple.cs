using System.Text;
using Informix.Data.Types;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// Encodes one INSERT row into the fixed-width tuple image the insert-cursor
/// <c>SQ_PUT</c> protocol carries — the inverse of <see cref="SqliSession.DecodeColumn"/>'s
/// per-type decode. Each field is written at its describe offset in the raw tuple
/// format (integers big-endian, CHAR space-padded, DECIMAL/MONEY packed with no length
/// prefix, DATE as a day count), with the same in-band NULL sentinels the reader
/// recognises. Only the fixed-width scalar types are supported; a statement whose
/// describe contains a variable-length column (VARCHAR/LVARCHAR/blob/complex) is not
/// eligible and the loader falls back to the SQ_BIND/SQ_EXECUTE batch engine.
/// </summary>
internal static class SqliInsertTuple
{
    /// <summary>True when every field can be encoded into a fixed-width tuple image.</summary>
    public static bool IsEligible(IReadOnlyList<IfxColumnMetadata> fields)
    {
        if (fields.Count == 0)
            return false;
        foreach (var f in fields)
            if (!IsFixedWidth(f.SqliTypeCode))
                return false;
        return true;
    }

    private static bool IsFixedWidth(SqliTypeCode t) => t is
        SqliTypeCode.Int or SqliTypeCode.Serial or SqliTypeCode.SmallInt
        or SqliTypeCode.Int8 or SqliTypeCode.Serial8 or SqliTypeCode.BigInt or SqliTypeCode.BigSerial
        or SqliTypeCode.Float or SqliTypeCode.SmFloat
        or SqliTypeCode.Char or SqliTypeCode.NChar
        or SqliTypeCode.Decimal or SqliTypeCode.Money
        or SqliTypeCode.Date;

    /// <summary>
    /// Builds one <paramref name="tupleSize"/>-byte row image from <paramref name="values"/>,
    /// laid out by the field describe (each field occupies the bytes from its start
    /// offset to the next field's start, the last to the tuple end). Throws
    /// <see cref="InvalidCastException"/>/<see cref="OverflowException"/> naming the
    /// 1-based column on a bad value.
    /// </summary>
    public static byte[] Encode(
        IReadOnlyList<IfxColumnMetadata> fields, int tupleSize, ReadOnlySpan<object?> values, Encoding encoding)
    {
        var row = new byte[tupleSize]; // zero-filled: the resting state for a NULL field
        for (int i = 0; i < fields.Count; i++)
        {
            int offset = fields[i].StartPosition;
            int footprint = (i + 1 < fields.Count ? fields[i + 1].StartPosition : tupleSize) - offset;
            EncodeField(fields[i], row.AsSpan(offset, footprint), values[i], encoding, i);
        }
        return row;
    }

    // NULLs use the same in-band sentinels the server writes: MinValue for integers, all 0xFF for floats,
    // INT8 sign 0, DATE int.MinValue; CHAR and packed decimals use an empty/zero image.
    private static void EncodeField(IfxColumnMetadata field, Span<byte> dst, object? value, Encoding encoding, int col)
    {
        // The same notion of NULL the SQ_BIND converters use (SqliBindValue.ConverterFor), so a
        // row means the same whichever engine the loader picked for the statement.
        bool isNull = value is null or DBNull or IfxDecimal { IsNull: true };
        switch (field.SqliTypeCode)
        {
            case SqliTypeCode.SmallInt:
                IfxWireConverter.WriteInt16(dst, isNull
                    ? short.MinValue
                    : (short)RangeChecked(AsInt64(value!, col), short.MinValue, short.MaxValue, col));
                break;

            case SqliTypeCode.Int:
            case SqliTypeCode.Serial:
                IfxWireConverter.WriteInt32(dst, isNull
                    ? int.MinValue
                    : (int)RangeChecked(AsInt64(value!, col), int.MinValue, int.MaxValue, col));
                break;

            case SqliTypeCode.Int8:
            case SqliTypeCode.Serial8:
                IfxWireConverter.ToInt8(isNull ? IfxWireConverter.Int8Null : AsInt64(value!, col)).CopyTo(dst);
                break;

            case SqliTypeCode.BigInt:
            case SqliTypeCode.BigSerial:
                IfxWireConverter.WriteBigInt(dst, isNull ? long.MinValue : AsInt64(value!, col));
                break;

            case SqliTypeCode.SmFloat:
                if (isNull) dst.Fill(0xFF); else IfxWireConverter.ToReal(AsSingle(value!, col)).CopyTo(dst);
                break;

            case SqliTypeCode.Float:
                if (isNull) dst.Fill(0xFF); else IfxWireConverter.ToDouble(AsDouble(value!, col)).CopyTo(dst);
                break;

            case SqliTypeCode.Char:
            case SqliTypeCode.NChar:
                if (!isNull) EncodeChar(dst, AsString(value!, col), encoding, col);
                // NULL: leave the field all-zero — a CHAR whose first byte is 0 is SQL NULL.
                break;

            case SqliTypeCode.Decimal:
            case SqliTypeCode.Money:
                if (!isNull) EncodeDecimal(dst, AsDecimal(value!, col), field, col);
                // NULL: leave all-zero — a zero exponent byte is the packed-decimal NULL marker.
                break;

            case SqliTypeCode.Date:
                if (isNull) IfxWireConverter.WriteInt32(dst, IfxWireConverter.DateNull);
                else IfxWireConverter.ToDate(AsDate(value!, col)).CopyTo(dst);
                break;

            default:
                throw new NotSupportedException(
                    $"column {col + 1}: SQLI type {field.SqliTypeCode} is not supported by the insert-cursor encoder.");
        }
    }

    private static void EncodeChar(Span<byte> dst, string s, Encoding encoding, int col)
    {
        byte[] bytes = IfxWireConverter.EncodeChar(s, encoding);
        if (bytes.Length > dst.Length)
            throw new OverflowException(
                $"column {col + 1}: a string of {bytes.Length} byte(s) does not fit the CHAR({dst.Length}) column.");
        dst.Fill((byte)' ');            // CHAR is blank-padded to its full width
        bytes.CopyTo(dst);
    }

    private static void EncodeDecimal(Span<byte> dst, decimal value, IfxColumnMetadata field, int col)
    {
        // A row image is stored as given: unlike a bound parameter, nothing on the server
        // converts it to the column's type first. So the value is brought to the column's
        // precision and scale here, the way an assignment would: rounded to the declared scale
        // (half away from zero, Informix's rounding) and refused when the integer part is wider
        // than the column allows.
        if (field.NumericScale is { } scale and >= 0 and <= 28)
        {
            value = Math.Round(value, scale, MidpointRounding.AwayFromZero);
            if (field.NumericPrecision is { } precision && precision >= scale)
            {
                decimal limit = 1m;
                for (int i = 0; i < precision - scale && i < 28; i++)
                    limit *= 10m;
                if (precision - scale <= 28 && Math.Abs(value) >= limit)
                    throw new OverflowException(
                        $"column {col + 1}: decimal {value} does not fit the column's DECIMAL({precision},{scale}).");
            }
        }

        // ToWireBytes is the bind form [len:int16][exp][digits]; the tuple carries the bare
        // packed bytes (exp + digits) left-aligned in the fixed footprint, the trailing bytes
        // staying zero — byte-for-byte what the server sends on a fetch. That means the
        // normalized form: no leading zero digit (0.5 is exponent 0 + digit 50, not exponent 1
        // + digits 00 50, which is what the text-driven encoder produces for a value below 1)
        // and no trailing zero digits.
        //
        // The magnitude is packed and normalized first and the sign applied last, so the
        // 100's complement of a negative value is taken over the final digits.
        bool negative = value < 0;
        byte[] wire = PackedValue.FromDecimal(Math.Abs(value)).ToWireBytes();
        int exponent = wire[2] & 0x7F;      // excess-64 exponent of the (positive) magnitude
        Span<byte> digits = wire.AsSpan(3);
        if (!IsAllZero(digits))
        {
            while (digits[0] == 0)
            {
                digits = digits[1..];
                exponent--;
            }
            while (digits[^1] == 0)
                digits = digits[..^1];
        }
        else
        {
            negative = false; // zero has no sign
        }

        int bare = 1 + digits.Length;
        if (bare > dst.Length)
            throw new OverflowException(
                $"column {col + 1}: decimal {value} needs {bare} packed byte(s) but the column holds {dst.Length}.");
        if (negative)
        {
            PackedValue.DecComplement(digits);
            dst[0] = (byte)((exponent ^ 0x7F) & 0x7F);
        }
        else
        {
            dst[0] = (byte)(exponent | 0x80);
        }
        digits.CopyTo(dst[1..]);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> digits)
    {
        foreach (byte d in digits)
            if (d != 0)
                return false;
        return true;
    }

    // ---- cheap coercions (mirror SqliBindValue.ConverterFor's §4.1 rules) ----

    private static long AsInt64(object v, int col) => v switch
    {
        sbyte x => x, byte x => x, short x => x, ushort x => x,
        int x => x, uint x => x, long x => x,
        ulong x when x <= long.MaxValue => (long)x,
        _ => throw Wrong(col, v, "an integer value"),
    };

    private static float AsSingle(object v, int col) => v switch
    {
        float x => x,
        double x => (float)x,
        _ => throw Wrong(col, v, "a float or double"),
    };

    private static double AsDouble(object v, int col) => v switch
    {
        double x => x,
        float x => x,
        _ => throw Wrong(col, v, "a double or float"),
    };

    private static decimal AsDecimal(object v, int col) => v switch
    {
        decimal x => x,
        IfxDecimal x => x.Value,
        sbyte x => x, byte x => x, short x => x, ushort x => x,
        int x => x, uint x => x, long x => x, ulong x => x,
        _ => throw Wrong(col, v, "a decimal or integer value"),
    };

    private static string AsString(object v, int col) => v switch
    {
        string s => s,
        char c => c.ToString(),
        _ => throw Wrong(col, v, "a string"),
    };

    private static DateTime AsDate(object v, int col) => v switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTime dt => dt.Date,
        _ => throw Wrong(col, v, "a DateOnly or DateTime"),
    };

    private static long RangeChecked(long value, long min, long max, int col)
    {
        if (value < min || value > max)
            throw new OverflowException($"column {col + 1}: value {value} is outside the column's range.");
        return value;
    }

    private static InvalidCastException Wrong(int col, object v, string expected) =>
        new($"column {col + 1} expects {expected} but got {v.GetType().Name}.");
}
