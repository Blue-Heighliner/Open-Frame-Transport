package org.blueheighliner.openframetransport;

import java.net.InetSocketAddress;

/**
 * The identity of an OFT connection's remote side.
 *
 * @param endpoint    the connection's remote TCP endpoint
 * @param certificate the remote side's TLS certificate identity, or {@code null} if it didn't
 *                    present one - always {@code null} for a connection established with
 *                    {@link OftSecurityMode#TRUSTED} (no TLS at all), and also {@code null} for the
 *                    accepting side of a connection established under a mode that never requests a
 *                    client certificate (see {@link OftSecurityMode#DUAL_AUTHENTICATION})
 * @param info        the opaque, application-controlled data the remote side sent in its hail (see
 *                    README.md &sect;3)
 */
public record OftIdentity(InetSocketAddress endpoint, OftCertificateIdentity certificate, String info) {
}
