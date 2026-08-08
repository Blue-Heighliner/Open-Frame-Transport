namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Options for an <see cref="IOftPeer"/>, extending <see cref="OftConnectionOptions"/> with settings
/// for the pool of connections a peer keeps rather than a single one. TLS 1.3 is the only protocol
/// version ever negotiated (see Docs/OFT.md §1) — there is no option to allow an older version.
/// </summary>
public sealed record OftPeerOptions : OftConnectionOptions
{
    /// <summary>
    /// How long a connection may sit idle (no send or receive) before it is automatically
    /// disconnected. Since eviction is only ever checked once per <see cref="IOftPeer"/>'s fixed,
    /// non-configurable 30-second eviction check interval (see its own doc comment), a value below
    /// 30 seconds here has no effect beyond that floor — the connection is disconnected on the first
    /// check after it goes idle, not the instant it does.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// The maximum total lifetime of a connection before it is automatically disconnected,
    /// regardless of activity. Since eviction is only ever checked once per <see cref="IOftPeer"/>'s
    /// fixed, non-configurable 30-second eviction check interval (see its own doc comment), a value
    /// below 30 seconds here has no effect beyond that floor — the connection is disconnected on the
    /// first check after it expires, not the instant it does.
    /// </summary>
    public TimeSpan MaxConnectionLifetime { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// The maximum number of connections this peer keeps at once. When exceeded, the oldest
    /// connections (by when they were established) are disconnected first. A connection with
    /// pending data (see <see cref="IOftConnection.HasPendingData"/>) is never counted toward this
    /// limit for eviction purposes — an application that briefly sends to more distinct hosts than
    /// this at once is never cut off mid-send; connections beyond the limit are only evicted, oldest
    /// first, once their data has finished sending and a fixed grace period (see
    /// <see cref="IOftPeer"/>'s own doc comment) has passed.
    /// </summary>
    public int MaxConnectionCount { get; init; } = 16;
}
