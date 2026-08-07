package org.blueheighliner.openframetransport;

import javax.net.ssl.SSLContext;
import java.time.Duration;
import java.util.Objects;

/**
 * Options for an {@link OftConnector}.
 */
public final class OftConnectOptions {
    private final String info;
    private final SSLContext sslContext;
    private final int maxPacketDataSize;
    private final Duration rekeyInterval;
    private final OftSecurityMode securityMode;
    private final Duration pollInterval;
    private final Duration pollTimeout;

    private OftConnectOptions(Builder builder) {
        this.info = Objects.requireNonNull(builder.info, "info");
        this.securityMode = Objects.requireNonNull(builder.securityMode, "securityMode");
        this.sslContext = builder.sslContext;
        this.maxPacketDataSize = builder.maxPacketDataSize;
        this.rekeyInterval = builder.rekeyInterval;
        this.pollInterval = builder.pollInterval;
        this.pollTimeout = builder.pollTimeout;
    }

    /** Opaque, application-controlled data sent to the peer in this side's hail (see README.md &sect;3). */
    public String info() {
        return this.info;
    }

    /**
     * The {@link SSLContext} used to validate the accepting side's certificate (under
     * {@link OftSecurityMode#SERVER_AUTHENTICATION}) and to present this side's own certificate (under
     * {@link OftSecurityMode#DUAL_AUTHENTICATION}, via a key manager configured on the context).
     * Required (non-{@code null}) for both of those modes; unused under
     * {@link OftSecurityMode#SECURE} (which validates nothing) and
     * {@link OftSecurityMode#TRUSTED} (which never negotiates TLS at all).
     */
    public SSLContext sslContext() {
        return this.sslContext;
    }

    /** The maximum number of payload bytes carried in a single packet's data field. */
    public int maxPacketDataSize() {
        return this.maxPacketDataSize;
    }

    /**
     * When set, connections automatically rekey their TLS session on this interval. Ignored when
     * {@link #securityMode()} is {@link OftSecurityMode#TRUSTED}.
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

    /** Creates a new builder. */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link OftConnectOptions}. */
    public static final class Builder {
        private String info = "";
        private SSLContext sslContext;
        private int maxPacketDataSize = 1024;
        private Duration rekeyInterval;
        private OftSecurityMode securityMode = OftSecurityMode.SECURE;
        private Duration pollInterval = Duration.ofSeconds(1);
        private Duration pollTimeout = Duration.ofSeconds(5);

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

        public OftConnectOptions build() {
            return new OftConnectOptions(this);
        }
    }
}
