package org.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftListenerTest {
    @Test
    void close_calledTwice_isIdempotent() throws Exception {
        OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0));
        listener.close();
        listener.close();
    }

    @Test
    void removeConnectedListener_stopsReceivingNotifications() throws Exception {
        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0))) {
            CompletableFuture<OftConnection> unexpected = new CompletableFuture<>();
            Consumer<OftConnection> connectedListener = unexpected::complete;
            listener.addConnectedListener(connectedListener);
            listener.removeConnectedListener(connectedListener);

            CompletableFuture<OftConnection> accepted = new CompletableFuture<>();
            listener.addConnectedListener(accepted::complete);

            OftConnectOptions connectOptions = OftConnectOptions.builder().info("client").build();
            try (OftConnection connection = OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions)) {
                accepted.get(10, TimeUnit.SECONDS);
            }

            assertNull(unexpected.getNow(null));
        }
    }

    @Test
    void handleAccepted_malformedClient_doesNotCrashListener() throws Exception {
        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0),
                OftHostOptions.builder().info("server").securityMode(OftSecurityMode.INSECURE).build())) {
            CompletableFuture<OftConnection> accepted = new CompletableFuture<>();
            listener.addConnectedListener(accepted::complete);

            try (Socket rogue = new Socket("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                rogue.getOutputStream().write(new byte[] {1, 2, 3, 4, 5});
                rogue.getOutputStream().flush();
            }

            // The listener must still be usable afterward: a well-behaved client can still connect.
            OftConnectOptions connectOptions = OftConnectOptions.builder().info("client").securityMode(OftSecurityMode.INSECURE).build();
            try (OftConnection connection = OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions)) {
                assertNotNull(accepted.get(10, TimeUnit.SECONDS));
            }
        }
    }
}
