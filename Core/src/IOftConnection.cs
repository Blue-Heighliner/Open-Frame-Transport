namespace OpenFrameTransport;

/// <summary>
/// A single established OFT connection, as described in README.md. Instances are produced by
/// <see cref="IOftHoster"/>/<see cref="IOftListener"/> (for inbound connections) and
/// <see cref="IOftConnector"/> (for outbound connections), never constructed directly.
/// </summary>
public interface IOftConnection : IAsyncDisposable
{
    /// <summary>
    /// The remote TCP endpoint of this connection.
    /// </summary>
    IPEndPoint RemoteEndPoint { get; }

    /// <summary>
    /// The opaque, application-controlled data the peer sent in its hail (see Docs/OFT.md §3).
    /// </summary>
    string RemoteInfo { get; }

    /// <summary>
    /// When the OFT handshake (TLS session plus hail exchange) completed.
    /// </summary>
    DateTimeOffset ConnectedAt { get; }

    /// <summary>
    /// When the last packet was sent on the connection.
    /// </summary>
    DateTimeOffset LastSentAt { get; }

    /// <summary>
    /// When the last packet was received on the connection.
    /// </summary>
    DateTimeOffset LastReceivedAt { get; }

    /// <summary>
    /// Whether this connection currently has any outbound message that hasn't been fully
    /// acknowledged yet (queued, in flight, or awaiting its final <c>Receipt</c>), or any inbound
    /// multi-packet message that has started arriving but hasn't been fully reassembled yet. An
    /// <see cref="IOftPeer"/> never automatically disconnects a connection while this is
    /// <see langword="true"/>, regardless of its idle timeout, maximum lifetime, or maximum
    /// connection count settings, so that in-flight data is never silently dropped.
    /// </summary>
    bool HasPendingData { get; }

    /// <summary>
    /// Raised whenever a complete application message has been received. Nothing raised before the
    /// first subscriber ever attaches is lost — it's delivered to that first subscriber as soon as
    /// it attaches (see README.md and <c>OftBufferedEvent</c>), since this connection may start
    /// processing inbound packets before a caller has had a chance to subscribe.
    /// </summary>
    event EventHandler<OftReceivedEventArgs>? Received;

    /// <summary>
    /// Raised once, when the connection closes for any reason. Buffered the same way
    /// <see cref="Received"/> is — see its doc comment.
    /// </summary>
    event EventHandler<OftDisconnectedEventArgs>? Disconnected;

    /// <summary>
    /// Queues a message for sending at the given priority (see Docs/OFT.md §5-§7). Larger priority
    /// values are sent first; a lower-priority message already being sent is transparently
    /// interrupted and resumed later (Docs/OFT.md §6).
    /// </summary>
    /// <param name="data">The message payload.</param>
    /// <param name="priority">
    /// The priority to send the message at. Larger values are higher priority. Defaults to 0.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that, when cancelled, abandons the message: immediately if it has not yet started
    /// sending, or by sending a <c>Cancellation</c> packet if it has (see Docs/OFT.md §7).
    /// </param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    Task Send(ReadOnlyMemory<byte> data, int priority = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a message for sending, taking ownership of <paramref name="data"/>: this connection
    /// disposes it (returning its pooled memory, if any) once the message has been fully delivered,
    /// cancelled, or the connection closes, whichever happens first — the caller must not use or
    /// dispose <paramref name="data"/> after calling this. Since this connection owns the memory for
    /// the message's whole lifetime, it is never copied, unlike
    /// <see cref="Send(ReadOnlyMemory{byte}, int, CancellationToken)"/>.
    /// </summary>
    /// <param name="data">
    /// The message payload, e.g. from <see cref="MemoryPool{T}.Rent"/>. Ownership transfers to this
    /// connection.
    /// </param>
    /// <param name="priority">
    /// The priority to send the message at. Larger values are higher priority. Defaults to 0.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that, when cancelled, abandons the message: immediately if it has not yet started
    /// sending, or by sending a <c>Cancellation</c> packet if it has (see Docs/OFT.md §7).
    /// </param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    Task Send(IMemoryOwner<byte> data, int priority = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a TLS 1.3 <c>KeyUpdate</c> (see Docs/OFT.md §8): fresh traffic keys for both
    /// directions, derived in place on the existing TLS session without a new handshake or any
    /// interruption to application traffic. A no-op if the connection was established with
    /// <see cref="OftConnectionOptions.SecurityMode"/> set to <see cref="OftSecurityMode.Insecure"/>
    /// — there is no TLS session to rekey.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the request before it's sent.</param>
    /// <returns>A task that completes once the local key update request has been sent.</returns>
    Task Rekey(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the connection.
    /// </summary>
    Task Disconnect();
}
