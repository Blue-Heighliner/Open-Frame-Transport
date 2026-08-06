package org.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.net.InetSocketAddress;
import java.time.Duration;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftPeerTest {
    private static OftPeer createListeningPeer(String info) throws Exception {
        return OftPeer.create(OftPeerOptions.builder()
                .info(info)
                .sslContext(TestCertificates.createPeerContext())
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .build());
    }

    private static OftPeer createOutboundOnlyPeer(String info) throws Exception {
        return OftPeer.create(OftPeerOptions.builder()
                .info(info)
                .sslContext(TestCertificates.createPeerContext())
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .build());
    }

    private static OftTestHarness.TrackedListener createListeningListener(String info) throws Exception {
        OftHostOptions options = OftHostOptions.builder()
                .info(info)
                .sslContext(TestCertificates.createServerContext())
                .securityMode(OftSecurityMode.AUTHENTICATION)
                .build();

        return OftTestHarness.TrackedListener.start(new InetSocketAddress("127.0.0.1", 0), options);
    }

    @Test
    void removeReceivedListener_stopsReceivingMessages() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            listener.addConnectedListener(connection -> connection.send("payload".getBytes(), 0));

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(2);
            OftReceivedListener receivedListener = (peer, data) -> received.add(data);
            client.addReceivedListener(receivedListener);
            client.removeReceivedListener(receivedListener);

            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "hello".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            assertNull(received.poll(2, TimeUnit.SECONDS));
        }
    }

    @Test
    void send_reusesConnectionAcrossCalls() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(2);
            listener.addConnectedListener(connection -> connection.addReceivedListener((c, data) -> received.add(data)));

            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "first".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);
            client.send("127.0.0.1", port, "second".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            assertEquals("first", new String(received.poll(10, TimeUnit.SECONDS)));
            assertEquals("second", new String(received.poll(10, TimeUnit.SECONDS)));

            // Still exactly one inbound connection: the second send reused the cached outbound
            // connection rather than dialing a new one.
            assertEquals(1, listener.getConnections().size());
        }
    }

    @Test
    void eviction_disconnectsIdleConnections() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server")) {
            // idleTimeout must comfortably exceed how long the initial send itself can take (connect +
            // handshake + one round trip), or eviction can race the send and disconnect the connection
            // before it ever finishes; the eviction check only needs to run a couple of times afterward
            // to observe the connection going idle, so a short interval is fine.
            OftPeer client = OftPeer.create(OftPeerOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createPeerContext())
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .idleTimeout(Duration.ofSeconds(3))
                    .evictionCheckInterval(Duration.ofMillis(100))
                    .build());

            try {
                int port = listener.getLocalEndpoint().getPort();
                client.send("127.0.0.1", port, "hi".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);
                assertEquals(1, listener.getConnections().size());

                OftTestHarness.waitUntil(() -> listener.getConnections().isEmpty(), Duration.ofSeconds(20));
            } finally {
                client.close();
            }
        }
    }

    @Test
    void eviction_disconnectsIdleInboundConnections() throws Exception {
        OftPeer listeningPeer = OftPeer.create(OftPeerOptions.builder()
                .info("listener")
                .sslContext(TestCertificates.createPeerContext())
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .idleTimeout(Duration.ofMillis(200))
                .evictionCheckInterval(Duration.ofMillis(50))
                .build());

        try {
            listeningPeer.open(new InetSocketAddress("127.0.0.1", 0));

            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createPeerContext())
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .build();

            OftConnector connector = OftConnector.create();
            OftConnection connection = connector.connect("127.0.0.1", listeningPeer.getLocalEndpoint().getPort(), connectOptions);
            try {
                CompletableFuture<Void> disconnectedFuture = new CompletableFuture<>();
                connection.addDisconnectedListener(exception -> disconnectedFuture.complete(null));

                disconnectedFuture.get(10, TimeUnit.SECONDS);
            } finally {
                connection.close();
            }
        } finally {
            listeningPeer.close();
        }
    }

    @Test
    void outboundOnlyPeer_hasNoLocalEndpointAndIgnoresOpenStop() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            assertNull(client.getLocalEndpoint());
            client.stop();
        }
    }

    @Test
    void received_deliversOnBothInboundAndOutboundConnections() throws Exception {
        try (OftPeer peerA = createListeningPeer("peerA"); OftPeer peerB = createListeningPeer("peerB")) {
            peerA.open(new InetSocketAddress("127.0.0.1", 0));
            peerB.open(new InetSocketAddress("127.0.0.1", 0));

            BlockingQueue<byte[]> peerBReceived = new ArrayBlockingQueue<>(1);
            BlockingQueue<byte[]> peerAReceived = new ArrayBlockingQueue<>(1);
            peerB.addReceivedListener((connection, data) -> {
                peerBReceived.add(data);
                connection.send("pong".getBytes(), 0);
            });
            peerA.addReceivedListener((connection, data) -> peerAReceived.add(data));

            int portB = peerB.getLocalEndpoint().getPort();
            peerA.send("127.0.0.1", portB, "ping".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            assertEquals("ping", new String(peerBReceived.poll(10, TimeUnit.SECONDS)));
            assertEquals("pong", new String(peerAReceived.poll(10, TimeUnit.SECONDS)));
        }
    }

    @Test
    void close_calledTwice_isIdempotent() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");
        client.close();
        client.close();
    }

    @Test
    void send_connectFailure_propagatesIOException() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            assertThrows(java.io.IOException.class, () -> client.send("127.0.0.1", 1, "hi".getBytes(), 0));
        }
    }

    @Test
    void eviction_disconnectsExcessConnectionsBeyondMaxCount() throws Exception {
        try (OftTestHarness.TrackedListener listenerA = createListeningListener("serverA");
             OftTestHarness.TrackedListener listenerB = createListeningListener("serverB")) {
            OftPeer client = OftPeer.create(OftPeerOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createPeerContext())
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .maxConnectionCount(1)
                    .evictionCheckInterval(Duration.ofMillis(100))
                    .build());

            try {
                client.send("127.0.0.1", listenerA.getLocalEndpoint().getPort(), "a".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);
                client.send("127.0.0.1", listenerB.getLocalEndpoint().getPort(), "b".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

                OftTestHarness.waitUntil(() -> listenerA.getConnections().isEmpty(), Duration.ofSeconds(15));
                assertEquals(1, listenerB.getConnections().size());
            } finally {
                client.close();
            }
        }
    }

    @Test
    void pendingData_preventsAutomaticDisconnectionUntilAcknowledged() throws Exception {
        try (OftTestHarness.TrackedListener receiverListener = createListeningListener("receiver")) {
            OftPeer sender = OftPeer.create(OftPeerOptions.builder()
                    .info("sender")
                    .sslContext(TestCertificates.createPeerContext())
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .maxPacketDataSize(8)
                    .idleTimeout(Duration.ofMillis(300))
                    .evictionCheckInterval(Duration.ofMillis(50))
                    .build());

            try {
                // ~50 acknowledged round trips (one packet in flight at a time), which comfortably
                // outlasts the 300ms idle timeout above: if eviction ignored pending data, the
                // connection would already be gone well before this send finishes.
                byte[] payload = new byte[400];
                java.util.Arrays.fill(payload, (byte) 7);

                CompletableFuture<Void> sendFuture =
                        sender.send("127.0.0.1", receiverListener.getLocalEndpoint().getPort(), payload, 0).completion();

                Thread.sleep(400);
                assertFalse(sendFuture.isDone());
                assertEquals(1, receiverListener.getConnections().size());

                sendFuture.get(10, TimeUnit.SECONDS);

                OftTestHarness.waitUntil(() -> receiverListener.getConnections().isEmpty(), Duration.ofSeconds(10));
            } finally {
                sender.close();
            }
        }
    }

    @Test
    void rekey_rekeysOutboundAndInboundConnections() throws Exception {
        try (OftPeer listeningPeer = createListeningPeer("listener"); OftPeer caller = createOutboundOnlyPeer("caller")) {
            listeningPeer.open(new InetSocketAddress("127.0.0.1", 0));

            int port = listeningPeer.getLocalEndpoint().getPort();
            caller.send("127.0.0.1", port, "hello".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            caller.rekey().get(10, TimeUnit.SECONDS);
            listeningPeer.rekey().get(10, TimeUnit.SECONDS);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            listeningPeer.addReceivedListener((connection, data) -> received.add(data));

            caller.send("127.0.0.1", port, "post-rekey".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            assertEquals("post-rekey", new String(received.poll(10, TimeUnit.SECONDS)));
        }
    }

    @Test
    void rekey_noConnections_completesImmediately() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            client.rekey().get(10, TimeUnit.SECONDS);
        }
    }

    @Test
    void disconnect_disconnectsOutboundAndInboundConnections() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            BlockingQueue<OftConnection> inboundConnections = new ArrayBlockingQueue<>(1);
            listener.addConnectedListener(inboundConnections::add);

            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "hi".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            OftConnection inboundOnListener = inboundConnections.poll(10, TimeUnit.SECONDS);
            assertEquals(1, listener.getConnections().size());

            CompletableFuture<Void> disconnectedFuture = new CompletableFuture<>();
            inboundOnListener.addDisconnectedListener(exception -> disconnectedFuture.complete(null));

            client.disconnect();

            disconnectedFuture.get(10, TimeUnit.SECONDS);
            OftTestHarness.waitUntil(() -> listener.getConnections().isEmpty(), Duration.ofSeconds(10));
        }
    }

    @Test
    void disconnect_peerRemainsUsableAfterward() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "first".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            client.disconnect();

            client.send("127.0.0.1", port, "second".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);
        }
    }

    @Test
    void disconnect_noConnections_doesNotThrow() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            client.disconnect();
        }
    }
}
