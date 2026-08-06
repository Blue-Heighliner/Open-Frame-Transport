package org.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.openframetransport.proto.Hail;

import java.net.InetSocketAddress;
import java.net.Socket;
import java.time.Duration;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class LivenessPollingTest {
    @Test
    void poll_keepsIdleConnectionAliveBeyondPollTimeout() throws Exception {
        try (OftTestHarness.Pair pair = OftTestHarness.establish(
                16384, null, OftSecurityMode.AUTHENTICATION, Duration.ofMillis(50), Duration.ofMillis(200))) {
            java.util.concurrent.atomic.AtomicBoolean serverClosed = new java.util.concurrent.atomic.AtomicBoolean(false);
            pair.serverConnection().addDisconnectedListener(exception -> serverClosed.set(true));

            // No application traffic at all in either direction for well beyond pollTimeout: if the
            // background Poll packets weren't keeping the connection alive, the watchdog would have
            // already closed it.
            Thread.sleep(500);

            assertFalse(serverClosed.get());
        }
    }

    @Test
    void poll_closesConnectionWhenPeerGoesSilent() throws Exception {
        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .securityMode(OftSecurityMode.INSECURE)
                .pollInterval(Duration.ofMillis(50))
                .pollTimeout(Duration.ofMillis(200))
                .build();

        try (OftListener listener = OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0), hostOptions)) {
            CompletableFuture<OftConnection> serverConnectionFuture = new CompletableFuture<>();
            listener.addConnectedListener(serverConnectionFuture::complete);

            try (Socket rawSocket = new Socket("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                OftFrameStream frameStream = new OftFrameStream(rawSocket.getInputStream(), rawSocket.getOutputStream());
                frameStream.write(Hail.newBuilder().setVersion(OftProtocolVersion.CURRENT).setInfo("silent-client").build());
                frameStream.readHail();

                OftConnection serverConnection = serverConnectionFuture.get(10, TimeUnit.SECONDS);

                CompletableFuture<Throwable> disconnectedFuture = new CompletableFuture<>();
                serverConnection.addDisconnectedListener(disconnectedFuture::complete);

                // The raw client above never sends another byte (no Poll, nothing) after the hail:
                // the server side must notice via its liveness watchdog and close on its own.
                Throwable exception = disconnectedFuture.get(10, TimeUnit.SECONDS);
                assertNotNull(exception);
                assertInstanceOf(java.util.concurrent.TimeoutException.class, exception);
            }
        }
    }
}
