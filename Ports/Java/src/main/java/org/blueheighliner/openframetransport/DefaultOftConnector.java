package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.concurrent.CompletableFuture;

/**
 * {@inheritDoc}
 */
final class DefaultOftConnector implements OftConnector {
    @Override
    public CompletableFuture<OftConnection> connect(String host, int port) {
        return connect(host, port, defaultOptions());
    }

    @Override
    public CompletableFuture<OftConnection> connect(String host, int port, OftConnectOptions options) {
        if (options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION && options.sslContext() == null) {
            throw new IllegalArgumentException("sslContext is required when securityMode is DUAL_AUTHENTICATION.");
        }

        return OftBlocking.supplyAsync("oft-connect-" + host + ":" + port, () -> {
            Socket socket = new Socket();
            try {
                socket.connect(new InetSocketAddress(host, port));
                DefaultOftConnection connection = DefaultOftConnection.establishAsClient(socket, host, options);

                // Safe to start processing immediately: received/disconnected notifications are
                // backed by BufferedHandlerSlot, so nothing raised before the caller gets this
                // connection back and assigns a handler is lost - there's no ordering requirement
                // to satisfy here.
                connection.startProcessing();
                return connection;
            } catch (IOException | RuntimeException e) {
                try {
                    socket.close();
                } catch (IOException ignored) {
                    // Best-effort cleanup.
                }

                throw e;
            }
        });
    }

    private static OftConnectOptions defaultOptions() {
        return OftConnectOptions.builder().build();
    }
}
