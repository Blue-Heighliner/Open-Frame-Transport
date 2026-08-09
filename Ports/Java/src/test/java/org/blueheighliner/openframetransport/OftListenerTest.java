package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftListenerTest {
    @Test
    void close_calledTwice_isIdempotent() throws Exception {
        OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0)));
        listener.close();
        listener.close();
    }

    @Test
    void handler_reassignedToNull_stopsReceivingNotifications() throws Exception {
        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0)))) {
            AtomicInteger invocationCount = new AtomicInteger();
            listener.setConnectedHandler(c -> invocationCount.incrementAndGet());
            listener.setConnectedHandler(null);

            CompletableFuture<OftConnection> accepted = new CompletableFuture<>();
            listener.setConnectedHandler(accepted::complete);

            OftConnectOptions connectOptions = OftConnectOptions.builder().info("client").build();
            try (OftConnection connection = OftTestHarness.await(OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions))) {
                accepted.get(10, TimeUnit.SECONDS);
            }

            assertEquals(0, invocationCount.get());
        }
    }

    @Test
    void handleAccepted_malformedClient_doesNotCrashListener() throws Exception {
        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0),
                OftHostOptions.builder().info("server").securityMode(OftSecurityMode.TRUSTED).build()))) {
            CompletableFuture<OftConnection> accepted = new CompletableFuture<>();
            listener.setConnectedHandler(accepted::complete);

            try (Socket rogue = new Socket("127.0.0.1", listener.getLocalEndpoint().getPort())) {
                rogue.getOutputStream().write(new byte[] {1, 2, 3, 4, 5});
                rogue.getOutputStream().flush();
            }

            // The listener must still be usable afterward: a well-behaved client can still connect.
            OftConnectOptions connectOptions = OftConnectOptions.builder().info("client").securityMode(OftSecurityMode.TRUSTED).build();
            try (OftConnection connection = OftTestHarness.await(OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions))) {
                assertNotNull(accepted.get(10, TimeUnit.SECONDS));
            }
        }
    }

    @Test
    void handler_assignedAfterAcceptStillReceivesIt() throws Exception {
        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(new InetSocketAddress("127.0.0.1", 0)))) {
            OftConnectOptions connectOptions = OftConnectOptions.builder().info("client").build();

            // No handler is assigned yet, so the accept below races against handleAccepted's own
            // thread with nothing here to synchronize on but a plain sleep - this is exactly the
            // scenario that would silently lose the connection notification without the connected
            // notification being backed by BufferedHandlerSlot.
            try (OftConnection connection = OftTestHarness.await(OftConnector.create().connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions))) {
                Thread.sleep(200);

                CompletableFuture<OftConnection> accepted = new CompletableFuture<>();
                listener.setConnectedHandler(accepted::complete);

                assertNotNull(accepted.get(10, TimeUnit.SECONDS));
            }
        }
    }

    @Test
    void host_withPort_listensOnAnyAddressAtTheGivenPort() throws Exception {
        int port = OftTestHarness.reserveFreePort();

        try (OftListener listener = OftTestHarness.await(OftHoster.create().host(port))) {
            assertTrue(listener.getLocalEndpoint().getAddress().isAnyLocalAddress());
            assertEquals(port, listener.getLocalEndpoint().getPort());
        }
    }
}
