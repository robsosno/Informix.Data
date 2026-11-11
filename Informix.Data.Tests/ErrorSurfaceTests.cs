using Xunit;

namespace Informix.Data.Tests;

public class ErrorSurfaceTests
{
    [Fact]
    public void IfxException_Errors_carries_primary_and_isam_records()
    {
        var ex = new IfxException("update failed", sqlCode: -908, isamCode: -111, sqlState: "IX000");

        Assert.Equal(2, ex.Errors.Count);
        Assert.Equal("update failed", ex.Errors[0].Message);
        Assert.Equal(-908, ex.Errors[0].NativeError);
        Assert.Equal("IX000", ex.Errors[0].SQLState);
        Assert.Equal(-111, ex.Errors[1].NativeError);
    }

    [Fact]
    public void IfxException_Errors_has_a_single_record_without_an_isam_code()
    {
        var ex = new IfxException("table not found", sqlCode: -206);
        Assert.Single(ex.Errors);
        Assert.Equal(-206, ex.Errors[0].NativeError);
    }

    [Fact]
    public void IfxErrorCollection_copies_to_a_typed_array()
    {
        var ex = new IfxException("boom", sqlCode: -908, isamCode: -111);
        var target = new IfxError[2];
        ex.Errors.CopyTo(target, 0);
        Assert.Equal(-908, target[0].NativeError);
        Assert.Equal(-111, target[1].NativeError);
    }

    [Fact]
    public void InfoMessage_fires_when_a_statement_completes_with_warnings()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DoneFrame(rows: 1, warnings: 1),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        IfxInfoMessageEventArgs? received = null;
        cn.InfoMessage += (_, e) => received = e;

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";
        Assert.Equal(1, cmd.ExecuteNonQuery());

        Assert.NotNull(received);
        Assert.True(received!.Errors.Count >= 1);
        Assert.Contains("warning", received.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InfoMessage_stays_silent_when_there_are_no_warnings()
    {
        byte[] script = SqliStatementKit.Join(
            SqliStatementKit.DoneFrame(rows: 1, warnings: 0),
            SqliStatementKit.EotFrame());

        using var cn = SqliStatementKit.OpenConnection(script, out _);
        bool fired = false;
        cn.InfoMessage += (_, _) => fired = true;

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update t set x = 1";
        cmd.ExecuteNonQuery();

        Assert.False(fired);
    }
}
