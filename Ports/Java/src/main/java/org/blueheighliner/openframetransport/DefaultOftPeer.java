package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/**
 * {@inheritDoc}
 */
final class DefaultOftPeer implements OftPeer {
    private record HostPort(String host, int port) {
    }

    /** Wraps an {@link IOException} so it can cross {@link ConcurrentHashMap#computeIfAbsent}, which only propagates unchecked exceptions. */
    private static final class UncheckedConnectException extends RuntimeException {
        UncheckedConnectException(IOException cause) {
            super(cause);
        }
    }

    private final OftPeerOptions options;
    private final OftConnector connector;
    private final OftConnectOptions connectOptions;
    private final OftHoster hoster;
    private final OftHostOptions hostOptions;

    private final Map<HostPort, OftConnection> outboundConnections = new ConcurrentHashMap<>();
    private final Set<OftConnection> inboundConnections = ConcurrentHashMap.newKeySet();
    private final BufferedHandlerSlot<Consumer<OftPeerReception>> receivedSlot = new BufferedHandlerSlot<>();

    /**
     * How long a connection must have had no pending data (see
     * {@link OftConnection#hasPendingData()}) before it becomes eligible for automatic eviction
     * (idle, lifetime, or excess-count based) at all - a fixed value, not configurable, giving the
     * underlying TLS/TCP layers time to actually flush and acknowledge everything after the last
     * application-level message completes, rather than evicting the instant
     * {@link OftConnection#hasPendingData()} turns {@code false}.
     */
    private static final Duration EVICTION_GRACE_PERIOD = Duration.ofSeconds(30);

    /**
     * How often {@link #runEviction()} checks connections against {@link OftPeerOptions#idleTimeout()},
     * {@link OftPeerOptions#maxConnectionLifetime()}, and {@link OftPeerOptions#maxConnectionCount()}
     * - a fixed value, not configurable (see {@link OftPeer}'s own documentation). Since eviction
     * only ever runs on this cadence, neither of those two duration-based options can take effect any
     * sooner than this floor.
     */
    private static final Duration EVICTION_CHECK_INTERVAL = Duration.ofSeconds(30);

    /**
     * When each tracked connection was first observed, by {@link #runEviction()}, to have no
     * pending data (see {@link OftConnection#hasPendingData()}) - cleared again the moment a
     * connection is next observed with pending data, so a connection that resumes sending after a
     * quiet period gets a fresh {@link #EVICTION_GRACE_PERIOD} once it finishes again. A connection
     * isn't a candidate for any automatic eviction (idle, lifetime, or excess-count) until this has
     * aged past {@link #EVICTION_GRACE_PERIOD}.
     */
    private final Map<OftConnection, Instant> pendingDataClearedAt = new ConcurrentHashMap<>();

    private final ScheduledExecutorService evictionExecutor = Executors.newSingleThreadScheduledExecutor(runnable -> {
        Thread thread = new Thread(runnable, "oft-peer-eviction");
        thread.setDaemon(true);
        return thread;
    });

    private volatile OftListener listener;
    private volatile boolean disposed;

    DefaultOftPeer(OftPeerOptions options) {
        this.options = options;
        this.connector = OftConnector.create();
        this.hoster = OftHoster.create();

        this.connectOptions = OftConnectOptions.builder()
                .info(options.info())
                .sslContext(options.sslContext())
                .connectionValidation(options.connectionValidation())
                .maxPacketDataSize(options.maxPacketDataSize())
                .rekeyInterval(options.rekeyInterval())
                .securityMode(options.securityMode())
                .pollInterval(options.pollInterval())
                .pollTimeout(options.pollTimeout())
                .build();

        this.hostOptions = OftHostOptions.builder()
                .info(options.info())
                .sslContext(options.sslContext())
                .connectionValidation(options.connectionValidation())
                .maxPacketDataSize(options.maxPacketDataSize())
                .rekeyInterval(options.rekeyInterval())
                .securityMode(options.securityMode())
                .pollInterval(options.pollInterval())
                .pollTimeout(options.pollTimeout())
                .build();

        long intervalMillis = EVICTION_CHECK_INTERVAL.toMillis();
        this.evictionExecutor.scheduleAtFixedRate(this::runEviction, intervalMillis, intervalMillis, TimeUnit.MILLISECONDS);
    }

