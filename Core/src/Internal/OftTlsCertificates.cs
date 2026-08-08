namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// Bridges between .NET's <see cref="X509Certificate2"/>-based certificate/key representation (used
/// throughout OFT's public options types) and BouncyCastle's TLS API, and implements the default
/// certificate validation OFT falls back to when a caller doesn't supply their own
/// <see cref="RemoteCertificateValidationCallback"/>.
/// </summary>
internal static class OftTlsCertificates
{
    /// <summary>
    /// Wraps <paramref name="certificate"/> as a single-entry BouncyCastle certificate chain, as
    /// presented during the TLS handshake.
    /// </summary>
    /// <param name="certificate">The certificate to wrap. Only its public portion is used.</param>
    /// <param name="crypto">The crypto backend the resulting chain is bound to.</param>
    public static Certificate ToBcCertificateChain(X509Certificate2 certificate, BcTlsCrypto crypto)
    {
        BcTlsCertificate bcCertificate = new(crypto, certificate.RawData);

        // TLS 1.3 (the only version this codebase ever negotiates) requires a certificate request
        // context to be present - even when empty, as it always is here, since OFT never uses the
        // multiple-simultaneous-certificate-requests feature that field exists for - rather than
        // absent: Certificate.Encode() rejects a null one for a TLS 1.3 connection.
        return new Certificate(TlsUtilities.EmptyBytes, [new CertificateEntry(bcCertificate, null)]);
    }

    /// <summary>
    /// Extracts <paramref name="certificate"/>'s private key as a BouncyCastle key parameter, for use
    /// when presenting it as TLS credentials.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="certificate"/> has no private key, or its key algorithm isn't RSA or ECDSA.
    /// </exception>
    public static AsymmetricKeyParameter ToBcPrivateKey(X509Certificate2 certificate)
    {
        AsymmetricAlgorithm? key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey();
        if (key is null)
        {
            throw new NotSupportedException($"Certificate '{certificate.Subject}' has no private key, or its key algorithm is not RSA or ECDSA.");
        }

        return DotNetUtilities.GetKeyPair(key).Private;
    }

    /// <summary>
    /// Picks the TLS 1.3 signature scheme to sign with for <paramref name="certificate"/>'s key
    /// algorithm: RSA-PSS for an RSA key, or the ECDSA scheme matching the curve for an ECDSA key.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="certificate"/>'s key algorithm or, for ECDSA, curve isn't supported.
    /// </exception>
    public static SignatureAndHashAlgorithm PickSignatureAndHashAlgorithm(X509Certificate2 certificate)
    {
        using RSA? rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
        {
            return SignatureAndHashAlgorithm.rsa_pss_rsae_sha256;
        }

        using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa is not null)
        {
            int keySizeInBits = ecdsa.KeySize;
            int signatureScheme = keySizeInBits switch
            {
                256 => SignatureScheme.ecdsa_secp256r1_sha256,
                384 => SignatureScheme.ecdsa_secp384r1_sha384,
                521 => SignatureScheme.ecdsa_secp521r1_sha512,
                _ => throw new NotSupportedException($"Unsupported ECDSA key size {keySizeInBits} on certificate '{certificate.Subject}'; only P-256, P-384, and P-521 are supported."),
            };

            return SignatureScheme.GetSignatureAndHashAlgorithm(signatureScheme);
        }

