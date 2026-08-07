package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.net.InetSocketAddress;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftConnectorTest {
    @Test
    void connect_receivedNeverMissesAMessageSentImmediately() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .sslContext(TestCertificates.createServerContext())
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .build();

        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), hostOptions)) {
            // Queued as early as structurally possible - before this connection's own send loop
            // even exists yet (see OftListener#setConnectedHandler's contract) - so it's flushed as
            // the very first thing once the listener starts processing this connection, immediately
            // after this callback returns: about as fast as a peer's first message could possibly
            // arrive.
            listener.setConnectedHandler(connection -> connection.send("immediate".getBytes(), 0));

            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createClientContext())
                    .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                    .build();

            OftConnection connection = OftConnector.create().connect(
                    "127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions);

            // Assigning a callback after connect() returns is safe precisely because it's backed by
            // BufferedHandlerSlot: nothing raised before this assignment is lost, so this isn't a
            // race against the listener's immediate reply above.
            CompletableFuture<byte[]> received = new CompletableFuture<>();
            connection.setReceivedHandler(received::complete);

            assertArrayEquals("immediate".getBytes(), received.get(10, TimeUnit.SECONDS));
            connection.close();
        }
    }

    @Test
    void connect_noOptions_receivedNeverMissesAMessageSentImmediately() throws Exception {
        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0))) {
            listener.setConnectedHandler(connection -> connection.send("immediate".getBytes(), 0));

            OftConnection connection = OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort());

            CompletableFuture<byte[]> received = new CompletableFuture<>();
            connection.setReceivedHandler(received::complete);

            assertArrayEquals("immediate".getBytes(), received.get(10, TimeUnit.SECONDS));
            connection.close();
        }
    }

    @Test
    void connect_noOptions_usesDefaults() throws Exception {
        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0))) {
            try (OftConnection connection = OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                assertEquals("", connection.getRemoteInfo());
            }
        }
    }

    @Test
    void connect_nothingListening_throws() throws Exception {
        int freePort = OftTestHarness.reserveFreePort();
        assertThrows(Exception.class, () -> OftConnector.create().connect("127.0.0.1", freePort));
    }
}
