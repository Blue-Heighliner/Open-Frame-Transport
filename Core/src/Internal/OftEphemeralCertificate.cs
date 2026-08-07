namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// Generates a throwaway self-signed certificate for <see cref="OftSecurityMode.Secure"/>, where the
/// server side of a connection needs a certificate to negotiate TLS but nobody is ever going to
/// validate its identity against anything (see <see cref="OftSecurityMode.Secure"/>'s own doc
/// comment).
/// </summary>
internal static class OftEphemeralCertificate
{
    /// <summary>
    /// Creates a new self-signed certificate, with its private key, usable as a TLS server
    /// certificate for exactly this purpose.
    /// </summary>
    public static X509Certificate2 Create()
    {
        using RSA rsa = RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new("CN=oft-ephemeral", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
