namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Validates a fully-established OFT connection. Invoked once per connection, after the OFT hail
/// exchange completes (see Docs/OFT.md §3) — unlike
/// <see cref="OftConnectionOptions.CertificateValidation"/>, which runs earlier, during the TLS
/// handshake itself, and only ever sees the certificate in isolation, this runs for every connection
/// under every <see cref="OftSecurityMode"/> (including <see cref="OftSecurityMode.Trusted"/> and
/// <see cref="OftSecurityMode.Secure"/>, where <paramref name="certificate"/> and
/// <paramref name="chain"/> are always <see langword="null"/>) with the connection's fully-populated
/// <see cref="OftIdentity"/> available alongside whatever certificate data the TLS handshake produced.
/// </summary>
/// <param name="identity">The connection's fully-populated remote identity.</param>
/// <param name="certificate">
/// The certificate the remote side presented during the TLS handshake, or <see langword="null"/>
/// under <see cref="OftSecurityMode.Trusted"/> (no TLS at all), under <see cref="OftSecurityMode.Secure"/>
/// on the accepting side, or if the remote side didn't present one.
/// </param>
/// <param name="chain">
/// The certificate chain built while validating <paramref name="certificate"/>, or
/// <see langword="null"/> whenever <paramref name="certificate"/> is, or the certificate was accepted
/// unconditionally without building a chain (see <see cref="OftSecurityMode.Secure"/>).
/// </param>
/// <param name="sslErrors">
/// The policy errors found while validating <paramref name="certificate"/>'s chain, or
/// <see cref="SslPolicyErrors.None"/> whenever <paramref name="chain"/> is <see langword="null"/>.
/// </param>
/// <returns><see langword="true"/> to accept the connection, or <see langword="false"/> to reject it.</returns>
public delegate Task<bool> OftConnectionValidationCallback(OftIdentity identity, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslErrors);
