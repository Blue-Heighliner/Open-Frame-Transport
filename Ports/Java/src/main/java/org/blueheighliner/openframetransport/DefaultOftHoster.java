package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.util.concurrent.CompletableFuture;

/**
 * {@inheritDoc}
 */
final class DefaultOftHoster implements OftHoster {
    @Override
    public CompletableFuture<OftListener> host(InetSocketAddress listenEndpoint) {
        return host(listenEndpoint, defaultOptions());
    }

    @Override
    public CompletableFuture<OftListener> host(InetSocketAddress listenEndpoint, OftHostOptions options) {
        if (options.securityMode() == OftSecurityMode.SERVER_AUTHENTICATION || options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION) {
            if (options.sslContext() == null) {
                throw new IllegalArgumentException(
                        "sslContext is required when securityMode is SERVER_AUTHENTICATION or DUAL_AUTHENTICATION.");
            }
        }

        OftHostOptions resolvedOptions = options;
        return OftBlocking.supplyAsync("oft-host-" + listenEndpoint, () -> {
            OftHostOptions optionsToUse = resolvedOptions;
            if (optionsToUse.securityMode() == OftSecurityMode.SECURE) {
                // Resolved once per listener rather than once per accepted connection: nothing
                // validates this certificate under SECURE mode, so one throwaway identity reused
                // for the listener's whole lifetime is both correct and far cheaper than
                // generating a fresh RSA keypair on every single inbound connection.
                try {
                    optionsToUse = OftHostOptions.builder()
                            .info(optionsToUse.info())
                            .sslContext(OftEphemeralSslContext.createServerContext())
                            .maxPacketDataSize(optionsToUse.maxPacketDataSize())
                            .rekeyInterval(optionsToUse.rekeyInterval())
                            .securityMode(optionsToUse.securityMode())
                            .pollInterval(optionsToUse.pollInterval())
                            .pollTimeout(optionsToUse.pollTimeout())
                            .build();
                } catch (IOException e) {
                    throw e;
                } catch (Exception e) {
                    throw new IOException(e);
                }
            }

            return DefaultOftListener.start(listenEndpoint, optionsToUse);
        });
    }

    private static OftHostOptions defaultOptions() {
        return OftHostOptions.builder().build();
    }
}
