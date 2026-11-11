namespace Informix.Data.Types;

/// <summary>
/// An Informix year-month interval (INTERVAL YEAR TO MONTH and its narrower
/// forms). The value is a signed total month count; the qualifier selects
/// the fields.
/// </summary>
internal sealed class IfxIntervalYearMonth : IfxInterval
{
    /// <summary>The signed total number of months.</summary>
    public long Months { get; private set; }

    /// <summary>An empty YEAR(4) TO MONTH interval.</summary>
    public IfxIntervalYearMonth()
    {
        Qualifier = IfxQualifier.ForInterval(4, IfxTimeUnit.Year, IfxTimeUnit.Month);
    }

    public IfxIntervalYearMonth(int years, int months)
        : this()
    {
        Set(years, months);
    }

    /// <summary>Wraps a total month count under an explicit qualifier (the <c>intervalValue()</c> path).</summary>
    public IfxIntervalYearMonth(int months, short qualifier)
    {
        Qualifier = qualifier;
        Months = months;
    }

    /// <summary>
    /// Wraps a total month count that may exceed <see cref="int.MaxValue"/> — a
    /// YEAR(9) interval reaches 999,999,999 years, i.e. ~12 billion months.
    /// </summary>
    public IfxIntervalYearMonth(long months, short qualifier)
    {
        Qualifier = qualifier;
        Months = months;
    }

    public IfxIntervalYearMonth(string value, short qualifier)
    {
        Qualifier = qualifier;
        Set(value);
    }

    /// <summary>
    /// Sets the value, normalizing mixed signs so years and months agree
    /// (port of <c>set(int, int)</c>).
    /// </summary>
    public void Set(int years, int months)
    {
        if (years < 0)
        {
            if (months > 0)
            {
                years++;
                months -= 12;
            }
        }
        else if (months < 0)
        {
            if (years > 0)
            {
                years--;
                months += 12;
            }
        }
        Months = months + years * 12L;
    }

    /// <summary>Parses "yyyy-mm" per the qualifier (port of <c>fromString</c>).</summary>
    public void Set(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool negative = StripSign(ref value);
        int[] data = StringToUnits(value, Qualifier);
        Months = data[0] * 12L + data[1];
        if (negative)
            Months = -Months;
    }

    public long GetYears() => Months / 12;

    /// <summary>The month component 0..11 (the part beyond whole years).</summary>
    public long GetMonthsPart() => Months % 12;

    /// <summary>
    /// Formats per the qualifier, e.g. "3-07" for YEAR TO MONTH (faithful port
    /// of <c>IntervalYM.toString()</c>).
    /// </summary>
    public override string ToString()
    {
        byte endCode = EndCode;
        byte startCode = StartCode;

        long months = Months;
        bool negative = false;
        if (months < 0)
        {
            negative = true;
            months = -months;
        }

        string retVal;
        if (startCode == 0 && endCode == 2)
        {
            string monthStr = months % 12 < 10 ? "0" : "";
            retVal = months / 12 + Delimiters[0].ToString() + monthStr + months % 12;
        }
        else if (startCode == 0)
        {
            retVal = (months / 12).ToString();
        }
        else if (startCode == 2)
        {
            retVal = months.ToString();
        }
        else
        {
            throw new IfxException("Invalid qualifier for a year-month interval.", -79707);
        }

        return negative ? "-" + retVal : retVal;
    }

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Months);

    public override bool Equals(object? obj) =>
        base.Equals(obj) && obj is IfxIntervalYearMonth other && Months == other.Months;

    public bool GreaterThan(IfxIntervalYearMonth other) => Months > other.Months;

    public bool LessThan(IfxIntervalYearMonth other) => Months < other.Months;
}
