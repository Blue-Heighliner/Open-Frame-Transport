namespace BlueHeighliner.OpenFrameTransport.Tests;

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
            () => OftTlsCertificates.Validate(emptyChain, callback: null, targetHost: null, out _));
        Assert.Contains("did not present a certificate", exception.Message);
    }

    [Fact]
    public void Validate_EmptyChain_CallbackAccepts_DoesNotThrow()
    {
        Certificate emptyChain = Certificate.EmptyChain;

        X509Chain? resultChain = OftTlsCertificates.Validate(
            emptyChain,
            callback: (_, _, _, errors) => errors == SslPolicyErrors.RemoteCertificateNotAvailable,
            targetHost: null,
            out _);

        Assert.Null(resultChain);
    }

    [Fact]
    public void Validate_EmptyChain_CallbackRejects_Throws()
    {
        Certificate emptyChain = Certificate.EmptyChain;

        Assert.Throws<AuthenticationException>(
            () => OftTlsCertificates.Validate(emptyChain, callback: (_, _, _, _) => false, targetHost: null, out _));
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
            () => OftTlsCertificates.Validate(chain, callback: null, targetHost: null, out _));
        Assert.Contains("rejected by the validation policy", exception.Message);
    }

    [Fact]
    public void Validate_UntrustedSelfSignedChain_CallbackAcceptsDespiteErrors_DoesNotThrow()
    {
        using X509Certificate2 certificate = TestCertificate.Create();
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        using X509Chain? resultChain = OftTlsCertificates.Validate(chain, callback: (_, _, _, _) => true, targetHost: null, out _);
    }

    [Fact]
    public void Validate_HostnameMatchesDnsSan_CallbackSeesNoNameMismatch()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithDnsName("example.oft.test");
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        SslPolicyErrors? observedErrors = null;
        using X509Chain? resultChain = OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "example.oft.test",
            out _);

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
        using X509Chain? resultChain = OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "not-example.oft.test",
            out _);

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
        using X509Chain? resultChain = OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "127.0.0.1",
            out _);

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
        using X509Chain? resultChain = OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: "10.0.0.1",
            out _);

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
        using X509Chain? resultChain = OftTlsCertificates.Validate(
            chain,
            callback: (_, _, _, errors) =>
            {
                observedErrors = errors;
                return true;
            },
            targetHost: null,
            out _);

        Assert.NotNull(observedErrors);
        Assert.False(observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void ExtractLeafCertificate_EmptyChain_ReturnsNull()
    {
        Assert.Null(OftTlsCertificates.ExtractLeafCertificate(Certificate.EmptyChain));
    }

    [Fact]
    public void ExtractLeafCertificate_NonEmptyChain_ReturnsLeaf()
    {
        using X509Certificate2 certificate = TestCertificate.Create();
        BcTlsCrypto crypto = new();
        Certificate chain = OftTlsCertificates.ToBcCertificateChain(certificate, crypto);

        using X509Certificate2? leaf = OftTlsCertificates.ExtractLeafCertificate(chain);

        Assert.NotNull(leaf);
        Assert.Equal(certificate.RawData, leaf!.RawData);
    }

    [Fact]
    public void ExtractCommonName_CertificateHasCommonName_ReturnsIt()
    {
        using X509Certificate2 certificate = TestCertificate.Create();

        Assert.Equal("localhost", OftTlsCertificates.ExtractCommonName(certificate.SubjectName));
    }

    [Fact]
    public void ExtractCommonName_NoCommonName_ReturnsNull()
    {
        using RSA rsa = RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new("O=NoCommonNameHere", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Null(OftTlsCertificates.ExtractCommonName(certificate.SubjectName));
    }

    [Fact]
    public void ExtractSubjectAlternativeNames_NoSanExtension_ReturnsEmpty()
    {
        using X509Certificate2 certificate = TestCertificate.Create();

        Assert.Empty(OftTlsCertificates.ExtractSubjectAlternativeNames(certificate));
    }

    [Fact]
    public void ExtractSubjectAlternativeNames_DnsSan_ReturnsIt()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithDnsName("example.oft.test");

        Assert.Equal(["example.oft.test"], OftTlsCertificates.ExtractSubjectAlternativeNames(certificate));
    }

    [Fact]
    public void ExtractSubjectAlternativeNames_IpAddressSan_ReturnsIt()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithIpAddress(IPAddress.Loopback);

        Assert.Equal(["127.0.0.1"], OftTlsCertificates.ExtractSubjectAlternativeNames(certificate));
    }
}
