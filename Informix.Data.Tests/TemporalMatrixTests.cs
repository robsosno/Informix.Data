using Informix.Data.Sqli;
using Informix.Data.Types;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Offline deterministic temporal matrix
/// (<c>test/test_temporal_matrix.c</c>): every DATETIME / INTERVAL qualifier is
/// generated with a xorshift64 PRNG (seed 0xc0ffee, 2,036 cases) and encoded by
/// an independent "field -> base-100 pairs -> normalize" wire model
/// (<c>make_wire</c>). 
/// </summary>
public class TemporalMatrixTests
{
    private const int Year = 0, Month = 1, Day = 2, Hour = 3, Minute = 4, Second = 5, Fraction = 6;
    private static readonly string[] Names = { "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND", "FRACTION" };

    private sealed class Case
    {
        public string Type = "";
        public int[] Fields = new int[7];
        public int Start, End, Scale, Precision;
        public bool Interval, Negative, IsNull;
    }

    // ---------------------------------------------------------------- generator

    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public uint Bounded(uint limit)
        {
            ulong x = _state;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            _state = x;
            return (uint)(x % limit);
        }
    }

    private static int Pow10(int n)
    {
        int v = 1;
        while (n-- > 0)
            v *= 10;
        return v;
    }

    private static List<Case> Generate(ulong seed)
    {
        var rng = new Rng(seed);
        var list = new List<Case>();
        for (int start = Year; start <= Fraction; start++)
            for (int end = start; end <= Fraction; end++)
                for (int scale = end == Fraction ? 1 : 0; scale <= (end == Fraction ? 5 : 0); scale++)
                    for (int v = 0; v < 4; v++)
                        Build(list, rng, false, start, end, scale, 0, v);
        for (int start = Year; start <= Fraction; start++)
        {
            int last = start <= Month ? Month : Fraction;
            for (int end = start; end <= last; end++)
                for (int scale = end == Fraction ? 1 : 0; scale <= (end == Fraction ? 5 : 0); scale++)
                    for (int precision = start == Fraction ? 0 : 1; precision <= (start == Fraction ? 0 : 9); precision++)
                        for (int v = 0; v < 6; v++)
                            Build(list, rng, true, start, end, scale, precision, v);
        }
        return list;
    }

    private static void Build(List<Case> list, Rng rng, bool interval, int start, int end, int scale, int precision, int variant)
    {
        var c = new Case { Interval = interval, Start = start, End = end, Scale = scale, Precision = precision };
        list.Add(c);
        c.IsNull = variant == (interval ? 5 : 3);
        c.Negative = interval && (variant == 2 || variant == 4);
        bool maximum = variant == 1 || (interval && variant == 2);
        bool random = interval ? (variant == 3 || variant == 4) : variant == 2;
        string first = interval && start != Fraction ? $"{Names[start]}({precision})" : Names[start];
        string last = end == Fraction ? $"FRACTION({scale})" : Names[end];
        c.Type = $"{(interval ? "INTERVAL" : "DATETIME")} {first} TO {last} #{variant}";
        bool nonzero = false;
        int[] dtMin = { 1, 1, 1, 0, 0, 0, 0 }, dtMax = { 9999, 12, 31, 23, 59, 59, 0 }, ivMax = { 0, 11, 0, 23, 59, 59, 0 };
        for (int f = start; f <= end; f++)
        {
            int low = interval ? 0 : dtMin[f];
            int high = interval ? ivMax[f] : dtMax[f];
            if (f == Fraction)
                high = Pow10(scale) - 1;
            else if (interval && f == start)
                high = Pow10(precision) - 1;
            if (!interval && f == Day && random)
                high = 28;
            int value = maximum ? high : low;
            if (random)
                value = low + (int)rng.Bounded((uint)(high - low + 1));
            c.Fields[f] = value;
            nonzero |= value != 0;
        }
        if (!nonzero && c.Negative)
            c.Negative = false;
    }

    // ---------------------------------------------------------------- wire model

    // Port of make_wire: independent of the driver's encoder on purpose.
    private static (byte[] Bytes, short Qual) MakeWire(Case c)
    {
        var pairs = new byte[20];
        int count = 0;
        int leading = c.Interval ? c.Precision : c.Start == Year ? 4 : 2;
        int start = c.Start == Fraction ? 12 : c.Start * 2;
        int end = c.End == Fraction ? 10 + c.Scale : c.End * 2;
        int digits = c.Start == Fraction ? c.Scale : leading + end - start;
        int padding = c.Interval && c.Start != Fraction ? leading & 1 : 0;
        int length = 1 + (digits + padding + 1) / 2;
        short qual = (short)((digits << 8) | (start << 4) | end);
        var bytes = new byte[length];
        if (c.IsNull)
            return (bytes, qual);

        int exponent = 0;
        for (int field = c.Start; field <= c.End && field <= Second; field++)
        {
            int width = field == c.Start ? (leading + 1) / 2 : 1;
            uint value = (uint)c.Fields[field];
            for (int i = width; i > 0; i--)
            {
                pairs[count + i - 1] = (byte)(value % 100);
                value /= 100;
            }
            Assert.Equal(0u, value); // generator never overflows its own field width
            count += width;
        }
        if (c.Start != Fraction)
            exponent = count + Second - (c.End > Second ? Second : c.End);
        if (c.End == Fraction)
        {
            int width = (c.Scale + 1) / 2;
            uint value = (uint)c.Fields[Fraction];
            if ((c.Scale & 1) != 0)
                value *= 10;
            for (int i = width; i > 0; i--)
            {
                pairs[count + i - 1] = (byte)(value % 100);
                value /= 100;
            }
            count += width;
        }
        int first = 0;
        while (first < count && pairs[first] == 0)
        {
            first++;
            exponent--;
        }
        while (count > first && pairs[count - 1] == 0)
            count--;
        if (first == count)
        {
            bytes[0] = 128;
        }
        else
        {
            uint encoded = (uint)(exponent + 64);
            bytes[0] = (byte)(encoded | 128);
            Array.Copy(pairs, first, bytes, 1, count - first);
            if (c.Negative)
            {
                bytes[0] = (byte)(encoded ^ 0x7f);
                uint carry = 1;
                for (int i = length; i > 1; i--)
                {
                    uint digit = 99u - bytes[i - 1] + carry;
                    bytes[i - 1] = (byte)(digit % 100);
                    carry = digit / 100;
                }
            }
        }
        return (bytes, qual);
    }

    // ---------------------------------------------------------------- tests

    private static readonly List<Case> Cases = Generate(0xc0ffee);

    private static int[] ExpectedFields(Case c)
    {
        var exp = new int[7];
        for (int i = c.Start; i <= c.End && i <= Second; i++)
            exp[i] = c.Fields[i];
        if (c.End == Fraction)
            exp[Fraction] = c.Fields[Fraction] * Pow10(9 - c.Scale);
        return exp;
    }

    [Fact]
    public void Generator_ProducesLibsqliCaseCount()
    {
        Assert.Equal(2036, Cases.Count);
    }

    [Fact]
    public void Decode_MatchesLiveVerifiedWireModel()
    {
        var failures = new List<string>();
        foreach (var c in Cases)
        {
            var (bytes, qual) = MakeWire(c);
            var v = PackedValue.FromWire(bytes, qual, !c.Interval);
            if (c.IsNull)
            {
                if (!v.IsNull)
                    failures.Add("NULL not null: " + c.Type);
                continue;
            }

            var got = new int[7];
            v.FieldsFromPacked(got);
            var exp = ExpectedFields(c);
            if (!got.SequenceEqual(exp))
            {
                failures.Add($"{c.Type} wire={Convert.ToHexString(bytes)} q={qual:x4} got [{string.Join(",", got)}] expected [{string.Join(",", exp)}]");
                continue;
            }

            if (c.Interval)
            {
                var iv = v.ToInterval()!;
                bool negative = iv is IfxIntervalYearMonth ym
                    ? ym.Months < 0
                    : ((IfxIntervalDayFraction)iv).Seconds < 0 || ((IfxIntervalDayFraction)iv).NanoSeconds < 0;
                if (negative != c.Negative)
                    failures.Add($"{c.Type} sign got {negative} expected {c.Negative}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} decode mismatches:\n" + string.Join("\n", failures.Take(15)));
    }

    [Fact]
    public void IntervalEncode_RoundTripsThroughDecoder()
    {
        var failures = new List<string>();
        foreach (var c in Cases.Where(x => x.Interval && !x.IsNull))
        {
            var (_, qual) = MakeWire(c);
            PackedValue enc;
            if (c.Start <= Month)
            {
                long months = c.Start == Month
                    ? c.Fields[Month]
                    : c.Fields[Year] * 12L + (c.End == Month ? c.Fields[Month] : 0);
                if (c.Negative)
                    months = -months;
                enc = PackedValue.FromInterval(new IfxIntervalYearMonth(months, qual));
            }
            else
            {
                long secs = 0;
                for (int i = c.Start; i <= c.End && i <= Second; i++)
                    secs += i switch
                    {
                        Day => c.Fields[i] * 86400L,
                        Hour => c.Fields[i] * 3600L,
                        Minute => c.Fields[i] * 60L,
                        Second => c.Fields[i],
                        _ => 0L,
                    };
                long nanos = c.End == Fraction ? (long)c.Fields[Fraction] * Pow10(9 - c.Scale) : 0;
                if (c.Negative)
                {
                    secs = -secs;
                    nanos = -nanos;
                }
                enc = PackedValue.FromInterval(new IfxIntervalDayFraction(secs, nanos, qual));
            }

            var back = PackedValue.FromWire(enc.ToWireBytes().AsSpan(2), qual);
            var got = new int[7];
            back.FieldsFromPacked(got);
            var exp = ExpectedFields(c);
            if (!got.SequenceEqual(exp))
                failures.Add($"{c.Type} got [{string.Join(",", got)}] expected [{string.Join(",", exp)}]");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} encode round-trip mismatches:\n" + string.Join("\n", failures.Take(15)));
    }

    [Theory]
    [InlineData(999_999_999, false)]
    [InlineData(999_999_999, true)]
    [InlineData(178_956_971, false)] // first YEAR whose month count exceeds int.MaxValue
    public void IntervalYear9_BeyondInt32Months_KeepsMagnitudeAndSign(int years, bool negative)
    {
        short qual = IfxQualifier.ForInterval(9, IfxTimeUnit.Year, IfxTimeUnit.Year);
        long months = years * 12L * (negative ? -1 : 1);
        var back = PackedValue
            .FromWire(PackedValue.FromInterval(new IfxIntervalYearMonth(months, qual)).ToWireBytes().AsSpan(2), qual)
            .ToInterval();
        var ym = Assert.IsType<IfxIntervalYearMonth>(back);
        Assert.Equal(months, ym.Months);
    }
}
