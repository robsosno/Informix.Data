namespace Informix.Data.Asf;

/// <summary>
/// An error reported by the server during the ASF handshake (connection
/// reject) — the .NET port of <c>com.informix.asf.IfxASFRemoteException</c>.
/// The service error surfaces as <see cref="IfxException.IfxSqlCode"/> and the
/// OS error as <see cref="IfxException.IsamCode"/>.
/// </summary>
internal sealed class AsfRemoteException : IfxException
{
    public AsfRemoteException(short svcError, short osError, string message)
        : base(message.Length > 0 ? message : $"The Informix server rejected the connection (error {svcError}).",
               sqlCode: svcError, isamCode: osError)
    {
    }
}

/// <summary>
/// The server redirected the client to another node (an HDR secondary or a
/// Connection Manager target). Not supported so it currently surfaces as this exception.
/// </summary>
internal sealed class AsfRedirectException : IfxException
{
    /// <summary>The redirect target detail string the server sent.</summary>
    public string RedirectDetail { get; }

    public AsfRedirectException(string redirectDetail)
        : base($"The Informix server redirected the connection: {redirectDetail}", sqlCode: -79998)
    {
        RedirectDetail = redirectDetail;
    }
}
