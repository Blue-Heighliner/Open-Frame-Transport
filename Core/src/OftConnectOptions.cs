namespace OpenFrameTransport;

/// <summary>
/// Options for an <see cref="IOftConnector"/> connection.
/// </summary>
public sealed record OftConnectOptions : OftConnectionOptions
{
    /// <summary>
    /// Certificates this side authenticates itself with during the TLS handshake, if the server
    /// requests one. Required when <see cref="OftConnectionOptions.SecurityMode"/> is
    /// <see cref="OftSecurityMode.DualAuthentication"/>; unused otherwise.
    /// </summary>
    public X509CertificateCollection? ClientCertificates { get; init; }

    /// <summary>
    /// An optional callback used to validate a connected server's certificate. When
    /// <see langword="null"/>, the default .NET validation is used. Only consulted when
    /// <see cref="OftConnectionOptions.SecurityMode"/> is <see cref="OftSecurityMode.Authentication"/>
    /// or <see cref="OftSecurityMode.DualAuthentication"/> — under
    /// <see cref="OftSecurityMode.Secure"/>, the server's certificate is accepted unconditionally
    /// regardless of this callback, since it's an ephemeral certificate with nothing meaningful to
    /// validate it against.
    /// </summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; init; }
}
