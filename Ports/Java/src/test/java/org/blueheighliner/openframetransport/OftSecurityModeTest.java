package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.net.InetSocketAddress;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftSecurityModeTest {
    @Test
    void secure_noSslContextConfigured_connectionEstablishesAndExchangesMessages() throws Exception {
        // Secure mode needs no SSLContext from either side: the host generates its own throwaway
        // identity internally, and the connecting side accepts it unconditionally.
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16384, null, OftSecurityMode.SECURE, java.time.Duration.ofSeconds(1), java.time.Duration.ofSeconds(5))) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "hello under secure mode".getBytes();
            pair.clientConnection().send(payload, 0).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(payload, received.poll(10, TimeUnit.SECONDS));
        }
    }

    @Test
    void secure_configuredSslContextIsIgnored() throws Exception {
        // A caller-supplied sslContext is meaningless under SECURE mode (nothing validates it), so
        // hosting must succeed even though this context is never actually presented.
        OftHostOptions options = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.SECURE)
                .sslContext(TestCertificates.createServerContext())
                .build();

        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), options)) {
            assertNotNull(listener);
        }
    }

    @Test
    void dualAuthentication_connectWithoutSslContext_throws() {
        OftConnectOptions options = OftConnectOptions.builder()
                .info("client")
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .build();

        assertThrows(IllegalArgumentException.class,
                () -> OftConnector.create().connect("127.0.0.1", OftTestHarness.reserveFreePort(), options));
    }

    @Test
    void dualAuthentication_bothSidesPresentCertificates_connectionEstablishesAndExchangesMessages() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .sslContext(TestCertificates.createPeerContext())
                .build();

        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), hostOptions)) {
            CompletableFuture<OftConnection> serverConnectionFuture = new CompletableFuture<>();
            listener.setConnectedHandler(serverConnectionFuture::complete);

            OftConnectOptions connectOptions = OftConnectOptions.builder()
                    .info("client")
                    .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                    .sslContext(TestCertificates.createPeerContext())
                    .build();

            OftConnection clientConnection = OftConnector.create()
                    .connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions);
            OftConnection serverConnection = serverConnectionFuture.get(10, TimeUnit.SECONDS);

            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            serverConnection.setReceivedHandler(received::add);

            byte[] payload = "hello under mutual tls".getBytes();
            clientConnection.send(payload, 0).completion().get(10, TimeUnit.SECONDS);

            assertArrayEquals(payload, received.poll(10, TimeUnit.SECONDS));

            clientConnection.close();
            serverConnection.close();
        }
    }
}
