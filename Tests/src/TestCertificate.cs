namespace BlueHeighliner.OpenFrameTransport.Tests;

/// <summary>
/// Generates throwaway self-signed certificates for exercising TLS handshakes in tests.
/// </summary>
internal static class TestCertificate
{
    /// <summary>
    /// Creates a new self-signed certificate, with its private key, usable as an OFT server
    /// certificate.
    /// </summary>
    public static X509Certificate2 Create()
    {
        using RSA rsa = RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// Creates a new self-signed certificate with an ECDSA key on the given curve, with its private
    /// key.
    /// </summary>
    public static X509Certificate2 CreateEcdsa(ECCurve curve)
    {
        using ECDsa ecdsa = ECDsa.Create(curve);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new("CN=localhost", ecdsa, HashAlgorithmName.SHA256);
        X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// Creates a new self-signed certificate, with its private key, carrying the given Subject
    /// Alternative Name DNS entry.
    /// </summary>
    public static X509Certificate2 CreateWithDnsName(string dnsName)
    {
        using RSA rsa = RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder sanBuilder = new();
        sanBuilder.AddDnsName(dnsName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// Creates a new self-signed certificate, with its private key, carrying the given Subject
    /// Alternative Name IP address entry.
    /// </summary>
    public static X509Certificate2 CreateWithIpAddress(IPAddress address)
    {
        using RSA rsa = RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder sanBuilder = new();
        sanBuilder.AddIpAddress(address);
        request.CertificateExtensions.Add(sanBuilder.Build());

        X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// Returns a copy of <paramref name="certificate"/> with only its public portion - no private
    /// key.
    /// </summary>
    public static X509Certificate2 PublicOnly(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
}
