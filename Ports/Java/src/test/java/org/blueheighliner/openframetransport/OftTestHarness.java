package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.time.Duration;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.BiConsumer;
import java.util.function.Consumer;

/** Shared setup for tests that need a live, established OFT connection over real TCP/TLS on loopback. */
final class OftTestHarness {
    static final Duration DEFAULT_TIMEOUT = Duration.ofSeconds(10);

    private OftTestHarness() {
    }

    /**
     * Blocks for {@code future} to complete, unwrapping {@link ExecutionException} to rethrow its
     * cause directly (as an {@link Exception}, matching every test method's own {@code throws
     * Exception}) - so an {@code assertThrows(SomeException.class, ...)} written against the old,
     * directly-blocking {@code connect()}/{@code host()} APIs still sees the same exception type
     * now that those return a {@link CompletableFuture} instead.
     */
    static <T> T await(CompletableFuture<T> future) throws Exception {
        try {
            return future.get(DEFAULT_TIMEOUT.toSeconds(), TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof Exception exception) {
                throw exception;
            }

            throw e;
        }
    }

    record Pair(OftListener listener, OftConnection serverConnection, OftConnection clientConnection) implements AutoCloseable {
        @Override
        public void close() {
            this.clientConnection.close();
            this.serverConnection.close();
            this.listener.close();
        }
    }

    static Pair establish() throws Exception {
        return establish(16384, null);
    }

    static Pair establish(int maxPacketDataSize, Duration rekeyInterval) throws Exception {
        return establish(maxPacketDataSize, rekeyInterval, OftSecurityMode.SERVER_AUTHENTICATION, Duration.ofSeconds(1), Duration.ofSeconds(5));
    }

    static Pair establish(int maxPacketDataSize, Duration rekeyInterval, OftSecurityMode securityMode, Duration pollInterval, Duration pollTimeout) throws Exception {
        boolean needsServerContext = securityMode == OftSecurityMode.SERVER_AUTHENTICATION || securityMode == OftSecurityMode.DUAL_AUTHENTICATION;

        OftHostOptions hostOptions = OftHostOptions.builder()
                .info("server")
                .sslContext(needsServerContext ? TestCertificates.createServerContext() : null)
                .maxPacketDataSize(maxPacketDataSize)
                .rekeyInterval(rekeyInterval)
                .securityMode(securityMode)
                .pollInterval(pollInterval)
                .pollTimeout(pollTimeout)
                .build();

        OftHoster hoster = OftHoster.create();
        OftListener listener = await(hoster.host(new InetSocketAddress("127.0.0.1", 0), hostOptions));
        CompletableFuture<OftConnection> serverConnectionFuture = new CompletableFuture<>();
        listener.setConnectedHandler(serverConnectionFuture::complete);

        OftConnectOptions connectOptions = OftConnectOptions.builder()
                .info("client")
                .sslContext(needsServerContext ? TestCertificates.createClientContext() : null)
                .maxPacketDataSize(maxPacketDataSize)
                .rekeyInterval(rekeyInterval)
                .securityMode(securityMode)
                .pollInterval(pollInterval)
                .pollTimeout(pollTimeout)
                .build();

        OftConnector connector = OftConnector.create();
        OftConnection clientConnection = await(connector.connect("127.0.0.1", listener.getLocalEndpoint().getPort(), connectOptions));
        OftConnection serverConnection = serverConnectionFuture.get(DEFAULT_TIMEOUT.toSeconds(), TimeUnit.SECONDS);

        return new Pair(listener, serverConnection, clientConnection);
    }

    static void waitUntil(java.util.function.BooleanSupplier condition, Duration timeout) throws TimeoutException {
        long deadline = System.currentTimeMillis() + timeout.toMillis();
        while (!condition.getAsBoolean()) {
            if (System.currentTimeMillis() > deadline) {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            try {
                Thread.sleep(20);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new RuntimeException(e);
            }
        }
    }

    /** Reserves a TCP port on loopback and immediately releases it, for tests that need a port nothing is listening on. */
    static int reserveFreePort() throws IOException {
        try (ServerSocket socket = new ServerSocket(0)) {
            return socket.getLocalPort();
        }
    }

    /**
     * Wraps an {@link OftListener} and tracks the connections it has accepted, since
     * {@link OftListener} itself doesn't (see its own doc comment) - mirrors the C# reference
     * implementation's test-only {@code TrackedListener}.
     */
    static final class TrackedListener implements AutoCloseable {
        private final OftListener listener;
        private final List<OftConnection> connections = new CopyOnWriteArrayList<>();

        /** Called whenever this listener accepts a new connection, in addition to this type's own tracking of {@link #getConnections}. */
        volatile Consumer<OftConnection> onConnectedExtra;

        /**
         * Called whenever a connection this listener accepted disconnects, in addition to this
         * type's own tracking - since each such connection's disconnected callback is already used
         * internally for that tracking (and is single-slot), a test that needs its own
         * per-connection disconnected notification goes through this field instead of assigning the
         * connection's disconnected callback directly.
         */
        volatile BiConsumer<OftConnection, Throwable> onConnectionDisconnectedExtra;

        private TrackedListener(OftListener listener) {
            this.listener = listener;
            listener.setConnectedHandler(this::onConnected);
        }

        static TrackedListener start(InetSocketAddress listenEndpoint, OftHostOptions options) throws Exception {
            return new TrackedListener(await(OftHoster.create().host(listenEndpoint, options)));
        }

        InetSocketAddress getLocalEndpoint() {
            return this.listener.getLocalEndpoint();
        }

        List<OftConnection> getConnections() {
            return List.copyOf(this.connections);
        }

        private void onConnected(OftConnection connection) {
            this.connections.add(connection);
            connection.setDisconnectedHandler(exception -> {
                this.connections.remove(connection);
                if (this.onConnectionDisconnectedExtra != null) {
                    this.onConnectionDisconnectedExtra.accept(connection, exception);
                }
            });

            if (this.onConnectedExtra != null) {
                this.onConnectedExtra.accept(connection);
            }
        }

        @Override
        public void close() {
            this.listener.close();
        }
    }
}
