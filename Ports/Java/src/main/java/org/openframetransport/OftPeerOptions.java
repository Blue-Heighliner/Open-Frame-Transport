package org.openframetransport;

import javax.net.ssl.SSLContext;
import java.time.Duration;
import java.util.Objects;

/**
 * Options for an {@link OftPeer}.
 */
public final class OftPeerOptions {
    private final String info;
    private final SSLContext sslContext;
    private final int maxPacketDataSize;
    private final Duration rekeyInterval;
    private final OftSecurityMode securityMode;
    private final Duration pollInterval;
    private final Duration pollTimeout;
    private final Duration idleTimeout;
    private final Duration maxConnectionLifetime;
    private final int maxConnectionCount;
    private final Duration evictionCheckInterval;

    private OftPeerOptions(Builder builder) {
        this.info = Objects.requireNonNull(builder.info, "info");
        this.securityMode = Objects.requireNonNull(builder.securityMode, "securityMode");
        this.sslContext = builder.sslContext;
        this.maxPacketDataSize = builder.maxPacketDataSize;
        this.rekeyInterval = builder.rekeyInterval;
        this.pollInterval = builder.pollInterval;
        this.pollTimeout = builder.pollTimeout;
        this.idleTimeout = builder.idleTimeout;
        this.maxConnectionLifetime = builder.maxConnectionLifetime;
        this.maxConnectionCount = builder.maxConnectionCount;
        this.evictionCheckInterval = builder.evictionCheckInterval;
    }

    /** Opaque, application-controlled data sent to every peer in this side's hail (see README.md &sect;3). */
    public String info() {
        return this.info;
    }

    /**
     * The {@link SSLContext} this peer authenticates itself with, for both inbound and outbound
     * connections. Required (non-{@code null}) for {@link OftSecurityMode#AUTHENTICATION} and
     * {@link OftSecurityMode#DUAL_AUTHENTICATION}; unused under {@link OftSecurityMode#SECURE} and
     * {@link OftSecurityMode#INSECURE}.
     */
    public SSLContext sslContext() {
        return this.sslContext;
    }

    /** The maximum number of payload bytes carried in a single packet's data field. */
    public int maxPacketDataSize() {
        return this.maxPacketDataSize;
    }

    /**
     * When set, every connection automatically rekeys its TLS session on this interval. Ignored
     * when {@link #securityMode()} is {@link OftSecurityMode#INSECURE}.
     */
    public Duration rekeyInterval() {
        return this.rekeyInterval;
    }

    /** The security mode connections are established under (see README.md &sect;9). */
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

    /** How long a connection may sit idle (no send or receive) before it is automatically disconnected. */
    public Duration idleTimeout() {
        return this.idleTimeout;
    }

    /** The maximum total lifetime of a connection before it is automatically disconnected, regardless of activity. */
    public Duration maxConnectionLifetime() {
        return this.maxConnectionLifetime;
    }

    /**
     * The maximum number of connections this peer keeps at once. When exceeded, the oldest
     * connections (by when they were established) are disconnected first.
     */
    public int maxConnectionCount() {
        return this.maxConnectionCount;
    }

    /**
     * How often the peer checks connections against {@link #idleTimeout()},
     * {@link #maxConnectionLifetime()}, and {@link #maxConnectionCount()}.
     */
    public Duration evictionCheckInterval() {
        return this.evictionCheckInterval;
    }

    /** Creates a new builder. */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link OftPeerOptions}. */
    public static final class Builder {
        private String info = "";
        private SSLContext sslContext;
        private int maxPacketDataSize = 1024;
        private Duration rekeyInterval;
        private OftSecurityMode securityMode = OftSecurityMode.SECURE;
        private Duration pollInterval = Duration.ofSeconds(1);
        private Duration pollTimeout = Duration.ofSeconds(5);
        private Duration idleTimeout = Duration.ofMinutes(5);
        private Duration maxConnectionLifetime = Duration.ofHours(1);
        private int maxConnectionCount = 128;
        private Duration evictionCheckInterval = Duration.ofSeconds(30);

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

        public Builder evictionCheckInterval(Duration evictionCheckInterval) {
            this.evictionCheckInterval = evictionCheckInterval;
            return this;
        }

        public OftPeerOptions build() {
            return new OftPeerOptions(this);
        }
    }
}
