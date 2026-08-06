package org.openframetransport;

import java.net.InetSocketAddress;
import java.util.function.Consumer;

/**
 * Accepts inbound OFT connections on a TCP endpoint. Produced by {@link OftHoster#host}; there is
 * no way to stop listening short of {@link #close() closing} the listener entirely - call
 * {@link OftHoster#host} again for a fresh listener if needed. Closing a listener does not affect
 * connections it has already accepted; this type doesn't track them, so a caller that needs to
 * enumerate or bulk-close accepted connections must track them itself (e.g. via
 * {@link #addConnectedListener}).
 */
public interface OftListener extends AutoCloseable {
    /**
     * The endpoint being listened on. Useful for discovering which port was chosen when
     * {@code listenEndpoint} specified port 0.
     */
    InetSocketAddress getLocalEndpoint();

    /** Registers a listener invoked whenever a new inbound connection completes its handshake. */
    void addConnectedListener(Consumer<OftConnection> listener);

    /** Unregisters a listener previously passed to {@link #addConnectedListener(Consumer)}. */
    void removeConnectedListener(Consumer<OftConnection> listener);

    /** Stops listening for new inbound connections. Already-accepted connections are left open. */
    @Override
    void close();
}
