using System.Globalization;

namespace Informix.Data.Types;

/// <summary>
/// The Informix packed-decimal engine (formerly <c>PackedDecimal</c>). One
/// representation backs four server types: DECIMAL, MONEY, DATETIME and INTERVAL.
///
/// <para><b>Representation.</b> A value is a sign (<c>dec_pos</c>: 1 positive,
/// 0 negative, -1 NULL), an exponent counted in base-100 ("centesimal") digit
/// positions (<c>dec_exp</c>), and up to 16 centesimal digits, each 0..99
/// (<c>dec_dgts</c>). Value = 0.d1d2... × 100^exp.</para>
///
/// <para><b>Wire format</b> (see <see cref="ToWireBytes"/> / <see cref="FromWire"/>):
/// a 2-byte big-endian length, one exponent byte, then the digits. Positive
/// values carry <c>(exp+64)|0x80</c>; negative values store the digits in
/// 100's-complement with the exponent byte XOR-ed with 0x7F; NULL is a zero
/// exponent byte with zero digits.</para>
///
/// <para>DATETIME and INTERVAL are the same packed number whose digits are the
/// calendar fields selected by a qualifier (see <see cref="IfxQualifier"/>),
/// aligned so that the base-100 point sits after the SECOND field.</para>
///
/// <para>Values wider than <see cref="decimal"/>'s 28-29 digits still round-trip on
/// the wire and via strings. <see cref="ToDecimal"/> follows <c>decimal.Parse</c>
/// semantics: excess <em>fractional</em> precision rounds silently, but a
/// <em>magnitude</em> beyond the decimal range (about ±7.92e28) throws
/// <see cref="OverflowException"/> — decode sites wrap it via
/// <see cref="DecimalRangeError"/> so the error names the column being read.
/// <see cref="ToDouble"/> has no such limit and backs
/// <see cref="Informix.Data.IfxDataReader.GetDouble"/>, the escape hatch for reading
/// those values (approximately) without a SQL cast.</para>
/// </summary>
internal sealed class PackedValue
{
    /// <summary>Maximum number of centesimal digits a server DECIMAL carries (<c>DECSIZE</c>).</summary>
    public const int DecSize = 16;

    private const short PosNull = -1;      // DECPOSNULL
    private const short PosPositive = 1;   // DECPOSPOSITIVE
    private const short PosNegative = 0;   // DECPOSNEGATIVE

    // Base-100 exponent; _dgts are the digit pairs, _ndgts how many are significant; _pos is the sign or NULL.
    private short _exp;                    // dec_exp
    private short _pos = PosNull;          // dec_pos
    private short _ndgts;                  // dec_ndgts
    private byte[] _dgts = [];             // dec_dgts
    private short _qualifier;              // precStored
    private bool _isDateTime;              // timestamp

    private PackedValue() { }

    /// <summary>True for the SQL NULL value.</summary>
    public bool IsNull => _pos == PosNull;

    /// <summary>The DATETIME/INTERVAL qualifier this value was decoded with, when applicable.</summary>
    public short Qualifier => _qualifier;

    // ===================================================================== //
    //  Decoding — wire bytes to value                                       //
    // ===================================================================== //

    /// <summary>
    /// Decodes the wire payload (exponent byte + digits, i.e. the bytes after
    /// the 2-byte length prefix). <paramref name="qualifier"/> carries the
    /// DECIMAL precision/scale word or the DATETIME/INTERVAL qualifier.
    /// </summary>
    public static PackedValue FromWire(ReadOnlySpan<byte> payload, short qualifier, bool isDateTime = false)
    {
        var d = new PackedValue { _isDateTime = isDateTime, _qualifier = qualifier };

        // A zero exponent byte with a zero first digit is the NULL marker.
        if (payload.Length == 0 || (payload[0] == 0 && (payload.Length < 2 || payload[1] == 0)))
            return d;

        d._dgts = payload[1..].ToArray();
        int expon = payload[0];
        d._pos = PosPositive;
        if ((expon & 0x80) == 0)
        {
            // Negative numbers arrive complemented; restore the magnitude and mark the sign.
            DecComplement(d._dgts);
            expon ^= 0x7F;
            d._pos = PosNegative;
        }
        d._exp = (short)((expon & 0x7F) - 64);

        bool allZero = true;
        foreach (byte b in d._dgts)
        {
            if (b != 0) { allZero = false; break; }
        }
        d._ndgts = allZero ? (short)0 : (short)d._dgts.Length;
        return d;
    }

