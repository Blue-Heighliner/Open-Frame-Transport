namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// How a connection (or an <see cref="IOftPeer"/>'s connections) uses TLS. Not negotiated on the
/// wire (see Docs/OFT.md §9) — both sides of a connection must be configured with the same mode, or
/// the exchange fails outright.
/// </summary>
public enum OftSecurityMode
{
    /// <summary>
    /// No TLS at all: hails are sent directly over the raw TCP connection as soon as it's formed
    /// (see Docs/OFT.md §9). No confidentiality, integrity, or authentication of either side. Only
    /// appropriate on a network already trusted by other means (see Docs/OFT.md §9).
    /// </summary>
    Trusted,

    /// <summary>
    /// TLS provides encryption (and integrity) in transit, but no authentication of either side's
    /// identity: the server presents an ephemeral, throwaway certificate it generates itself
    /// (<see cref="OftConnectionOptions.Certificate"/> is unused), and the connecting side accepts
    /// it unconditionally rather than validating it — there would be nothing meaningful to validate
    /// it against. This is the default.
    /// </summary>
    Secure,

    /// <summary>
    /// TLS provides both encryption and authentication of the server's identity: the server must
    /// present its own certificate via <see cref="OftConnectionOptions.Certificate"/>, and the
    /// connecting side validates it normally (via a caller-supplied validation callback, or the
    /// default .NET chain/hostname validation). The client is not authenticated. Not a valid mode
    /// for <see cref="IOftPeer"/>, which has no client/server delineation and so cannot express a
    /// one-sided authentication requirement (use <see cref="DualAuthentication"/> instead).
    /// </summary>
    ServerAuthentication,

    /// <summary>
    /// Mutual TLS: both sides authenticate each other. In addition to everything
    /// <see cref="ServerAuthentication"/> requires, the connecting side must present a certificate
    /// via <see cref="OftConnectionOptions.Certificate"/>, and the accepting side requests and
    /// validates it.
    /// </summary>
    DualAuthentication,
}
