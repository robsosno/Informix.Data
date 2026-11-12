using System.Data;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Regression tests for the defects found in the driver-wide correctness review: each test
/// fails against the code as it stood before the corresponding fix.
/// </summary>
public class CorrectnessReviewTests
{
    private static byte[] IntRow(int n)
    {
        var row = new byte[4];
        IfxWireConverter.WriteInt32(row, n);
        return row;
    }

    private static byte[] Locator()
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(0x20 + i);
        return l;
    }

    private static readonly byte[] Eot = SqliStatementKit.EotFrame();

    // ---- smart LOB: a read reports what the server actually returned ----

    // SQ_LODATA read reply for a read at end of object: opType 0, amount 0, one trailing word.
    private static byte[] EmptyLoRead()
    {
        var ms = new MemoryStream();
        var w = new IfxWireWriter(ms);
        w.WriteInt16((short)SqliMessage.LoData);
        w.WriteInt16(0);
        w.WriteInt32(0);
        w.WriteInt16(0);
        return SqliStatementKit.Join(ms.ToArray(), Eot);
    }

    [Fact]
    public void Blob_read_returns_the_bytes_the_object_has_and_then_zero()
    {
        byte[] content = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.RoutineHandleFrame("db", 67), Eot,          // ifx_lo_open handle
            SqliStatementKit.FpReturnFrame(39), Eot,                     // fd 39
            SqliStatementKit.LoDataReadFrame(content), Eot,              // 10 of the 100 asked for
            EmptyLoRead(),                                               // at end of object
            Eot);

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator()));
        blob.Open(IfxSmartLOBOpenMode.ReadOnly);

        var buffer = new byte[100];
        // Used to report 100 -- the size of the request -- with 90 phantom zero bytes, and never
        // 0, so the usual "read until 0" loop over a BLOB did not terminate.
        Assert.Equal(10, blob.Read(buffer));
        Assert.Equal(content, buffer[..10]);
        Assert.Equal(10, blob.Position);
        Assert.Equal(0, blob.Read(buffer));
        Assert.Equal(10, blob.Position);
    }

    // ---- interval bind: the leading field is wide enough for the value ----

    [Theory]
    [InlineData(99, 2)]
    [InlineData(100, 3)]
    [InlineData(150, 3)]
    [InlineData(40000, 5)]
    public void TimeSpan_bind_widens_the_day_field_to_fit(int days, int expectedDayDigits)
    {
        var span = new TimeSpan(days, 3, 4, 5);
        SqliBindValue bind = SqliBindValue.From(span, System.Text.Encoding.Latin1);

        Assert.Equal(SqliTypeCode.Interval, bind.Type);
        Assert.Equal(expectedDayDigits, IfxQualifier.GetLeadingFieldWidth(bind.Precision));
        // Every packed digit is a base-100 digit; 150 days in a DAY(2) field used to go out as
        // the single byte 150.
        Assert.All(bind.Data!.AsSpan(3).ToArray(), digit => Assert.InRange(digit, (byte)0, (byte)99));

        var decoded = (IfxIntervalDayFraction)PackedValue.FromWire(bind.Data.AsSpan(2), bind.Precision).ToInterval()!;
        Assert.Equal(span, decoded.ToTimeSpan());
    }

    [Fact]
    public void Negative_long_TimeSpan_round_trips_through_the_bind()
    {
        var span = -new TimeSpan(1234, 5, 6, 7);
        SqliBindValue bind = SqliBindValue.From(span, System.Text.Encoding.Latin1);
        var decoded = (IfxIntervalDayFraction)PackedValue.FromWire(bind.Data.AsSpan(2), bind.Precision).ToInterval()!;
        Assert.Equal(span, decoded.ToTimeSpan());
    }

    [Fact]
    public void Month_span_bind_widens_the_year_field_to_fit()
    {
        var span = new IfxMonthSpan(12345, 6);
        SqliBindValue bind = SqliBindValue.From(span, System.Text.Encoding.Latin1);
        Assert.All(bind.Data!.AsSpan(3).ToArray(), digit => Assert.InRange(digit, (byte)0, (byte)99));
        var decoded = (IfxIntervalYearMonth)PackedValue.FromWire(bind.Data.AsSpan(2), bind.Precision).ToInterval()!;
        Assert.Equal(12345L * 12 + 6, decoded.Months);
    }

    // ---- unsigned CLR integers bind without overflow ----

    [Fact]
    public void Unsigned_integers_bind_as_the_next_wider_type()
    {
        var enc = System.Text.Encoding.Latin1;
        Assert.Equal(SqliTypeCode.Int, SqliBindValue.From((ushort)40000, enc).Type);
        Assert.Equal(SqliTypeCode.Int8, SqliBindValue.From(4_000_000_000u, enc).Type);
        Assert.Equal(SqliTypeCode.Decimal, SqliBindValue.From(ulong.MaxValue, enc).Type);
    }

    // ---- a failed execute does not leave the session reserved ----

    [Fact]
    public void A_command_that_fails_before_executing_releases_its_session()
    {
        using var cn = SqliStatementKit.OpenConnection(Eot, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into t values (?, ?)";
        cmd.Parameters.Add(new IfxParameter { Value = 1 });   // one value for two placeholders

        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteReader());
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteScrollReader());

        // Still reserved, the default session would never be handed out again and every later
        // statement would run on a new sibling session.
        Assert.False(cn.SqliSession!.IsReservedForAcquire);
        Assert.Single(cn.SqliSession.Link.Sessions);
        Assert.Same(cn.SqliSession, cn.AcquireSession());
    }

    [Fact]
    public void An_empty_batch_releases_its_session()
    {
        using var cn = SqliStatementKit.OpenConnection(Eot, out _);
        using var batch = cn.CreateBatch();

        Assert.Equal(0, batch.ExecuteNonQuery());
        using (batch.ExecuteReader()) { }

        Assert.False(cn.SqliSession!.IsReservedForAcquire);
    }

    // ---- a reader whose cursor a commit closed keeps its session ----

    [Fact]
    public void Rows_buffered_before_a_commit_survive_a_later_statement()
    {
        byte[] script = SqliStatementKit.Join(
            Eot,                                                         // SQ_BEGIN
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, 1, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(), Eot,                           // prepare
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(101)),
            SqliStatementKit.TupleFrame(IntRow(102)), Eot,               // open + first buffer (2 rows)
            Eot,                                                         // SQ_CMMTWORK
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, 2, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(), Eot,                           // second statement: prepare
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(201)), Eot,               // ... open + fetch
            Eot, Eot,                                                    // ... close + release
            Eot);                                                        // first reader: release

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        var tx = cn.BeginTransaction();
        using var first = cn.CreateCommand();
        first.Transaction = tx;
        first.CommandText = "select n from a";
        using var reader = first.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(101, reader.GetInt32(0));

        tx.Commit(); // closes the non-hold cursor on the server; row 102 is already buffered

        using var second = cn.CreateCommand();
        second.CommandText = "select n from b";
        // Used to run on the first reader's session (its cursor being closed made it look free),
        // wiping the buffered row: the Read() below then quietly reported end of data.
        Assert.Equal(201, second.ExecuteScalar());

        Assert.True(reader.Read());
        Assert.Equal(102, reader.GetInt32(0));
        var ex = Assert.Throws<InvalidOperationException>(() => reader.Read());
        Assert.Contains("transaction ended", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disposing_a_reader_after_its_connection_closed_does_not_throw()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, 1, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(), Eot,
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(1)), Eot);                // cursor left open

        var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from a";
        var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        cn.Close();
        long sentAtClose = duplex.Output.Length;

        reader.Dispose(); // used to write SQ_CLOSE / SQ_RELEASE into the closed transport
        cmd.Dispose();
        Assert.Equal(sentAtClose, duplex.Output.Length);
    }

    // ---- transactions: the driver's view follows what the server confirmed ----

    [Fact]
    public void A_failed_commit_leaves_the_transaction_to_be_rolled_back()
    {
        byte[] script = SqliStatementKit.Join(
            Eot,                                                         // SQ_BEGIN
            SqliStatementKit.ErrorReply(-691, "missing key in referenced table"), Eot, // SQ_CMMTWORK fails
            Eot);                                                        // SQ_RBWORK from Dispose

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);
        var tx = cn.BeginTransaction();

        var ex = Assert.Throws<IfxException>(() => tx.Commit());
        Assert.Equal(-691, ex.IfxSqlCode);
        // Used to be forgotten here, so neither Dispose nor Close rolled it back and the
        // connection stayed inside a transaction nothing knew about.
        Assert.Same(tx, cn.ActiveTransaction);

        long before = duplex.Output.Length;
        tx.Dispose();
        Assert.Null(cn.ActiveTransaction);
        byte[] sent = duplex.Output.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 0, (byte)SqliMessage.RollbackWork, 0, 0, 0, (byte)SqliMessage.Eot }, sent);
    }

    [Fact]
    public void A_transaction_whose_characteristics_are_rejected_is_rolled_back()
    {
        byte[] script = SqliStatementKit.Join(
            Eot,                                                         // SQ_BEGIN
            SqliStatementKit.ErrorReply(-201, "A syntax error has occurred."), Eot, // SET TRANSACTION fails
            Eot,                                                         // SQ_RBWORK
            Eot);                                                        // the next SQ_BEGIN

        using var cn = SqliStatementKit.OpenConnection(script, out var duplex);

        Assert.Throws<IfxException>(() => cn.BeginTransaction(IsolationLevel.RepeatableRead));
        Assert.Null(cn.ActiveTransaction);
        byte[] sent = duplex.Output.ToArray();
        // The BEGIN was undone rather than left open on the server with no owner.
        Assert.Equal(
            new byte[] { 0, (byte)SqliMessage.RollbackWork, 0, 0, 0, (byte)SqliMessage.Eot },
            sent[^6..]);

        using var tx = cn.BeginTransaction(); // "already in transaction" on the server otherwise
        Assert.Same(tx, cn.ActiveTransaction);
    }

    [Fact]
    public void BeginTransaction_with_an_isolation_level_works_while_a_reader_is_open()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DescribeFrame(
                (short)SqliStatementType.Select, 1, tupleSize: 4,
                new SqliStatementKit.Col("n", (short)SqliTypeCode.Int, 4, 0)),
            SqliStatementKit.DoneFrame(), Eot,
            SqliStatementKit.DoneFrame(),
            SqliStatementKit.TupleFrame(IntRow(1)), Eot,                 // reader open on the default session
            Eot,                                                         // SQ_BEGIN
            SqliStatementKit.DoneFrame(), Eot,                           // SET TRANSACTION (on a free session)
            Eot);                                                        // rollback at dispose

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select n from a with hold";
        cmd.HoldCursor = true;
        var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        // Used to throw "A data reader is already open" from the SET TRANSACTION -- after
        // SQ_BEGIN had already gone out, leaving an ownerless transaction on the server.
        var tx = cn.BeginTransaction(IsolationLevel.ReadCommitted);
        Assert.Same(tx, cn.ActiveTransaction);
        Assert.Equal(1, reader.GetInt32(0)); // the reader's row is untouched
        tx.Dispose();
    }

    // ---- parser: the braces of a collection constructor are not a comment ----

    [Theory]
    [InlineData("insert into t values (1, SET{?, ?})", 2)]
    [InlineData("insert into t values (1, set {?}, LIST  {?, ?}, multiset\n{?})", 4)]
    [InlineData("insert into t values (SET{ROW(?, LIST{?})})", 2)]
    [InlineData("select {+ FULL(t) } a from t where b = ?", 1)]            // a directive is still a comment
    [InlineData("select a {is this ? a comment} from t where b = ?", 1)]
    [InlineData("update t {SET ?} set a = ?", 1)]                          // SET inside a comment is not the keyword
    [InlineData("select a -- set\n{?} from t where b = ?", 1)]             // ... nor at the end of a line comment
    [InlineData("select 'set' {?} from t where b = ?", 1)]                 // ... nor inside a literal
    [InlineData("select offset{?} from t where b = ?", 1)]                 // a longer identifier ending in "set"
    [InlineData("select a from t where b = ? {trailing ?}", 1)]
    public void Positional_placeholders_inside_collection_constructors_are_counted(string sql, int expected)
    {
        IfxParsedCommand parsed = IfxSqlParser.Parse(sql);
        Assert.Equal(expected, parsed.PlaceholderCount);
        Assert.Same(sql, parsed.Text); // positional text is still sent exactly as written
        Assert.Equal(expected, IfxSqlParser.CountPositionalPlaceholders(sql));
    }

    [Fact]
    public void Named_placeholders_inside_collection_constructors_are_rewritten()
    {
        IfxParsedCommand parsed = IfxSqlParser.Parse(
            "insert into t values (:id, SET{:a, :b}, LIST{@c}) {+ not :a_param }");
        Assert.Equal("insert into t values (?, SET{?, ?}, LIST{?}) {+ not :a_param }", parsed.Text);
        Assert.Equal(new[] { "id", "a", "b", "c" }, parsed.ParameterNames);
    }

    // ---- insert-cursor row images: DECIMAL in the column's own shape ----

    private static IfxColumnMetadata DecimalColumn(int precision, int scale) => new()
    {
        Name = "d",
        SqliTypeCode = SqliTypeCode.Decimal,
        Length = precision << 8 | scale,
        PrecisionScale = (short)(precision << 8 | scale),
        StartPosition = 0,
    };

    private static byte[] EncodeDecimal(object value, int precision = 7, int scale = 2)
    {
        var column = DecimalColumn(precision, scale);
        return SqliInsertTuple.Encode([column], column.ByteLength, [value], System.Text.Encoding.Latin1);
    }

    [Fact]
    public void Insert_tuple_decimal_below_one_is_normalized()
    {
        // 0.5 = 0.50 x 100^0: exponent byte (0+64)|0x80, one digit 50 -- what the server itself
        // stores and returns. The un-normalized 0xC1 00 50 used to go into the row image as is.
        Assert.Equal(new byte[] { 0xC0, 50, 0, 0, 0 }, EncodeDecimal(0.5m));
        Assert.Equal(new byte[] { 0xC0, 5, 0, 0, 0 }, EncodeDecimal(0.05m));
        // -0.5: digits 100's-complemented, exponent byte XOR 0x7F.
        Assert.Equal(new byte[] { 0x3F, 50, 0, 0, 0 }, EncodeDecimal(-0.5m));
        // Trailing zero digits are dropped: 100 = 0.01 x 100^2.
        Assert.Equal(new byte[] { 0xC2, 1, 0, 0, 0 }, EncodeDecimal(100m));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("-0.01")]
    [InlineData("0.5")]
    [InlineData("-0.5")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("99.99")]
    [InlineData("-99.99")]
    [InlineData("100")]
    [InlineData("-100")]
    [InlineData("12345.67")]
    [InlineData("-12345.67")]
    [InlineData("99999.99")]
    [InlineData("-99999.99")]
    [InlineData("10000")]
    [InlineData("-9900.01")]
    public void Insert_tuple_decimal_round_trips(string text)
    {
        decimal value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        byte[] image = EncodeDecimal(value);
        Assert.Equal(value, PackedValue.FromWire(image, (short)(7 << 8 | 2)).ToDecimal());
    }

    [Fact]
    public void Insert_tuple_decimal_is_rounded_to_the_column_scale()
    {
        Assert.Equal(EncodeDecimal(1.01m), EncodeDecimal(1.005m));     // half away from zero
        Assert.Equal(EncodeDecimal(-1.01m), EncodeDecimal(-1.005m));
        Assert.Equal(EncodeDecimal(0.12m), EncodeDecimal(0.123m));     // used to be stored as 0.123
    }

    [Fact]
    public void Insert_tuple_decimal_wider_than_the_column_is_refused()
    {
        var ex = Assert.Throws<OverflowException>(() => EncodeDecimal(100000m));
        Assert.Contains("DECIMAL(7,2)", ex.Message);
        Assert.Throws<OverflowException>(() => EncodeDecimal(99999.995m)); // rounds up out of range
    }

    [Fact]
    public void Insert_tuple_accepts_IfxDecimal_like_the_bind_engine()
    {
        Assert.Equal(EncodeDecimal(12.34m), EncodeDecimal(new IfxDecimal(12.34m)));
        Assert.Equal(new byte[5], EncodeDecimal(IfxDecimal.Null));     // SQL NULL image
    }
}
