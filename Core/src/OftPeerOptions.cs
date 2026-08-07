namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Options for an <see cref="IOftPeer"/>. TLS 1.3 is the only protocol version ever negotiated (see
/// Docs/OFT.md §1) — there is no option to allow an older version.
/// </summary>
public sealed record OftPeerOptions
{
    /// <summary>
    /// Opaque, application-controlled data sent to every peer in this side's hail (see README.md
    /// §3).
    /// </summary>
    public required string Info { get; init; }

    /// <summary>
    /// The certificate the peer authenticates itself with when accepting an inbound connection.
    /// Required to call <see cref="IOftPeer.Open(IPEndPoint, CancellationToken)"/> when
    /// <see cref="SecurityMode"/> is <see cref="OftSecurityMode.DualAuthentication"/> (the only
    /// authenticating mode a peer supports — see <see cref="OftSecurityMode.ServerAuthentication"/>);
    /// unused otherwise.
    /// </summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>
    /// The maximum number of payload bytes carried in a single packet's data field (see
    /// Docs/OFT.md §4). Defaults to 1 KiB.
    /// </summary>
    public int MaxPacketDataSize { get; init; } = 1024;

    /// <summary>
    /// When set, every connection automatically rekeys its TLS session (see Docs/OFT.md §8) on this
    /// interval. Ignored when <see cref="SecurityMode"/> is <see cref="OftSecurityMode.Trusted"/>.
    /// </summary>
    public TimeSpan? RekeyInterval { get; init; }

    /// <summary>
    /// How this peer's connections use TLS (see <see cref="OftSecurityMode"/>). Defaults to
    /// <see cref="OftSecurityMode.Secure"/> — encrypted, but with no authentication of either side's
    /// identity. <see cref="OftSecurityMode.ServerAuthentication"/> is not a valid value here — see
    /// its own documentation for why — and <see cref="IOftPeerFactory.Create(OftPeerOptions?)"/>
    /// throws if it's set.
    /// </summary>
    public OftSecurityMode SecurityMode { get; init; } = OftSecurityMode.Secure;

    /// <summary>
    /// How often each connection sends an empty <c>Poll</c> packet to its peer as a liveness
    /// signal, once established (see Docs/OFT.md §10). Defaults to 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a connection may go without receiving anything at all from its peer (a
    /// <c>Poll</c> packet or any other packet) before it assumes the peer is unreachable and closes
    /// itself (see Docs/OFT.md §10). Defaults to 5 seconds.
    /// </summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Certificates this peer authenticates itself with on outbound connections, if the remote side
    /// requests one. Required when <see cref="SecurityMode"/> is
    /// <see cref="OftSecurityMode.DualAuthentication"/>; unused otherwise.
    /// </summary>
    public X509CertificateCollection? ClientCertificates { get; init; }

    /// <summary>
    /// An optional callback used to validate the peer's certificate on either an outbound
    /// connection's server or an inbound connection's client. When <see langword="null"/>, the
    /// default .NET validation is used. Only consulted when <see cref="SecurityMode"/> is
    /// <see cref="OftSecurityMode.DualAuthentication"/>.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    /// <summary>
    /// How long a connection may sit idle (no send or receive) before it is automatically
    /// disconnected.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The maximum total lifetime of a connection before it is automatically disconnected,
    /// regardless of activity.
    /// </summary>
    public TimeSpan MaxConnectionLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The maximum number of connections this peer keeps at once. When exceeded, the oldest
    /// connections (by when they were established) are disconnected first.
    /// </summary>
    public int MaxConnectionCount { get; init; } = 128;

    /// <summary>
    /// How often the peer checks connections against <see cref="IdleTimeout"/>,
    /// <see cref="MaxConnectionLifetime"/>, and <see cref="MaxConnectionCount"/>.
    /// </summary>
    public TimeSpan EvictionCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
}
