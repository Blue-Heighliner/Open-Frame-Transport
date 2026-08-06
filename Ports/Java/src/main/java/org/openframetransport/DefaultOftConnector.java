package org.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.function.Consumer;

/**
 * {@inheritDoc}
 */
final class DefaultOftConnector implements OftConnector {
    @Override
    public OftConnection connect(String host, int port) throws IOException {
        return connect(host, port, defaultOptions(), null);
    }

    @Override
    public OftConnection connect(String host, int port, Consumer<OftConnection> onEstablished) throws IOException {
        return connect(host, port, defaultOptions(), onEstablished);
    }

    @Override
    public OftConnection connect(String host, int port, OftConnectOptions options) throws IOException {
        return connect(host, port, options, null);
    }

    @Override
    public OftConnection connect(String host, int port, OftConnectOptions options, Consumer<OftConnection> onEstablished) throws IOException {
        if (options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION && options.sslContext() == null) {
            throw new IllegalArgumentException("sslContext is required when securityMode is DUAL_AUTHENTICATION.");
        }

        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(host, port));
            DefaultOftConnection connection = DefaultOftConnection.establishAsClient(socket, host, options);

            if (onEstablished != null) {
                onEstablished.accept(connection);
            }

            // Started only now, after onEstablished has had a chance to attach its own listeners:
            // starting any earlier risks the receive loop delivering (and discarding, for lack of a
            // subscriber) this connection's first inbound message before the caller ever gets to
            // see it - see onEstablished's own doc comment on the interface.
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
