package org.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;

/**
 * {@inheritDoc}
 */
final class DefaultOftHoster implements OftHoster {
    @Override
    public OftListener host(InetSocketAddress listenEndpoint) throws IOException {
        return host(listenEndpoint, defaultOptions());
    }

    @Override
    public OftListener host(InetSocketAddress listenEndpoint, OftHostOptions options) throws IOException {
        if (options.securityMode() == OftSecurityMode.AUTHENTICATION || options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION) {
            if (options.sslContext() == null) {
                throw new IllegalArgumentException(
                        "sslContext is required when securityMode is AUTHENTICATION or DUAL_AUTHENTICATION.");
            }
        } else if (options.securityMode() == OftSecurityMode.SECURE) {
            // Resolved once per listener rather than once per accepted connection: nothing
            // validates this certificate under SECURE mode, so one throwaway identity reused for
            // the listener's whole lifetime is both correct and far cheaper than generating a
            // fresh RSA keypair on every single inbound connection.
            try {
                options = OftHostOptions.builder()
                        .info(options.info())
                        .sslContext(OftEphemeralSslContext.createServerContext())
                        .maxPacketDataSize(options.maxPacketDataSize())
                        .rekeyInterval(options.rekeyInterval())
                        .securityMode(options.securityMode())
                        .pollInterval(options.pollInterval())
                        .pollTimeout(options.pollTimeout())
                        .build();
            } catch (IOException e) {
                throw e;
            } catch (Exception e) {
                throw new IOException(e);
            }
        }

        return DefaultOftListener.start(listenEndpoint, options);
    }

    private static OftHostOptions defaultOptions() {
        return OftHostOptions.builder().build();
    }
}
