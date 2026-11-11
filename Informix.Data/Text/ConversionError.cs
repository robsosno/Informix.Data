namespace Informix.Data.Text;

/// <summary>
/// The text-conversion error codes and their messages — the negative values the client
/// library returns from <c>deccvasc</c>, <c>rstoi</c>, <c>rstrdate</c>, <c>dtcvasc</c> and
/// the rest of that family, surfaced here as <see cref="IfxException.IfxSqlCode"/>.
/// </summary>
internal static class ConversionError
{
    public const int Ok = 0;

    // Numeric conversions.
    public const int DecimalOverflow = -1200;      // number too large for a decimal
    public const int DecimalUnderflow = -1201;     // number too small for a decimal
    public const int NonNumeric = -1213;           // non-numeric character in the string
    public const int IntTooLarge = -1214;          // value does not fit rstoi's target
    public const int LongTooLarge = -1215;         // value does not fit rstol's target
    public const int Int8Overflow = -1284;         // int8/bigint overflow or underflow

    // Date conversions.
    public const int InvalidYear = -1204;
    public const int InvalidMonth = -1205;
    public const int InvalidDay = -1206;
    public const int DateFormatIncomplete = -1212; // format lacks a month, day or year
    public const int DateFormatMismatch = -1218;   // string does not match the format

    // DATETIME/INTERVAL conversions.
    public const int TooManyDigitsInFirstField = -1261;
    public const int NonNumericInDateTime = -1262;
    public const int FieldOutOfRange = -1263;
    public const int ExtraCharacters = -1264;
    public const int BadQualifier = -1268;

    public static string Message(int code) => code switch
    {
        DecimalOverflow => "The number is too large to fit into a decimal type structure (overflow).",
        DecimalUnderflow => "The number is too small to fit into a decimal type structure (underflow).",
        InvalidYear => "The year in the date is invalid.",
        InvalidMonth => "The month in the date is invalid.",
        InvalidDay => "The day in the date is invalid.",
        DateFormatIncomplete => "The data-conversion format must contain a month, day or year component.",
        NonNumeric => "The string has non-numeric characters.",
        IntTooLarge => "The number is too large to fit into an int.",
        LongTooLarge => "The number is too large to fit into a long.",
        DateFormatMismatch => "The string does not match the date format.",
        TooManyDigitsInFirstField => "There are too many digits in the first field of the datetime or interval value.",
        NonNumericInDateTime => "A non-numeric character was found in the datetime or interval value.",
        FieldOutOfRange => "A field in the datetime or interval value is out of range or incorrect.",
        ExtraCharacters => "There are extra characters at the end of the datetime or interval value.",
        BadQualifier => "The datetime or interval qualifier is invalid.",
        Int8Overflow => "The operation resulted in an int8 overflow or underflow.",
        _ => $"Informix conversion error {code}.",
    };

    /// <summary>Builds the exception the <c>Parse</c>/<c>Format</c> entry points throw for a C error code.</summary>
    public static IfxException Exception(int code) => new(Message(code), code);
}
