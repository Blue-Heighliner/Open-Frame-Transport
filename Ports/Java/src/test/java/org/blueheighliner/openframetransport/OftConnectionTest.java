package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.blueheighliner.openframetransport.proto.Hail;

import java.io.IOException;
import java.net.ServerSocket;
import java.net.Socket;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftConnectionTest {

    @Test
    void establish_exchangesInfoAsHail() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertEquals("server", pair.clientConnection().getIdentity().info());
            assertEquals("client", pair.serverConnection().getIdentity().info());
        }
    }

    @Test
    void establish_serverAuthentication_clientSeesServerCertificate() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertNotNull(pair.clientConnection().getIdentity().certificate());
            assertEquals("CN=localhost", pair.clientConnection().getIdentity().certificate().getSubjectX500Principal().getName());

            // Server authentication only authenticates the server - the server never sees a client
            // certificate.
            assertNull(pair.serverConnection().getIdentity().certificate());
        }
    }

    @Test
    void send_small_deliveredAsUnit() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "hello".getBytes();
            pair.clientConnection().send(payload, 0, null).completion().get(10, TimeUnit.SECONDS);

            byte[] result = received.poll(10, TimeUnit.SECONDS);
            assertArrayEquals(payload, result);
        }
    }

    @Test
    void send_emptyPayload_deliveredAsEmptyMessage() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            pair.clientConnection().send(new byte[0], 0, null).completion().get(10, TimeUnit.SECONDS);

            byte[] result = received.poll(10, TimeUnit.SECONDS);
            assertNotNull(result);
            assertEquals(0, result.length);
        }
    }

    @Test
    void send_largerThanPacketSize_splitAndReassembled() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16, null)) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = new byte[1000];
            for (int i = 0; i < payload.length; i++) {
                payload[i] = (byte) i;
            }

            pair.clientConnection().send(payload, 3, null).completion().get(10, TimeUnit.SECONDS);

            byte[] result = received.poll(10, TimeUnit.SECONDS);
            assertArrayEquals(payload, result);
        }
    }

    @Test
    void send_oneByteOverPacketSize_splitWithMinimalFinalChunk() throws Exception {
        // The smallest possible split: one full Data chunk plus a 1-byte Completion chunk. This is
        // the boundary case the Completion-carries-the-proto3-default-control-value design (README.md
        // §4) depends on - a Completion packet's data must never be empty, and this is as close to
        // empty as a real one can get.
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16, null)) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = new byte[17];
            for (int i = 0; i < payload.length; i++) {
                payload[i] = (byte) i;
            }

            pair.clientConnection().send(payload, 1, null).completion().get(10, TimeUnit.SECONDS);

            byte[] result = received.poll(10, TimeUnit.SECONDS);
            assertArrayEquals(payload, result);
        }
    }

    @Test
    void send_higherPriorityInterruptsLowerPriority() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(8, null)) {
            List<Integer> receivedOrder = new CopyOnWriteArrayList<>();
            CountDownLatch bothReceived = new CountDownLatch(2);
            pair.serverConnection().setReceivedHandler(data -> {
                receivedOrder.add(data.length);
                bothReceived.countDown();
            });

            byte[] lowPriorityPayload = new byte[500];
            byte[] highPriorityPayload = new byte[24];

            OftSendHandle lowSend = pair.clientConnection().send(lowPriorityPayload, 0, null);
            Thread.sleep(20);
            OftSendHandle highSend = pair.clientConnection().send(highPriorityPayload, 5, null);

            assertTrue(bothReceived.await(10, TimeUnit.SECONDS));
            lowSend.completion().get(10, TimeUnit.SECONDS);
            highSend.completion().get(10, TimeUnit.SECONDS);

            assertEquals(List.of(highPriorityPayload.length, lowPriorityPayload.length), receivedOrder);
        }
    }

    @Test
    void send_cancelledBeforeStart_neverDelivered() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            OftSendHandle handle = pair.clientConnection().send("should not arrive".getBytes(), 0, null);
            handle.cancel();

            CompletableFuture<Void> completion = handle.completion();
            assertThrows(Exception.class, () -> completion.get(5, TimeUnit.SECONDS));

            assertNull(received.poll(200, TimeUnit.MILLISECONDS));
        }
    }

    @Test
    void send_cancelledAfterStart_sendsCancellationAndConnectionStaysHealthy() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(8, null)) {
            byte[] payload = new byte[400];
            OftSendHandle handle = pair.clientConnection().send(payload, 0, null);
            Thread.sleep(50);
            handle.cancel();

            CompletableFuture<Void> completion = handle.completion();
            assertThrows(Exception.class, () -> completion.get(10, TimeUnit.SECONDS));

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] followUp = "still alive".getBytes();
            pair.clientConnection().send(followUp, 0, null).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(followUp, received.poll(10, TimeUnit.SECONDS));
        }
    }

    @Test
    void rekey_initiatedFromClient_connectionStillWorksAfterward() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            pair.clientConnection().rekey().get(10, TimeUnit.SECONDS);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "post-rekey".getBytes();
            pair.clientConnection().send(payload, 0, null).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(payload, received.poll(10, TimeUnit.SECONDS));
        }
    }

    @Test
    void rekey_initiatedFromServer_connectionStillWorksAfterward() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            pair.serverConnection().rekey().get(10, TimeUnit.SECONDS);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.clientConnection().setReceivedHandler(received::add);

            byte[] payload = "post-rekey-from-server".getBytes();
            pair.serverConnection().send(payload, 0, null).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(payload, received.poll(10, TimeUnit.SECONDS));
        }
    }

    @Test
    void rekey_initiatedSimultaneouslyFromBothSides_doesNotDeadlock() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            CompletableFuture<Void> clientRekey = pair.clientConnection().rekey();
            CompletableFuture<Void> serverRekey = pair.serverConnection().rekey();

            CompletableFuture.allOf(clientRekey, serverRekey).get(10, TimeUnit.SECONDS);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "after simultaneous rekey".getBytes();
            pair.clientConnection().send(payload, 0, null).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(payload, received.poll(10, TimeUnit.SECONDS));
        }
    }

    @Test
    void rekeyInterval_automaticallyRekeysWithoutBreakingConnection() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16384, Duration.ofMillis(150))) {
            Thread.sleep(500);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "still here".getBytes();
            pair.clientConnection().send(payload, 0, null).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(payload, received.poll(10, TimeUnit.SECONDS));
        }
    }

    @Test
    void connect_clientCertRequiredButNotPresented_handshakeFails() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .sslContext(TestCertificates.createServerContext())
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions))) {
            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createClientContext())
                    .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                    .build();

            OftConnector connector = OftConnector.create();
            assertThrows(Exception.class, () -> OftTestHarness.await(connector.connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions)));
        }
    }

    @Test
    void getIdentity_endpointReturnsThePeersActualAddress() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertEquals(pair.listener().getLocalEndpoint().getPort(), pair.clientConnection().getIdentity().endpoint().getPort());
        }
    }

    @Test
    void establish_connectionValidationNull_allConnectionsAccepted() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertTrue(pair.clientConnection().isConnected());
            assertTrue(pair.serverConnection().isConnected());
        }
    }

    @Test
    void establish_serverAuthentication_connectionValidationSeesIdentityAndCertificateChain() throws Exception {
        CompletableFuture<OftIdentity> observedIdentity = new CompletableFuture<>();
        CompletableFuture<java.security.cert.Certificate[]> observedChain = new CompletableFuture<>();

        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .sslContext(TestCertificates.createServerContext())
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions))) {
            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createClientContext())
                    .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                    .connectionValidation((identity, certificateChain, session) -> {
                        observedIdentity.complete(identity);
                        observedChain.complete(certificateChain);
                        return true;
                    })
                    .build();

            try (OftConnection clientConnection = OftTestHarness.await(OftConnector.create()
                    .connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions))) {
                OftIdentity identity = observedIdentity.get(10, TimeUnit.SECONDS);
                java.security.cert.Certificate[] chain = observedChain.get(10, TimeUnit.SECONDS);

                assertEquals("server", identity.info());
                assertNotNull(chain);
                assertTrue(chain.length > 0);
            }
        }
    }

    @Test
    void establish_trustedMode_connectionValidationSeesNoCertificateChainOrSession() throws Exception {
        CompletableFuture<java.security.cert.Certificate[]> observedChain = new CompletableFuture<>();
        CompletableFuture<javax.net.ssl.SSLSession> observedSession = new CompletableFuture<>();

        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.TRUSTED)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions))) {
            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .securityMode(OftSecurityMode.TRUSTED)
                    .connectionValidation((identity, certificateChain, session) -> {
                        observedChain.complete(certificateChain);
                        observedSession.complete(session);
                        return true;
                    })
                    .build();

            try (OftConnection clientConnection = OftTestHarness.await(OftConnector.create()
                    .connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions))) {
                assertNull(observedChain.get(10, TimeUnit.SECONDS));
                assertNull(observedSession.get(10, TimeUnit.SECONDS));
            }
        }
    }

    @Test
    void connect_connectionValidationReturnsFalse_throws() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.SECURE)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions))) {
            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .securityMode(OftSecurityMode.SECURE)
                    .connectionValidation((identity, certificateChain, session) -> false)
                    .build();

            assertThrows(IOException.class, () -> OftTestHarness.await(OftConnector.create()
                    .connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions)));
        }
    }

    @Test
    void handler_reassignedToNull_ignoresFutureReceivedNotifications() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            java.util.concurrent.atomic.AtomicInteger invocationCount = new java.util.concurrent.atomic.AtomicInteger();
            pair.serverConnection().setReceivedHandler(data -> invocationCount.incrementAndGet());
            pair.serverConnection().setReceivedHandler(null);

            pair.clientConnection().send("ignored".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            MessageCapture capture = new MessageCapture();
            pair.serverConnection().setReceivedHandler(capture::set);

            pair.clientConnection().send("after".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);
            capture.await(10);

            assertEquals(0, invocationCount.get());
        }
    }

    @Test
    void handler_reassignedToNull_ignoresDisconnectedNotification() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            java.util.concurrent.atomic.AtomicInteger invocationCount = new java.util.concurrent.atomic.AtomicInteger();
            pair.serverConnection().setDisconnectedHandler(exception -> invocationCount.incrementAndGet());
            pair.serverConnection().setDisconnectedHandler(null);

            pair.serverConnection().disconnect();

            assertEquals(0, invocationCount.get());
        }
    }

    @Test
    void send_withTag_raisesAcknowledgedHandlerWithTag() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            Object tag = new Object();
            CompletableFuture<Object> acknowledged = new CompletableFuture<>();
            pair.clientConnection().setAcknowledgedHandler(acknowledged::complete);

            pair.clientConnection().send("hello".getBytes(), 0, tag).completion().get(10, TimeUnit.SECONDS);

            Object acknowledgedTag = acknowledged.get(10, TimeUnit.SECONDS);
            assertTrue(tag == acknowledgedTag);
        }
    }

    @Test
    void send_withTagLargerThanPacketSize_raisesAcknowledgedHandlerOnlyAfterFinalCompletion() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16, null)) {
            Object tag = new Object();
            CompletableFuture<Object> acknowledged = new CompletableFuture<>();
            pair.clientConnection().setAcknowledgedHandler(acknowledged::complete);

            byte[] payload = new byte[1000];
            for (int i = 0; i < payload.length; i++) {
                payload[i] = (byte) i;
            }
            OftSendHandle sendHandle = pair.clientConnection().send(payload, 0, tag);

            Object acknowledgedTag = acknowledged.get(10, TimeUnit.SECONDS);
            assertTrue(tag == acknowledgedTag);
            sendHandle.completion().get(10, TimeUnit.SECONDS);
        }
    }

    @Test
    void send_withNullTag_neverRaisesAcknowledgedHandler() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            java.util.concurrent.atomic.AtomicBoolean acknowledgedHandlerRaised = new java.util.concurrent.atomic.AtomicBoolean();
            pair.clientConnection().setAcknowledgedHandler(tag -> acknowledgedHandlerRaised.set(true));

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            pair.clientConnection().send("hello".getBytes(), 0, null).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals("hello".getBytes(), received.poll(10, TimeUnit.SECONDS));
            assertFalse(acknowledgedHandlerRaised.get());
        }
    }

    @Test
    void send_cancelledWithTag_neverRaisesAcknowledgedHandler() throws Exception {
        // Unlike C#'s equivalent test, this cancels mid-transfer (via a multi-packet payload plus a
        // short delay) rather than immediately after send() returns: Java's OftSendHandle#cancel(),
        // unlike a pre-cancelled CancellationToken, races against the send loop for a single-packet
        // message and can't deterministically preempt it.
        try (OftTestHarness.Pair pair = OftTestHarness.establish(8, null)) {
            java.util.concurrent.atomic.AtomicBoolean acknowledgedHandlerRaised = new java.util.concurrent.atomic.AtomicBoolean();
            pair.clientConnection().setAcknowledgedHandler(tag -> acknowledgedHandlerRaised.set(true));

            byte[] payload = new byte[400];
            OftSendHandle handle = pair.clientConnection().send(payload, 0, new Object());
            Thread.sleep(50);
            handle.cancel();

            CompletableFuture<Void> completion = handle.completion();
            assertThrows(Exception.class, () -> completion.get(10, TimeUnit.SECONDS));

            Thread.sleep(200);
            assertFalse(acknowledgedHandlerRaised.get());
        }
    }

    @Test
    void send_negativePriority_throws() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertThrows(IllegalArgumentException.class, () -> pair.clientConnection().send("hi".getBytes(), -1, null));
        }
    }

    @Test
    void send_afterClosed_throws() throws Exception {
        OftTestHarness.Pair pair = OftTestHarness.establish();
        pair.clientConnection().close();

        assertThrows(OftDisconnectedException.class, () -> pair.clientConnection().send("hi".getBytes(), 0, null));

        pair.close();
    }

    @Test
    void rekey_afterClosed_throws() throws Exception {
        OftTestHarness.Pair pair = OftTestHarness.establish();
        pair.clientConnection().close();

        assertThrows(OftDisconnectedException.class, () -> pair.clientConnection().rekey());

        pair.close();
    }

    @Test
    void isConnected_trueUntilClosed() throws Exception {
        OftTestHarness.Pair pair = OftTestHarness.establish();

        assertTrue(pair.clientConnection().isConnected());

        pair.clientConnection().close();

        assertFalse(pair.clientConnection().isConnected());

        pair.close();
    }

    @Test
    void isConnected_falseAfterRemoteDisconnect() throws Exception {
        OftTestHarness.Pair pair = OftTestHarness.establish();

        CompletableFuture<Void> disconnectedFuture = new CompletableFuture<>();
        pair.clientConnection().setDisconnectedHandler(exception -> disconnectedFuture.complete(null));

        pair.serverConnection().close();
        disconnectedFuture.get(10, TimeUnit.SECONDS);

        assertFalse(pair.clientConnection().isConnected());

        pair.close();
    }

    @Test
    void establishAsClient_dualAuthenticationWithoutSslContext_throws() throws Exception {
        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0)))) {
            OftConnectOptions options = OftConnectOptions.builder()
                    .info("client")
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .build();

            try (Socket socket = new Socket("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                assertThrows(IllegalArgumentException.class,
                        () -> DefaultOftConnection.establishAsClient(socket, "127.0.0.1", options));
            }
        }
    }

    @Test
    void establishAsClient_authenticationWithoutSslContext_fallsBackToDefaultTrustStoreAndRejectsUntrustedCertificate() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .sslContext(TestCertificates.createServerContext())
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions))) {
            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                    .build();

            try (Socket socket = new Socket("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                assertThrows(IOException.class,
                        () -> DefaultOftConnection.establishAsClient(socket, "127.0.0.1", connectOptions));
            }
        }
    }

    @Test
    void establishAsServer_incompatibleHailVersion_throws() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.TRUSTED)
                .build();

        try (ServerSocket serverSocket = new ServerSocket(0)) {
            try (Socket clientSocket = new Socket("127.0.0.1", serverSocket.getLocalPort());
                 Socket acceptedSocket = serverSocket.accept()) {
                OftFrameStream clientFrameStream = new OftFrameStream(clientSocket.getInputStream(), clientSocket.getOutputStream());
                clientFrameStream.write(Hail.newBuilder().setVersion("oft/999").setInfo("rogue").build());

                assertThrows(IllegalStateException.class, () -> DefaultOftConnection.establishAsServer(acceptedSocket, hostOptions));
            }
        }
    }

    @Test
    void establishAsServer_peerClosesBeforeSendingHail_throws() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.TRUSTED)
                .build();

        try (ServerSocket serverSocket = new ServerSocket(0)) {
            Socket clientSocket = new Socket("127.0.0.1", serverSocket.getLocalPort());
            Socket acceptedSocket = serverSocket.accept();
            clientSocket.close();

            assertThrows(IOException.class, () -> DefaultOftConnection.establishAsServer(acceptedSocket, hostOptions));
            acceptedSocket.close();
        }
    }

    @Test
    void establishAsServer_peerShutsDownOutputWithoutSendingHail_throwsCleanEofMessage() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.TRUSTED)
                .build();

        try (ServerSocket serverSocket = new ServerSocket(0);
             Socket clientSocket = new Socket("127.0.0.1", serverSocket.getLocalPort());
             Socket acceptedSocket = serverSocket.accept()) {
            // Shutting down only the write half (rather than closing outright) lets the server's
            // own Hail write still succeed, so it's the clean-EOF branch that's hit here, not the
            // write-failure branch already covered above.
            clientSocket.shutdownOutput();

            IOException exception = assertThrows(IOException.class, () -> DefaultOftConnection.establishAsServer(acceptedSocket, hostOptions));
            assertEquals("Connection closed before completing the OFT hail handshake.", exception.getMessage());
        }
    }

    private static final class MessageCapture {
        private final java.util.concurrent.CountDownLatch latch = new java.util.concurrent.CountDownLatch(1);

        void set(byte[] data) {
            this.latch.countDown();
        }

        void await(int timeoutSeconds) throws InterruptedException {
            this.latch.await(timeoutSeconds, TimeUnit.SECONDS);
        }
    }
}