        throw new NotSupportedException($"Certificate '{certificate.Subject}' has no RSA or ECDSA private key.");
    }

    /// <summary>
    /// Extracts the leaf (first) certificate from a certificate chain presented during a TLS
    /// handshake.
    /// </summary>
    /// <param name="chain">The presented certificate chain, leaf certificate first.</param>
    /// <returns>The leaf certificate, or <see langword="null"/> if none was presented.</returns>
    public static X509Certificate2? ExtractLeafCertificate(Certificate chain) =>
        chain.IsEmpty ? null : X509CertificateLoader.LoadCertificate(chain.GetCertificateList()[0].GetEncoded());

    /// <summary>
    /// Validates a certificate chain presented by the peer during the TLS handshake, either by
    /// delegating to <paramref name="callback"/> if supplied, or by performing .NET's standard
    /// <see cref="X509Chain"/>-based trust validation (against the OS trust store) plus, when
    /// <paramref name="targetHost"/> is given, a Subject Alternative Name hostname check.
    /// </summary>
    /// <param name="chain">The peer's certificate chain, leaf certificate first.</param>
    /// <param name="callback">
    /// The caller-supplied validation callback, or <see langword="null"/> to use the default policy.
    /// </param>
    /// <param name="targetHost">
    /// The hostname the connection was dialed to, checked against the leaf certificate's Subject
    /// Alternative Name when validating a server's certificate (client-side only; always
    /// <see langword="null"/> when validating a client's certificate).
    /// </param>
    /// <param name="policyErrors">
    /// The policy errors found while validating the chain (<see cref="SslPolicyErrors.None"/> if it
    /// validated cleanly), regardless of whether the certificate was ultimately accepted or rejected.
    /// </param>
    /// <returns>
    /// The <see cref="X509Chain"/> built while validating the certificate, transferring ownership to
    /// the caller (who becomes responsible for disposing it) — <see langword="null"/> if the peer
    /// didn't present a certificate at all.
    /// </returns>
    /// <exception cref="AuthenticationException">The certificate was rejected.</exception>
    public static X509Chain? Validate(Certificate chain, RemoteCertificateValidationCallback? callback, string? targetHost, out SslPolicyErrors policyErrors)
    {
        X509Certificate2? leaf = ExtractLeafCertificate(chain);
        if (leaf is null)
        {
            policyErrors = SslPolicyErrors.RemoteCertificateNotAvailable;

            if (callback is not null && callback(null!, null, null, policyErrors))
            {
                return null;
            }

            throw new AuthenticationException("The peer did not present a certificate.");
        }

        TlsCertificate[] entries = chain.GetCertificateList();

        X509Chain x509Chain = new();
        for (int i = 1; i < entries.Length; i++)
        {
            x509Chain.ChainPolicy.ExtraStore.Add(X509CertificateLoader.LoadCertificate(entries[i].GetEncoded()));
        }

        bool chainIsValid = x509Chain.Build(leaf);

        policyErrors = SslPolicyErrors.None;
        if (!chainIsValid)
        {
            policyErrors |= SslPolicyErrors.RemoteCertificateChainErrors;
        }

        if (targetHost is not null && !MatchesHostname(leaf, targetHost))
        {
            policyErrors |= SslPolicyErrors.RemoteCertificateNameMismatch;
        }

        bool accepted = callback is not null
            ? callback(null!, leaf, x509Chain, policyErrors)
            : policyErrors == SslPolicyErrors.None;

        if (!accepted)
        {
            x509Chain.Dispose();
            throw new AuthenticationException($"The remote certificate was rejected by the validation policy ({policyErrors}).");
        }

        return x509Chain;
    }

    /// <summary>
    /// Checks <paramref name="hostname"/> against <paramref name="certificate"/>'s Subject
    /// Alternative Name DNS entries (or IP address entries, if <paramref name="hostname"/> parses as
    /// one), matching modern hostname-verification practice: the deprecated fallback to the Subject
    /// Common Name is intentionally not implemented.
    /// </summary>
    private static bool MatchesHostname(X509Certificate2 certificate, string hostname)
    {
        bool isIpAddress = IPAddress.TryParse(hostname, out IPAddress? targetAddress);

        foreach ((string kind, string value) in EnumerateSubjectAlternativeNameEntries(certificate))
        {
            if (!isIpAddress && kind is "DNS Name" or "DNS")
            {
                if (string.Equals(value, hostname, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (isIpAddress && kind is "IP Address")
            {
                if (IPAddress.TryParse(value, out IPAddress? sanAddress) && sanAddress.Equals(targetAddress))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <paramref name="certificate"/>'s Subject Alternative Name extension, if it has one,
    /// into kind/value pairs (e.g. <c>("DNS", "example.com")</c>).
    /// </summary>
    private static IEnumerable<(string Kind, string Value)> EnumerateSubjectAlternativeNameEntries(X509Certificate2 certificate)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            string sanText = extension.Format(multiLine: false);
            foreach (string entry in sanText.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                // The separator between an entry's kind and its value is platform-dependent: .NET's
                // Windows (CryptoAPI-backed) formatter uses "DNS Name=value", while its
                // cross-platform (OpenSSL-backed) formatter used on Linux/macOS uses "DNS:value" -
                // accepting either keeps this working regardless of which one produced the text.
                // Taking the *first* occurrence (rather than, say, the last) is what keeps this
                // correct for an IP Address entry whose value is itself IPv6 and so contains further
                // colons after the one separating it from its "IP Address" kind label.
                int separatorIndex = entry.IndexOfAny(['=', ':']);
                if (separatorIndex < 0)
                {
                    continue;
                }

                yield return (entry[..separatorIndex].Trim(), entry[(separatorIndex + 1)..].Trim());
            }
        }
    }
}
