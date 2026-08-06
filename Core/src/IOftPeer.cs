namespace OpenFrameTransport;

/// <summary>
/// A peer-to-peer convenience layer over <see cref="IOftHoster"/>/<see cref="IOftListener"/> and
/// <see cref="IOftConnector"/>. Sending a message to a host/port transparently reuses an existing
/// connection or creates and caches a new one; idle, expired, or excess cached connections are
/// disconnected automatically, and connections with a configured
/// <see cref="OftPeerOptions.RekeyInterval"/> rekey themselves automatically (see Docs/OFT.md §8).
/// There is no way to enumerate or look up an individual connection this peer holds;
/// <see cref="Rekey"/> and <see cref="Disconnect"/> act on all of them at once.
/// </summary>
public interface IOftPeer : IAsyncDisposable
{
    /// <summary>
    /// The endpoint actually being listened on once <see cref="Open"/> has completed, or
    /// <see langword="null"/> if the peer isn't currently listening.
    /// </summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Raised whenever a complete application message has been received on any connection this
    /// peer holds. Nothing raised before the first subscriber ever attaches is lost — it's
    /// delivered to that first subscriber as soon as it attaches (see README.md and
    /// <c>OftBufferedEvent</c>).
    /// </summary>
    event EventHandler<OftReceivedEventArgs>? Received;

    /// <summary>
    /// Starts listening for inbound connections. A peer that never calls this only ever makes
    /// outbound connections.
    /// </summary>
    /// <param name="listenEndPoint">The local endpoint to listen for incoming TCP connections on.</param>
    /// <param name="cancellationToken">A token that stops listening when cancelled.</param>
    /// <exception cref="ArgumentException">
    /// <see cref="OftPeerOptions.ServerCertificate"/> was not set and
    /// <see cref="OftPeerOptions.SecurityMode"/> requires one (see
    /// <see cref="OftSecurityMode.Authentication"/>/<see cref="OftSecurityMode.DualAuthentication"/>).
    /// </exception>
    Task Open(IPEndPoint listenEndPoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops listening for new inbound connections. Already-established connections are left open.
    /// </summary>
    Task Close();

    /// <summary>
    /// Sends a message to <paramref name="host"/>:<paramref name="port"/>, reusing a cached
    /// connection if one already exists, or creating and caching a new one otherwise.
    /// </summary>
    /// <param name="host">The remote host to send to.</param>
    /// <param name="port">The remote port to send to.</param>
    /// <param name="data">The message payload.</param>
    /// <param name="priority">The priority to send the message at (see Docs/OFT.md §5-§6).</param>
    /// <param name="cancellationToken">A token used to cancel connecting or sending.</param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    Task Send(string host, int port, ReadOnlyMemory<byte> data, int priority = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to <paramref name="host"/>:<paramref name="port"/>, taking ownership of
    /// <paramref name="data"/> (see
    /// <see cref="IOftConnection.Send(IMemoryOwner{byte}, int, CancellationToken)"/>):
    /// reusing a cached connection if one already exists, or creating and caching a new one
    /// otherwise. The caller must not use or dispose <paramref name="data"/> after calling this.
    /// </summary>
    /// <param name="host">The remote host to send to.</param>
    /// <param name="port">The remote port to send to.</param>
    /// <param name="data">
    /// The message payload, e.g. from <see cref="MemoryPool{T}.Rent"/>. Ownership transfers to the
    /// underlying connection.
    /// </param>
    /// <param name="priority">The priority to send the message at (see Docs/OFT.md §5-§6).</param>
    /// <param name="cancellationToken">A token used to cancel connecting or sending.</param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    Task Send(string host, int port, IMemoryOwner<byte> data, int priority = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a TLS 1.3 <c>KeyUpdate</c> (see Docs/OFT.md §8) on every connection this peer
    /// currently holds, both outbound and inbound (a no-op for any held connection established with
    /// <see cref="OftPeerOptions.SecurityMode"/> set to <see cref="OftSecurityMode.Insecure"/> —
    /// there is no TLS session to rekey). Connections established after this call is issued are
    /// unaffected.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the requests.</param>
    /// <returns>A task that completes once every connection's local key update request has been sent.</returns>
    Task Rekey(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects every connection this peer currently holds, both outbound and inbound. The peer
    /// itself is left usable - a subsequent <see cref="Send(string, int, ReadOnlyMemory{byte}, int, CancellationToken)"/>
    /// call creates and caches a new outbound connection as usual, and, if listening, new inbound
    /// connections keep being accepted.
    /// </summary>
    /// <returns>A task that completes once every connection has disconnected.</returns>
    Task Disconnect();
}
