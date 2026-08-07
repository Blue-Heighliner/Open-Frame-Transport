package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * {@inheritDoc}
 */
final class DefaultOftConnector implements OftConnector {
    @Override
    public OftConnection connect(String host, int port) throws IOException {
        return connect(host, port, defaultOptions());
    }

    @Override
    public OftConnection connect(String host, int port, OftConnectOptions options) throws IOException {
        if (options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION && options.sslContext() == null) {
            throw new IllegalArgumentException("sslContext is required when securityMode is DUAL_AUTHENTICATION.");
        }

        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(host, port));
            DefaultOftConnection connection = DefaultOftConnection.establishAsClient(socket, host, options);

            // Safe to start processing immediately: received/disconnected notifications are backed
            // by BufferedHandlerSlot, so nothing raised before the caller gets this connection back
            // and assigns a handler is lost - there's no ordering requirement to satisfy here.
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
    }

    private static OftConnectOptions defaultOptions() {
        return OftConnectOptions.builder().build();
    }
}
