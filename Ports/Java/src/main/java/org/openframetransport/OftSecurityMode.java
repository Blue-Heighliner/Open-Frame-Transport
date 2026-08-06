package org.openframetransport;

/**
 * The security mode a connection is established under (see README.md &sect;9). Mirrors the C#
 * reference implementation's {@code OftSecurityMode} enum.
 */
public enum OftSecurityMode {
    /**
     * No TLS at all: hails are sent directly over the raw TCP connection as soon as it's formed.
     * No confidentiality, integrity, or authentication of either side.
     */
    INSECURE,

    /**
     * TLS provides confidentiality and integrity but no authentication of either side. The
     * accepting side uses a certificate it generates internally rather than one supplied by the
     * caller; the connecting side accepts whatever certificate it's presented with unconditionally.
     */
    SECURE,

    /**
     * Traditional one-way TLS: the accepting side must supply a real {@code SSLContext} carrying
     * its own certificate, which the connecting side validates normally via its own
     * {@code SSLContext}'s trust manager(s).
     */
    AUTHENTICATION,

    /**
     * Mutual TLS: everything {@link #AUTHENTICATION} requires, plus the connecting side's
     * {@code SSLContext} must also carry its own certificate (via a key manager), which the
     * accepting side requests and validates.
     */
    DUAL_AUTHENTICATION,
}
