package org.blueheighliner.openframetransport;

import javax.net.ssl.SSLContext;
import java.time.Duration;
import java.util.Objects;

/**
 * Options for an {@link OftPeer}.
 */
public final class OftPeerOptions {
    private final String info;
    private final SSLContext sslContext;
    private final OftConnectionValidationCallback connectionValidation;
    private final int maxPacketDataSize;
    private final Duration rekeyInterval;
    private final OftSecurityMode securityMode;
    private final Duration pollInterval;
    private final Duration pollTimeout;
    private final Duration idleTimeout;
    private final Duration maxConnectionLifetime;
    private final int maxConnectionCount;

    private OftPeerOptions(Builder builder) {
        this.info = Objects.requireNonNull(builder.info, "info");
        this.securityMode = Objects.requireNonNull(builder.securityMode, "securityMode");
        this.sslContext = builder.sslContext;
        this.connectionValidation = builder.connectionValidation;
        this.maxPacketDataSize = builder.maxPacketDataSize;
        this.rekeyInterval = builder.rekeyInterval;
        this.pollInterval = builder.pollInterval;
        this.pollTimeout = builder.pollTimeout;
        this.idleTimeout = builder.idleTimeout;
        this.maxConnectionLifetime = builder.maxConnectionLifetime;
        this.maxConnectionCount = builder.maxConnectionCount;
    }

    /** Opaque, application-controlled data sent to every peer in this side's hail (see README.md &sect;3). */
    public String info() {
        return this.info;
    }

    /**
     * The {@link SSLContext} this peer authenticates itself with, for both inbound and outbound
     * connections. Required (non-{@code null}) for {@link OftSecurityMode#DUAL_AUTHENTICATION} (the
     * only authenticating mode a peer supports — see {@link OftSecurityMode#SERVER_AUTHENTICATION});
     * unused under {@link OftSecurityMode#SECURE} and {@link OftSecurityMode#TRUSTED}.
     */
    public SSLContext sslContext() {
        return this.sslContext;
    }

    /**
     * An optional callback used to validate a fully-established connection, invoked once the OFT
     * hail exchange completes (see README.md &sect;3), for every {@link #securityMode()} - unlike
     * the trust manager configured on {@link #sslContext()}, which only runs during the TLS
     * handshake. When {@code null} (the default), every connection is accepted; otherwise,
     * establishing the connection fails if the callback returns {@code false}.
     */
    public OftConnectionValidationCallback connectionValidation() {
        return this.connectionValidation;
    }

    /** The maximum number of payload bytes carried in a single packet's data field. */
    public int maxPacketDataSize() {
        return this.maxPacketDataSize;
    }

    /**
     * When set, every connection automatically rekeys its TLS session on this interval. Ignored
     * when {@link #securityMode()} is {@link OftSecurityMode#TRUSTED}.
     */
    public Duration rekeyInterval() {
        return this.rekeyInterval;
    }

    /**
     * The security mode connections are established under (see README.md &sect;9).
     * {@link OftSecurityMode#SERVER_AUTHENTICATION} is not a valid value here — see its own
     * documentation for why — and {@link OftPeer#create(OftPeerOptions)} throws if it's set.
     */
    public OftSecurityMode securityMode() {
        return this.securityMode;
    }

    /**
     * How often each connection sends an empty {@code Poll} packet to its peer as a liveness
     * signal, once established (see README.md &sect;10).
     */
    public Duration pollInterval() {
        return this.pollInterval;
    }

    /**
     * How long a connection may go without receiving anything at all from its peer (a {@code Poll}
     * packet or any other packet) before it assumes the peer is unreachable and closes itself (see
     * README.md &sect;10).
     */
    public Duration pollTimeout() {
        return this.pollTimeout;
    }

    /**
     * How long a connection may sit idle (no send or receive) before it is automatically
     * disconnected. Since eviction is only ever checked once per {@link OftPeer}'s fixed,
     * non-configurable 30-second eviction check interval (see its own documentation), a value below
     * 30 seconds here has no effect beyond that floor — the connection is disconnected on the first
     * check after it goes idle, not the instant it does.
     */
    public Duration idleTimeout() {
        return this.idleTimeout;
    }

    /**
     * The maximum total lifetime of a connection before it is automatically disconnected, regardless
     * of activity. Since eviction is only ever checked once per {@link OftPeer}'s fixed,
     * non-configurable 30-second eviction check interval (see its own documentation), a value below
     * 30 seconds here has no effect beyond that floor — the connection is disconnected on the first
     * check after it expires, not the instant it does.
     */
    public Duration maxConnectionLifetime() {
        return this.maxConnectionLifetime;
    }

    /**
     * The maximum number of connections this peer keeps at once. When exceeded, the oldest
     * connections (by when they were established) are disconnected first. A connection with
     * pending data (see {@link OftConnection#hasPendingData()}) is never counted toward this limit
     * for eviction purposes — an application that briefly sends to more distinct hosts than this at
     * once is never cut off mid-send; connections beyond the limit are only evicted, oldest first,
     * once their data has finished sending and a fixed grace period (see {@link OftPeer}'s own
     * documentation) has passed.
     */
    public int maxConnectionCount() {
        return this.maxConnectionCount;
    }

    /** Creates a new builder. */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link OftPeerOptions}. */
    public static final class Builder {
        private String info = "";
        private SSLContext sslContext;
        private OftConnectionValidationCallback connectionValidation;
        private int maxPacketDataSize = 1024;
        private Duration rekeyInterval;
        private OftSecurityMode securityMode = OftSecurityMode.SECURE;
        private Duration pollInterval = Duration.ofSeconds(1);
        private Duration pollTimeout = Duration.ofSeconds(5);
        private Duration idleTimeout = Duration.ofHours(2);
        private Duration maxConnectionLifetime = Duration.ofDays(1);
        private int maxConnectionCount = 16;

        private Builder() {
        }

        public Builder info(String info) {
            this.info = info;
            return this;
        }

        public Builder sslContext(SSLContext sslContext) {
            this.sslContext = sslContext;
            return this;
        }

        public Builder connectionValidation(OftConnectionValidationCallback connectionValidation) {
            this.connectionValidation = connectionValidation;
            return this;
        }

        public Builder maxPacketDataSize(int maxPacketDataSize) {
            this.maxPacketDataSize = maxPacketDataSize;
            return this;
        }

        public Builder rekeyInterval(Duration rekeyInterval) {
            this.rekeyInterval = rekeyInterval;
            return this;
        }

        public Builder securityMode(OftSecurityMode securityMode) {
            this.securityMode = securityMode;
            return this;
        }

        public Builder pollInterval(Duration pollInterval) {
            this.pollInterval = pollInterval;
            return this;
        }

        public Builder pollTimeout(Duration pollTimeout) {
            this.pollTimeout = pollTimeout;
            return this;
        }

        public Builder idleTimeout(Duration idleTimeout) {
            this.idleTimeout = idleTimeout;
            return this;
        }

        public Builder maxConnectionLifetime(Duration maxConnectionLifetime) {
            this.maxConnectionLifetime = maxConnectionLifetime;
            return this;
        }

        public Builder maxConnectionCount(int maxConnectionCount) {
            this.maxConnectionCount = maxConnectionCount;
            return this;
        }

        public OftPeerOptions build() {
            return new OftPeerOptions(this);
        }
    }
}
