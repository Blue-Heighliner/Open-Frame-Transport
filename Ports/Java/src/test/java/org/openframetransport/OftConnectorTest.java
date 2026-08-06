package org.openframetransport;

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
    void connect_onEstablishedCallback_neverMissesAMessageSentImmediately() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .sslContext(TestCertificates.createServerContext())
                .securityMode(OftSecurityMode.AUTHENTICATION)
                .build();

        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), hostOptions)) {
            // Queued as early as structurally possible - before this connection's own send loop
            // even exists yet (see OftListener#addConnectedListener's contract) - so it's flushed
            // as the very first thing once the listener starts processing this connection,
            // immediately after this listener returns: about as fast as a peer's first message
            // could possibly arrive.
            listener.addConnectedListener(connection -> connection.send("immediate".getBytes(), 0));

            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createClientContext())
                    .securityMode(OftSecurityMode.AUTHENTICATION)
                    .build();

            CompletableFuture<byte[]> received = new CompletableFuture<>();

            // Subscribing via onEstablished, rather than after connect() returns, is what
            // guarantees this listener is attached before the connection's receive loop starts -
            // without it, this test would be a race against the accepting side's immediate reply
            // above.
            OftConnection connection = OftConnector.create().connect(
                    "127.0.0.1",
                    listener.getLocalEndpoint().getPort(),
                    connectOptions,
                    established -> established.addReceivedListener((c, data) -> received.complete(data)));

            assertArrayEquals("immediate".getBytes(), received.get(10, TimeUnit.SECONDS));
            connection.close();
        }
    }

    @Test
    void connect_noOptionsWithOnEstablishedCallback_neverMissesAMessageSentImmediately() throws Exception {
        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0))) {
            listener.addConnectedListener(connection -> connection.send("immediate".getBytes(), 0));

            CompletableFuture<byte[]> received = new CompletableFuture<>();
            OftConnection connection = OftConnector.create().connect(
                    "127.0.0.1",
                    listener.getLocalEndpoint().getPort(),
                    established -> established.addReceivedListener((c, data) -> received.complete(data)));

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
