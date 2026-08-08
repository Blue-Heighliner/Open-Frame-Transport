namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// The client side of an OFT connection's TLS 1.3 handshake (see Docs/OFT.md §1). Under
/// <see cref="OftSecurityMode.ServerAuthentication"/>/<see cref="OftSecurityMode.DualAuthentication"/>,
/// validates the server's certificate — via a caller-supplied
/// <see cref="RemoteCertificateValidationCallback"/> if one was configured, or .NET's standard
/// chain/hostname validation otherwise (see <see cref="OftTlsCertificates"/>) — and, if the server
/// requests one, presents a client certificate. Under <see cref="OftSecurityMode.Secure"/>, accepts
/// the server's certificate unconditionally instead, since it's an ephemeral certificate with
/// nothing meaningful to validate it against.
/// </summary>
internal sealed class OftTlsClient : DefaultTlsClient
{
    private readonly string targetHost;
    private readonly bool skipServerCertificateValidation;
    private readonly X509Certificate2? clientCertificate;
    private readonly RemoteCertificateValidationCallback? serverCertificateValidation;

    public OftTlsClient(
            BcTlsCrypto crypto, string targetHost, bool skipServerCertificateValidation,
            X509Certificate2? clientCertificate, RemoteCertificateValidationCallback? serverCertificateValidation)
        : base(crypto)
    {
        this.targetHost = targetHost;
        this.skipServerCertificateValidation = skipServerCertificateValidation;
        this.clientCertificate = clientCertificate;
        this.serverCertificateValidation = serverCertificateValidation;
    }

    /// <summary>
    /// The certificate the server presented during the TLS handshake, or <see langword="null"/> if
    /// the handshake hasn't reached that point yet. Captured regardless of
    /// <see cref="skipServerCertificateValidation"/>, since a caller may still want to know which
    /// certificate was presented even under <see cref="OftSecurityMode.Secure"/>, where it isn't
    /// validated.
    /// </summary>
    public X509Certificate2? RemoteCertificate { get; private set; }

    /// <summary>
    /// The certificate chain built while validating <see cref="RemoteCertificate"/>, or
    /// <see langword="null"/> if <see cref="RemoteCertificate"/> is, or under
    /// <see cref="OftSecurityMode.Secure"/>, where the certificate is accepted unconditionally
    /// without building one. Ownership belongs to whoever reads this property; disposed by
    /// <see cref="OftConnection"/> once it's done with it.
    /// </summary>
    public X509Chain? RemoteCertificateChain { get; private set; }

    /// <summary>
    /// The policy errors found while validating <see cref="RemoteCertificate"/>'s chain, or
    /// <see cref="SslPolicyErrors.None"/> whenever <see cref="RemoteCertificateChain"/> is
    /// <see langword="null"/>.
    /// </summary>
    public SslPolicyErrors RemoteCertificateSslErrors { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

    public override TlsAuthentication GetAuthentication() =>
        new Authentication(this, this.m_context, this.targetHost, this.skipServerCertificateValidation, this.clientCertificate, this.serverCertificateValidation);

    private sealed class Authentication : TlsAuthentication
    {
        private readonly OftTlsClient owner;
        private readonly TlsClientContext context;
        private readonly string targetHost;
        private readonly bool skipServerCertificateValidation;
        private readonly X509Certificate2? clientCertificate;
        private readonly RemoteCertificateValidationCallback? serverCertificateValidation;

        public Authentication(
                OftTlsClient owner, TlsClientContext context, string targetHost, bool skipServerCertificateValidation,
                X509Certificate2? clientCertificate, RemoteCertificateValidationCallback? serverCertificateValidation)
        {
            this.owner = owner;
            this.context = context;
            this.targetHost = targetHost;
            this.skipServerCertificateValidation = skipServerCertificateValidation;
            this.clientCertificate = clientCertificate;
            this.serverCertificateValidation = serverCertificateValidation;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            this.owner.RemoteCertificate = OftTlsCertificates.ExtractLeafCertificate(serverCertificate.Certificate);

            if (this.skipServerCertificateValidation)
            {
                return;
            }

            this.owner.RemoteCertificateChain = OftTlsCertificates.Validate(
                serverCertificate.Certificate, this.serverCertificateValidation, this.targetHost, out SslPolicyErrors sslErrors);
            this.owner.RemoteCertificateSslErrors = sslErrors;
        }

        public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest)
        {
            if (this.clientCertificate is null)
            {
                return null!;
            }

            BcTlsCrypto crypto = (BcTlsCrypto)this.context.Crypto;
            SignatureAndHashAlgorithm algorithm = OftTlsCertificates.PickSignatureAndHashAlgorithm(this.clientCertificate);

            return new BcDefaultTlsCredentialedSigner(
                new TlsCryptoParameters(this.context),
                crypto,
                OftTlsCertificates.ToBcPrivateKey(this.clientCertificate),
                OftTlsCertificates.ToBcCertificateChain(this.clientCertificate, crypto),
                algorithm);
        }
    }
}
