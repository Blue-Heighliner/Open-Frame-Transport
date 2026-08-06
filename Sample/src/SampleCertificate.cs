namespace OpenFrameTransport.Sample;

/// <summary>
/// Generates a throwaway self-signed certificate so the sample can demonstrate OFT's TLS handshake
/// without requiring the user to provision a real certificate. Not suitable for anything other than
/// demonstration: every instance of the sample trusts every other instance's certificate
/// unconditionally (see <see cref="MainWindow"/>).
/// </summary>
internal static class SampleCertificate
{
    /// <summary>
    /// Creates a new self-signed certificate, with its private key, usable as an OFT server
    /// certificate.
    /// </summary>
    public static X509Certificate2 Create()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=oft-sample", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
