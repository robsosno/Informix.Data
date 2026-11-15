using System.Text;

namespace Informix.Data.Types;

/// <summary>
/// An Informix day-time interval (INTERVAL DAY TO FRACTION and its narrower
/// forms). The value is
/// seconds plus nanoseconds, both carrying the sign; the qualifier selects
/// which fields exist. <see cref="ToTimeSpan"/>/the TimeSpan constructor bridge
/// to the idiomatic CLR type (TimeSpan's 100 ns ticks cover Informix's 10 µs
/// FRACTION(5) resolution exactly).
/// </summary>
internal sealed class IfxIntervalDayFraction : IfxInterval
{
    public long Seconds { get; private set; }

    // 0 to 999 999 999 for a positive interval; negative together with a negative Seconds.
    public long NanoSeconds { get; private set; }

    /// <summary>An empty DAY(2) TO FRACTION(5) interval.</summary>
    public IfxIntervalDayFraction()
        : this(0, 0)
    {
    }

    // Default qualifier DAY(2) TO FRACTION(5), the widest range Informix can store with 5 fraction digits.
    public IfxIntervalDayFraction(long seconds, long nanos)
        : this(seconds, nanos, IfxQualifier.ForInterval(2, IfxTimeUnit.Day, IfxTimeUnit.Fraction5))
    {
    }

    public IfxIntervalDayFraction(long seconds, long nanos, short qualifier)
    {
        Qualifier = qualifier;
        Set(seconds, nanos);
    }

    public IfxIntervalDayFraction(TimeSpan span)
        : this(span.Ticks / TimeSpan.TicksPerSecond, span.Ticks % TimeSpan.TicksPerSecond * 100)
    {
    }

    public IfxIntervalDayFraction(string value, short qualifier)
    {
        Qualifier = qualifier;
        Set(value);
    }

    /// <summary>
    /// Sets the value, normalizing mixed signs so seconds and nanos agree
    /// (port of <c>set(long, long)</c>).
    /// </summary>
    public void Set(long seconds, long nanos)
    {
        if (seconds < 0)
        {
            if (nanos > 0)
            {
                seconds++;
                nanos -= 1_000_000_000;
            }
        }
        else if (nanos < 0)
        {
            if (seconds > 0)
            {
                seconds--;
                nanos += 1_000_000_000;
            }
        }
        Seconds = seconds;
        NanoSeconds = nanos;
    }

    /// <summary>Parses "dd hh:mm:ss.fffff" per the qualifier (port of <c>fromString</c>).</summary>
    public void Set(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool negative = StripSign(ref value);
        int[] data = StringToUnits(value, Qualifier);

        long seconds = data[2] * 86400L + data[3] * 3600L + data[4] * 60L + data[5];
        long nanos = data[6];
        if (negative)
        {
            seconds = -seconds;
            nanos = -nanos;
        }
        Seconds = seconds;
        NanoSeconds = nanos;
    }

    // A TimeSpan tick is 100 ns, so anything finer than that is truncated.
    public TimeSpan ToTimeSpan() =>
        new(Seconds * TimeSpan.TicksPerSecond + NanoSeconds / 100);

    /// <summary>
    /// Formats only the fields between the qualifier's start and end codes, with two-digit zero padding, then trims
    /// leading zeros of the first field to blanks as the server's text form does.
    /// </summary>
    public override string ToString()
    {
        long secs = Seconds;
        long nans = NanoSeconds;
        var retval = new StringBuilder();

        byte endCode = EndCode;
        byte startCode = StartCode;

        bool negative = false;
        if (Seconds < 0 || (Seconds == 0 && NanoSeconds < 0))
        {
            negative = true;
            secs = -Seconds;
            nans = -nans;
        }

        if (startCode == 4)
        {
            long days = secs / 86400;
            secs %= 86400;
            retval.Append(days);
            if (endCode > 4)
                retval.Append(Delimiters[2]);
        }
        if (startCode <= 6 && endCode >= 6)
        {
            long hours = secs / 3600;
            secs %= 3600;
            if (hours < 10)
                retval.Append('0');
            retval.Append(hours);
            if (endCode > 6)
                retval.Append(Delimiters[3]);
        }
        if (startCode <= 8 && endCode >= 8)
        {
            long mins = secs / 60;
            secs %= 60;
            if (mins < 10)
                retval.Append('0');
            retval.Append(mins);
            if (endCode > 8)
                retval.Append(Delimiters[4]);
        }
        if (startCode <= 10 && endCode >= 10)
        {
            if (secs < 10)
                retval.Append('0');
            retval.Append(secs);
        }

        if (endCode > 10)
        {
            retval.Append(Delimiters[5]);
            if (nans == 0)
            {
                for (int i = 11; i <= endCode; i++)
                    retval.Append('0');
            }
            else
            {
                int numDigsNeeded = endCode - 10;
                int numDigs = 0;
                int i = 11;
                for (long scale = 100_000_000; i <= endCode && nans != 0; scale /= 10, i++)
                {
                    if (nans < scale)
                    {
                        retval.Append('0');
                        numDigs++;
                    }
                    else
                    {
                        retval.Append(nans / scale);
                        numDigs++;
                        nans %= scale;
                    }
                }
                for (i = numDigs; i < numDigsNeeded; i++)
                    retval.Append('0');
            }
        }

        int insertIdx = 0;
        int lastPos = retval.Length - 1;
        while (insertIdx < lastPos && retval[insertIdx] == '0' && char.IsAsciiDigit(retval[insertIdx + 1]))
        {
            retval[insertIdx] = ' ';
            insertIdx++;
        }
        if (negative)
            retval.Insert(insertIdx, '-');

        return retval.ToString();
    }

    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), Seconds, NanoSeconds);

    public override bool Equals(object? obj) =>
        base.Equals(obj) && obj is IfxIntervalDayFraction other &&
        Seconds == other.Seconds && NanoSeconds == other.NanoSeconds;

    public bool GreaterThan(IfxIntervalDayFraction other) =>
        Seconds > other.Seconds || (Seconds == other.Seconds && NanoSeconds > other.NanoSeconds);

    public bool LessThan(IfxIntervalDayFraction other) =>
        Seconds < other.Seconds || (Seconds == other.Seconds && NanoSeconds < other.NanoSeconds);
}