    @Override
    public boolean isConnected() {
        return !this.disposed;
    }

    @Override
    public InetSocketAddress getLocalEndpoint() {
        OftListener currentListener = this.listener;
        return currentListener == null ? null : currentListener.getLocalEndpoint();
    }

    @Override
    public void setReceivedHandler(Consumer<OftPeerReception> handler) {
        this.receivedSlot.setHandler(handler);
    }

    @Override
    public Consumer<OftPeerReception> getReceivedHandler() {
        return this.receivedSlot.getHandler();
    }

    @Override
    public synchronized void listen(InetSocketAddress listenEndpoint) throws IOException {
        if (this.disposed) {
            throw new IllegalStateException("This peer is no longer connected.");
        }

        OftListener newListener = this.hoster.host(listenEndpoint, this.hostOptions);
        newListener.setConnectedHandler(connection -> {
            this.inboundConnections.add(connection);
            trackConnection(connection, this.inboundConnections::remove);
        });
        this.listener = newListener;
    }

    @Override
    public synchronized void stopListening() {
        if (this.disposed) {
            throw new IllegalStateException("This peer is no longer connected.");
        }

        closeListener();
    }

    private void closeListener() {
        OftListener currentListener = this.listener;
        if (currentListener == null) {
            return;
        }

        currentListener.close();
        this.listener = null;
    }

    @Override
    public OftSendHandle send(String host, int port, byte[] data, int priority) throws IOException {
        if (this.disposed) {
            throw new OftDisconnectedException("This peer is no longer connected.");
        }

        OftConnection connection = getOrConnect(host, port);
        return connection.send(data, priority);
    }

    @Override
    public CompletableFuture<Void> rekey() {
        if (this.disposed) {
            throw new OftDisconnectedException("This peer is no longer connected.");
        }

        List<OftConnection> tracked = getTrackedConnections();
        return CompletableFuture.allOf(tracked.stream().map(OftConnection::rekey).toArray(CompletableFuture[]::new));
    }

    @Override
    public void drop() {
        if (this.disposed) {
            throw new IllegalStateException("This peer is no longer connected.");
        }

        for (OftConnection connection : getTrackedConnections()) {
            connection.disconnect();
        }
    }

    @Override
    public synchronized void close() {
        if (this.disposed) {
            return;
        }

        this.disposed = true;

        this.evictionExecutor.shutdown();

        closeListener();

        // disconnect(), not close(): an immediate teardown of every held connection, without
        // waiting for each one's background threads to finish - matching the C# reference
        // implementation's IOftPeer.Dispose(), which likewise uses IOftConnection.Dispose() (not the
        // graceful, thread-joining Disconnect()) here.
        for (OftConnection connection : getTrackedConnections()) {
            connection.disconnect();
        }

        // Discards anything still buffered for lack of a callback, matching the per-connection
        // cleanup in DefaultOftConnection#close and DefaultOftListener#close.
        this.receivedSlot.clear();
    }

    private OftConnection getOrConnect(String host, int port) throws IOException {
        HostPort key = new HostPort(host, port);

        try {
            return this.outboundConnections.computeIfAbsent(key, ignored -> {
                try {
                    // Tracking the connection before it's exposed via outboundConnections keeps a
                    // concurrent send() on the same host/port from ever observing a connection that
                    // isn't wired up yet - unrelated to message loss (received/disconnected
                    // notifications are buffered, see BufferedHandlerSlot), just atomicity of this
                    // map's contents.
                    OftConnection connection = this.connector.connect(host, port, this.connectOptions);
                    trackConnection(connection, tracked -> this.outboundConnections.remove(key, tracked));
                    return connection;
                } catch (IOException e) {
                    throw new UncheckedConnectException(e);
                }
            });
        } catch (UncheckedConnectException e) {
            throw (IOException) e.getCause();
        }
    }