    // ===================================================================== //
    //  Encoding — value to wire bytes                                       //
    // ===================================================================== //

    /// <summary>
    /// Encodes to the full bind format: 2-byte big-endian length, exponent
    /// byte, digits (100's-complemented for negatives).
    /// </summary>
    public byte[] ToWireBytes()
    {
        var cp = new byte[_ndgts + 3];
        int len = _ndgts + 1;
        cp[0] = (byte)(len >> 8);
        cp[1] = (byte)len;
        if (_pos == PosNull)
            return cp;

        cp[2] = (byte)((_exp + 64) & 0x7F);
        Array.Copy(_dgts, 0, cp, 3, Math.Min(_dgts.Length, _ndgts));
        if (_pos == PosNegative)
        {
            if (_ndgts == 0)
            {
                cp[2] |= 0x80; // negative zero encodes as positive
            }
            else
            {
                DecComplement(cp.AsSpan(3));
                cp[2] ^= 0x7F;
            }
        }
        else
        {
            cp[2] |= 0x80;
        }
        return cp;
    }

    /// <summary>
    /// In-place 100's complement, skipping trailing zero digits (port of
    /// <c>decComplement</c>): the last non-zero digit is subtracted from 100,
    /// the ones before it from 99.
    /// </summary>
    internal static void DecComplement(Span<byte> b)
    {
        int digit = 100;
        for (int bp = b.Length - 1; bp >= 0; bp--)
        {
            if (b[bp] != 0 || digit != 100)
            {
                b[bp] = (byte)(digit - b[bp]);
                digit = 99;
            }
        }
    }

    // ===================================================================== //
    //  Numeric factories                                                    //
    // ===================================================================== //

    /// <summary>
    /// Parses a plain decimal string ("-123.45") with round-to-16-digits behaviour.
    /// Scientific notation is only honoured when a decimal point is present.
    /// </summary>
    public static PackedValue Parse(string num)
    {
        ArgumentException.ThrowIfNullOrEmpty(num);
        var d = new PackedValue();
        d.InitFromString(num);
        return d;
    }

    /// <summary>Converts a CLR decimal.</summary>
    public static PackedValue FromDecimal(decimal value) =>
        Parse(value.ToString(CultureInfo.InvariantCulture));

    // Builds base-100 digits from decimal text. The integer-digit count (plus any E exponent) decides the base-100
    // exponent; an odd count shifts the first digit into the low half of the first pair. Values beyond 16 digit bytes are rounded.
    private void InitFromString(string num)
    {
        int first = 0;
        int exp = 0;
        int buflen = num.Length;
        int upnxt = 0;
        _pos = PosPositive;

        char nxt = num[0];
        if (nxt == '-')
        {
            _pos = PosNegative;
            first++;
        }
        else if (nxt == '+')
        {
            first++;
        }
        if (first > 0)
            buflen--;

        // Count integer digits (everything before the '.').
        int i;
        for (i = first; i < num.Length && num[i] != '.'; i++)
            exp++;
        if (i != num.Length)
        {
            buflen--;
            exp += ExtractExponent(num, i);
        }

        // Odd digit counts are left-padded so digits pack in pairs.
        if (exp % 2 != 0)
        {
            buflen++;
            _exp = (short)((exp + 1) / 2);
            upnxt = 1;
        }
        else
        {
            _exp = (short)(exp / 2);
        }

        if (buflen % 2 == 1)
            buflen++;
        var unpack = new byte[buflen];

        for (i = 0; i < num.Length && num[i] != 'E'; i++)
        {
            if (num[i] is >= '0' and <= '9')
                unpack[upnxt++] = (byte)(num[i] - '0');
        }

        _ndgts = (short)(buflen / 2);
        _dgts = new byte[_ndgts];
        for (int j = 0; j < _dgts.Length; j++)
            _dgts[j] = (byte)(unpack[2 * j] * 10 + unpack[2 * j + 1]);

        // Round half-up into the 16-digit server capacity.
        if (_ndgts > DecSize && _dgts[16] >= 50 && _dgts[15] < 99)
        {
            _dgts[15]++;
            Array.Resize(ref _dgts, DecSize);
            _ndgts = DecSize;
        }
    }

