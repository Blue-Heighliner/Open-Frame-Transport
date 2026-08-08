namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// The server side of an OFT connection's TLS 1.3 handshake (see Docs/OFT.md §1). Presents its own
/// certificate as credentials and, if configured to, requests and validates a client certificate —
/// via a caller-supplied <see cref="RemoteCertificateValidationCallback"/> if one was configured, or
/// .NET's standard chain validation otherwise (see <see cref="OftTlsCertificates"/>).
/// </summary>
internal sealed class OftTlsServer : DefaultTlsServer
{
    private readonly X509Certificate2 serverCertificate;
    private readonly bool clientCertificateRequired;
    private readonly RemoteCertificateValidationCallback? clientCertificateValidation;

    /// <param name="crypto">The crypto backend to negotiate with.</param>
    /// <param name="serverCertificate">The certificate this server presents as its own credentials.</param>
    /// <param name="clientCertificateRequired">Whether to request and require a client certificate.</param>
    /// <param name="clientCertificateValidation">
    /// The callback used to validate a presented client certificate, or <see langword="null"/> to use
    /// .NET's standard validation.
    /// </param>
    public OftTlsServer(BcTlsCrypto crypto, X509Certificate2 serverCertificate, bool clientCertificateRequired, RemoteCertificateValidationCallback? clientCertificateValidation)
        : base(crypto)
    {
        this.serverCertificate = serverCertificate;
        this.clientCertificateRequired = clientCertificateRequired;
        this.clientCertificateValidation = clientCertificateValidation;
    }

    /// <summary>
    /// The certificate the client presented during the TLS handshake (only ever requested under
    /// <see cref="OftSecurityMode.DualAuthentication"/>), or <see langword="null"/> if none was
    /// requested or presented.
    /// </summary>
    public X509Certificate2? RemoteCertificate { get; private set; }

    /// <summary>
    /// The certificate chain built while validating <see cref="RemoteCertificate"/>, or
    /// <see langword="null"/> if <see cref="RemoteCertificate"/> is. Ownership belongs to whoever
    /// reads this property; disposed by <see cref="OftConnection"/> once it's done with it.
    /// </summary>
    public X509Chain? RemoteCertificateChain { get; private set; }

    /// <summary>
    /// The policy errors found while validating <see cref="RemoteCertificate"/>'s chain, or
    /// <see cref="SslPolicyErrors.None"/> whenever <see cref="RemoteCertificateChain"/> is
    /// <see langword="null"/>.
    /// </summary>
    public SslPolicyErrors RemoteCertificateSslErrors { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

    public override TlsCredentials GetCredentials()
    {
        BcTlsCrypto crypto = (BcTlsCrypto)this.Crypto;
        SignatureAndHashAlgorithm algorithm = OftTlsCertificates.PickSignatureAndHashAlgorithm(this.serverCertificate);

        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(this.m_context),
            crypto,
            OftTlsCertificates.ToBcPrivateKey(this.serverCertificate),
            OftTlsCertificates.ToBcCertificateChain(this.serverCertificate, crypto),
            algorithm);
    }

    public override Org.BouncyCastle.Tls.CertificateRequest GetCertificateRequest()
    {
        if (!this.clientCertificateRequired)
        {
            return null!;
        }

        return new Org.BouncyCastle.Tls.CertificateRequest(
            TlsUtilities.EmptyBytes,
            TlsUtilities.GetDefaultSupportedSignatureAlgorithms(this.m_context),
            null,
            null);
    }

    public override void NotifyClientCertificate(Certificate clientCertificate)
    {
        this.RemoteCertificate = OftTlsCertificates.ExtractLeafCertificate(clientCertificate);
        this.RemoteCertificateChain = OftTlsCertificates.Validate(
            clientCertificate, this.clientCertificateValidation, targetHost: null, out SslPolicyErrors sslErrors);
        this.RemoteCertificateSslErrors = sslErrors;
    }
}
