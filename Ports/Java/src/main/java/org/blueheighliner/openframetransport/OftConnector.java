package org.blueheighliner.openframetransport;

import java.util.concurrent.CompletableFuture;

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
     * The dial, handshake, and hail exchange all run on a dedicated background thread (see
     * {@link OftBlocking}), not the calling thread.
     *
     * @param host the remote host to connect to
     * @param port the remote port to connect to
     * @return a future that completes with the established connection, or completes exceptionally
     * if dialing, the TLS handshake, or the hail exchange fails
     */
    CompletableFuture<OftConnection> connect(String host, int port);

    /**
     * Dials {@code host}:{@code port}, performs the TLS handshake and hail exchange (see
     * README.md &sect;1-&sect;3), and returns the resulting established connection. The dial,
     * handshake, and hail exchange all run on a dedicated background thread (see
     * {@link OftBlocking}), not the calling thread.
     *
     * @param host    the remote host to connect to
     * @param port    the remote port to connect to
     * @param options the options used for this connection
     * @return a future that completes with the established connection, or completes exceptionally
     * if dialing, the TLS handshake, or the hail exchange fails
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION} - thrown
     *                                   synchronously rather than deferred to the returned future,
     *                                   since it's an immediate argument-validation failure rather
     *                                   than something that requires dialing out; under
     *                                   {@link OftSecurityMode#SERVER_AUTHENTICATION}, a {@code null}
     *                                   {@code sslContext} instead falls back to the JVM's default
     *                                   trust store
     */
    CompletableFuture<OftConnection> connect(String host, int port, OftConnectOptions options);
}
