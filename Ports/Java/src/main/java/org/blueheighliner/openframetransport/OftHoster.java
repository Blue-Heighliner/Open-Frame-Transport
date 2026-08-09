package org.blueheighliner.openframetransport;

import java.net.InetSocketAddress;
import java.util.concurrent.CompletableFuture;

/**
 * Hosts a listener on a TCP endpoint for inbound OFT connections. Stateless: a single instance can
 * host any number of independent listeners, each with its own options.
 */
public interface OftHoster {
    /** Creates a new hoster. */
    static OftHoster create() {
        return new DefaultOftHoster();
    }

    /**
     * Starts listening on {@code listenEndpoint} using default options. Binding, and (for
     * {@link OftSecurityMode#SECURE}) generating the listener's ephemeral identity, run on a
     * dedicated background thread (see {@link OftBlocking}), not the calling thread.
     *
     * @param listenEndpoint the local endpoint to listen for incoming TCP connections on
     * @return a future that completes with the new listener, or completes exceptionally if binding
     * fails
     */
    CompletableFuture<OftListener> host(InetSocketAddress listenEndpoint);

    /**
     * Starts listening on {@code listenEndpoint}, accepting every connection with {@code options}.
     * Binding, and (for {@link OftSecurityMode#SECURE}) generating the listener's ephemeral
     * identity, run on a dedicated background thread (see {@link OftBlocking}), not the calling
     * thread.
     *
     * @param listenEndpoint the local endpoint to listen for incoming TCP connections on
     * @param options        the options used to accept every connection
     * @return a future that completes with the new listener, or completes exceptionally if binding
     * fails
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#SERVER_AUTHENTICATION} or
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION} - thrown
     *                                   synchronously rather than deferred to the returned future,
     *                                   since it's an immediate argument-validation failure rather
     *                                   than something that requires binding
     */
    CompletableFuture<OftListener> host(InetSocketAddress listenEndpoint, OftHostOptions options);

    /**
     * Starts listening on {@code port}, on any local address, using default options. Equivalent to
     * calling {@link #host(InetSocketAddress)} with {@code new InetSocketAddress(port)}.
     *
     * @param port the local port to listen for incoming TCP connections on
     * @return a future that completes with the new listener, or completes exceptionally if binding
     * fails
     */
    default CompletableFuture<OftListener> host(int port) {
        return this.host(new InetSocketAddress(port));
    }

    /**
     * Starts listening on {@code port}, on any local address, accepting every connection with
     * {@code options}. Equivalent to calling {@link #host(InetSocketAddress, OftHostOptions)} with
     * {@code new InetSocketAddress(port)}.
     *
     * @param port    the local port to listen for incoming TCP connections on
     * @param options the options used to accept every connection
     * @return a future that completes with the new listener, or completes exceptionally if binding
     * fails
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#SERVER_AUTHENTICATION} or
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION} - thrown
     *                                   synchronously rather than deferred to the returned future,
     *                                   since it's an immediate argument-validation failure rather
     *                                   than something that requires binding
     */
    default CompletableFuture<OftListener> host(int port, OftHostOptions options) {
        return this.host(new InetSocketAddress(port), options);
    }
}
