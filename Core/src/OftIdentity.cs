namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// The identity of an OFT connection's remote side.
/// </summary>
public sealed record OftIdentity
{
    /// <summary>The connection's remote TCP endpoint.</summary>
    public required IPEndPoint EndPoint { get; init; }

    /// <summary>
    /// The remote side's TLS certificate identity, or <see langword="null"/> if it didn't present one
    /// — always <see langword="null"/> for a connection established with
    /// <see cref="OftSecurityMode.Trusted"/> (no TLS at all), and also <see langword="null"/> for the
    /// accepting side of a connection established under a mode that never requests a client
    /// certificate (see <see cref="OftSecurityMode.DualAuthentication"/>).
    /// </summary>
    public required OftCertificateIdentity? Certificate { get; init; }

    /// <summary>
    /// The opaque, application-controlled data the remote side sent in its hail (see
    /// Docs/OFT.md §3).
    /// </summary>
    public required string Info { get; init; }
}
