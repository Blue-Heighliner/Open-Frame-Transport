package org.blueheighliner.openframetransport;

import java.net.InetSocketAddress;
import java.util.function.Consumer;

/**
 * Accepts inbound OFT connections on a TCP endpoint. Produced by {@link OftHoster#host}; there is
 * no way to stop listening short of {@link #close() closing} the listener entirely - call
 * {@link OftHoster#host} again for a fresh listener if needed. Closing a listener does not affect
 * connections it has already accepted; this type doesn't track them, so a caller that needs to
 * enumerate or bulk-close accepted connections must track them itself (e.g. via
 * {@link #setConnectedHandler}).
 */
public interface OftListener extends AutoCloseable {
    /**
     * The endpoint being listened on. Useful for discovering which port was chosen when
     * {@code listenEndpoint} specified port 0.
     */
    InetSocketAddress getLocalEndpoint();

    /**
     * Called whenever a new inbound connection completes its TLS handshake and hail exchange, or
     * {@code null} if no callback is currently assigned. There is only ever one callback at a time -
     * assigning a new value here always replaces any previous one. The first time this is ever
     * assigned a non-null value, it is synchronously delivered, in order, every connection accepted
     * before that assignment (see README.md), since this listener may accept and establish
     * connections before a caller has had a chance to assign a callback. Assigning {@code null}
     * afterward simply discards any connection accepted while no callback is assigned (it is not
     * automatically closed, unlike a discarded received message - the caller may still reach it
     * later, e.g. by enumerating a peer's tracked connections).
     */
    void setConnectedHandler(Consumer<OftConnection> handler);

    /** The callback currently assigned via {@link #setConnectedHandler}, or {@code null} if none is. */
    Consumer<OftConnection> getConnectedHandler();

    /** Stops listening for new inbound connections. Already-accepted connections are left open. */
    @Override
    void close();
}
