package org.blueheighliner.openframetransport;

import javax.net.ssl.SSLSession;
import java.security.cert.Certificate;

/**
 * Validates a fully-established OFT connection. Invoked once per connection, after the OFT hail
 * exchange completes (see README.md &sect;3), for every {@link OftSecurityMode} - including
 * {@link OftSecurityMode#TRUSTED} and {@link OftSecurityMode#SECURE}, where
 * {@code certificateChain} and {@code session} are always {@code null}. Unlike the trust-manager
 * validation baked into the {@link javax.net.ssl.SSLContext} handed to {@link OftConnectOptions}/
 * {@link OftHostOptions} (which runs earlier, during the TLS handshake itself, and only ever sees
 * the certificate in isolation), this runs after the connection's {@link OftIdentity} is fully
 * populated.
 */
@FunctionalInterface
public interface OftConnectionValidationCallback {
    /**
     * @param identity         the connection's fully-populated remote identity
     * @param certificateChain the certificate chain the remote side presented during the TLS
     *                         handshake (leaf first), already accepted by the {@link SSLSession}'s
     *                         trust manager by the time this runs - {@code null} under
     *                         {@link OftSecurityMode#TRUSTED} (no TLS at all) or if the remote side
     *                         didn't present one
     * @param session          the TLS session the connection negotiated, or {@code null} under
     *                         {@link OftSecurityMode#TRUSTED}
     * @return {@code true} to accept the connection, or {@code false} to reject it
     */
    boolean validate(OftIdentity identity, Certificate[] certificateChain, SSLSession session);
}
