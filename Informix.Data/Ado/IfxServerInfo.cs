namespace Informix.Data;

/// <summary>
/// Server identity reported in the connection-accept response of the login
/// handshake: version, serial number and the raw capability words. Exposed on
/// <see cref="IfxConnection.ServerInfo"/> once the connection is open.
/// </summary>
/// <remarks>Ported from the fields <c>DecodeAscBinary</c> populated on
/// <c>com.informix.asf.Connection</c>.</remarks>
public sealed class IfxServerInfo
{
    /// <summary>Server version, e.g. "9.28.FC4"; the server rewrites "9.50" to "10.00".</summary>
    public string VersionNumber { get; internal set; } = "";

    public string SerialNumber { get; internal set; } = "";

    public string ApplicationId { get; internal set; } = "";

    // Raw capability words of the handshake; the effective capabilities are negotiated later (see IfxServerCapabilities).
    public int Capability1 { get; internal set; }

    public int Capability2 { get; internal set; }

    public int Capability3 { get; internal set; }

    public short Warnings { get; internal set; }
}
