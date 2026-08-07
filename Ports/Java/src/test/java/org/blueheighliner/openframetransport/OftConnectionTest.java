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
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftConnectionTest {

    @Test
    void establish_exchangesInfoAsHail() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertEquals("server", pair.clientConnection().getRemoteInfo());
            assertEquals("client", pair.serverConnection().getRemoteInfo());
        }
    }

    @Test
    void send_small_deliveredAsUnit() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "hello".getBytes();
            pair.clientConnection().send(payload, 0).completion().get(10, TimeUnit.SECONDS);

            byte[] result = received.poll(10, TimeUnit.SECONDS);
            assertArrayEquals(payload, result);
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

            pair.clientConnection().send(payload, 3).completion().get(10, TimeUnit.SECONDS);

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

            OftSendHandle lowSend = pair.clientConnection().send(lowPriorityPayload, 0);
            Thread.sleep(20);
            OftSendHandle highSend = pair.clientConnection().send(highPriorityPayload, 5);

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

            OftSendHandle handle = pair.clientConnection().send("should not arrive".getBytes(), 0);
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
            OftSendHandle handle = pair.clientConnection().send(payload, 0);
            Thread.sleep(50);
            handle.cancel();

            CompletableFuture<Void> completion = handle.completion();
            assertThrows(Exception.class, () -> completion.get(10, TimeUnit.SECONDS));

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] followUp = "still alive".getBytes();
            pair.clientConnection().send(followUp, 0).completion().get(10, TimeUnit.SECONDS);

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
            pair.clientConnection().send(payload, 0).completion().get(10, TimeUnit.SECONDS);

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
            pair.serverConnection().send(payload, 0).completion().get(10, TimeUnit.SECONDS);

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
            pair.clientConnection().send(payload, 0).completion().get(10, TimeUnit.SECONDS);

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
            pair.clientConnection().send(payload, 0).completion().get(10, TimeUnit.SECONDS);

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

        try (OftListener listener = OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions)) {
            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .sslContext(TestCertificates.createClientContext())
                    .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                    .build();

            OftConnector connector = OftConnector.create();
            assertThrows(Exception.class, () -> connector.connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions));
        }
    }

    @Test
    void getRemoteEndpoint_returnsThePeersActualAddress() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertEquals(pair.listener().getLocalEndpoint().getPort(), pair.clientConnection().getRemoteEndpoint().getPort());
        }
    }

    @Test
    void handler_reassignedToNull_ignoresFutureReceivedNotifications() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            java.util.concurrent.atomic.AtomicInteger invocationCount = new java.util.concurrent.atomic.AtomicInteger();
            pair.serverConnection().setReceivedHandler(data -> invocationCount.incrementAndGet());
            pair.serverConnection().setReceivedHandler(null);

            pair.clientConnection().send("ignored".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);

            MessageCapture capture = new MessageCapture();
            pair.serverConnection().setReceivedHandler(capture::set);

            pair.clientConnection().send("after".getBytes(), 0).completion().get(10, TimeUnit.SECONDS);
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
    void send_negativePriority_throws() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish()) {
            assertThrows(IllegalArgumentException.class, () -> pair.clientConnection().send("hi".getBytes(), -1));
        }
    }

    @Test
    void send_afterClosed_throws() throws Exception {
        OftTestHarness.Pair pair = OftTestHarness.establish();
        pair.clientConnection().close();

        assertThrows(IllegalStateException.class, () -> pair.clientConnection().send("hi".getBytes(), 0));

        pair.close();
    }

    @Test
    void establishAsClient_dualAuthenticationWithoutSslContext_throws() throws Exception {
        try (OftListener listener = OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0))) {
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

        try (OftListener listener = OftHoster.create().host(new java.net.InetSocketAddress("127.0.0.1", 0), hostOptions)) {
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
