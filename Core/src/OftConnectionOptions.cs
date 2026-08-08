namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Options that govern how an individual OFT connection behaves once its TLS session is
/// established: used both to make an outbound connection via
/// <see cref="IOftConnector.Connect(string, int, OftConnectionOptions?, CancellationToken)"/> and to
/// accept inbound connections via
/// <see cref="IOftHoster.Host(IPEndPoint, OftConnectionOptions?, CancellationToken)"/>. Also the base
/// for <see cref="OftPeerOptions"/>, whose <see cref="IOftPeer"/> makes both outbound and inbound
/// connections of its own and so needs every setting here too. TLS 1.3 is the only protocol version
/// ever negotiated (see Docs/OFT.md §1) — there is no option to allow an older version.
/// </summary>
public record OftConnectionOptions
{
    /// <summary>
    /// Opaque, application-controlled data sent to the peer in this side's hail (see Docs/OFT.md §3).
    /// </summary>
    public required string Info { get; init; }

    /// <summary>
    /// The certificate this side authenticates itself with during the TLS handshake: when hosting,
    /// presented up front, to every connecting client, as the server's own identity; when
    /// connecting, presented only if the server requests one. Required when hosting and
    /// <see cref="SecurityMode"/> is <see cref="OftSecurityMode.ServerAuthentication"/> or
    /// <see cref="OftSecurityMode.DualAuthentication"/>; required when connecting only when
    /// <see cref="SecurityMode"/> is <see cref="OftSecurityMode.DualAuthentication"/>. Unused under
    /// <see cref="OftSecurityMode.Secure"/> (the server side uses an ephemeral certificate instead)
    /// and <see cref="OftSecurityMode.Trusted"/> (no TLS at all).
    /// </summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>
    /// An optional callback used to validate the remote side's certificate: a connected server's
    /// certificate when connecting, or a connecting client's certificate when hosting. When
    /// <see langword="null"/>, the default .NET validation is used. Only consulted when connecting
    /// and <see cref="SecurityMode"/> is <see cref="OftSecurityMode.ServerAuthentication"/> or
    /// <see cref="OftSecurityMode.DualAuthentication"/>, or when hosting and
    /// <see cref="SecurityMode"/> is <see cref="OftSecurityMode.DualAuthentication"/> (no other mode
    /// ever requests a client certificate) — under <see cref="OftSecurityMode.Secure"/>, the server's
    /// certificate is accepted unconditionally regardless of this callback, since it's an ephemeral
    /// certificate with nothing meaningful to validate it against.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    /// <summary>
    /// An optional async callback used to validate a fully-established connection, invoked once the
    /// OFT hail exchange completes (see Docs/OFT.md §3), for every connection under every
    /// <see cref="SecurityMode"/> — unlike <see cref="CertificateValidation"/>, which only runs during
    /// the TLS handshake and only for the side(s)/mode(s) that request certificate authentication.
    /// When <see langword="null"/> (the default), every connection is accepted; otherwise,
    /// establishing the connection fails with an <see cref="AuthenticationException"/> if the
    /// callback returns <see langword="false"/>.
    /// </summary>
    public OftConnectionValidationCallback? ConnectionValidation { get; init; }

    /// <summary>
    /// The maximum number of payload bytes carried in a single packet's data field. Bounds both the
    /// largest message sendable as a single <c>Unit</c> packet and the chunk size used when
    /// splitting a larger message into <c>Data</c> packets (see Docs/OFT.md §4). Defaults to 1 KiB.
    /// </summary>
    public int MaxPacketDataSize { get; init; } = 1024;

    /// <summary>
    /// When set, the connection automatically rekeys its TLS session (see Docs/OFT.md §8) on this
    /// interval. When <see langword="null"/>, automatic rekeying is disabled and rekeying only
    /// happens when requested manually via <see cref="IOftConnection.Rekey"/>. Ignored when
    /// <see cref="SecurityMode"/> is <see cref="OftSecurityMode.Trusted"/> — there is no TLS
    /// session to rekey.
    /// </summary>
    public TimeSpan? RekeyInterval { get; init; }

    /// <summary>
    /// How this connection uses TLS (see <see cref="OftSecurityMode"/>). Defaults to
    /// <see cref="OftSecurityMode.Secure"/> — encrypted, but with no authentication of either side's
    /// identity.
    /// </summary>
    public OftSecurityMode SecurityMode { get; init; } = OftSecurityMode.Secure;

    /// <summary>
    /// How often the connection sends an empty <c>Poll</c> packet to the peer as a liveness signal,
    /// once established (see Docs/OFT.md §10). Sent independent of application traffic — even while
    /// otherwise idle. Defaults to 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the connection may go without receiving anything at all from the peer (a
    /// <c>Poll</c> packet or any other packet) before it assumes the peer is unreachable and closes
    /// itself (see Docs/OFT.md §10). Defaults to 5 seconds.
    /// </summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
