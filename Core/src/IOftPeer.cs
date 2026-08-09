namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// A peer-to-peer convenience layer over <see cref="IOftHoster"/>/<see cref="IOftListener"/> and
/// <see cref="IOftConnector"/>. Sending a message to a host/port transparently reuses an existing
/// connection or creates and caches a new one; idle, expired, or excess cached connections are
/// disconnected automatically, and connections with a configured
/// <see cref="OftConnectionOptions.RekeyInterval"/> rekey themselves automatically (see Docs/OFT.md §8).
/// A connection only ever becomes eligible for automatic disconnection once it has had no pending
/// data (see <see cref="IOftConnection.HasPendingData"/>) for a fixed 30-second grace period — not
/// configurable — giving the underlying TLS/TCP layers time to actually flush and acknowledge
/// everything after the last application-level message completes. Eviction itself (checking
/// connections against <see cref="OftPeerOptions.IdleTimeout"/>,
/// <see cref="OftPeerOptions.MaxConnectionLifetime"/>, and
/// <see cref="OftPeerOptions.MaxConnectionCount"/>) is likewise only ever run on a fixed,
/// non-configurable 30-second interval — so, combined with the grace period above, neither
/// <see cref="OftPeerOptions.IdleTimeout"/> nor <see cref="OftPeerOptions.MaxConnectionLifetime"/>
/// can take effect any sooner than roughly 30-60 seconds after it's reached, regardless of how much
/// shorter either is configured to be. There is no way to enumerate or
/// look up an individual connection this peer holds;
/// <see cref="Rekey"/> and <see cref="Drop"/> act on all of them at once.
/// <see cref="DisposeAsync"/> and <see cref="IDisposable.Dispose"/> both permanently
/// put this peer itself into a disconnected state — unlike <see cref="Drop"/>, which only
/// disconnects this peer's currently held connections and leaves the peer itself usable — after
/// which <see cref="IsConnected"/> is permanently <see langword="false"/> and every other member
/// below throws: <see cref="Listen"/>, <see cref="StopListening"/>, and <see cref="Drop"/> throw
/// <see cref="ObjectDisposedException"/>, while <see cref="Send(string, int, ReadOnlyMemory{byte}, int, object?, CancellationToken)"/>
/// and <see cref="Rekey"/> throw <see cref="OftDisconnectedException"/>. <see cref="IDisposable.Dispose"/>
/// does this immediately, without waiting for any background work to finish; call
/// <see cref="DisposeAsync"/> instead for a graceful, awaitable teardown that waits
/// for it.
/// </summary>
public interface IOftPeer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Whether this peer is still connected: <see langword="true"/> until
    /// <see cref="DisposeAsync"/> or <see cref="IDisposable.Dispose"/> is called,
    /// after which it is permanently <see langword="false"/>. Unlike
    /// <see cref="IOftConnection.IsConnected"/>, this is unaffected
    /// by any individual connection this peer holds disconnecting (locally via <see cref="Drop"/> or
    /// remotely) — connection lifecycle is this peer's own implementation detail (see
    /// <see cref="ReceivedHandler"/>'s own doc comment).
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// The endpoint actually being listened on once <see cref="Listen"/> has completed, or
    /// <see langword="null"/> if the peer isn't currently listening.
    /// </summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Called for every message received on any connection this peer holds, both inbound and
    /// outbound, with the identity of the connection it arrived on and ownership of its pooled
    /// payload — the callback must dispose the payload (returning its memory to its pool) once done
    /// with it, e.g. via a <see langword="using"/> statement. <see langword="null"/> if no callback
    /// is currently assigned. There is only ever one callback at a time — assigning a new value here
    /// always replaces any previous one, and the same buffering-until-first-non-null-assignment
    /// guarantee <see cref="IOftConnection.ReceivedHandler"/> itself makes applies here too (see
    /// README.md). This peer deliberately exposes no way to enumerate, look up, or be notified about
    /// the individual connections it holds beyond the identity passed here (e.g. no disconnected
    /// notification): connection lifecycle is this peer's own implementation detail, transparently
    /// managed (reconnecting, evicting, etc.) behind
    /// <see cref="Send(string, int, ReadOnlyMemory{byte}, int, object?, CancellationToken)"/>.
    /// </summary>
    Action<OftIdentity, IMemoryOwner<byte>>? ReceivedHandler { get; set; }

    /// <summary>
    /// Called whenever a message sent with a non-null <c>tag</c> (see
    /// <see cref="Send(string, int, ReadOnlyMemory{byte}, int, object?, CancellationToken)"/>)
    /// changes delivery status (see <see cref="OftDeliveryStatus"/> for the full lifecycle), with
    /// that same tag and its new status — deliberately without identifying which connection it was
    /// sent over, unlike <see cref="ReceivedHandler"/>: the caller already knows, since it's the same
    /// caller that made the <c>Send</c> call this is reporting on. Called multiple times per send,
    /// once per status it passes through. Never called for a message sent with a
    /// <see langword="null"/> tag. <see langword="null"/> if no callback is currently assigned. There
    /// is only ever one callback at a time — assigning a new value here always replaces any previous
    /// one. Unlike <see cref="ReceivedHandler"/>, this does <em>not</em> buffer a raise that happens
    /// before a callback is ever assigned — see
    /// <see cref="IOftConnection.DeliveryStatusHandler"/>'s own doc comment for why that's safe.
    /// </summary>
    Action<object, OftDeliveryStatus>? DeliveryStatusHandler { get; set; }

    /// <summary>
    /// Starts listening for inbound connections. A peer that never calls this only ever makes
    /// outbound connections.
    /// </summary>
    /// <param name="listenEndPoint">The local endpoint to listen for incoming TCP connections on.</param>
    /// <param name="cancellationToken">A token that stops listening when cancelled.</param>
    /// <exception cref="ArgumentException">
    /// <see cref="OftConnectionOptions.Certificate"/> was not set and
    /// <see cref="OftConnectionOptions.SecurityMode"/> requires one (see
    /// <see cref="OftSecurityMode.DualAuthentication"/> — the only authenticating mode a peer
    /// supports).
    /// </exception>
    /// <exception cref="ObjectDisposedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Listen(IPEndPoint listenEndPoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops listening for new inbound connections. Already-established connections are left open.
    /// </summary>
    /// <exception cref="ObjectDisposedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task StopListening();

    /// <summary>
    /// Sends a message to <paramref name="host"/>:<paramref name="port"/>, reusing a cached
    /// connection if one already exists, or creating and caching a new one otherwise.
    /// </summary>
    /// <param name="host">The remote host to send to.</param>
    /// <param name="port">The remote port to send to.</param>
    /// <param name="data">The message payload.</param>
    /// <param name="priority">The priority to send the message at (see Docs/OFT.md §5-§6).</param>
    /// <param name="tag">
    /// An opaque, application-controlled value attached to this send, so it can be referenced later —
    /// passed back to <see cref="DeliveryStatusHandler"/>, along with each status this send passes
    /// through (see <see cref="OftDeliveryStatus"/>), if non-null. <see langword="null"/> (the
    /// default) means this send never raises <see cref="DeliveryStatusHandler"/>.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel connecting or sending.</param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    /// <exception cref="OftDisconnectedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Send(string host, int port, ReadOnlyMemory<byte> data, int priority = 0, object? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to <paramref name="host"/>:<paramref name="port"/>, taking ownership of
    /// <paramref name="data"/> (see
    /// <see cref="IOftConnection.Send(IMemoryOwner{byte}, int, object?, CancellationToken)"/>):
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
    /// <param name="tag">
    /// An opaque, application-controlled value attached to this send, so it can be referenced later —
    /// passed back to <see cref="DeliveryStatusHandler"/>, along with each status this send passes
    /// through (see <see cref="OftDeliveryStatus"/>), if non-null. <see langword="null"/> (the
    /// default) means this send never raises <see cref="DeliveryStatusHandler"/>.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel connecting or sending.</param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    /// <exception cref="OftDisconnectedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Send(string host, int port, IMemoryOwner<byte> data, int priority = 0, object? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a TLS 1.3 <c>KeyUpdate</c> (see Docs/OFT.md §8) on every connection this peer
    /// currently holds, both outbound and inbound (a no-op for any held connection established with
    /// <see cref="OftConnectionOptions.SecurityMode"/> set to <see cref="OftSecurityMode.Trusted"/> —
    /// there is no TLS session to rekey). Connections established after this call is issued are
    /// unaffected.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the requests.</param>
    /// <returns>A task that completes once every connection's local key update request has been sent.</returns>
    /// <exception cref="OftDisconnectedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Rekey(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects every connection this peer currently holds, both outbound and inbound. Unlike
    /// <see cref="DisposeAsync"/>, this peer itself is left usable afterward - a
    /// subsequent <see cref="Send(string, int, ReadOnlyMemory{byte}, int, object?, CancellationToken)"/> call creates and
    /// caches a new outbound connection as usual, and, if listening, new inbound connections keep
    /// being accepted.
    /// </summary>
    /// <returns>A task that completes once every connection has disconnected.</returns>
    /// <exception cref="ObjectDisposedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Drop();

    /// <summary>
    /// Permanently puts this peer itself into a disconnected state: stops listening (if applicable),
    /// disconnects every connection it currently holds (both outbound and inbound), and waits for
    /// their background work to fully finish, for a graceful teardown. Equivalent to
    /// <see cref="IDisposable.Dispose"/> for the purpose of releasing this peer's resources - it is
    /// already disconnected and its resources already released by the time this returns - but,
    /// unlike <see cref="IDisposable.Dispose"/>, does not return until that background work has
    /// completely stopped. Safe to call more than once; every call after the first is a no-op.
    /// </summary>
    /// <returns>A task that completes once the peer has fully disconnected.</returns>
    new ValueTask DisposeAsync();
}
