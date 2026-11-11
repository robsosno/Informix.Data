using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxException.IsTransient"/>: the modern <see cref="System.Data.Common.DbException"/>
/// contract that retry frameworks (EF Core execution strategies, Polly) consult. Checks both the
/// primary SQLCODE slot and the companion ISAM slot, since a few ambiguous primary SQLCODEs are
/// only a transient lock conflict when their ISAM companion code says so.
/// </summary>
public class IfxExceptionTransientTests
{
    [Theory]
    [InlineData(-908)]  // attempt to connect failed (every network-failure path, incl. pool-wait timeout)
    [InlineData(-213)]  // statement interrupted (this driver's CommandTimeout OOB cancel)
    [InlineData(-233)]  // cannot read record locked by another user
    [InlineData(-263)]  // could not lock row for UPDATE
    [InlineData(-289)]  // cannot lock table in requested mode
    [InlineData(-347)]  // could not open table for exclusive access
    [InlineData(-378)]  // record currently locked (pre-6.0 legacy)
    [InlineData(-106)]  // ISAM: non-exclusive access
    [InlineData(-107)]  // ISAM: record is locked
    [InlineData(-113)]  // ISAM: the file is locked
    [InlineData(-134)]  // ISAM: no more locks
    [InlineData(-143)]  // ISAM: deadlock detected
    [InlineData(-144)]  // ISAM: key value locked
    [InlineData(-154)]  // ISAM: lock timeout expired / distributed deadlock
    public void Transient_code_as_the_primary_SQLCODE_is_transient(int code)
    {
        var ex = new IfxException("boom", sqlCode: code);
        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData(-107)]
    [InlineData(-143)]
    [InlineData(-154)]
    public void Transient_code_as_the_ISAM_companion_is_transient_even_with_an_unrelated_primary(int isamCode)
    {
        // e.g. -244 "unable to reposition to last cursor position" carries a lock ISAM
        // companion -- transient only via the ISAM slot, not the primary SQLCODE alone.
        var ex = new IfxException("boom", sqlCode: -244, isamCode: isamCode);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public void Ambiguous_primary_without_a_lock_ISAM_companion_is_not_transient()
    {
        var ex = new IfxException("boom", sqlCode: -244, isamCode: 0);
        Assert.False(ex.IsTransient);
    }

    [Theory]
    [InlineData(-201)]   // syntax error
    [InlineData(-458)]   // needs admin action
    [InlineData(-255)]   // logging-mode config error
    [InlineData(-221)]   // needs admin action
    [InlineData(-124)]   // transaction-state logic error
    [InlineData(-627)]   // two-phase-commit/STAR recovery state
    [InlineData(-11020)] // Informix CLI/ODBC driver-side code, never synthesized by this driver
    public void Rejected_code_is_not_transient(int code)
    {
        var ex = new IfxException("boom", sqlCode: code);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public void Redirect_is_not_transient()
    {
        var ex = new IfxException("redirected", sqlCode: -79998);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public void Success_or_unset_code_is_not_transient()
    {
        var ex = new IfxException("no code set");
        Assert.False(ex.IsTransient);
    }
}