    // Port of extractExponent: value of the trailing E-notation exponent.
    private static int ExtractExponent(string num, int start)
    {
        int i;
        for (i = start; i < num.Length && num[i] != 'E'; i++) { }
        if (i == num.Length)
            return 0;
        i++;

        bool positive = true;
        if (num[i] == '+')
        {
            i++;
        }
        else if (num[i] == '-')
        {
            i++;
            positive = false;
        }

        int exp = 0;
        for (int j = i; j < num.Length; j++)
            exp = exp * 10 + (num[j] - '0');
        return positive ? exp : -exp;
    }

    // ===================================================================== //
    //  Numeric conversions                                                  //
    // ===================================================================== //

    /// <summary>
    /// The exact string the driver feeds to decimal: index 0 is a sign slot
    /// ('0' or '-') and a trailing '.' may remain, e.g. "0123." for 123.
    /// Kept verbatim because the digit math is the specification;
    /// <see cref="ToString"/> normalizes.
    /// </summary>
    public string ToInformixString()
    {
        if (_pos == PosNull)
            return "";
        int exp = _exp * 2;

        if (_ndgts <= 0 && _exp == 0)
            return "0.0";

        char[] sigDig = SigDigitsToChars();
        char[] buf;
        if (exp > 0)
        {
            if (exp <= sigDig.Length)
            {
                buf = new char[sigDig.Length + 2];
                CharCopy(sigDig, 0, exp - 1, buf, 1);
                buf[exp + 1] = '.';
                if (exp != sigDig.Length)
                    CharCopy(sigDig, exp, sigDig.Length - 1, buf, exp + 2);
            }
            else
            {
                buf = new char[exp + 2];
                CharCopy(sigDig, 0, sigDig.Length - 1, buf, 1);
                CharFill(buf, sigDig.Length + 1, exp, '0');
                buf[exp + 1] = '.';
            }
        }
        else
        {
            exp = -exp;
            buf = new char[exp + sigDig.Length + 3];
            buf[1] = '0';
            buf[2] = '.';
            CharFill(buf, 3, exp + 2, '0');
            CharCopy(sigDig, 0, sigDig.Length, buf, exp + 3);
        }

        char[] tbuf = TrimZeros(buf, 1);
        tbuf[0] = _pos == PosPositive ? '0' : '-';
        return new string(tbuf);
    }

    /// <summary>Human-readable value; the wire string with the sign slot and trailing '.' cleaned up.</summary>
    public override string ToString()
    {
        if (IsNull)
            return "";
        string s = ToInformixString();
        if (s.StartsWith('0') && s.Length > 1 && s[1] != '.')
            s = s[1..];
        if (s.EndsWith('.'))
            s = s[..^1];
        return s.Length == 0 || s == "-" ? "0" : s;
    }

