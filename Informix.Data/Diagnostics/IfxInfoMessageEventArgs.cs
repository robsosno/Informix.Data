namespace Informix.Data;

/// <summary>
/// The argument of <see cref="IfxConnection.InfoMessage"/> — carries the
/// low-severity diagnostics (server warnings) the connection would otherwise
/// swallow.
/// </summary>
public sealed class IfxInfoMessageEventArgs : EventArgs
{
    internal IfxInfoMessageEventArgs(IfxErrorCollection errors) => Errors = errors;

    /// <summary>The warning diagnostic records.</summary>
    public IfxErrorCollection Errors { get; }

    /// <summary>The first warning message (empty when there are none).</summary>
    public string Message => Errors.Count > 0 ? Errors[0].Message : "";

    public override string ToString() => Message;
}
