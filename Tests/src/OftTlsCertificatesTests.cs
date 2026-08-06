namespace OpenFrameTransport.Tests;

public sealed class OftTlsCertificatesTests
{
    [Fact]
    public void ToBcCertificateChain_WrapsCertificateAsSingleEntryChain()
    {
        using X509Certificate2 certificate = TestCertificate.Create();
        BcTlsCrypto crypto = new();

        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        TlsCertificate[] entries = chain.GetCertificateList();
        Assert.Single(entries);
        Assert.Equal(certificate.RawData, entries[0].GetEncoded());
    }

    [Fact]
    public void ToBcPrivateKey_Rsa_ReturnsPrivateKey()
    {
        using X509Certificate2 certificate = TestCertificate.Create();

        AsymmetricKeyParameter key = OftTlsCertificates.ToBcPrivateKey(certificate);

        RsaKeyParameters rsaKey = Assert.IsAssignableFrom<RsaKeyParameters>(key);
        Assert.True(rsaKey.IsPrivate);
    }

    [Fact]
    public void ToBcPrivateKey_Ecdsa_ReturnsPrivateKey()
    {
        using X509Certificate2 certificate = TestCertificate.CreateEcdsa(ECCurve.NamedCurves.nistP256);

        AsymmetricKeyParameter key = OftTlsCertificates.ToBcPrivateKey(certificate);

        ECPrivateKeyParameters ecKey = Assert.IsType<ECPrivateKeyParameters>(key);
        Assert.True(ecKey.IsPrivate);
    }

    [Fact]
    public void ToBcPrivateKey_NoPrivateKey_Throws()
    {
        using X509Certificate2 fullCertificate = TestCertificate.Create();
        using X509Certificate2 publicOnly = TestCertificate.PublicOnly(fullCertificate);

        Assert.Throws<NotSupportedException>(() => OftTlsCertificates.ToBcPrivateKey(publicOnly));
    }

    [Fact]
    public void PickSignatureAndHashAlgorithm_Rsa_ReturnsRsaPssSha256()
    {
        using X509Certificate2 certificate = TestCertificate.Create();

        SignatureAndHashAlgorithm algorithm = OftTlsCertificates.PickSignatureAndHashAlgorithm(certificate);

        Assert.Equal(SignatureAndHashAlgorithm.rsa_pss_rsae_sha256.Signature, algorithm.Signature);
        Assert.Equal(SignatureAndHashAlgorithm.rsa_pss_rsae_sha256.Hash, algorithm.Hash);
    }

    [Theory]
    [MemberData(nameof(EcdsaCurves))]
    public void PickSignatureAndHashAlgorithm_Ecdsa_ReturnsMatchingScheme(ECCurve curve, int expectedScheme)
    {
        using X509Certificate2 certificate = TestCertificate.CreateEcdsa(curve);

        SignatureAndHashAlgorithm algorithm = OftTlsCertificates.PickSignatureAndHashAlgorithm(certificate);
        SignatureAndHashAlgorithm expected = SignatureScheme.GetSignatureAndHashAlgorithm(expectedScheme);

        Assert.Equal(expected.Signature, algorithm.Signature);
        Assert.Equal(expected.Hash, algorithm.Hash);
    }

    public static TheoryData<ECCurve, int> EcdsaCurves() => new()
    {
        { ECCurve.NamedCurves.nistP256, SignatureScheme.ecdsa_secp256r1_sha256 },
        { ECCurve.NamedCurves.nistP384, SignatureScheme.ecdsa_secp384r1_sha384 },
        { ECCurve.NamedCurves.nistP521, SignatureScheme.ecdsa_secp521r1_sha512 },
    };

    [Fact]
    public void PickSignatureAndHashAlgorithm_NoPrivateKey_Throws()
    {
        using X509Certificate2 fullCertificate = TestCertificate.Create();
        using X509Certificate2 publicOnly = TestCertificate.PublicOnly(fullCertificate);

        Assert.Throws<NotSupportedException>(() => OftTlsCertificates.PickSignatureAndHashAlgorithm(publicOnly));
    }

    [Fact]
    public void Validate_EmptyChain_NoCallback_Throws()
    {
        Certificate emptyChain = Certificate.EmptyChain;

        AuthenticationException exception = Assert.Throws<AuthenticationException>(
            () => OftTlsCertificates.Validate(emptyChain, callback: null, targetHost: null));
        Assert.Contains("did not present a certificate", exception.Message);
    }

    [Fact]
    public void Validate_EmptyChain_CallbackAccepts_DoesNotThrow()
    {
        Certificate emptyChain = Certificate.EmptyChain;

        OftTlsCertificates.Validate(
            emptyChain,
            callback: (_, _, _, errors) => errors == SslPolicyErrors.RemoteCertificateNotAvailable,
            targetHost: null);
    }

    [Fact]
    public void Validate_EmptyChain_CallbackRejects_Throws()
    {
        Certificate emptyChain = Certificate.EmptyChain;

        Assert.Throws<AuthenticationException>(
            () => OftTlsCertificates.Validate(emptyChain, callback: (_, _, _, _) => false, targetHost: null));
    }

    [Fact]
    public void Validate_UntrustedSelfSignedChain_NoCallback_Throws()
    {
        using X509Certificate2 certificate = TestCertificate.Create();
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        // No callback supplied: falls back to .NET's default X509Chain validation, which rejects a
        // self-signed certificate no trust store recognizes.
        AuthenticationException exception = Assert.Throws<AuthenticationException>(
            () => OftTlsCertificates.Validate(chain, callback: null, targetHost: null));
        Assert.Contains("rejected by the validation policy", exception.Message);
    }

    [Fact]
    public void Validate_UntrustedSelfSignedChain_CallbackAcceptsDespiteErrors_DoesNotThrow()
    {
        using X509Certificate2 certificate = TestCertificate.Create();
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        OftTlsCertificates.Validate(chain, callback: (_, _, _, _) => true, targetHost: null);
    }

    [Fact]
    public void Validate_HostnameMatchesDnsSan_CallbackSeesNoNameMismatch()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithDnsName("example.oft.test");
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        SslPolicyErrors? observedErrors = null;
        OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "example.oft.test");

        Assert.NotNull(observedErrors);
        Assert.False(observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_HostnameDoesNotMatchDnsSan_CallbackSeesNameMismatch()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithDnsName("example.oft.test");
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        SslPolicyErrors? observedErrors = null;
        OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "not-example.oft.test");

        Assert.NotNull(observedErrors);
        Assert.True(observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_HostnameMatchesIpAddressSan_CallbackSeesNoNameMismatch()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithIpAddress(IPAddress.Loopback);
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        SslPolicyErrors? observedErrors = null;
        OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "127.0.0.1");

        Assert.NotNull(observedErrors);
        Assert.False(observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_HostnameDoesNotMatchIpAddressSan_CallbackSeesNameMismatch()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithIpAddress(IPAddress.Loopback);
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        SslPolicyErrors? observedErrors = null;
        OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "10.0.0.1");

        Assert.NotNull(observedErrors);
        Assert.True(observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_NoTargetHost_DoesNotCheckHostname()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithDnsName("example.oft.test");
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        SslPolicyErrors? observedErrors = null;
        OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: null);

        Assert.NotNull(observedErrors);
        Assert.False(observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }
}
