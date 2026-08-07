package org.blueheighliner.openframetransport;

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

    /**
     * Called whenever a complete application message has been received, or {@code null} if no
     * callback is currently assigned. There is only ever one callback at a time - assigning a new
     * value here always replaces any previous one. The first time this is ever assigned a non-null
     * value, it is synchronously delivered, in order, every message received before that assignment
     * (see README.md), since this connection may start processing inbound packets - and therefore
     * receiving messages - before a caller has had a chance to assign a callback. Assigning
     * {@code null} afterward simply discards any message received while no callback is assigned.
     */
    void setReceivedHandler(Consumer<byte[]> handler);

    /** The callback currently assigned via {@link #setReceivedHandler}, or {@code null} if none is. */
    Consumer<byte[]> getReceivedHandler();

    /**
     * Called once, when this connection closes for any reason, with the exception that caused it to
     * close, or {@code null} if it closed cleanly (e.g. because {@link #disconnect()} was called).
     * {@code null} if no callback is currently assigned. There is only ever one callback at a time -
     * assigning a new value here always replaces any previous one, and the same
     * buffering-until-first-non-null-assignment guarantee {@link #setReceivedHandler} itself makes
     * applies here too (see README.md).
     */
    void setDisconnectedHandler(Consumer<Throwable> handler);

    /** The callback currently assigned via {@link #setDisconnectedHandler}, or {@code null} if none is. */
    Consumer<Throwable> getDisconnectedHandler();

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
     * future) if the connection was established with {@link OftSecurityMode#TRUSTED} - there is no
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
