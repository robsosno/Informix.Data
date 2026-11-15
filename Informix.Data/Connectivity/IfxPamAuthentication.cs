namespace Informix.Data;

/// <summary>
/// The kind of PAM prompt the server sent.
/// </summary>
public enum PamMessageType : short
{
    Unknown = 0,
    /// <summary>A hidden prompt — typically the password.</summary>
    PromptEchoOff = 1,
    /// <summary>A visible prompt — typically a user name or one-time token.</summary>
    PromptEchoOn = 2,
    /// <summary>An informational error message; no response is expected.</summary>
    ErrorMessage = 3,
    /// <summary>Informational text; no response is expected.</summary>
    TextInfo = 4,
}

/// <summary>
/// A PAM challenge the server issued during authentication.
/// </summary>
public sealed class IfxPamChallenge(string message, PamMessageType type)
{
    /// <summary>The prompt/informational text (empty when the server sent none).</summary>
    public string Message { get; } = message;

    public PamMessageType Type { get; } = type;

    /// <summary>True when this challenge expects a reply (prompts, not info/error messages).</summary>
    public bool RequiresResponse => Type is not (PamMessageType.ErrorMessage or PamMessageType.TextInfo);
}

/// <summary>
/// A client answer to an <see cref="IfxPamChallenge"/>.
/// </summary>
public sealed class IfxPamResponse
{
    /// <summary>A response longer than this many characters is rejected (-79886).</summary>
    public const int MaxMessageSize = 512;

    public IfxPamResponse(string? response) => Response = response;

    /// <summary>The reply text (e.g. the password); may be null/empty for info prompts.</summary>
    public string? Response { get; set; }

    /// <summary>Set to abort authentication and close the connection instead of replying.</summary>
    public bool TerminateConnection { get; set; }

    /// <summary>A response that aborts the PAM exchange.</summary>
    public static IfxPamResponse Terminate() => new(null) { TerminateConnection = true };
}

/// <summary>
/// Supplies answers to Informix PAM challenges. Plug a custom handler into
/// <see cref="IfxConnection.PamHandler"/> for multi-factor / one-time-token schemes.
/// </summary>
/// <remarks>Plays the role of the JDBC driver's PAM callback interface (loaded by class name from the <c>IFX_PAM_CLASS</c> property).</remarks>
public interface IIfxPamHandler
{
    /// <summary>Answers a single PAM challenge. Return <see cref="IfxPamResponse.Terminate"/> to abort.</summary>
    IfxPamResponse Respond(IfxPamChallenge challenge);
}

/// <summary>
/// The default PAM handler for the common <c>pamauth=(password)</c> service: it
/// answers hidden password prompts (<see cref="PamMessageType.PromptEchoOff"/>)
/// with the connection password and echoes the user name for visible prompts.
/// </summary>
public sealed class PasswordPamHandler(string? userName, string? password) : IIfxPamHandler
{
    public IfxPamResponse Respond(IfxPamChallenge challenge) => challenge.Type switch
    {
        PamMessageType.PromptEchoOff => new IfxPamResponse(password),
        PamMessageType.PromptEchoOn => new IfxPamResponse(userName),
        _ => new IfxPamResponse(null), // info/error prompts need no answer
    };
}