    /// <summary>
    /// The value as a CLR decimal.
    /// Throws <see cref="InvalidOperationException"/> for NULL and
    /// <see cref="OverflowException"/> for values outside the decimal range.
    /// </summary>
    public decimal ToDecimal()
    {
        if (IsNull)
            throw new InvalidOperationException("The Informix DECIMAL value is NULL.");
        return decimal.Parse(ToInformixString(), NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the user-facing <see cref="OverflowException"/> for a value whose
    /// magnitude exceeds the <see cref="decimal"/> range: names what was being read
    /// (<paramref name="subject"/> — column or ROW member), quotes the value and
    /// points at the way to read it anyway. Called from decode sites' catch handlers
    /// only, so the ordinary decode path pays nothing for the richer message.
    /// </summary>
    /// <param name="hasDoubleAccessor">
    /// Whether the caller sits behind a reader whose <c>GetDouble</c> can convert this value
    /// from the packed digits (true for a result-set column). False for a ROW/collection
    /// member, which has no per-member accessor — there only the SQL cast helps.
    /// </param>
    public OverflowException DecimalRangeError(string subject, OverflowException inner, bool hasDoubleAccessor = true) => new(
        $"{subject}: the Informix DECIMAL value {ToString()} has a magnitude outside the range of " +
        "System.Decimal (about ±7.92e28) and cannot be returned as decimal. " +
        (hasDoubleAccessor
            ? "Read it as a double instead (GetDouble / GetFieldValue<double>, ~15-17 significant digits), or cast "
            : "Cast ") +
        "the column in the query, e.g. \"col::float\" (approximate) or \"col::lvarchar\" (exact digits).",
        inner);

    /// <summary>The value as a double.</summary>
    public double ToDouble()
    {
        if (IsNull)
            throw new InvalidOperationException("The Informix DECIMAL value is NULL.");
        return double.Parse(ToInformixString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // Port of sigdigToChar(): the digits as decimal characters, two per centesimal digit.
    private char[] SigDigitsToChars()
    {
        var digits = new char[_ndgts * 2];
        for (int i = 0; i < _ndgts; i++)
        {
            digits[i * 2] = (char)('0' + _dgts[i] / 10);
            digits[i * 2 + 1] = (char)('0' + _dgts[i] % 10);
        }
        return digits;
    }

    // Port of charcopy: copies from[fromStart..fromEnd] (inclusive), bounded by both arrays.
    private static void CharCopy(char[] from, int fromStart, int fromEnd, char[] to, int toStart)
    {
        int j = toStart;
        for (int i = fromStart; i < from.Length && i <= fromEnd && j < to.Length; i++, j++)
            to[j] = from[i];
    }

    // Port of charfill: fills b[from..to] (inclusive), bounded by the array.
    private static void CharFill(char[] b, int from, int to, char f)
    {
        for (int i = from; i <= to && i < b.Length; i++)
            b[i] = f;
    }

    // Port of trimZerosFB: strips leading and trailing '0' runs, keeping slot 0 for the sign.
    private static char[] TrimZeros(char[] ibuf, int start)
    {
        int left;
        for (left = start; left < ibuf.Length && ibuf[left] == '0'; left++) { }
        if (left == ibuf.Length)
            return ['\0', '0'];

        int right;
        for (right = ibuf.Length - 1; right >= left && ibuf[right] == '0'; right--) { }

        var obuf = new char[right - left + 2];
        int j = 1;
        for (int i = left; i <= right; i++, j++)
            obuf[j] = ibuf[i];
        return obuf;
    }

    // ===================================================================== //
    //  DATETIME                                                             //
    // ===================================================================== //

    /// <summary>
    /// Encodes a DateTime as a full DATETIME YEAR TO FRACTION(5) packed value
    /// (port of <c>convertTimestampToDecimal</c> + the year-path constructor).
    /// The sub-second part is kept to Informix's maximum FRACTION(5)
    /// resolution of 10 µs.
    /// </summary>
    public static PackedValue FromDateTime(DateTime value)
    {
        // Fraction in 10^-5 s units.
        int fffff = (int)(value.Ticks % TimeSpan.TicksPerSecond / 100);
        int yyyy = value.Year;
        var d = new PackedValue
        {
            _pos = PosPositive,
            _isDateTime = true,
            _qualifier = IfxQualifier.ForDateTime(IfxTimeUnit.Year, IfxTimeUnit.Fraction5),
            _ndgts = 10,
            _exp = 7,
            _dgts = new byte[10],
        };
        d._dgts[0] = (byte)(yyyy / 100);
        d._dgts[1] = (byte)(yyyy % 100);
        d._dgts[2] = (byte)value.Month;
        d._dgts[3] = (byte)value.Day;
        d._dgts[4] = (byte)value.Hour;
        d._dgts[5] = (byte)value.Minute;
        d._dgts[6] = (byte)value.Second;
        d._dgts[7] = (byte)(fffff / 1000);
        d._dgts[8] = (byte)(fffff % 1000 / 10);
        d._dgts[9] = (byte)(fffff % 10 * 10);
        return d;
    }

    /// <summary>
    /// Encodes a DateTime as a DATETIME with exactly the fields of <paramref name="qualifier"/>
    /// (YEAR TO MINUTE, MONTH TO DAY, HOUR TO FRACTION(3) ...), the way an <see cref="IfxDateTime"/>
    /// carries them. Each calendar field is one base-100 digit (the year is two); a FRACTION(n)
    /// end adds n decimal digits packed in pairs, the last one padded with a zero.
    /// </summary>
    public static PackedValue FromDateTime(DateTime value, short qualifier)
    {
        var start = (IfxTimeUnit)IfxQualifier.GetStartCode(qualifier);
        var end = (IfxTimeUnit)IfxQualifier.GetEndCode(qualifier);
        if (start > IfxTimeUnit.Second || end < start)
            return FromDateTime(value);

        var digits = new List<byte>(10);
        void Field(IfxTimeUnit unit, int fieldValue)
        {
            if (unit < start || unit > end) return;
            if (unit == IfxTimeUnit.Year)
            {
                digits.Add((byte)(fieldValue / 100));
                digits.Add((byte)(fieldValue % 100));
            }
            else
                digits.Add((byte)fieldValue);
        }
        Field(IfxTimeUnit.Year, value.Year);
        Field(IfxTimeUnit.Month, value.Month);
        Field(IfxTimeUnit.Day, value.Day);
        Field(IfxTimeUnit.Hour, value.Hour);
        Field(IfxTimeUnit.Minute, value.Minute);
        Field(IfxTimeUnit.Second, value.Second);
        // The base-100 point always sits after the SECOND field, whether or not the qualifier reaches it:
        // YEAR TO MINUTE has six digits and an exponent of seven, MONTH TO DAY two digits and five.
        int integerDigits = 0;
        foreach (var unit in new[] { IfxTimeUnit.Year, IfxTimeUnit.Month, IfxTimeUnit.Day, IfxTimeUnit.Hour, IfxTimeUnit.Minute, IfxTimeUnit.Second })
            if (unit >= start)
                integerDigits += unit == IfxTimeUnit.Year ? 2 : 1;

        if (end >= IfxTimeUnit.Fraction1)
        {
            int n = Math.Min((int)end - (int)IfxTimeUnit.Fraction1 + 1, 5);
            string fraction = (value.Ticks % TimeSpan.TicksPerSecond / 100).ToString("D5", System.Globalization.CultureInfo.InvariantCulture)[..n];
            if ((fraction.Length & 1) != 0) fraction += "0";
            for (int i = 0; i < fraction.Length; i += 2)
                digits.Add((byte)((fraction[i] - '0') * 10 + (fraction[i + 1] - '0')));
        }

        return new PackedValue
        {
            _pos = PosPositive,
            _isDateTime = true,
            _qualifier = qualifier,
            _ndgts = (short)digits.Count,
            _exp = (short)integerDigits,
            _dgts = digits.ToArray(),
        };
    }

    /// <summary>
    /// Encodes a time-of-day as DATETIME HOUR TO SECOND (port of
    /// <c>convertTimeToDecimal</c> + the hour-path constructor), or HOUR TO
    /// FRACTION(5) when <paramref name="value"/> carries a sub-second component
    /// (no silent truncation — Informix's finest DATETIME resolution is FRACTION(5),
    /// 10 &#181;s, matching <see cref="TimeOnly"/>'s 100 ns ticks to that precision).
    /// </summary>
    public static PackedValue FromTime(TimeOnly value)
    {
        long subSecondTicks = value.Ticks % TimeSpan.TicksPerSecond;
        if (subSecondTicks == 0)
        {
            return new PackedValue
            {
                _pos = PosPositive,
                _isDateTime = true,
                _qualifier = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Second),
                _ndgts = 3,
                _exp = 3,
                _dgts = [(byte)value.Hour, (byte)value.Minute, (byte)value.Second],
            };
        }

        // Fraction in 10^-5 s units (same scaling as FromDateTime's fffff).
        int fffff = (int)(subSecondTicks / 100);
        return new PackedValue
        {
            _pos = PosPositive,
            _isDateTime = true,
            _qualifier = IfxQualifier.ForDateTime(IfxTimeUnit.Hour, IfxTimeUnit.Fraction5),
            _ndgts = 6,
            _exp = 3,
            _dgts =
            [
                (byte)value.Hour, (byte)value.Minute, (byte)value.Second,
                (byte)(fffff / 1000), (byte)(fffff % 1000 / 10), (byte)(fffff % 10 * 10),
            ],
        };
    }

    /// <summary>
    /// Decodes a DATETIME value. Returns
    /// <c>null</c> for SQL NULL or when the value was not read as a DATETIME.
    /// Missing leading fields default the Informix way: year 1700, the current
    /// month, day 1. A value without a month whose day does not exist in the current
    /// month (DAY TO ... with day 29-31) falls back to January, so the read does not
    /// depend on the date it runs on.
    /// </summary>
    public DateTime? ToDateTime()
    {
        if (!_isDateTime || _pos == PosNull)
            return null;

        var t = new int[7];
        FieldsFromPacked(t);

        if (t[0] == 0)
            t[0] = 1700;
        if (t[1] == 0)
        {
            t[1] = DateTime.Now.Month;
            if (t[2] > DateTime.DaysInMonth(t[0], t[1]))
                t[1] = 1; // January has every day 1-31
        }
        if (t[2] == 0)
            t[2] = 1;

        var dt = new DateTime(t[0], t[1], t[2], t[3], t[4], t[5], DateTimeKind.Unspecified);
        return dt.AddTicks(t[6] / 100); // nanos -> 100ns ticks
    }

    /// <summary>
    /// Decodes a time-only DATETIME value (qualifier start HOUR or finer) straight to
    /// a <see cref="TimeOnly"/>, without <see cref="ToDateTime"/>'s 1700/current-month
    /// date-default fudge (there is no date part to default). Returns <c>null</c> for
    /// SQL NULL or when the value was not read as a DATETIME.
    /// </summary>
    public TimeOnly? ToTimeOnly()
    {
        if (!_isDateTime || _pos == PosNull)
            return null;

        var t = new int[7];
        FieldsFromPacked(t);
        var span = new TimeSpan(0, t[3], t[4], t[5]) + TimeSpan.FromTicks(t[6] / 100); // nanos -> 100ns ticks
        return TimeOnly.FromTimeSpan(span);
    }

    // ===================================================================== //
    //  INTERVAL                                                             //
    // ===================================================================== //

    /// <summary>
    /// Encodes a day-time interval.
    /// </summary>
    public static PackedValue FromInterval(IfxIntervalDayFraction interval)
    {
        var b = new byte[20];
        bool sign = false;
        int idx = 0;

        short qual = interval.Qualifier;
        byte start = IfxQualifier.GetStartCode(qual);
        byte end = IfxQualifier.GetEndCode(qual);

        long secs = interval.Seconds;
        long nans = interval.NanoSeconds;
        if (secs < 0 || (secs == 0 && nans < 0))
        {
            sign = true;
            secs = -secs;
            nans = -nans;
        }

        // The leading field holds everything above it (a DAY TO SECOND interval's days, an
        // HOUR TO MINUTE one's hours, ...), so it must be wide enough for the value. The default
        // qualifier a TimeSpan binds with is DAY(2): 100 days or more would not fit its one
        // base-100 digit and would go out as a byte above 99 -- not a digit at all.
        long leading = start switch
        {
            4 => secs / 86400,
            6 => secs / 3600,
            8 => secs / 60,
            10 => secs,
            _ => 0,
        };
        qual = WidenLeadingField(qual, leading);
        int lfprec = IfxQualifier.GetLeadingFieldWidth(qual);

        if (start == 12)
            start = 11;
        int currentField = start;
        int subField = 0;
        while (end >= currentField)
        {
            int tmpInt;
            switch (currentField)
            {
                case 4:
                    tmpInt = (int)(secs / 86400);
                    secs %= 86400;
                    subField = 2;
                    break;
                case 6:
                    tmpInt = (int)(secs / 3600);
                    secs %= 3600;
                    subField = 2;
                    break;
                case 8:
                    tmpInt = (int)(secs / 60);
                    secs %= 60;
                    subField = 2;
                    break;
                case 10:
                    tmpInt = (int)secs;
                    subField = 1;
                    break;
                default:
                    // Fractional digits: scale nanos down to the declared
                    // precision, then emit centesimal digit pairs.
                    tmpInt = 15 - end - 1 + 4;
                    for (int i = 0; i <= tmpInt; i++)
                        nans /= 10;
                    for (tmpInt = end - currentField + 1; tmpInt > 0; tmpInt -= 2)
                    {
                        int mod = 1;
                        for (int loop = tmpInt - 2; loop > 0; loop--)
                            mod *= 10;
                        if (tmpInt == 1)
                            nans *= 10;
                        b[idx] = (byte)(nans / mod);
                        nans %= mod;
                        idx++;
                    }
                    currentField += 10;
                    break;
            }

            if (currentField == start)
                idx = ExpandLeadingField(b, idx, tmpInt, lfprec);
            else if (currentField <= end)
                b[idx++] = (byte)tmpInt;

            currentField += subField;
        }

        return FromIntervalDigits(sign, b, idx, qual);
    }

    /// <summary>
    /// Encodes a year-month interval.
    /// </summary>
    public static PackedValue FromInterval(IfxIntervalYearMonth interval)
    {
        var b = new byte[20];
        bool sign = false;
        int idx = 0;

        short qual = interval.Qualifier;
        byte start = IfxQualifier.GetStartCode(qual);
        byte end = IfxQualifier.GetEndCode(qual);

        long months = interval.Months;
        if (months < 0)
        {
            sign = true;
            months = -months;
        }

        // As for the day-time form: the leading field (years, or months for MONTH TO MONTH)
        // must be wide enough for the value it carries.
        qual = WidenLeadingField(qual, start == 2 ? months : months / 12);
        int lfprec = IfxQualifier.GetLeadingFieldWidth(qual);

        if (start == 2)
        {
            idx = ExpandLeadingField(b, idx, (int)months, lfprec);
        }
        else
        {
            int years = (int)(months / 12);
            idx = ExpandLeadingField(b, idx, years, lfprec);
            if (end == 2)
                b[idx++] = (byte)(months % 12);
        }

        return FromIntervalDigits(sign, b, idx, qual);
    }

    // Returns <paramref name="qual"/> with its leading-field precision raised to the number of
    // decimal digits <paramref name="leading"/> needs, or unchanged when it already fits. The
    // start and end units stay as they are. The server then checks the value against the target
    // column's own precision, as it does for any interval.
    private static short WidenLeadingField(short qual, long leading)
    {
        int needed = 1;
        for (long v = leading; v >= 10; v /= 10)
            needed++;
        if (needed <= IfxQualifier.GetLeadingFieldWidth(qual))
            return qual;
        if (needed > 9)
            throw new OverflowException(
                $"An interval whose leading field is {leading} needs {needed} digits; Informix allows at most 9.");
        return IfxQualifier.ForInterval(
            needed, (IfxTimeUnit)IfxQualifier.GetStartCode(qual), (IfxTimeUnit)IfxQualifier.GetEndCode(qual));
    }

    // The leading field spans lfprec decimal digits; split it into centesimal
    // digits most-significant first (the common block of both interval encoders).
    private static int ExpandLeadingField(byte[] b, int idx, int value, int lfprec)
    {
        if (lfprec % 2 > 0)
        {
            lfprec--;
            int mod = Pow10(lfprec);
            b[idx++] = (byte)(value / mod);
            value %= mod;
        }
        while (lfprec > 0)
        {
            lfprec -= 2;
            int mod = Pow10(lfprec);
            b[idx++] = (byte)(value / mod);
            value %= mod;
        }
        return idx;
    }

    private static int Pow10(int n)
    {
        int mod = 1;
        for (int loop = n; loop > 0; loop--)
            mod *= 10;
        return mod;
    }

    // Exponent is derived from the qualifier (digit count and end code), not from the value.
    private static PackedValue FromIntervalDigits(bool negative, byte[] b, int blen, short qual)
    {
        var d = new PackedValue
        {
            _pos = negative ? PosNegative : PosPositive,
            _ndgts = (short)blen,
            _exp = (short)((IfxQualifier.GetLength(qual) + 10 - IfxQualifier.GetEndCode(qual) + 1) / 2),
            _qualifier = qual,
            _dgts = new byte[blen],
        };
        Array.Copy(b, d._dgts, blen);
        return d;
    }

    /// <summary>
    /// Decodes an INTERVAL value: a
    /// <see cref="IfxIntervalDayFraction"/> for DAY..FRACTION qualifiers, a
    /// <see cref="IfxIntervalYearMonth"/> for YEAR/MONTH ones, <c>null</c> for
    /// SQL NULL.
    /// </summary>
    public IfxInterval? ToInterval()
    {
        if (_pos == PosNull)
            return null;

        byte start = IfxQualifier.GetStartCode(_qualifier);
        var f = new int[7];
        FieldsFromPacked(f);

        int index = start > 10 ? 6 : start / 2;
        long total = 0;
        for (int i = start; i < 11; i += 2, index++)
        {
            total += i switch
            {
                0 => f[index] * 12L,
                2 => f[index],
                4 => f[index] * 86400L,
                6 => f[index] * 3600L,
                8 => f[index] * 60L,
                10 => f[index],
                _ => 0L,
            };
        }
        long nanos = f[6];

        if (_pos == PosNegative)
        {
            total = -total;
            nanos = -nanos;
        }

        return start >= 4
            ? new IfxIntervalDayFraction(total, nanos, _qualifier)
            : new IfxIntervalYearMonth(total, _qualifier);
    }

    // ===================================================================== //
    //  Field extraction                                                     //
    // ===================================================================== //

    /// <summary>
    /// Splits the packed digits into calendar fields per the qualifier:
    /// data[0]=year, [1]=month, [2]=day, [3]=hour, [4]=minute, [5]=second,
    /// [6]=nanoseconds. Leading-field width uses the corrected TU_FLEN (see
    /// <see cref="IfxQualifier.GetLeadingFieldWidth"/>).
    /// </summary>
    internal void FieldsFromPacked(int[] data)
    {
        short len = IfxQualifier.GetLength(_qualifier);
        byte start = IfxQualifier.GetStartCode(_qualifier);
        byte end = IfxQualifier.GetEndCode(_qualifier);
        int flen = IfxQualifier.GetLeadingFieldWidth(_qualifier);

        int dataIndex = start > 10 ? 6 : start / 2;

        // Trim trailing zero digits.
        for (int i = _ndgts - 1; i > 0 && _dgts[i] == 0; i--)
            _ndgts--;

        // Align the digits so the base-100 point sits after the SECOND field.
        int bexpon = (len + 10 - end + 1) / 2;
        var dtbuf = new byte[16];
        if (_ndgts > 0 && bexpon >= _exp)
        {
            int offset = bexpon - _exp;
            Array.Copy(_dgts, 0, dtbuf, offset, Math.Min(_ndgts, 16 - offset));
        }

        byte currentField = start;
        int dtbufIndex = 0;
        int currentValue = 0;

        // The leading field may span several centesimal digits.
        if (currentField != 12)
        {
            int j = flen / 2;
            if ((flen & 0x1) > 0)
                j++;
            for (; dtbufIndex < j; dtbufIndex++)
                currentValue = currentValue * 100 + dtbuf[dtbufIndex];
            currentField += 2;
            data[dataIndex++] = currentValue;
        }

        // Middle fields are one centesimal digit each.
        while (currentField <= end && currentField <= 10)
        {
            data[dataIndex++] = dtbuf[dtbufIndex++];
            currentField += 2;
        }

        // Fractional seconds scale up to nanoseconds.
        currentValue = 0;
        if (end > 10)
        {
            currentField = 11;
            while (currentField <= end)
            {
                currentValue = currentValue * 100 + dtbuf[dtbufIndex++];
                currentField += 2;
            }
            int fractionNumber = 15 - end - 1 + 4;
            if ((fractionNumber & 0x1) == 0)
                fractionNumber++;
            for (int j = 0; j < fractionNumber; j++)
                currentValue *= 10;
            data[dataIndex] = currentValue;
        }
    }
}
