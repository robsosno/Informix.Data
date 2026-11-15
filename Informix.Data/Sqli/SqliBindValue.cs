using System.Text;
using Informix.Data.Types;
using Informix.Data.Wire;

namespace Informix.Data.Sqli;

/// <summary>
/// One parameter value in SQ_BIND wire form: the Informix type code, the
/// encoded payload (<c>null</c> for SQL NULL) and the precision word the bind
/// carries (the encoded byte length, or the scale for DECIMAL and the
/// qualifier for DATETIME — the exact quirk of <c>IfxSqli.sendBind</c>).
/// </summary>
internal readonly struct SqliBindValue
{
    public required SqliTypeCode Type { get; init; }
    public byte[]? Data { get; init; }
    public short Precision { get; init; }

    /// <summary>
    /// For a BYTE/TEXT parameter, the large object's payload — sent in the
    /// SQ_BBIND phase that follows SQ_BIND. Null for every non-blob (or NULL blob)
    /// value; the 56-byte descriptor itself travels in <see cref="Data"/>.
    /// </summary>
    public byte[]? BlobData { get; init; }

    /// <summary>
    /// True for the placeholder of an argument the caller left out and the routine declares a
    /// DEFAULT for: it travels as the argument's type word with the null indicator -2, which the
    /// server reads as "use the default".
    /// </summary>
    public bool UseDefault { get; init; }

    /// <summary>The bind for an omitted argument that has a DEFAULT, typed as the routine's descriptor says.</summary>
    public static SqliBindValue DefaultValue(short type) =>
        new() { Type = (SqliTypeCode)type, Data = null, UseDefault = true };

    /// <summary>True when this bind is a simple large object needing an SQ_BBIND follow-up.</summary>
    public bool IsBlob => Type is SqliTypeCode.Byte or SqliTypeCode.Text;

    private readonly string? _extendedOwner;
    private readonly string? _extendedTypeName;

    /// <summary>
    /// Extended-type owner for an opaque/UDT bind (type code &gt; 18): written
    /// after the type word in SQ_BIND, exactly as <c>IfxSqli.sendBind</c> does
    /// for USVER servers. Empty encodes as the zero-length string for a null owner.
    /// </summary>
    public string ExtendedOwner { get => _extendedOwner ?? ""; init => _extendedOwner = value; }

    /// <summary>Extended-type name for an opaque/UDT bind (e.g. "ifx_lo_spec", "clob").</summary>
    public string ExtendedTypeName { get => _extendedTypeName ?? ""; init => _extendedTypeName = value; }

    /// <summary>
    /// An opaque UDT argument in the <c>IfxUDT.toIfx</c> wire form: type
    /// SENDRECV(44) and the payload carried with a 4-byte length prefix. FastPath
    /// arguments send precision 0; the INSERT locator bind sends the payload length.
    /// </summary>
    public static SqliBindValue Udt(string name, byte[] payload, short precision = 0, string owner = "")
    {
        var data = new byte[4 + payload.Length];
        IfxWireConverter.WriteInt32(data.AsSpan(0, 4), payload.Length);
        payload.CopyTo(data.AsSpan(4));
        return new SqliBindValue
        {
            Type = SqliTypeCode.SendRecv,
            Data = data,
            Precision = precision,
            ExtendedOwner = owner,
            ExtendedTypeName = name,
        };
    }

    /// <summary>
    /// A character argument for a server routine, in the self-describing LVARCHAR form FastPath
    /// calls use: wire type 43 with the extended name <c>lvarchar</c>, payload
    /// <c>[len:int32][bytes]</c>.
    /// </summary>
    public static SqliBindValue LVarCharArgument(string value, Encoding encoding)
    {
        byte[] bytes = IfxWireConverter.EncodeChar(value, encoding);
        var data = new byte[4 + bytes.Length];
        IfxWireConverter.WriteInt32(data.AsSpan(0, 4), bytes.Length);
        bytes.CopyTo(data.AsSpan(4));
        return new SqliBindValue
        {
            Type = SqliTypeCode.LVarChar,
            Data = data,
            Precision = (short)bytes.Length,
            ExtendedOwner = "",
            ExtendedTypeName = "lvarchar",
        };
    }

    /// <summary>
    /// Binds a smart-large-object locator (the 72-byte <c>ifx_lo_t</c> handed
    /// back by <c>ifx_lo_create</c>) into an INSERT/UPDATE parameter:
    /// type SENDRECV(44), extended owner "informix", extended name "clob"/"blob",
    /// precision 72, data = [len:int32][locator]. A null <paramref name="locator"/>
    /// binds SQL NULL (the type/name words still travel, per <c>sendBind</c>'s
    /// null path).
    /// </summary>
    public static SqliBindValue SmartBlobLocator(byte[]? locator, bool isClob)
    {
        string name = isClob ? "clob" : "blob";
        if (locator is null)
            return new SqliBindValue
            {
                Type = SqliTypeCode.SendRecv,
                Data = null,
                ExtendedOwner = "informix",
                ExtendedTypeName = name,
            };
        return Udt(name, locator, precision: (short)locator.Length, owner: "informix");
    }

    /// <summary>
    /// A SQL NULL that still names its type — what an SPL <c>OUT</c> argument binds as,
    /// since the server needs the slot's type word to know what to write back into it
    /// (<c>IfxCallableStatement.putOutParamInfoIntoVector</c>'s typed <c>setNull</c>).
    /// An opaque/UDT type additionally carries its extended owner and type name.
    /// </summary>
    public static SqliBindValue TypedNull(SqliTypeCode type, string extendedTypeName = "", string owner = "") =>
        new() { Type = type, Data = null, ExtendedOwner = owner, ExtendedTypeName = extendedTypeName };

    /// <summary>
    /// Binds a <see cref="DateTime"/> or <see cref="TimeOnly"/> as a DATETIME with exactly
    /// <paramref name="qualifier"/> (the target column's), dropping the fields it lacks. A
    /// <see cref="TimeOnly"/> is used only for a time-only qualifier (HOUR or finer start):
    /// it has no date to supply. Returns false for any other value, which binds as usual.
    /// </summary>
    internal static bool TryDateTimeWithQualifier(object? value, short qualifier, out SqliBindValue bind)
    {
        var start = (IfxTimeUnit)IfxQualifier.GetStartCode(qualifier);
        var end = (IfxTimeUnit)IfxQualifier.GetEndCode(qualifier);
        DateTime? dt = value switch
        {
            DateTime d when start <= IfxTimeUnit.Second && end >= start => d,
            TimeOnly t when start is >= IfxTimeUnit.Hour and <= IfxTimeUnit.Second && end >= start
                => DateTime.MinValue.Add(t.ToTimeSpan()),
            _ => null,
        };
        if (dt is not { } v)
        {
            bind = default;
            return false;
        }
        var packed = PackedValue.FromDateTime(v, qualifier);
        bind = new SqliBindValue { Type = SqliTypeCode.DateTime, Data = packed.ToWireBytes(), Precision = packed.Qualifier };
        return true;
    }

    /// <summary>
    /// Encodes a CLR value. Strings travel as CHAR (the server casts to the target type),
    /// whole numbers as their exact-width types, DECIMAL/DATETIME as packed
    /// values. A <paramref name="hint"/> of <see cref="SqliTypeCode.Text"/> forces a
    /// string to bind as a TEXT large object; <c>byte[]</c> binds as BYTE.
    /// <paramref name="longRowId"/> selects the wider Informix 15 blob-descriptor
    /// layout and must match what the session negotiated.
    /// </summary>
    public static SqliBindValue From(
        object? value, Encoding encoding, SqliTypeCode hint = SqliTypeCode.Unknown, bool longRowId = false)
    {
        switch (value)
        {
            case null or DBNull:
                return new SqliBindValue { Type = SqliTypeCode.Char, Data = null };

            case byte[] bytes:
                return Blob(hint == SqliTypeCode.Text ? SqliTypeCode.Text : SqliTypeCode.Byte, bytes, longRowId);

            case string s when hint == SqliTypeCode.Text:
                return Blob(SqliTypeCode.Text, IfxWireConverter.EncodeChar(s, encoding), longRowId);

            case string s:
            {
                // CHAR binds carry the value length-prefixed.
                // Sending the bare bytes makes the server read the first two data
                // bytes as a length and block waiting for a huge phantom value.
                byte[] raw = IfxWireConverter.EncodeChar(s, encoding);
                var data = new byte[raw.Length + 2];
                data[0] = (byte)(raw.Length >> 8);
                data[1] = (byte)raw.Length;
                raw.CopyTo(data, 2);
                return new SqliBindValue { Type = SqliTypeCode.Char, Data = data, Precision = 0 };
            }

            case char c:
                return From(c.ToString(), encoding);

            // Informix BOOLEAN is sent as the character 't' or 'f'.
            case bool b:
                return From(b ? "t" : "f", encoding);

            case byte or sbyte or short:
                return new SqliBindValue
                {
                    Type = SqliTypeCode.SmallInt,
                    Data = IfxWireConverter.ToInt16(Convert.ToInt16(value)),
                    Precision = 2,
                };

            case int i:
                return new SqliBindValue { Type = SqliTypeCode.Int, Data = IfxWireConverter.ToInt32(i), Precision = 4 };

            // The unsigned types bind as the next wider signed type, the narrowest that holds
            // their whole range (a ushort above 32767 does not fit SMALLINT); ulong has no wider
            // integer to go to and binds as DECIMAL.
            case ushort us:
                return From((int)us, encoding);

            case uint ui:
                return From((long)ui, encoding);

            case ulong ul:
                return From((decimal)ul, encoding);

            // long maps to INT8 (the 10-byte format), not BIGINT, so it works on servers without bit 54.
            case long l:
                return new SqliBindValue
                {
                    Type = SqliTypeCode.Int8,
                    Data = IfxWireConverter.ToInt8(l),
                    Precision = IfxWireConverter.Int8Size,
                };

            case float f:
                return new SqliBindValue { Type = SqliTypeCode.SmFloat, Data = IfxWireConverter.ToReal(f), Precision = 4 };

            case double d:
                return new SqliBindValue { Type = SqliTypeCode.Float, Data = IfxWireConverter.ToDouble(d), Precision = 8 };

            // The bind precision of a DECIMAL is its scale.
            case decimal m:
                return new SqliBindValue
                {
                    Type = SqliTypeCode.Decimal,
                    Data = PackedValue.FromDecimal(m).ToWireBytes(),
                    Precision = (short)m.Scale, // sendBind sends the scale for DECIMAL
                };

            case DateOnly dateOnly:
                return new SqliBindValue
                {
                    Type = SqliTypeCode.Date,
                    Data = IfxWireConverter.ToDate(dateOnly.ToDateTime(TimeOnly.MinValue)),
                    Precision = 4,
                };

            // A DateTime at exactly midnight is sent as DATE; use IfxType.DateTime to force a DATETIME.
            case DateTime dt when dt.TimeOfDay == TimeSpan.Zero:
                return new SqliBindValue { Type = SqliTypeCode.Date, Data = IfxWireConverter.ToDate(dt), Precision = 4 };

            case DateTime dt:
            {
                var packed = PackedValue.FromDateTime(dt);
                return new SqliBindValue
                {
                    Type = SqliTypeCode.DateTime,
                    // Bound packed values keep the 2-byte length prefix
                    // (PackedValue.toIfx == convIfx(prependLength: true));
                    // only tuple data arrives without it.
                    Data = packed.ToWireBytes(),
                    Precision = packed.Qualifier,
                };
            }

            case TimeOnly timeOnly:
            {
                // A parameter explicitly pinned to IfxType.Date cannot receive a
                // TimeOnly value — it has no date part to supply. The bind is
                // otherwise always HOUR-start (DATETIME HOUR TO SECOND/FRACTION(5)).
                if (hint == SqliTypeCode.Date)
                    throw new NotSupportedException(
                        "A TimeOnly value cannot supply the date part of a parameter pinned to IfxType.Date.");
                var packed = PackedValue.FromTime(timeOnly);
                return new SqliBindValue { Type = SqliTypeCode.DateTime, Data = packed.ToWireBytes(), Precision = packed.Qualifier };
            }

            // Mapped to INTERVAL DAY(2) TO SECOND, or to FRACTION(5) when there is a sub-second part (ticks -> 100 ns units).
            case TimeSpan timeSpan:
                return TimeSpanBind(timeSpan);

            // A value of the driver's own decimal / datetime types binds like the CLR one it holds; an
            // IfxDateTime also keeps its qualifier, so a YEAR TO MINUTE stays a YEAR TO MINUTE.
            case IfxDecimal ifxDecimal:
                return ifxDecimal.IsNull ? NullBind(SqliTypeCode.Decimal) : DecimalBind(ifxDecimal.Value);

            case IfxDateTime ifxDateTime:
                return ifxDateTime.IsNull ? NullBind(SqliTypeCode.DateTime) : IfxDateTimeBind(ifxDateTime);

            case IfxTimeSpan ifxTimeSpan:
                return ifxTimeSpan.IsNull
                    ? new SqliBindValue { Type = SqliTypeCode.Interval, Data = null }
                    : IntervalBind(PackedValue.FromInterval(ifxTimeSpan.ToInterval()));

            case IfxMonthSpan ifxMonthSpan:
                return ifxMonthSpan.IsNull
                    ? new SqliBindValue { Type = SqliTypeCode.Interval, Data = null }
                    : IntervalBind(PackedValue.FromInterval(ifxMonthSpan.ToInterval()));

            default:
                throw new NotSupportedException(
                    $"Binding a parameter of type {value.GetType()} is not supported by the Informix SQLI port yet.");
        }
    }

    /// <summary>
    /// Resolves a reusable per-column converter for <see cref="IfxBulkLoader"/>: a delegate
    /// that turns one CLR value (or <c>null</c>/<see cref="DBNull"/>) into a wire bind of the
    /// column's declared type. The delegate does exactly one cheap coercion — a strict cast
    /// plus the already-minimal wire encoding, with no per-value type sniffing, culture,
    /// parsing or <c>Convert.To*</c> semantics. Each converter accepts its primary CLR type
    /// plus the cheap checked casts of the loader design §4.1; anything else throws
    /// <see cref="InvalidCastException"/> and a narrowing overflow throws
    /// <see cref="OverflowException"/>, both naming the (1-based) column and declared type.
    /// A <c>null</c>/<see cref="DBNull"/> is bound as SQL NULL with the column's type word.
    /// </summary>
    /// <param name="declared">The column's declared provider type.</param>
    /// <param name="column">The zero-based placeholder ordinal (for error messages).</param>
    /// <param name="encoding">The connection's client character encoding.</param>
    /// <param name="longRowId">
    /// Whether the session negotiated the wider Informix 15 blob-descriptor layout.
    /// </param>
    internal static Func<object?, SqliBindValue> ConverterFor(
        IfxType declared, int column, Encoding encoding, bool longRowId = false)
    {
        bool IsNull(object? v) => v is null or DBNull
            or IfxDecimal { IsNull: true } or IfxDateTime { IsNull: true } or IfxTimeSpan { IsNull: true } or IfxMonthSpan { IsNull: true };

        switch (declared)
        {
            case IfxType.SmallInt or IfxType.TinyInt:
                return v => IsNull(v) ? NullBind(SqliTypeCode.SmallInt)
                    : Int16Bind((short)RangeChecked(AsInt64(v!, column, declared), short.MinValue, short.MaxValue, column, declared));

            case IfxType.Integer or IfxType.Int or IfxType.Serial:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Int)
                    : Int32Bind((int)RangeChecked(AsInt64(v!, column, declared), int.MinValue, int.MaxValue, column, declared));

            case IfxType.Int8 or IfxType.Serial8 or IfxType.BigInt or IfxType.BigSerial:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Int8) : Int8Bind(AsInt64(v!, column, declared));

            case IfxType.SmallFloat or IfxType.Real:
                return v => IsNull(v) ? NullBind(SqliTypeCode.SmFloat) : SmallFloatBind(AsSingle(v!, column, declared));

            case IfxType.Float or IfxType.Double:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Float) : FloatBind(AsDouble(v!, column, declared));

            case IfxType.Decimal or IfxType.Numeric or IfxType.Money:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Decimal) : DecimalBind(AsDecimal(v!, column, declared));

            case IfxType.Char or IfxType.VarChar or IfxType.NChar or IfxType.NVarChar
                or IfxType.LVarChar or IfxType.Char1:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Char) : From(AsString(v!, column, declared), encoding);

            case IfxType.Boolean or IfxType.Bit:
                // Booleans travel as the CHAR 't'/'f' the server casts (IfxBoolean.toIfx path).
                return v => IsNull(v) ? NullBind(SqliTypeCode.Char) : From(AsBool(v!, column, declared) ? "t" : "f", encoding);

            case IfxType.Date:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Date) : DateBind(AsDate(v!, column, declared));

            case IfxType.DateTime or IfxType.Timestamp or IfxType.SmallDateTime or IfxType.Time:
                return v => IsNull(v) ? NullBind(SqliTypeCode.DateTime)
                    : v is IfxDateTime idt ? IfxDateTimeBind(idt) : DateTimeBind(AsDateTime(v!, column, declared));

            case IfxType.IntervalDayFraction:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Interval)
                    : v switch
                    {
                        IfxTimeSpan ts => IntervalBind(PackedValue.FromInterval(ts.ToInterval())),
                        TimeSpan t => TimeSpanBind(t),
                        _ => throw WrongType(column, declared, v!, "a TimeSpan or IfxTimeSpan"),
                    };

            case IfxType.IntervalYearMonth:
                return v => IsNull(v) ? NullBind(SqliTypeCode.Interval)
                    : v is IfxMonthSpan ms
                        ? IntervalBind(PackedValue.FromInterval(ms.ToInterval()))
                        : throw WrongType(column, declared, v!, "an IfxMonthSpan");

            case IfxType.Byte or IfxType.Binary or IfxType.VarBinary or IfxType.Image:
                return v => IsNull(v)
                    ? NullBind(SqliTypeCode.Byte)
                    : Blob(SqliTypeCode.Byte, AsBytes(v!, column, declared), longRowId);

            case IfxType.Text or IfxType.NText:
                return v =>
                {
                    if (IsNull(v)) return NullBind(SqliTypeCode.Text);
                    return v is byte[] b
                        ? Blob(SqliTypeCode.Text, b, longRowId)
                        : Blob(SqliTypeCode.Text,
                            IfxWireConverter.EncodeChar(AsString(v!, column, declared), encoding), longRowId);
                };

            default:
                throw new NotSupportedException(
                    $"IfxBulkLoader does not support binding a parameter declared as IfxType.{declared}.");
        }
    }

    // ---- converter building blocks (each produces the same bind SqliBindValue.From does) ----

    private static SqliBindValue NullBind(SqliTypeCode type) => new() { Type = type, Data = null };

    private static SqliBindValue Int16Bind(short v) =>
        new() { Type = SqliTypeCode.SmallInt, Data = IfxWireConverter.ToInt16(v), Precision = 2 };

    private static SqliBindValue Int32Bind(int v) =>
        new() { Type = SqliTypeCode.Int, Data = IfxWireConverter.ToInt32(v), Precision = 4 };

    private static SqliBindValue Int8Bind(long v) =>
        new() { Type = SqliTypeCode.Int8, Data = IfxWireConverter.ToInt8(v), Precision = IfxWireConverter.Int8Size };

    private static SqliBindValue SmallFloatBind(float v) =>
        new() { Type = SqliTypeCode.SmFloat, Data = IfxWireConverter.ToReal(v), Precision = 4 };

    private static SqliBindValue FloatBind(double v) =>
        new() { Type = SqliTypeCode.Float, Data = IfxWireConverter.ToDouble(v), Precision = 8 };

    private static SqliBindValue DecimalBind(decimal v) =>
        new() { Type = SqliTypeCode.Decimal, Data = PackedValue.FromDecimal(v).ToWireBytes(), Precision = (short)v.Scale };

    private static SqliBindValue DateBind(DateTime dateOnly) =>
        new() { Type = SqliTypeCode.Date, Data = IfxWireConverter.ToDate(dateOnly), Precision = 4 };

    private static SqliBindValue DateTimeBind(DateTime dt)
    {
        var packed = PackedValue.FromDateTime(dt);
        return new SqliBindValue { Type = SqliTypeCode.DateTime, Data = packed.ToWireBytes(), Precision = packed.Qualifier };
    }

    private static SqliBindValue IfxDateTimeBind(IfxDateTime value)
    {
        var packed = PackedValue.FromDateTime(new DateTime(value.Ticks), value.Qualifier);
        return new SqliBindValue { Type = SqliTypeCode.DateTime, Data = packed.ToWireBytes(), Precision = packed.Qualifier };
    }

    // A TimeSpan binds as INTERVAL DAY(2) TO SECOND, or DAY TO FRACTION(5) only when there is a
    // sub-second part to carry (no silent truncation, matching the TimeOnly bind).
    private static SqliBindValue TimeSpanBind(TimeSpan timeSpan)
    {
        long seconds = timeSpan.Ticks / TimeSpan.TicksPerSecond;
        long subSecondTicks = timeSpan.Ticks % TimeSpan.TicksPerSecond;
        var end = subSecondTicks == 0 ? IfxTimeUnit.Second : IfxTimeUnit.Fraction5;
        short qual = IfxQualifier.ForInterval(2, IfxTimeUnit.Day, end);
        return IntervalBind(PackedValue.FromInterval(new IfxIntervalDayFraction(seconds, subSecondTicks * 100, qual)));
    }

    // A packed DATETIME/INTERVAL value bound as SQ_BIND INTERVAL (same length-prefixed
    // packed shape as DateTimeBind; the qualifier travels in Precision).
    private static SqliBindValue IntervalBind(PackedValue packed) =>
        new() { Type = SqliTypeCode.Interval, Data = packed.ToWireBytes(), Precision = packed.Qualifier };

    // ---- cheap coercions (§4.1): the primary CLR type plus checked widening/narrowing ----

    private static long AsInt64(object v, int column, IfxType declared) => v switch
    {
        sbyte x => x,
        byte x => x,
        short x => x,
        ushort x => x,
        int x => x,
        uint x => x,
        long x => x,
        ulong x when x <= long.MaxValue => (long)x,
        _ => throw WrongType(column, declared, v, "an integer value"),
    };

    private static float AsSingle(object v, int column, IfxType declared) => v switch
    {
        float x => x,
        double x => (float)x,
        _ => throw WrongType(column, declared, v, "a float or double"),
    };

    private static double AsDouble(object v, int column, IfxType declared) => v switch
    {
        double x => x,
        float x => x,
        _ => throw WrongType(column, declared, v, "a double or float"),
    };

    private static decimal AsDecimal(object v, int column, IfxType declared) => v switch
    {
        decimal x => x,
        IfxDecimal x => x.Value,
        sbyte x => x,
        byte x => x,
        short x => x,
        ushort x => x,
        int x => x,
        uint x => x,
        long x => x,
        ulong x => x,
        _ => throw WrongType(column, declared, v, "a decimal or integer value"),
    };

    private static string AsString(object v, int column, IfxType declared) => v switch
    {
        string s => s,
        char c => c.ToString(),
        _ => throw WrongType(column, declared, v, "a string"),
    };

    private static bool AsBool(object v, int column, IfxType declared) =>
        v is bool b ? b : throw WrongType(column, declared, v, "a bool");

    private static DateTime AsDate(object v, int column, IfxType declared) => v switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTime dt => dt.Date,
        _ => throw WrongType(column, declared, v, "a DateOnly or DateTime"),
    };

    private static DateTime AsDateTime(object v, int column, IfxType declared) => v switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => throw WrongType(column, declared, v, "a DateTime"),
    };

    private static byte[] AsBytes(object v, int column, IfxType declared) =>
        v is byte[] b ? b : throw WrongType(column, declared, v, "a byte[]");

    private static long RangeChecked(long value, long min, long max, int column, IfxType declared)
    {
        if (value < min || value > max)
            throw new OverflowException(
                $"column {column + 1} (declared {declared}): value {value} is outside the range of {declared}.");
        return value;
    }

    private static InvalidCastException WrongType(int column, IfxType declared, object v, string expected) =>
        new($"column {column + 1} (declared {declared}) expects {expected} but got {v.GetType().Name}.");

    // A simple large object (BYTE/TEXT) bind. The SQ_BIND payload is the in-tuple
    // descriptor (IfxBlob.toIfx), which — verified live against IDS 9.59 — the server
    // treats as all-zero except tb_size; the authoritative length is the SQ_BLOB frame
    // in the SQ_BBIND phase (IfxBlob.getEncodedLength() gives the bind precision).
    // The payload itself rides in BlobData. With isLongRowId the descriptor is 68 bytes
    // and tb_size sits at [32..36) instead of [16..20) — the only two things that move.
    private static SqliBindValue Blob(SqliTypeCode type, byte[] data, bool longRowId)
    {
        int width = longRowId ? SqliSession.BlobDescriptorSizeLong : SqliSession.BlobDescriptorSize;
        int sizeOffset = longRowId ? SqliSession.BlobSizeOffsetLong : SqliSession.BlobSizeOffset;
        var descriptor = new byte[width];
        IfxWireConverter.WriteInt32(descriptor.AsSpan(sizeOffset, 4), data.Length); // tb_size
        return new SqliBindValue
        {
            Type = type,
            Data = descriptor,
            Precision = (short)width,
            BlobData = data,
        };
    }
}
