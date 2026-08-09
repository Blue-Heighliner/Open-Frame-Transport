namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// A single established OFT connection, as described in README.md. Instances are produced by
/// <see cref="IOftHoster"/>/<see cref="IOftListener"/> (for inbound connections) and
/// <see cref="IOftConnector"/> (for outbound connections), never constructed directly.
/// <see cref="IDisposable.Dispose"/> immediately terminates the connection and releases its
/// resources, without waiting for its background work to finish; call
/// <see cref="DisposeAsync"/> instead for a graceful, awaitable teardown that
/// waits for it.
/// </summary>
public interface IOftConnection : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// This connection's remote identity: its TCP endpoint, its TLS certificate (if any was
    /// presented), and the opaque, application-controlled data it sent in its hail (see
    /// Docs/OFT.md §3).
    /// </summary>
    OftIdentity Identity { get; }

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
    /// Whether this connection is still connected: <see langword="true"/> until it closes, for any
    /// reason — a local <see cref="DisposeAsync"/>/<see cref="IDisposable.Dispose"/>
    /// call, the remote side disconnecting, or an unrecoverable error (e.g. a liveness timeout) — after which it is
    /// permanently <see langword="false"/>. <see cref="Send(ReadOnlyMemory{byte}, int, object?, CancellationToken)"/>
    /// and <see cref="Rekey"/> both throw <see cref="OftDisconnectedException"/> once this is
    /// <see langword="false"/>.
    /// </summary>
    bool IsConnected { get; }

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
    /// Called whenever a complete application message has been received, with ownership of its
    /// pooled payload — the callback must dispose it (returning its memory to its pool) once done
    /// with it, e.g. via a <see langword="using"/> statement. <see langword="null"/> if no callback
    /// is currently assigned. There is only ever one callback at a time — assigning a new value here
    /// always replaces any previous one. The first time this is ever assigned a non-null value, it is
    /// synchronously delivered, in order, every message received before that assignment (see
    /// README.md), since this connection may start processing inbound packets — and therefore
    /// receiving messages — before a caller has had a chance to assign a callback. Assigning
    /// <see langword="null"/> afterward simply discards (without re-buffering, but still disposing)
    /// any message received while no callback is assigned.
    /// </summary>
    Action<IMemoryOwner<byte>>? ReceivedHandler { get; set; }

    /// <summary>
    /// Called once, when this connection closes for any reason, with the exception that caused it to
    /// close, or <see langword="null"/> if it closed cleanly (e.g. because
    /// <see cref="DisposeAsync"/> was called). <see langword="null"/> if no callback
    /// is currently assigned. There is only ever
    /// one callback at a time — assigning a new value here always replaces any previous one, and the
    /// same buffering-until-first-non-null-assignment guarantee <see cref="ReceivedHandler"/> itself
    /// makes applies here too (see README.md).
    /// </summary>
    Action<Exception?>? DisconnectedHandler { get; set; }

    /// <summary>
    /// Called whenever a message sent with a non-null <c>tag</c> (see
    /// <see cref="Send(ReadOnlyMemory{byte}, int, object?, CancellationToken)"/>) changes delivery
    /// status (see <see cref="OftDeliveryStatus"/> for the full lifecycle), with that same tag and
    /// its new status. Called multiple times per send, once per status it passes through. Never
    /// called for a message sent with a <see langword="null"/> tag. <see langword="null"/> if no
    /// callback is currently assigned. There is only ever one callback at a time — assigning a new
    /// value here always replaces any previous one. Unlike <see cref="ReceivedHandler"/>/
    /// <see cref="DisconnectedHandler"/>, this does <em>not</em> buffer a raise that happens before a
    /// callback is ever assigned: this can only ever be raised in response to a
    /// <see cref="Send(ReadOnlyMemory{byte}, int, object?, CancellationToken)"/> call the caller
    /// itself makes, so there is no message-loss race to guard against — assign this before making
    /// that call if you want to observe its status changes.
    /// </summary>
    Action<object, OftDeliveryStatus>? DeliveryStatusHandler { get; set; }

    /// <summary>
    /// Queues a message for sending at the given priority (see Docs/OFT.md §5-§7). Larger priority
    /// values are sent first; a lower-priority message already being sent is transparently
    /// interrupted and resumed later (Docs/OFT.md §6).
    /// </summary>
    /// <param name="data">The message payload.</param>
    /// <param name="priority">
    /// The priority to send the message at. Larger values are higher priority. Defaults to 0.
    /// </param>
    /// <param name="tag">
    /// An opaque, application-controlled value attached to this send, so it can be referenced later —
    /// passed back to <see cref="DeliveryStatusHandler"/>, along with each status this send passes
    /// through (see <see cref="OftDeliveryStatus"/>), if non-null. <see langword="null"/> (the
    /// default) means this send never raises <see cref="DeliveryStatusHandler"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that, when cancelled, abandons the message: immediately if it has not yet started
    /// sending, or by sending a <c>Cancellation</c> packet if it has (see Docs/OFT.md §7).
    /// </param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    /// <exception cref="OftDisconnectedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Send(ReadOnlyMemory<byte> data, int priority = 0, object? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a message for sending, taking ownership of <paramref name="data"/>: this connection
    /// disposes it (returning its pooled memory, if any) once the message has been fully delivered,
    /// cancelled, or the connection closes, whichever happens first — the caller must not use or
    /// dispose <paramref name="data"/> after calling this. Since this connection owns the memory for
    /// the message's whole lifetime, it is never copied, unlike
    /// <see cref="Send(ReadOnlyMemory{byte}, int, object?, CancellationToken)"/>.
    /// </summary>
    /// <param name="data">
    /// The message payload, e.g. from <see cref="MemoryPool{T}.Rent"/>. Ownership transfers to this
    /// connection.
    /// </param>
    /// <param name="priority">
    /// The priority to send the message at. Larger values are higher priority. Defaults to 0.
    /// </param>
    /// <param name="tag">
    /// An opaque, application-controlled value attached to this send, so it can be referenced later —
    /// passed back to <see cref="DeliveryStatusHandler"/>, along with each status this send passes
    /// through (see <see cref="OftDeliveryStatus"/>), if non-null. <see langword="null"/> (the
    /// default) means this send never raises <see cref="DeliveryStatusHandler"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that, when cancelled, abandons the message: immediately if it has not yet started
    /// sending, or by sending a <c>Cancellation</c> packet if it has (see Docs/OFT.md §7).
    /// </param>
    /// <returns>A task that completes once the message has been fully delivered.</returns>
    /// <exception cref="OftDisconnectedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Send(IMemoryOwner<byte> data, int priority = 0, object? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a TLS 1.3 <c>KeyUpdate</c> (see Docs/OFT.md §8): fresh traffic keys for both
    /// directions, derived in place on the existing TLS session without a new handshake or any
    /// interruption to application traffic. A no-op if the connection was established with
    /// <see cref="OftConnectionOptions.SecurityMode"/> set to <see cref="OftSecurityMode.Trusted"/>
    /// — there is no TLS session to rekey.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the request before it's sent.</param>
    /// <returns>A task that completes once the local key update request has been sent.</returns>
    /// <exception cref="OftDisconnectedException"><see cref="IsConnected"/> is <see langword="false"/>.</exception>
    Task Rekey(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the connection and waits for its background work (its receive and send loops) to fully
    /// finish, for a graceful teardown. Equivalent to <see cref="IDisposable.Dispose"/> for the
    /// purpose of releasing this connection's resources — the connection is already closed and its
    /// resources already released by the time this returns — but, unlike <see cref="IDisposable.Dispose"/>,
    /// does not return until that background work has completely stopped.
    /// </summary>
    /// <returns>A task that completes once the connection has fully closed.</returns>
    new ValueTask DisposeAsync();
}
