package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.blueheighliner.openframetransport.proto.Hail;

import java.net.InetSocketAddress;
import java.net.Socket;
import java.time.Duration;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class TrustedModeTest {
    @Test
    void trusted_connectionEstablishesAndExchangesMessages() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16384, null, OftSecurityMode.TRUSTED, Duration.ofSeconds(1), Duration.ofSeconds(5))) {
            BlockingQueue<byte[]> received = new ArrayBlockingQueue<>(1);
            pair.serverConnection().setReceivedHandler(received::add);

            byte[] payload = "hello over plain tcp".getBytes();
            pair.clientConnection().send(payload, 0, null).completion().get(10, TimeUnit.SECONDS);

            assertEquals("hello over plain tcp", new String(received.poll(10, TimeUnit.SECONDS)));
        }
    }

    @Test
    void trusted_noTlsSession_identityHasNoCertificate() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16384, null, OftSecurityMode.TRUSTED, Duration.ofSeconds(1), Duration.ofSeconds(5))) {
            assertNull(pair.clientConnection().getIdentity().certificate());
            assertNull(pair.serverConnection().getIdentity().certificate());
        }
    }

    @Test
    void host_serverAuthenticationModeWithoutSslContext_throws() {
        OftHostOptions options = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .build();

        assertThrows(IllegalArgumentException.class,
                () -> OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), options));
    }

    @Test
    void host_trustedWithoutSslContext_succeeds() throws Exception {
        OftHostOptions options = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.TRUSTED)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), options))) {
            assertNotNull(listener);
        }
    }

    @Test
    void host_secureWithoutSslContext_succeeds() throws Exception {
        OftHostOptions options = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.SECURE)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), options))) {
            assertNotNull(listener);
        }
    }

    @Test
    void rekey_onTrustedConnection_isNoOp() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(16384, null, OftSecurityMode.TRUSTED, Duration.ofSeconds(1), Duration.ofSeconds(5))) {
            pair.clientConnection().rekey().get(10, TimeUnit.SECONDS);
        }
    }

    @Test
    void trusted_hailIsExchangedDirectlyOverRawTcp() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.TRUSTED)
                .build();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), hostOptions))) {
            CompletableFuture<OftConnection> serverConnectionFuture = new CompletableFuture<>();
            listener.setConnectedHandler(serverConnectionFuture::complete);

            try (Socket rawSocket = new Socket("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                // No TLS handshake happened above: the hail is written as the very first bytes on
                // the raw TCP stream, immediately after connecting.
                OftFrameStream frameStream = new OftFrameStream(rawSocket.getInputStream(), rawSocket.getOutputStream());
                frameStream.write(Hail.newBuilder().setVersion(OftProtocolVersion.CURRENT).setInfo("raw-client").build());
                Hail serverHail = frameStream.readHail();
                assertNotNull(serverHail);
                assertEquals(OftProtocolVersion.CURRENT, serverHail.getVersion());

                OftConnection serverConnection = serverConnectionFuture.get(10, TimeUnit.SECONDS);
                assertEquals("raw-client", serverConnection.getIdentity().info());
            }
        }
    }
}
