using System.Globalization;
using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// The <c>GetXxx</c> conversion policy in <see cref="IfxConvert"/>: a value reads as its own
/// type or a wider one, integers narrow by value, unrelated kinds throw
/// <see cref="InvalidCastException"/>, <c>GetString</c> only formats integers, and
/// <c>GetChar</c> needs exactly one character. Pure functions over a boxed value and the
/// column metadata, so no server is involved.
/// </summary>
public class ConversionPolicyTests
{
    private static IfxColumnMetadata Col(SqliTypeCode type = SqliTypeCode.Int) => new() { Name = "c", SqliTypeCode = type };

    // ---- allowed: same type, wider type, integer narrowing by value ----

    [Fact]
    public void Integers_widen_without_an_exception()
    {
        Assert.Equal(7, IfxConvert.ToInt32((short)7, Col()));
        Assert.Equal(7L, IfxConvert.ToInt64((short)7, Col()));
        Assert.Equal(7L, IfxConvert.ToInt64(7, Col()));
        Assert.Equal(7m, IfxConvert.ToDecimal((short)7, Col()));
        Assert.Equal(7m, IfxConvert.ToDecimal(7, Col()));
        Assert.Equal(7m, IfxConvert.ToDecimal(7L, Col()));
        Assert.Equal(7d, IfxConvert.ToDouble(7, Col()));
        Assert.Equal(7f, IfxConvert.ToSingle(7, Col()));
    }

    [Fact]
    public void Floating_point_and_decimal_widen_towards_double()
    {
        Assert.Equal(1.5d, IfxConvert.ToDouble(1.5f, Col(SqliTypeCode.SmFloat)));
        Assert.Equal(1.5d, IfxConvert.ToDouble(1.5m, Col(SqliTypeCode.Decimal)));
        Assert.Equal(1.5f, IfxConvert.ToSingle(1.5m, Col(SqliTypeCode.Decimal)));
    }

    [Fact]
    public void Integers_narrow_by_value()
    {
        Assert.Equal((short)7, IfxConvert.ToInt16(7, Col()));
        Assert.Equal((short)7, IfxConvert.ToInt16(7L, Col(SqliTypeCode.BigInt)));
        Assert.Equal(7, IfxConvert.ToInt32(7L, Col(SqliTypeCode.BigInt)));
        Assert.Throws<OverflowException>(() => IfxConvert.ToInt16(40000, Col()));
        Assert.Throws<OverflowException>(() => IfxConvert.ToInt16(-40000L, Col(SqliTypeCode.BigInt)));
        Assert.Throws<OverflowException>(() => IfxConvert.ToInt32(long.MaxValue, Col(SqliTypeCode.BigInt)));
    }

    [Fact]
    public void GetDateTime_bridges_DATE_and_time_only_DATETIME()
    {
        Assert.Equal(new DateTime(2011, 11, 11), IfxConvert.ToDateTime(new DateOnly(2011, 11, 11), Col(SqliTypeCode.Date)));
        var fromTime = IfxConvert.ToDateTime(new TimeOnly(11, 12, 13), Col(SqliTypeCode.DateTime));
        Assert.Equal(new TimeOnly(11, 12, 13), TimeOnly.FromDateTime(fromTime));
    }

    // ---- rejected: unrelated kinds ----

