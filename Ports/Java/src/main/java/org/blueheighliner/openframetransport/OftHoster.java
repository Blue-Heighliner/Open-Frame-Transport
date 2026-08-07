package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;

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
     * Starts listening on {@code listenEndpoint} using default options.
     *
     * @param listenEndpoint the local endpoint to listen for incoming TCP connections on
     * @return the new listener
     */
    OftListener host(InetSocketAddress listenEndpoint) throws IOException;

    /**
     * Starts listening on {@code listenEndpoint}, accepting every connection with {@code options}.
     *
     * @param listenEndpoint the local endpoint to listen for incoming TCP connections on
     * @param options        the options used to accept every connection
     * @return the new listener
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#SERVER_AUTHENTICATION} or
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION}
     */
    OftListener host(InetSocketAddress listenEndpoint, OftHostOptions options) throws IOException;

    /**
     * Starts listening on {@code port}, on any local address, using default options. Equivalent to
     * calling {@link #host(InetSocketAddress)} with {@code new InetSocketAddress(port)}.
     *
     * @param port the local port to listen for incoming TCP connections on
     * @return the new listener
     */
    default OftListener host(int port) throws IOException {
        return this.host(new InetSocketAddress(port));
    }

    /**
     * Starts listening on {@code port}, on any local address, accepting every connection with
     * {@code options}. Equivalent to calling {@link #host(InetSocketAddress, OftHostOptions)} with
     * {@code new InetSocketAddress(port)}.
     *
     * @param port    the local port to listen for incoming TCP connections on
     * @param options the options used to accept every connection
     * @return the new listener
     * @throws IllegalArgumentException {@code options.sslContext()} is {@code null} and
     *                                   {@code options.securityMode()} is
     *                                   {@link OftSecurityMode#SERVER_AUTHENTICATION} or
     *                                   {@link OftSecurityMode#DUAL_AUTHENTICATION}
     */
    default OftListener host(int port, OftHostOptions options) throws IOException {
        return this.host(new InetSocketAddress(port), options);
    }
}
