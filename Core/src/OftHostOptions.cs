namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Options for an <see cref="IOftHoster"/>/<see cref="IOftListener"/>.
/// </summary>
public sealed record OftHostOptions : OftConnectionOptions
{
    /// <summary>
    /// The certificate the server authenticates itself with during the TLS handshake. Required when
    /// <see cref="OftConnectionOptions.SecurityMode"/> is <see cref="OftSecurityMode.ServerAuthentication"/>
    /// or <see cref="OftSecurityMode.DualAuthentication"/>; unused under
    /// <see cref="OftSecurityMode.Secure"/> (an ephemeral certificate is generated instead) and
    /// <see cref="OftSecurityMode.Trusted"/> (no TLS at all).
    /// </summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>
    /// An optional callback used to validate a connecting client's certificate. When
    /// <see langword="null"/>, the default .NET validation is used. Only consulted when
    /// <see cref="OftConnectionOptions.SecurityMode"/> is <see cref="OftSecurityMode.DualAuthentication"/>
    /// — no other mode ever requests a client certificate.
    /// </summary>
    public RemoteCertificateValidationCallback? ClientCertificateValidation { get; init; }
}