    public static TheoryData<string, object> Rejected => new()
    {
        { nameof(IfxConvert.ToBoolean), (short)1 },
        { nameof(IfxConvert.ToBoolean), 1 },
        { nameof(IfxConvert.ToBoolean), 1L },
        { nameof(IfxConvert.ToBoolean), 1m },
        { nameof(IfxConvert.ToBoolean), 1d },
        { nameof(IfxConvert.ToBoolean), "t" },
        { nameof(IfxConvert.ToByte), 1 },
        { nameof(IfxConvert.ToByte), (short)1 },
        { nameof(IfxConvert.ToByte), 1m },
        { nameof(IfxConvert.ToByte), true },
        { nameof(IfxConvert.ToInt16), true },
        { nameof(IfxConvert.ToInt16), "1" },
        { nameof(IfxConvert.ToInt32), true },
        { nameof(IfxConvert.ToInt32), "1" },
        { nameof(IfxConvert.ToInt64), true },
        { nameof(IfxConvert.ToInt64), "1" },
        { nameof(IfxConvert.ToSingle), true },
        { nameof(IfxConvert.ToSingle), "1" },
        { nameof(IfxConvert.ToSingle), 1d },
        { nameof(IfxConvert.ToDecimal), true },
        { nameof(IfxConvert.ToDecimal), "1" },
        { nameof(IfxConvert.ToDecimal), 1d },
        { nameof(IfxConvert.ToDecimal), 1f },
        { nameof(IfxConvert.ToDouble), true },
        { nameof(IfxConvert.ToDouble), "1" },
        { nameof(IfxConvert.ToChar), 1 },
        { nameof(IfxConvert.ToChar), true },
        { nameof(IfxConvert.ToText), true },
        { nameof(IfxConvert.ToText), 1m },
        { nameof(IfxConvert.ToText), 1d },
        { nameof(IfxConvert.ToText), 1f },
        { nameof(IfxConvert.ToText), new DateTime(2011, 11, 11) },
        { nameof(IfxConvert.ToText), new DateOnly(2011, 11, 11) },
        { nameof(IfxConvert.ToText), new TimeOnly(1, 2, 3) },
        { nameof(IfxConvert.ToText), new byte[] { 1 } },
        { nameof(IfxConvert.ToDateTime), true },
        { nameof(IfxConvert.ToDateTime), 1 },
        { nameof(IfxConvert.ToDateTime), 1m },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Unrelated_kinds_throw_InvalidCastException(string getter, object value) =>
        Assert.Throws<InvalidCastException>(() => Invoke(getter, value, Col()));

    public static TheoryData<string> Getters => new()
    {
        nameof(IfxConvert.ToBoolean), nameof(IfxConvert.ToByte), nameof(IfxConvert.ToChar), nameof(IfxConvert.ToInt16),
        nameof(IfxConvert.ToInt32), nameof(IfxConvert.ToInt64), nameof(IfxConvert.ToSingle), nameof(IfxConvert.ToDecimal),
        nameof(IfxConvert.ToDouble), nameof(IfxConvert.ToText), nameof(IfxConvert.ToDateTime),
    };

    [Theory]
    [MemberData(nameof(Getters))]
    public void SQL_NULL_throws_InvalidCastException(string getter) =>
        Assert.Throws<InvalidCastException>(() => Invoke(getter, DBNull.Value, Col()));

    private static object Invoke(string getter, object value, IfxColumnMetadata column) => getter switch
    {
        nameof(IfxConvert.ToBoolean) => IfxConvert.ToBoolean(value, column),
        nameof(IfxConvert.ToByte) => IfxConvert.ToByte(value, column),
        nameof(IfxConvert.ToChar) => IfxConvert.ToChar(value, column),
        nameof(IfxConvert.ToInt16) => IfxConvert.ToInt16(value, column),
        nameof(IfxConvert.ToInt32) => IfxConvert.ToInt32(value, column),
        nameof(IfxConvert.ToInt64) => IfxConvert.ToInt64(value, column),
        nameof(IfxConvert.ToSingle) => IfxConvert.ToSingle(value, column),
        nameof(IfxConvert.ToDecimal) => IfxConvert.ToDecimal(value, column),
        nameof(IfxConvert.ToDouble) => IfxConvert.ToDouble(value, column),
        nameof(IfxConvert.ToText) => IfxConvert.ToText(value, column),
        nameof(IfxConvert.ToDateTime) => IfxConvert.ToDateTime(value, column),
        _ => throw new ArgumentOutOfRangeException(nameof(getter)),
    };

    // ---- GetString: text only where it cannot depend on the culture ----

    [Theory]
    [InlineData("sv-SE")] // U+2212 as the minus sign
    [InlineData("pl-PL")] // decimal comma
    [InlineData("ar-SA")]
    public void GetString_formats_integers_with_the_invariant_culture(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("-32767", IfxConvert.ToText((short)-32767, Col(SqliTypeCode.SmallInt)));
            Assert.Equal("-2147483647", IfxConvert.ToText(-2147483647, Col()));
            Assert.Equal("-9223372036854775807", IfxConvert.ToText(-9223372036854775807L, Col(SqliTypeCode.BigInt)));
            Assert.Equal("1234567", IfxConvert.ToText(1234567, Col()));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void GetString_returns_a_string_value_as_is() =>
        Assert.Equal(" x ", IfxConvert.ToText(" x ", Col(SqliTypeCode.VarChar)));

    // ---- GetChar ----

    [Theory]
    [InlineData((short)SqliTypeCode.Char, "A", 'A')]
    [InlineData((short)SqliTypeCode.Char, "A    ", 'A')]
    [InlineData((short)SqliTypeCode.Char, "     ", ' ')]
    [InlineData((short)SqliTypeCode.Char, "", ' ')]
    [InlineData((short)SqliTypeCode.NChar, "A  ", 'A')]
    [InlineData((short)SqliTypeCode.NChar, "   ", ' ')]
    [InlineData((short)SqliTypeCode.VarChar, "A", 'A')]
    [InlineData((short)SqliTypeCode.VarChar, " ", ' ')]
    [InlineData((short)SqliTypeCode.NVarChar, "7", '7')]
    [InlineData((short)SqliTypeCode.LVarChar, "z", 'z')]
    public void GetChar_reads_one_character(short type, string value, char expected) =>
        Assert.Equal(expected, IfxConvert.ToChar(value, Col((SqliTypeCode)type)));

    [Theory]
    [InlineData((short)SqliTypeCode.Char, "AB   ")]
    [InlineData((short)SqliTypeCode.Char, " A")]       // leading blanks are data
    [InlineData((short)SqliTypeCode.NChar, "AB")]
    [InlineData((short)SqliTypeCode.VarChar, "")]      // an empty VARCHAR is not a character
    [InlineData((short)SqliTypeCode.VarChar, "A ")]    // VARCHAR is never trimmed
    [InlineData((short)SqliTypeCode.VarChar, " A")]
    [InlineData((short)SqliTypeCode.VarChar, "AB")]
    [InlineData((short)SqliTypeCode.LVarChar, "")]
    public void GetChar_throws_unless_exactly_one_character_remains(short type, string value) =>
        Assert.Throws<InvalidCastException>(() => IfxConvert.ToChar(value, Col((SqliTypeCode)type)));

    // ---- GetFieldValue<T> shares the rules ----

    [Fact]
    public void GetFieldValue_primitives_follow_the_getter_rules()
    {
        Assert.True(IfxConvert.TryConvertPrimitive(typeof(long), (short)5, Col(SqliTypeCode.SmallInt), out var widened));
        Assert.Equal(5L, widened);
        Assert.Throws<InvalidCastException>(() => IfxConvert.TryConvertPrimitive(typeof(int), "5", Col(SqliTypeCode.VarChar), out _));
        Assert.Throws<InvalidCastException>(() => IfxConvert.TryConvertPrimitive(typeof(bool), (short)1, Col(SqliTypeCode.SmallInt), out _));
        Assert.True(IfxConvert.TryConvertPrimitive(typeof(char), "x", Col(SqliTypeCode.VarChar), out var ch));
        Assert.Equal('x', ch);
    }

    [Fact]
    public void GetFieldValue_string_is_the_exact_type_read()
    {
        // GetString formats an integer; the generic exact-type form does not.
        Assert.Equal("5", IfxConvert.ToText(5, Col()));
        Assert.Throws<InvalidCastException>(() => IfxConvert.TryConvertPrimitive(typeof(string), 5, Col(), out _));
    }

    [Fact]
    public void TryConvertPrimitive_leaves_other_targets_to_the_caller()
    {
        Assert.False(IfxConvert.TryConvertPrimitive(typeof(DateTime), new DateOnly(2011, 1, 1), Col(SqliTypeCode.Date), out _));
        Assert.False(IfxConvert.TryConvertPrimitive(typeof(Guid), "x", Col(SqliTypeCode.VarChar), out _));
        Assert.False(IfxConvert.TryConvertPrimitive(typeof(object), "x", Col(SqliTypeCode.VarChar), out _));
    }
}
