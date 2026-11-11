namespace Informix.Data;

/// <summary>
/// Which flavour of Informix auto-increment column the most recently executed
/// <see cref="IfxCommand.ExecuteNonQuery"/> generated a value for — i.e. which of
/// <see cref="IfxCommand.LastSerial"/>, <see cref="IfxCommand.LastSerial8"/> and
/// <see cref="IfxCommand.LastBigSerial"/> is the meaningful one.
/// </summary>
/// <remarks>A single INSERT can only feed one auto-increment column, so at most one
/// of the three is non-zero; the server reports SERIAL in the SQ_DONE reply and
/// SERIAL8/BIGSERIAL in the SQ_INSERTDONE reply.</remarks>
public enum IfxSerialKind
{
    /// <summary>The statement generated no auto-increment value (all three
    /// <c>Last*</c> properties are 0).</summary>
    None = 0,

    /// <summary>A <c>SERIAL</c> value was generated — read <see cref="IfxCommand.LastSerial"/>.</summary>
    Serial = 1,

    /// <summary>A <c>SERIAL8</c> value was generated — read <see cref="IfxCommand.LastSerial8"/>.</summary>
    Serial8 = 2,

    /// <summary>A <c>BIGSERIAL</c> value was generated — read <see cref="IfxCommand.LastBigSerial"/>.</summary>
    BigSerial = 3,
}
