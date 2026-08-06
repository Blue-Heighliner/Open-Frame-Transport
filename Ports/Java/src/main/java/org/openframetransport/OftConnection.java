package org.openframetransport;

import java.net.InetSocketAddress;
import java.time.Instant;
import java.util.concurrent.CompletableFuture;
import java.util.function.Consumer;

/**
 * A single established OFT connection, as described in README.md. Instances are produced by
 * {@link OftHoster}/{@link OftListener} (for inbound connections) and {@link OftConnector} (for
 * outbound connections), never constructed directly.
 */
public interface OftConnection extends AutoCloseable {
    /** The remote TCP endpoint of this connection. */
    InetSocketAddress getRemoteEndpoint();

    /** The opaque, application-controlled data the peer sent in its hail (see README.md &sect;3). */
    String getRemoteInfo();

    /** When the OFT handshake (TLS session plus hail exchange) completed. */
    Instant getConnectedAt();

    /** When the last packet was sent on this connection. */
    Instant getLastSentAt();

    /** When the last packet was received on this connection. */
    Instant getLastReceivedAt();

    /**
     * Whether this connection currently has any outbound message that hasn't been fully
     * acknowledged yet (queued, in flight, or awaiting its final {@code Receipt}), or any inbound
     * multi-packet message that has started arriving but hasn't been fully reassembled yet. An
     * {@link OftPeer} never automatically disconnects a connection while this is {@code true},
     * regardless of its idle timeout, maximum lifetime, or maximum connection count settings, so
     * that in-flight data is never silently dropped.
     */
    boolean hasPendingData();

    /** Registers a listener invoked whenever a complete application message has been received. */
    void addReceivedListener(OftReceivedListener listener);

    /** Unregisters a listener previously passed to {@link #addReceivedListener(OftReceivedListener)}. */
    void removeReceivedListener(OftReceivedListener listener);

    /**
     * Registers a listener invoked once, when the connection closes for any reason. The argument is
     * the exception that caused the connection to close, or {@code null} if it closed cleanly.
     */
    void addDisconnectedListener(Consumer<Throwable> listener);

    /** Unregisters a listener previously passed to {@link #addDisconnectedListener(Consumer)}. */
    void removeDisconnectedListener(Consumer<Throwable> listener);

    /**
     * Queues a message for sending at the given priority (see README.md &sect;5-&sect;7). Larger
     * priority values are sent first; a lower-priority message already being sent is transparently
     * interrupted and resumed later (README.md &sect;6).
     *
     * @param data     the message payload
     * @param priority the priority to send the message at; larger values are higher priority
     * @return a handle that can be used to wait for delivery or cancel the message
     */
    OftSendHandle send(byte[] data, int priority);

    /**
     * Requests a TLS 1.3 {@code KeyUpdate} on this connection (see README.md &sect;8): fresh
     * traffic keys for both directions, derived in place on the existing TLS session without a new
     * handshake or any interruption to application traffic. A no-op (returns an already-completed
     * future) if the connection was established with {@link OftSecurityMode#INSECURE} - there is no
     * TLS session to rekey.
     *
     * @return a future that completes once the local key update request has been sent
     */
    CompletableFuture<Void> rekey();

    /** Closes the connection. */
    void disconnect();

    /** Closes the connection and waits for its background threads to finish. */
    @Override
    void close();
}
