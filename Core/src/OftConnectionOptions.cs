namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Options shared by <see cref="OftHostOptions"/> and <see cref="OftConnectOptions"/> that govern
/// how an individual OFT connection behaves once its TLS session is established. TLS 1.3 is the only
/// protocol version ever negotiated (see Docs/OFT.md §1) — there is no option to allow an older
/// version.
/// </summary>
public abstract record OftConnectionOptions
{
    /// <summary>
    /// Opaque, application-controlled data sent to the peer in this side's hail (see Docs/OFT.md §3).
    /// </summary>
    public required string Info { get; init; }

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
