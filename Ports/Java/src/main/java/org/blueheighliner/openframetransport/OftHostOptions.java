package org.blueheighliner.openframetransport;

import javax.net.ssl.SSLContext;
import java.time.Duration;
import java.util.Objects;

/**
 * Options for an {@link OftHoster}.
 */
public final class OftHostOptions {
    private final String info;
    private final SSLContext sslContext;
    private final OftConnectionValidationCallback connectionValidation;
    private final int maxPacketDataSize;
    private final Duration rekeyInterval;
    private final OftSecurityMode securityMode;
    private final Duration pollInterval;
    private final Duration pollTimeout;

    private OftHostOptions(Builder builder) {
        this.info = Objects.requireNonNull(builder.info, "info");
        this.securityMode = Objects.requireNonNull(builder.securityMode, "securityMode");
        this.sslContext = builder.sslContext;
        this.connectionValidation = builder.connectionValidation;
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
     * The {@link SSLContext} used to authenticate this listener's accepted connections (and, under
     * {@link OftSecurityMode#DUAL_AUTHENTICATION}, validate the connecting side's certificate via
     * the context's own trust manager(s)). Required (non-{@code null}) for
     * {@link OftSecurityMode#SERVER_AUTHENTICATION} and {@link OftSecurityMode#DUAL_AUTHENTICATION}; unused
     * under {@link OftSecurityMode#SECURE} (an internally generated certificate is used instead) and
     * {@link OftSecurityMode#TRUSTED} (which never negotiates TLS at all).
     */
    public SSLContext sslContext() {
        return this.sslContext;
    }

    /**
     * An optional callback used to validate a fully-established connection, invoked once the OFT
     * hail exchange completes (see README.md &sect;3), for every {@link #securityMode()} - unlike
     * the trust manager configured on {@link #sslContext()}, which only runs during the TLS
     * handshake. When {@code null} (the default), every connection is accepted; otherwise, hosting
     * fails with an {@link java.io.IOException} if the callback returns {@code false}.
     */
    public OftConnectionValidationCallback connectionValidation() {
        return this.connectionValidation;
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

    /** Builder for {@link OftHostOptions}. */
    public static final class Builder {
        private String info = "";
        private SSLContext sslContext;
        private OftConnectionValidationCallback connectionValidation;
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

        public OftHostOptions build() {
            return new OftHostOptions(this);
        }
    }
}
