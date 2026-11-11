using Informix.Data.Sqli;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Argument validation on the reader's bulk accessors. These used to surface as a
/// NullReferenceException (GetValues(null)) or to return 0 silently for a bufferOffset
/// beyond the buffer (GetBytes/GetChars); both are caller errors and now say so.
/// The checks run before the cursor is touched, so an empty cursor is enough.
/// </summary>
public class DataReaderArgumentValidationTests
{
    private static IfxDataReader Reader() => new(new SqliEmptyCursor(0));

    [Fact]
    public void GetValues_throws_ArgumentNullException_for_null()
    {
        using var reader = Reader();
        Assert.Throws<ArgumentNullException>(() => reader.GetValues(null!));
    }

    [Fact]
    public void GetBytes_throws_when_bufferOffset_is_past_the_buffer()
    {
        using var reader = Reader();
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, 0, new byte[4], 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, 0, new byte[4], -1, 0));
    }

    [Fact]
    public void GetChars_throws_when_bufferOffset_is_past_the_buffer()
    {
        using var reader = Reader();
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetChars(0, 0, new char[4], 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetChars(0, 0, new char[4], -1, 0));
    }

    [Fact]
    public void GetBytes_and_GetChars_throw_for_negative_dataOffset_or_length()
    {
        using var reader = Reader();
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, -1, new byte[4], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, 0, new byte[4], 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetChars(0, -1, new char[4], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetChars(0, 0, new char[4], 0, -1));
    }
}
