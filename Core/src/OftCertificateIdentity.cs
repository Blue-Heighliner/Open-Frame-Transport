namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Identity information extracted from an X.509 certificate presented during a TLS handshake.
/// </summary>
public sealed record OftCertificateIdentity
{
    /// <summary>
    /// The Common Name (CN) of the certificate's subject, or <see langword="null"/> if its subject
    /// has none.
    /// </summary>
    public required string? Name { get; init; }

    /// <summary>
    /// The Common Name (CN) of the certificate's issuer, or <see langword="null"/> if its issuer has
    /// none.
    /// </summary>
    public required string? Issuer { get; init; }

    /// <summary>
    /// The certificate's Subject Alternative Name entries (DNS names and IP addresses), in the order
    /// they appear on the certificate. Empty if the certificate has no Subject Alternative Name
    /// extension.
    /// </summary>
    public required IReadOnlyList<string> AlternativeNames { get; init; }

    /// <summary>
    /// Extracts identity information from <paramref name="certificate"/>.
    /// </summary>
    /// <param name="certificate">The certificate to extract identity information from.</param>
    /// <returns>The extracted identity information.</returns>
    public static OftCertificateIdentity FromCertificate(X509Certificate2 certificate) =>
        new()
        {
            Name = OftTlsCertificates.ExtractCommonName(certificate.SubjectName),
            Issuer = OftTlsCertificates.ExtractCommonName(certificate.IssuerName),
            AlternativeNames = OftTlsCertificates.ExtractSubjectAlternativeNames(certificate),
        };
}
