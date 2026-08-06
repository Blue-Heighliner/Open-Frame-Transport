package org.openframetransport;

import java.io.IOException;
import java.util.function.Consumer;

/**
 * Connects out to remote OFT endpoints. Stateless: a single instance can be used to open any
 * number of independent connections, each with its own options; it does not track or own the
 * connections it creates, so the caller is responsible for closing each one.
 */
public interface OftConnector {
    /** Creates a new connector. */
    static OftConnector create() {
        return new DefaultOftConnector();
    }

    /**
     * Dials {@code host}:{@code port} using default options, performs the TLS handshake and hail
     * exchange (see README.md &sect;1-&sect;3), and returns the resulting established connection.
     *
     * @param host the remote host to connect to
     * @param port the remote port to connect to
     * @return the established connection
     */
    OftConnection connect(String host, int port) throws IOException;

    /**
     * Dials {@code host}:{@code port} using default options, performs the TLS handshake and hail
     * exchange (see README.md &sect;1-&sect;3), and returns the resulting established connection.
     *
     * @param host          the remote host to connect to
     * @param port          the remote port to connect to
     * @param onEstablished invoked synchronously with the established connection before it starts
     *                      processing inbound packets - the only way to guarantee attaching an
     *                      {@link OftReceivedListener} (or other listener) can never race the
     *                      connection's own receive loop, which otherwise could deliver and
     *                      discard (for lack of a subscriber) the peer's first message before this
     *                      method returns and the caller gets a chance to subscribe on the
     *                      returned connection instead
     * @return the established connection
     */
    OftConnection connect(String host, int port, Consumer<OftConnection> onEstablished) throws IOException;

    /**
     * Dials {@code host}:{@code port}, performs the TLS handshake and hail exchange (see
     * README.md &sect;1-&sect;3), and returns the resulting established connection.
     *
     * @param host    the remote host to connect to
     * @param port    the remote port to connect to
     * @param options the options used for this connection
     * @return the established connection
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#AUTHENTICATION} or
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION}
     */
    OftConnection connect(String host, int port, OftConnectOptions options) throws IOException;

    /**
     * Dials {@code host}:{@code port}, performs the TLS handshake and hail exchange (see
     * README.md &sect;1-&sect;3), and returns the resulting established connection.
     *
     * @param host          the remote host to connect to
     * @param port          the remote port to connect to
     * @param options       the options used for this connection
     * @param onEstablished invoked synchronously with the established connection before it starts
     *                      processing inbound packets - see
     *                      {@link #connect(String, int, Consumer)}
     * @return the established connection
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#AUTHENTICATION} or
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION}
     */
    OftConnection connect(String host, int port, OftConnectOptions options, Consumer<OftConnection> onEstablished) throws IOException;
}
