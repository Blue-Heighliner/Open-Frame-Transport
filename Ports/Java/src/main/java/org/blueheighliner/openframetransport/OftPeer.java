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
 * There is no way to enumerate or look up an individual connection this peer holds;
 * {@link #rekey()} and {@link #disconnect()} act on all of them at once.
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
     * The endpoint actually being listened on once {@link #open(InetSocketAddress)} has completed,
     * or {@code null} if the peer isn't currently listening.
     */
    InetSocketAddress getLocalEndpoint();

    /**
     * Called for every message received on any connection this peer holds, both inbound and
     * outbound, identifying which one via its {@link OftConnection} argument, or {@code null} if no
     * callback is currently assigned. There is only ever one callback at a time - assigning a new
     * value here always replaces any previous one, and the same buffering-until-first-non-null-
     * assignment guarantee {@link OftConnection#setReceivedHandler} itself makes applies here too
     * (see README.md). The {@link OftConnection} passed here is only for replying on the same
     * connection a message arrived on - this peer otherwise deliberately exposes no way to
     * enumerate, look up, or be notified about the individual connections it holds (e.g. no
     * disconnected notification): connection lifecycle is this peer's own implementation detail,
     * transparently managed (reconnecting, evicting, etc.) behind {@link #send}.
     */
    void setReceivedHandler(BiConsumer<OftConnection, byte[]> handler);

    /** The callback currently assigned via {@link #setReceivedHandler}, or {@code null} if none is. */
    BiConsumer<OftConnection, byte[]> getReceivedHandler();

    /**
     * Starts listening for inbound connections. A peer that never calls this only ever makes
     * outbound connections.
     *
     * @param listenEndpoint the local endpoint to listen for incoming TCP connections on
     */
    void open(InetSocketAddress listenEndpoint) throws IOException;

    /**
     * Stops listening for new inbound connections. Already-established connections are left open.
     * Not named {@code close()}, unlike its C#/C counterparts: that name is reserved here for
     * {@link AutoCloseable#close()}'s full-teardown semantics (see {@link #close()}).
     */
    void stop();

    /**
     * Sends a message to {@code host}:{@code port}, reusing a cached connection if one already
     * exists, or creating and caching a new one otherwise.
     *
     * @param host     the remote host to send to
     * @param port     the remote port to send to
     * @param data     the message payload
     * @param priority the priority to send the message at (see README.md &sect;5-&sect;6)
     * @return a handle that can be used to wait for delivery or cancel the message
     */
    OftSendHandle send(String host, int port, byte[] data, int priority) throws IOException;

    /**
     * Requests a TLS 1.3 {@code KeyUpdate} (see README.md &sect;8) on every connection this peer
     * currently holds, both outbound and inbound. Connections established after this call is
     * issued are unaffected.
     *
     * @return a future that completes once every connection's local key update request has been sent
     */
    CompletableFuture<Void> rekey();

    /**
     * Disconnects every connection this peer currently holds, both outbound and inbound. The peer
     * itself is left usable - a subsequent {@link #send} call creates and caches a new outbound
     * connection as usual, and, if listening, new inbound connections keep being accepted.
     */
    void disconnect();

    /** Stops listening (if applicable) and closes every connection this peer holds. */
    @Override
    void close();
}
