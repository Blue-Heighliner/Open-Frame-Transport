package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.util.concurrent.CompletableFuture;
import java.util.function.BiConsumer;

/**
 * A peer-to-peer convenience layer over {@link OftHoster}/{@link OftListener} and
 * {@link OftConnector}. Sending a message to a host/port transparently reuses an existing
 * connection or creates and caches a new one; idle, expired, or excess cached connections are
 * disconnected automatically, and connections with a configured
 * {@link OftPeerOptions#rekeyInterval()} rekey themselves automatically (see README.md &sect;8).
 * A connection only ever becomes eligible for automatic disconnection once it has had no pending
 * data (see {@link OftConnection#hasPendingData()}) for a fixed 30-second grace period - not
 * configurable - giving the underlying TLS/TCP layers time to actually flush and acknowledge
 * everything after the last application-level message completes. Eviction itself (checking
 * connections against {@link OftPeerOptions#idleTimeout()}, {@link OftPeerOptions#maxConnectionLifetime()},
 * and {@link OftPeerOptions#maxConnectionCount()}) is likewise only ever run on a fixed,
 * non-configurable 30-second interval - so, combined with the grace period above, neither
 * {@link OftPeerOptions#idleTimeout()} nor {@link OftPeerOptions#maxConnectionLifetime()} can take
 * effect any sooner than roughly 30-60 seconds after it's reached, regardless of how much shorter
 * either is configured to be. There is no way to enumerate or
 * look up an individual connection this peer holds;
 * {@link #rekey()} and {@link #drop()} act on all of them at once. {@link #close()} permanently
 * puts this peer itself into a disconnected state - unlike {@link #drop()}, which only disconnects
 * this peer's currently held connections and leaves the peer itself usable - after which
 * {@link #isConnected()} is permanently {@code false} and every other member below throws:
 * {@link #listen}, {@link #stopListening()}, and {@link #drop()} throw {@link IllegalStateException}, while
 * {@link #send} and {@link #rekey()} throw {@link OftDisconnectedException}.
 */
public interface OftPeer extends AutoCloseable {
    /**
     * Creates a peer using the given options, building the connector and hoster it delegates to.
     *
     * @param options the peer's options
     * @return the new peer
     * @throws IllegalArgumentException {@code options.securityMode()} is
     *                                  {@link OftSecurityMode#SERVER_AUTHENTICATION} — not a valid
     *                                  mode for a peer, which has no client/server delineation and
     *                                  so cannot express a one-sided authentication requirement (use
     *                                  {@link OftSecurityMode#DUAL_AUTHENTICATION} instead)
     */
    static OftPeer create(OftPeerOptions options) {
        if (options.securityMode() == OftSecurityMode.SERVER_AUTHENTICATION) {
            throw new IllegalArgumentException(
                    "SERVER_AUTHENTICATION is not a valid securityMode for an OftPeer: a peer has no client/server "
                            + "delineation, so it cannot express a one-sided authentication requirement. Use "
                            + "DUAL_AUTHENTICATION instead.");
        }

        return new DefaultOftPeer(options);
    }

    /**
     * Whether this peer is still connected: {@code true} until {@link #close()} is called, after
     * which it is permanently {@code false}. Unlike {@link OftConnection#isConnected()}, this is
     * unaffected by any individual connection this peer holds disconnecting (locally via
     * {@link #drop()} or remotely) - connection lifecycle is this peer's own implementation detail
     * (see {@link #setReceivedHandler}'s own doc comment).
     */
    boolean isConnected();

    /**
     * The endpoint actually being listened on once {@link #listen(InetSocketAddress)} has completed,
     * or {@code null} if the peer isn't currently listening.
     */
    InetSocketAddress getLocalEndpoint();

    /**
     * Called for every message received on any connection this peer holds, both inbound and
     * outbound, with the identity of the connection it arrived on and its payload, or {@code null} if
     * no callback is currently assigned. There is only ever one callback at a time - assigning a new
     * value here always replaces any previous one, and the same buffering-until-first-non-null-assignment
     * guarantee {@link OftConnection#setReceivedHandler} itself makes applies here too (see
     * README.md). This peer deliberately exposes no way to enumerate, look up, or be notified about
     * the individual connections it holds beyond the identity passed here (e.g. no disconnected
     * notification): connection lifecycle is this peer's own implementation detail, transparently
     * managed (reconnecting, evicting, etc.) behind {@link #send}.
     */
    void setReceivedHandler(BiConsumer<OftIdentity, byte[]> handler);

