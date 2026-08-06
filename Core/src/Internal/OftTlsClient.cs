namespace OpenFrameTransport.Internal;

/// <summary>
/// The client side of an OFT connection's TLS 1.3 handshake (see Docs/OFT.md §1). Under
/// <see cref="OftSecurityMode.Authentication"/>/<see cref="OftSecurityMode.DualAuthentication"/>,
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
    private readonly X509CertificateCollection? clientCertificates;
    private readonly RemoteCertificateValidationCallback? serverCertificateValidation;

    public OftTlsClient(
            BcTlsCrypto crypto, string targetHost, bool skipServerCertificateValidation,
            X509CertificateCollection? clientCertificates, RemoteCertificateValidationCallback? serverCertificateValidation)
        : base(crypto)
    {
        this.targetHost = targetHost;
        this.skipServerCertificateValidation = skipServerCertificateValidation;
        this.clientCertificates = clientCertificates;
        this.serverCertificateValidation = serverCertificateValidation;
    }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

    public override TlsAuthentication GetAuthentication() =>
        new Authentication(this.m_context, this.targetHost, this.skipServerCertificateValidation, this.clientCertificates, this.serverCertificateValidation);

    private sealed class Authentication : TlsAuthentication
    {
        private readonly TlsClientContext context;
        private readonly string targetHost;
        private readonly bool skipServerCertificateValidation;
        private readonly X509CertificateCollection? clientCertificates;
        private readonly RemoteCertificateValidationCallback? serverCertificateValidation;

        public Authentication(
                TlsClientContext context, string targetHost, bool skipServerCertificateValidation,
                X509CertificateCollection? clientCertificates, RemoteCertificateValidationCallback? serverCertificateValidation)
        {
            this.context = context;
            this.targetHost = targetHost;
            this.skipServerCertificateValidation = skipServerCertificateValidation;
            this.clientCertificates = clientCertificates;
            this.serverCertificateValidation = serverCertificateValidation;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            if (this.skipServerCertificateValidation)
            {
                return;
            }

            OftTlsCertificates.Validate(serverCertificate.Certificate, this.serverCertificateValidation, this.targetHost);
        }

        public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest)
        {
            if (this.clientCertificates is null || this.clientCertificates.Count == 0)
            {
                return null!;
            }

            X509Certificate2 certificate = this.clientCertificates[0] as X509Certificate2
                ?? new X509Certificate2(this.clientCertificates[0]);
            BcTlsCrypto crypto = (BcTlsCrypto)this.context.Crypto;
            SignatureAndHashAlgorithm algorithm = OftTlsCertificates.PickSignatureAndHashAlgorithm(certificate);

            return new BcDefaultTlsCredentialedSigner(
                new TlsCryptoParameters(this.context),
                crypto,
                OftTlsCertificates.ToBcPrivateKey(certificate),
                OftTlsCertificates.ToBcCertificateChain(certificate, crypto),
                algorithm);
        }
    }
}