    /**
     * Forwards a tracked connection's received messages to this peer's own
     * {@link #receivedSlot}, and runs {@code onDisconnectedTrackingCleanup} when it disconnects to
     * untrack it (from {@link #outboundConnections} or {@link #inboundConnections} as appropriate)
     * - this peer has no external disconnected notification of its own to forward to (see
     * {@link OftPeer#setReceivedHandler}'s own doc comment for why).
     */
    private void trackConnection(OftConnection connection, Consumer<OftConnection> onDisconnectedTrackingCleanup) {
        connection.setReceivedHandler(data -> this.receivedSlot.raise(
                handler -> handler.accept(new DefaultOftPeerReception(data, connection.getIdentity()))));
        connection.setDisconnectedHandler(exception -> {
            onDisconnectedTrackingCleanup.accept(connection);
            this.pendingDataClearedAt.remove(connection);
        });
    }

    /**
     * Every connection this peer currently holds, both outbound and inbound.
     */
    private List<OftConnection> getTrackedConnections() {
        List<OftConnection> tracked = new ArrayList<>(this.outboundConnections.values());
        tracked.addAll(this.inboundConnections);
        return tracked;
    }

    private void runEviction() {
        Instant now = Instant.now();
        List<OftConnection> tracked = getTrackedConnections();

        // A connection with pending/unacknowledged data (see OftConnection#hasPendingData()) is
        // never auto-disconnected here, regardless of which eviction condition it would otherwise
        // meet: doing so could silently drop a message that's still queued, in flight, or only
        // partially reassembled. It's only a candidate once all of its data has been acknowledged,
        // and even then, only once EVICTION_GRACE_PERIOD has passed since that happened - giving the
        // underlying TLS/TCP layers time to actually flush and acknowledge everything rather than
        // evicting the instant hasPendingData() turns false.
        List<OftConnection> evictionCandidates = new ArrayList<>();
        for (OftConnection connection : tracked) {
            if (connection.hasPendingData()) {
                this.pendingDataClearedAt.remove(connection);
                continue;
            }

            Instant clearedAt = this.pendingDataClearedAt.computeIfAbsent(connection, ignored -> now);
            if (Duration.between(clearedAt, now).compareTo(EVICTION_GRACE_PERIOD) >= 0) {
                evictionCandidates.add(connection);
            }
        }

        Set<OftConnection> toDisconnect = ConcurrentHashMap.newKeySet();
        for (OftConnection connection : evictionCandidates) {
            Instant lastActivity = connection.getLastSentAt().isAfter(connection.getLastReceivedAt())
                    ? connection.getLastSentAt() : connection.getLastReceivedAt();
            if (Duration.between(lastActivity, now).compareTo(this.options.idleTimeout()) > 0
                    || Duration.between(connection.getConnectedAt(), now).compareTo(this.options.maxConnectionLifetime()) > 0) {
                toDisconnect.add(connection);
            }
        }

        int remainingCount = tracked.size() - toDisconnect.size();
        if (remainingCount > this.options.maxConnectionCount()) {
            int excess = remainingCount - this.options.maxConnectionCount();
            evictionCandidates.stream()
                    .filter(connection -> !toDisconnect.contains(connection))
                    .sorted((a, b) -> a.getConnectedAt().compareTo(b.getConnectedAt()))
                    .limit(excess)
                    .forEach(toDisconnect::add);
        }

        for (OftConnection connection : toDisconnect) {
            connection.disconnect();
        }
    }
}