    /** The callback currently assigned via {@link #setReceivedHandler}, or {@code null} if none is. */
    BiConsumer<OftIdentity, byte[]> getReceivedHandler();

    /**
     * Called whenever a message sent with a non-null {@code tag} (see {@link #send}) has been fully
     * delivered and acknowledged (see {@link OftConnection#setAcknowledgedHandler}), with the identity
     * of the connection it was sent over and that same tag. Never called for a message sent with a
     * {@code null} tag, or for one that was cancelled rather than delivered. {@code null} if no
     * callback is currently assigned. There is only ever one callback at a time - assigning a new
     * value here always replaces any previous one. Unlike {@link #setReceivedHandler}, this does
     * <em>not</em> buffer a raise that happens before a callback is ever assigned - see
     * {@link OftConnection#setAcknowledgedHandler}'s own doc comment for why that's safe.
     */
    void setAcknowledgedHandler(BiConsumer<OftIdentity, Object> handler);

    /** The callback currently assigned via {@link #setAcknowledgedHandler}, or {@code null} if none is. */
    BiConsumer<OftIdentity, Object> getAcknowledgedHandler();

    /**
     * Starts listening for inbound connections. A peer that never calls this only ever makes
     * outbound connections. Binding runs on a dedicated background thread (see
     * {@link OftHoster#host(InetSocketAddress, OftHostOptions)}), not the calling thread.
     *
     * @param listenEndpoint the local endpoint to listen for incoming TCP connections on
     * @return a future that completes once this peer is listening, or completes exceptionally if
     * binding fails
     * @throws IllegalStateException {@link #isConnected()} is {@code false} - thrown synchronously
     *                                rather than deferred to the returned future, since it's an
     *                                immediate state check rather than something that requires
     *                                binding
     */
    CompletableFuture<Void> listen(InetSocketAddress listenEndpoint);

    /**
     * Stops listening for new inbound connections. Already-established connections are left open.
     * Not named {@code close()}, matching C#'s {@code IOftPeer.StopListening()} and C's
     * {@code oft_peer_stop_listening()}: that name is reserved here, as in both, for
     * {@link AutoCloseable#close()}'s full-teardown semantics (see {@link #close()}).
     *
     * @return a future that completes once this peer has stopped listening
     * @throws IllegalStateException {@link #isConnected()} is {@code false}
     */
    CompletableFuture<Void> stopListening();

    /**
     * Sends a message to {@code host}:{@code port}, reusing a cached connection if one already
     * exists, or creating and caching a new one otherwise.
     *
     * @param host     the remote host to send to
     * @param port     the remote port to send to
     * @param data     the message payload
     * @param priority the priority to send the message at (see README.md &sect;5-&sect;6)
     * @param tag      an opaque, application-controlled value attached to this send, so it can be
     *                 referenced later - passed back to {@link #setAcknowledgedHandler}'s callback,
     *                 along with the identity of the connection it was sent over, once this message is
     *                 fully delivered and acknowledged, if non-null; {@code null} means this send
     *                 never raises it
     * @return a handle that can be used to wait for delivery or cancel the message
     * @throws OftDisconnectedException {@link #isConnected()} is {@code false}
     */
    OftSendHandle send(String host, int port, byte[] data, int priority, Object tag) throws IOException;

    /**
     * Requests a TLS 1.3 {@code KeyUpdate} (see README.md &sect;8) on every connection this peer
     * currently holds, both outbound and inbound. Connections established after this call is
     * issued are unaffected.
     *
     * @return a future that completes once every connection's local key update request has been sent
     * @throws OftDisconnectedException {@link #isConnected()} is {@code false}
     */
    CompletableFuture<Void> rekey();

    /**
     * Disconnects every connection this peer currently holds, both outbound and inbound. Unlike
     * {@link #close()}, this peer itself is left usable afterward - a subsequent {@link #send} call
     * creates and caches a new outbound connection as usual, and, if listening, new inbound
     * connections keep being accepted.
     *
     * @return a future that completes once every held connection's teardown has been requested
     * @throws IllegalStateException {@link #isConnected()} is {@code false}
     */
    CompletableFuture<Void> drop();

    /**
     * Permanently puts this peer itself into a disconnected state: stops listening (if applicable)
     * and disconnects every connection it currently holds, both outbound and inbound. Safe to call
     * more than once; every call after the first is a no-op.
     */
    @Override
    void close();
}
