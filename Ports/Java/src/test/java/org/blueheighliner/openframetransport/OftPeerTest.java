package org.blueheighliner.openframetransport;

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
import static org.junit.jupiter.api.Assertions.assertTrue;

// 150s rather than 30s: several eviction tests below must wait out OftPeer's fixed, non-configurable
// 30-second eviction grace period plus its fixed, non-configurable 30-second eviction check interval
// (see OftPeer's own documentation) before observing a disconnect - with generous margin on top of
// that ~60-second theoretical worst case, since the eviction timer can drift when other tests are
// consuming the JVM's threads concurrently.
@Timeout(value = 150, unit = TimeUnit.SECONDS)
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
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .build();

        return OftTestHarness.TrackedListener.start(new InetSocketAddress("127.0.0.1", 0), options);
    }

    @Test
    void create_serverAuthenticationMode_throws() throws Exception {
        OftPeerOptions options = OftPeerOptions.builder()
                .info("peer")
                .sslContext(TestCertificates.createPeerContext())
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .build();

        assertThrows(IllegalArgumentException.class, () -> OftPeer.create(options));
    }

    @Test
    void handler_reassignedToNull_stopsReceivingMessages() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            listener.onConnectedExtra = connection -> connection.send("payload".getBytes(), 0, null);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(2);
            client.setReceivedHandler((identity, data) -> received.add(data));
            client.setReceivedHandler(null);

            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "hello".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            assertNull(received.poll(2, TimeUnit.SECONDS));
        }
    }

    @Test
    void send_reusesConnectionAcrossCalls() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(2);
            listener.onConnectedExtra = connection -> connection.setReceivedHandler(received::add);

            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "first".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);
            client.send("127.0.0.1", port, "second".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

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
            OftPeer client = OftPeer.create(OftPeerOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createPeerContext())
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .idleTimeout(Duration.ofSeconds(3))
                    .build());

            try {
                int port = listener.getLocalEndpoint().getPort();
                client.send("127.0.0.1", port, "hi".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);
                assertEquals(1, listener.getConnections().size());

                // 120s rather than 20: the connection only becomes an eviction candidate once
                // OftPeer's fixed, non-configurable 30-second grace period has elapsed since it
                // finished sending, and eviction itself is only ever checked on a further fixed,
                // non-configurable 30-second interval on top of that (see OftPeer's own
                // documentation).
                OftTestHarness.waitUntil(() -> listener.getConnections().isEmpty(), Duration.ofSeconds(120));
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
                .build());

        try {
            listeningPeer.listen(new InetSocketAddress("127.0.0.1", 0));

            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createPeerContext())
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .build();

            OftConnector connector = OftConnector.create();
            OftConnection connection = connector.connect("127.0.0.1", listeningPeer.getLocalEndpoint().getPort(), connectOptions);
            try {
                CompletableFuture<Void> disconnectedFuture = new CompletableFuture<>();
                connection.setDisconnectedHandler(exception -> disconnectedFuture.complete(null));

                // 120s rather than 10: the connection only becomes an eviction candidate once
                // OftPeer's fixed, non-configurable 30-second grace period has elapsed, and eviction
                // itself is only ever checked on a further fixed, non-configurable 30-second
                // interval on top of that (see OftPeer's own documentation).
                disconnectedFuture.get(120, TimeUnit.SECONDS);
            } finally {
                connection.close();
            }
        } finally {
            listeningPeer.close();
        }
    }

    @Test
    void outboundOnlyPeer_hasNoLocalEndpointAndIgnoresListenStopListening() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            assertNull(client.getLocalEndpoint());
            client.stopListening();
        }
    }

    @Test
    void received_deliversForInboundConnections() throws Exception {
        try (OftPeer listeningPeer = createListeningPeer("listener"); OftPeer caller = createOutboundOnlyPeer("caller")) {
            listeningPeer.listen(new InetSocketAddress("127.0.0.1", 0));

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            listeningPeer.setReceivedHandler((identity, data) -> received.add(data));

            int port = listeningPeer.getLocalEndpoint().getPort();
            caller.send("127.0.0.1", port, "hello listener".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            byte[] data = received.poll(10, TimeUnit.SECONDS);
            assertEquals("hello listener", new String(data));
        }
    }

    @Test
    void send_withTag_raisesAcknowledgedHandlerWithIdentityAndTag() throws Exception {
        try (OftPeer listeningPeer = createListeningPeer("listener"); OftPeer caller = createOutboundOnlyPeer("caller")) {
            listeningPeer.listen(new InetSocketAddress("127.0.0.1", 0));

            Object tag = new Object();
            CompletableFuture<OftIdentity> acknowledgedIdentity = new CompletableFuture<>();
            CompletableFuture<Object> acknowledgedTag = new CompletableFuture<>();
            caller.setAcknowledgedHandler((identity, receivedTag) -> {
                acknowledgedIdentity.complete(identity);
                acknowledgedTag.complete(receivedTag);
            });

            int port = listeningPeer.getLocalEndpoint().getPort();
            caller.send("127.0.0.1", port, "hello listener".getBytes(), 0, tag).completion().get(10, TimeUnit.SECONDS);

            assertTrue(tag == acknowledgedTag.get(10, TimeUnit.SECONDS));
            assertEquals("listener", acknowledgedIdentity.get(10, TimeUnit.SECONDS).info());
        }
    }

    @Test
    void send_withoutTag_neverRaisesAcknowledgedHandler() throws Exception {
        try (OftPeer listeningPeer = createListeningPeer("listener"); OftPeer caller = createOutboundOnlyPeer("caller")) {
            listeningPeer.listen(new InetSocketAddress("127.0.0.1", 0));

            java.util.concurrent.atomic.AtomicBoolean acknowledgedHandlerRaised = new java.util.concurrent.atomic.AtomicBoolean();
            caller.setAcknowledgedHandler((identity, tag) -> acknowledgedHandlerRaised.set(true));

            int port = listeningPeer.getLocalEndpoint().getPort();
            caller.send("127.0.0.1", port, "hello listener".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            Thread.sleep(200);
            assertFalse(acknowledgedHandlerRaised.get());
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
            assertThrows(java.io.IOException.class, () -> client.send("127.0.0.1", 1, "hi".getBytes(), 0, null));
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
                    .build());

            try {
                client.send("127.0.0.1", listenerA.getLocalEndpoint().getPort(), "a".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);
                client.send("127.0.0.1", listenerB.getLocalEndpoint().getPort(), "b".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

                // Duration.ofSeconds(120) rather than 15: connectionA only becomes an eviction
                // candidate once OftPeer's fixed, non-configurable 30-second grace period has
                // elapsed since it finished sending, and eviction itself is only ever checked on a
                // further fixed, non-configurable 30-second interval on top of that (see OftPeer's
                // own documentation).
                OftTestHarness.waitUntil(() -> listenerA.getConnections().isEmpty(), Duration.ofSeconds(120));
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
                    .build());

            try {
                // ~50 acknowledged round trips (one packet in flight at a time), which comfortably
                // outlasts the 300ms idle timeout above: if eviction ignored pending data, the
                // connection would already be gone well before this send finishes.
                byte[] payload = new byte[400];
                java.util.Arrays.fill(payload, (byte) 7);

                CompletableFuture<Void> sendFuture =
                        sender.send("127.0.0.1", receiverListener.getLocalEndpoint().getPort(), payload, 0, null).completion();

                Thread.sleep(400);
                assertFalse(sendFuture.isDone());
                assertEquals(1, receiverListener.getConnections().size());

                sendFuture.get(10, TimeUnit.SECONDS);

                // 120s rather than 10: the connection only becomes an eviction candidate once
                // OftPeer's fixed, non-configurable 30-second grace period has elapsed since it
                // finished sending, and eviction itself is only ever checked on a further fixed,
                // non-configurable 30-second interval on top of that (see OftPeer's own
                // documentation).
                OftTestHarness.waitUntil(() -> receiverListener.getConnections().isEmpty(), Duration.ofSeconds(120));
            } finally {
                sender.close();
            }
        }
    }

    @Test
    void rekey_rekeysOutboundAndInboundConnections() throws Exception {
        try (OftPeer listeningPeer = createListeningPeer("listener"); OftPeer caller = createOutboundOnlyPeer("caller")) {
            listeningPeer.listen(new InetSocketAddress("127.0.0.1", 0));

            // Assigned before any message is ever sent: ReceivedHandler is backed by
            // BufferedHandlerSlot, so assigning here rather than after the "hello" send below
            // avoids seeing that earlier, unrelated message instead of the post-rekey one this test
            // actually cares about.
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(2);
            listeningPeer.setReceivedHandler((identity, data) -> received.add(data));

            int port = listeningPeer.getLocalEndpoint().getPort();
            caller.send("127.0.0.1", port, "hello".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            caller.rekey().get(10, TimeUnit.SECONDS);
            listeningPeer.rekey().get(10, TimeUnit.SECONDS);

            caller.send("127.0.0.1", port, "post-rekey".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            assertEquals("hello", new String(received.poll(10, TimeUnit.SECONDS)));
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
    void drop_disconnectsOutboundAndInboundConnections() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            BlockingQueue<OftConnection> inboundConnections = new ArrayBlockingQueue<>(1);
            listener.onConnectedExtra = inboundConnections::add;

            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "hi".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            OftConnection inboundOnListener = inboundConnections.poll(10, TimeUnit.SECONDS);
            assertEquals(1, listener.getConnections().size());

            CompletableFuture<Void> disconnectedFuture = new CompletableFuture<>();
            listener.onConnectionDisconnectedExtra = (connection, exception) -> {
                if (connection == inboundOnListener) {
                    disconnectedFuture.complete(null);
                }
            };

            client.drop();

            disconnectedFuture.get(10, TimeUnit.SECONDS);
            OftTestHarness.waitUntil(() -> listener.getConnections().isEmpty(), Duration.ofSeconds(10));
        }
    }

    @Test
    void drop_peerRemainsUsableAfterward() throws Exception {
        try (OftTestHarness.TrackedListener listener = createListeningListener("server"); OftPeer client = createOutboundOnlyPeer("client")) {
            int port = listener.getLocalEndpoint().getPort();
            client.send("127.0.0.1", port, "first".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            client.drop();

            assertTrue(client.isConnected());
            client.send("127.0.0.1", port, "second".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);
        }
    }

    @Test
    void drop_noConnections_doesNotThrow() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            client.drop();
        }
    }

    @Test
    void drop_afterClose_throws() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");
        client.close();

        assertThrows(IllegalStateException.class, client::drop);
    }

    @Test
    void isConnected_trueUntilClosed() throws Exception {
        try (OftPeer client = createOutboundOnlyPeer("client")) {
            assertTrue(client.isConnected());
        }
    }

    @Test
    void close_putsIntoDisconnectedState() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");

        client.close();

        assertFalse(client.isConnected());
    }

    @Test
    void listenCalledAfterClose_throws() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");
        client.close();

        assertThrows(IllegalStateException.class, () -> client.listen(new InetSocketAddress("127.0.0.1", 0)));
    }

    @Test
    void stopListeningCalledAfterClose_throws() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");
        client.close();

        assertThrows(IllegalStateException.class, client::stopListening);
    }

    @Test
    void rekeyCalledAfterClose_throwsOftDisconnectedException() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");
        client.close();

        assertThrows(OftDisconnectedException.class, client::rekey);
    }

    @Test
    void sendCalledAfterClose_throwsOftDisconnectedException() throws Exception {
        OftPeer client = createOutboundOnlyPeer("client");
        client.close();

        assertThrows(OftDisconnectedException.class, () -> client.send("127.0.0.1", 12345, "hi".getBytes(), 0, null));
    }
}
