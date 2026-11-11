using System.Text;

namespace Informix.Data.Text;

/// <summary>
/// DATE as text, in both directions (<c>rstrdate</c> and <c>rdatestr</c>), together with the
/// end-user format they honour (<c>DBDATE</c>, else <c>GL_DATE</c>, else <c>mm/dd/yyyy</c>)
/// and the <c>DBCENTURY</c> rule that expands one- and two-digit years.
/// </summary>
internal static class DateText
{
    /// <summary>Day 0 of the internal DATE (an <c>int4</c> day count), as the C library counts it.</summary>
    private static readonly DateOnly Epoch = new(1899, 12, 31);

    private static readonly string[] MonthAbbrev =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly string[] MonthNames =
        ["january", "february", "march", "april", "may", "june",
         "july", "august", "september", "october", "november", "december"];

    private static readonly string[] DayAbbrev = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private static readonly string[] DayNames =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>The internal DATE (<c>rmdyjul</c>'s <c>int4</c>) of a calendar date.</summary>
    public static int ToInternalDate(DateOnly date) => date.DayNumber - Epoch.DayNumber;

    /// <summary>The calendar date an internal DATE stands for (<c>rjulmdy</c>).</summary>
    public static DateOnly FromInternalDate(int internalDate) => Epoch.AddDays(internalDate);

    // ===================================================================== //
    //  DBDATE                                                               //
    // ===================================================================== //

    /// <summary>A parsed <c>DBDATE</c>: the field order, the year width and the separator.</summary>
    internal readonly record struct DbDateFormat(char[] Order, int YearDigits, char Separator)
    {
        public static readonly DbDateFormat Default = new(['M', 'D', 'Y'], 4, '/');
    }

    /// <summary>Parses a <c>DBDATE</c> value such as <c>"MDY4/"</c>, <c>"DMY2."</c> or <c>"Y4MD0"</c>.</summary>
    /// <returns>0, or -1212 when the value does not name a month, a day and a year.</returns>
    public static int TryParseDbDate(string dbDate, out DbDateFormat layout)
    {
        layout = DbDateFormat.Default;
        var order = new List<char>(3);
        int yearDigits = 4;
        char separator = '/';
        bool separatorSeen = false;

        for (int i = 0; i < dbDate.Length; i++)
        {
            char c = char.ToUpperInvariant(dbDate[i]);
            if (c is 'D' or 'M')
            {
                order.Add(c);
            }
            else if (c == 'Y')
            {
                order.Add(c);
                if (i + 1 < dbDate.Length && (dbDate[i + 1] == '2' || dbDate[i + 1] == '4'))
                    yearDigits = dbDate[++i] - '0';
            }
            else if (!separatorSeen)
            {
                // The trailing character is the separator; '0' means "no separator at all".
                separatorSeen = true;
                separator = dbDate[i] == '0' ? '\0' : dbDate[i];
            }
        }

        if (order.Count != 3 || !order.Contains('D') || !order.Contains('M') || !order.Contains('Y'))
            return ConversionError.DateFormatIncomplete;

        layout = new DbDateFormat([.. order], yearDigits, separator);
        return ConversionError.Ok;
    }

    // ===================================================================== //
    //  rstrdate                                                             //
    // ===================================================================== //

    /// <summary>
    /// Converts an end-user date string into a date, as <c>rstrdate()</c> does.
    /// <paramref name="date"/> is <see langword="null"/> for a blank string, which the C
    /// library reports as the null DATE.
    /// </summary>
    /// <returns>0, or the C error code (-1204, -1205, -1206, -1212, -1218).</returns>
    public static int TryParse(ReadOnlySpan<char> s, IfxTextFormat format, out DateOnly? date)
    {
        date = null;
        var trimmed = s.Trim(' ');
        if (trimmed.Length == 0)
            return ConversionError.Ok;

        if (string.IsNullOrEmpty(format.DbDate) && !string.IsNullOrEmpty(format.GlDate))
            return ParseWithGlFormat(trimmed, format.GlDate, format, out date);

        int rc = TryParseDbDate(format.DbDate ?? "MDY4/", out var layout);
        if (rc != ConversionError.Ok)
            return rc;

        int year = 0, month = 0, day = 0, yearDigits = 0;
        bool yearStandsAlone = false;
        int i = 0;
        for (int fieldIndex = 0; fieldIndex < layout.Order.Length; fieldIndex++)
        {
            char field = layout.Order[fieldIndex];
            // Any run of non-digits separates fields; a month may also be spelled out.
            int fieldStart = i;
            while (i < trimmed.Length && !char.IsAsciiDigit(trimmed[i]))
            {
                if (field == 'M' && char.IsLetter(trimmed[i]))
                {
                    int named = MatchMonthName(trimmed, ref i);
                    if (named > 0)
                    {
                        month = named;
                        goto next;
                    }
                }
                i++;
            }

            int start = i;
            // Fields written without separators are read at fixed widths -- two digits for the
            // month and the day -- so a leading year takes only what the rest of the run leaves
            // it ("180122" with DBDATE=Y4MD- is 2018-01-22).
            int run = 0;
            while (start + run < trimmed.Length && char.IsAsciiDigit(trimmed[start + run]))
                run++;
            int fieldsLeft = layout.Order.Length - 1 - fieldIndex;
            int max = 2;
            if (field == 'Y')
            {
                max = DigitGroups(trimmed[start..]) > fieldsLeft
                    ? 4                                     // its own group: take up to four digits
                    : Math.Min(4, Math.Max(run - 2 * fieldsLeft, 1));
            }
            int value = 0;
            while (i < trimmed.Length && char.IsAsciiDigit(trimmed[i]) && i - start < max)
                value = value * 10 + (trimmed[i++] - '0');
            if (i == start)
                return field == 'Y' ? ConversionError.InvalidYear
                     : field == 'M' ? ConversionError.InvalidMonth
                     : ConversionError.InvalidDay;

            switch (field)
            {
                case 'Y':
                    year = value;
                    yearDigits = i - start;
                    // The year stands on its own unless it is run together with a neighbour:
                    // no separator in front of it (and it is not the leading field), or more
                    // digits directly behind it.
                    yearStandsAlone = (start > fieldStart || fieldIndex == 0)
                        && !(fieldIndex < layout.Order.Length - 1 && i < trimmed.Length && char.IsAsciiDigit(trimmed[i]));
                    break;
                case 'M':
                    month = value;
                    break;
                default:
                    day = value;
                    break;
            }
        next:;
        }

        // A year that stopped short of four digits and is followed by yet another group of
        // digits means the string does not lay out the way the format does -- "2008-12-31"
        // read as mm/dd/yyyy, say. The C library blames the year for that.
        int after = i;
        while (after < trimmed.Length && !char.IsAsciiDigit(trimmed[after]))
            after++;
        if (yearDigits < 4 && after < trimmed.Length)
            return ConversionError.InvalidYear;

        if (trimmed[i..].Trim(' ').Length != 0)
            return ConversionError.DateFormatMismatch;     // anything left over, a trailing separator included

        // Fields written without a separator are read at fixed widths, so the year has to be
        // two or four digits there; "1231123" is the C library's -1204, while "1.2.345" is a
        // perfectly good year 345.
        if (!yearStandsAlone && yearDigits is not (2 or 4))
            return ConversionError.InvalidYear;

        return Assemble(year, yearDigits, month, day, format, out date);
    }

    /// <summary>How many runs of digits the text holds — one per field when it is fully separated.</summary>
    private static int DigitGroups(ReadOnlySpan<char> s)
    {
        int groups = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i]))
                continue;
            groups++;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                i++;
        }
        return groups;
    }

    private static int MatchMonthName(ReadOnlySpan<char> s, ref int i)
    {
        for (int m = 0; m < 12; m++)
        {
            foreach (string name in new[] { MonthNames[m], MonthAbbrev[m] })
            {
                if (i + name.Length <= s.Length && s.Slice(i, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    i += name.Length;
                    return m + 1;
                }
            }
        }
        return 0;
    }

    /// <summary>Validates the fields in the C library's order (year, then month, then day) and expands the century.</summary>
    private static int Assemble(int year, int yearDigits, int month, int day, IfxTextFormat format, out DateOnly? date)
    {
        date = null;
        int rc = ExpandYear(year, yearDigits, month, day, format, out year);
        if (rc != ConversionError.Ok)
            return rc;
        if (year is < 1 or > 9999)
            return ConversionError.InvalidYear;
        if (month is < 1 or > 12)
            return ConversionError.InvalidMonth;
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
            return ConversionError.InvalidDay;
        date = new DateOnly(year, month, day);
        return ConversionError.Ok;
    }

    /// <summary>
    /// Applies <c>DBCENTURY</c> to a one- or two-digit year: <c>R</c> (the default) takes the
    /// present century, <c>P</c> the most recent past date, <c>F</c> the nearest future date and
    /// <c>C</c> whichever of the two is closer.
    /// </summary>
    public static int ExpandYear(int year, int digits, int month, int day, IfxTextFormat format, out int expanded)
    {
        expanded = year;
        if (digits is 0 or >= 3)
            return ConversionError.Ok;

        DateOnly today = format.Today;
        int candidate = today.Year / 100 * 100 + year;
        char rule = string.IsNullOrEmpty(format.DbCentury) ? 'R' : char.ToUpperInvariant(format.DbCentury[0]);
        if (rule == 'R')
        {
            expanded = candidate;
            return ConversionError.Ok;
        }

        int safeMonth = month is >= 1 and <= 12 ? month : 1;
        int safeDay = Math.Clamp(day, 1, 28);
        DateOnly At(int y) => new(y, safeMonth, safeDay);

        expanded = rule switch
        {
            'P' => At(candidate) > today ? candidate - 100 : candidate,
            'F' => At(candidate) < today ? candidate + 100 : candidate,
            'C' => Closest(candidate, today, At),
            _ => candidate,
        };
        return ConversionError.Ok;
    }

    private static int Closest(int candidate, DateOnly today, Func<int, DateOnly> at)
    {
        int best = candidate;
        int bestDistance = Math.Abs(at(candidate).DayNumber - today.DayNumber);
        foreach (int other in new[] { candidate - 100, candidate + 100 })
        {
            if (other < 1 || other > 9999)
                continue;
            int distance = Math.Abs(at(other).DayNumber - today.DayNumber);
            if (distance < bestDistance)
            {
                best = other;
                bestDistance = distance;
            }
        }
        return best;
    }

    // ===================================================================== //
    //  rdatestr                                                             //
    // ===================================================================== //

    /// <summary>Formats a date the way <c>rdatestr()</c> does.</summary>
    /// <returns>0, or -1212 when <c>DBDATE</c> is incomplete.</returns>
    public static int TryFormat(DateOnly date, IfxTextFormat format, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(format.DbDate) && !string.IsNullOrEmpty(format.GlDate))
        {
            text = FormatStrftime(format.GlDate, date.ToDateTime(TimeOnly.MinValue), 0);
            return ConversionError.Ok;
        }

        int rc = TryParseDbDate(format.DbDate ?? "MDY4/", out var layout);
        if (rc != ConversionError.Ok)
            return rc;

        var sb = new StringBuilder();
        foreach (char field in layout.Order)
        {
            if (sb.Length > 0 && layout.Separator != '\0')
                sb.Append(layout.Separator);
            sb.Append(field switch
            {
                'Y' => layout.YearDigits == 2
                    ? (date.Year % 100).ToString("D2")
                    : date.Year.ToString("D4"),
                'M' => date.Month.ToString("D2"),
                _ => date.Day.ToString("D2"),
            });
        }
        text = sb.ToString();
        return ConversionError.Ok;
    }

    // ===================================================================== //
    //  GL_DATE / GL_DATETIME (a strftime-style format)                      //
    // ===================================================================== //

    /// <summary>
    /// Expands a <c>GL_DATE</c>/<c>GL_DATETIME</c> format. Supported directives:
    /// <c>%d %e %m %y %Y %b %h %B %a %A %j %H %I %M %S %p %T %R %F %n %t %%</c>;
    /// anything else is copied through unchanged.
    /// </summary>
    public static string FormatStrftime(string format, DateTime value, int fractionDigits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 == format.Length)
            {
                sb.Append(format[i]);
                continue;
            }

            char d = format[++i];
            switch (d)
            {
                case 'd': sb.Append(value.Day.ToString("D2")); break;
                case 'e': sb.Append(value.Day.ToString().PadLeft(2)); break;
                case 'm': sb.Append(value.Month.ToString("D2")); break;
                case 'y': sb.Append((value.Year % 100).ToString("D2")); break;
                case 'Y': sb.Append(value.Year.ToString("D4")); break;
                case 'b':
                case 'h': sb.Append(Capitalize(MonthAbbrev[value.Month - 1])); break;
                case 'B': sb.Append(Capitalize(MonthNames[value.Month - 1])); break;
                case 'a': sb.Append(Capitalize(DayAbbrev[(int)value.DayOfWeek])); break;
                case 'A': sb.Append(Capitalize(DayNames[(int)value.DayOfWeek])); break;
                case 'j': sb.Append(value.DayOfYear.ToString("D3")); break;
                case 'H': sb.Append(value.Hour.ToString("D2")); break;
                case 'I': sb.Append((value.Hour % 12 == 0 ? 12 : value.Hour % 12).ToString("D2")); break;
                case 'M': sb.Append(value.Minute.ToString("D2")); break;
                case 'S': sb.Append(value.Second.ToString("D2")); break;
                case 'p': sb.Append(value.Hour < 12 ? "AM" : "PM"); break;
                case 'T': sb.Append($"{value.Hour:D2}:{value.Minute:D2}:{value.Second:D2}"); break;
                case 'R': sb.Append($"{value.Hour:D2}:{value.Minute:D2}"); break;
                case 'F':
                {
                    int digits = fractionDigits;
                    if (i + 1 < format.Length && char.IsAsciiDigit(format[i + 1]))
                        digits = format[++i] - '0';
                    if (digits > 0)
                    {
                        long ticks = value.Ticks % TimeSpan.TicksPerSecond;
                        string fraction = (ticks / (TimeSpan.TicksPerSecond / 10_000_000)).ToString("D7");
                        sb.Append(fraction.PadRight(digits, '0')[..digits]);
                    }
                    break;
                }
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '%': sb.Append('%'); break;
                default: sb.Append('%').Append(d); break;
            }
        }
        return sb.ToString();
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Reads a date written in a <c>GL_DATE</c> format; literals must match exactly.</summary>
    private static int ParseWithGlFormat(ReadOnlySpan<char> s, string pattern, IfxTextFormat format, out DateOnly? date)
    {
        date = null;
        int year = 0, month = 0, day = 0, yearDigits = 0;
        int i = 0;

        for (int f = 0; f < pattern.Length; f++)
        {
            if (pattern[f] != '%' || f + 1 == pattern.Length)
            {
                if (i >= s.Length || s[i] != pattern[f])
                    return ConversionError.DateFormatMismatch;
                i++;
                continue;
            }

            char d = pattern[++f];
            switch (d)
            {
                case 'd':
                case 'e':
                    day = ReadNumber(s, ref i, 2, out int dayDigits);
                    if (dayDigits == 0)
                        return ConversionError.DateFormatMismatch;
                    break;
                case 'm':
                    month = ReadNumber(s, ref i, 2, out int monthDigits);
                    if (monthDigits == 0)
                        return ConversionError.DateFormatMismatch;
                    break;
                case 'y':
                    year = ReadNumber(s, ref i, 2, out yearDigits);
                    if (yearDigits == 0)
                        return ConversionError.DateFormatMismatch;
                    break;
                case 'Y':
                    year = ReadNumber(s, ref i, 4, out int fullYearDigits);
                    if (fullYearDigits == 0)
                        return ConversionError.DateFormatMismatch;
                    yearDigits = 4;                 // %Y is a full year: DBCENTURY never applies
                    break;
                case 'b':
                case 'h':
                case 'B':
                    month = MatchMonthName(s, ref i);
                    if (month == 0)
                        return ConversionError.DateFormatMismatch;
                    break;
                case 'a':
                case 'A':
                    if (!SkipDayName(s, ref i))
                        return ConversionError.DateFormatMismatch;
                    break;
                case 'n':
                case 't':
                    while (i < s.Length && char.IsWhiteSpace(s[i]))
                        i++;
                    break;
                case '%':
                    if (i >= s.Length || s[i] != '%')
                        return ConversionError.DateFormatMismatch;
                    i++;
                    break;
                default:
                    return ConversionError.DateFormatMismatch;
            }
        }

        // Text beyond the format is ignored here, as the GLS parser in the C library does.
        return Assemble(year, yearDigits, month, day, format, out date);
    }

    private static int ReadNumber(ReadOnlySpan<char> s, ref int i, int max, out int digits)
    {
        while (i < s.Length && s[i] == ' ')
            i++;
        int start = i, value = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]) && i - start < max)
            value = value * 10 + (s[i++] - '0');
        digits = i - start;
        return value;
    }

    private static bool SkipDayName(ReadOnlySpan<char> s, ref int i)
    {
        for (int d = 0; d < 7; d++)
        {
            foreach (string name in new[] { DayNames[d], DayAbbrev[d] })
            {
                if (i + name.Length <= s.Length && s.Slice(i, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    i += name.Length;
                    return true;
                }
            }
        }
        return false;
    }
}
