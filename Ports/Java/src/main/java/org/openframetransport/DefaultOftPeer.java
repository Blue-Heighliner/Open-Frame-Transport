package org.openframetransport;

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
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

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
    private final List<OftReceivedListener> receivedListeners = new CopyOnWriteArrayList<>();

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
                .maxPacketDataSize(options.maxPacketDataSize())
                .rekeyInterval(options.rekeyInterval())
                .securityMode(options.securityMode())
                .pollInterval(options.pollInterval())
                .pollTimeout(options.pollTimeout())
                .build();

        this.hostOptions = OftHostOptions.builder()
                .info(options.info())
                .sslContext(options.sslContext())
                .maxPacketDataSize(options.maxPacketDataSize())
                .rekeyInterval(options.rekeyInterval())
                .securityMode(options.securityMode())
                .pollInterval(options.pollInterval())
                .pollTimeout(options.pollTimeout())
                .build();

        long intervalMillis = options.evictionCheckInterval().toMillis();
        this.evictionExecutor.scheduleAtFixedRate(this::runEviction, intervalMillis, intervalMillis, TimeUnit.MILLISECONDS);
    }

    @Override
    public InetSocketAddress getLocalEndpoint() {
        OftListener currentListener = this.listener;
        return currentListener == null ? null : currentListener.getLocalEndpoint();
    }

    @Override
    public void addReceivedListener(OftReceivedListener listener) {
        this.receivedListeners.add(listener);
    }

    @Override
    public void removeReceivedListener(OftReceivedListener listener) {
        this.receivedListeners.remove(listener);
    }

    @Override
    public synchronized void open(InetSocketAddress listenEndpoint) throws IOException {
        OftListener newListener = this.hoster.host(listenEndpoint, this.hostOptions);
        newListener.addConnectedListener(this::onInboundConnectionEstablished);
        this.listener = newListener;
    }

    @Override
    public synchronized void stop() {
        OftListener currentListener = this.listener;
        if (currentListener == null) {
            return;
        }

        currentListener.close();
        this.listener = null;
    }

    @Override
    public OftSendHandle send(String host, int port, byte[] data, int priority) throws IOException {
        OftConnection connection = getOrConnect(host, port);
        return connection.send(data, priority);
    }

    @Override
    public CompletableFuture<Void> rekey() {
        List<OftConnection> tracked = getTrackedConnections();
        return CompletableFuture.allOf(tracked.stream().map(OftConnection::rekey).toArray(CompletableFuture[]::new));
    }

    @Override
    public void disconnect() {
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

        stop();

        for (OftConnection connection : getTrackedConnections()) {
            connection.close();
        }
    }

    private OftConnection getOrConnect(String host, int port) throws IOException {
        HostPort key = new HostPort(host, port);

        try {
            return this.outboundConnections.computeIfAbsent(key, ignored -> {
                try {
                    // Attaching these listeners via onEstablished, rather than after connect()
                    // returns, guarantees they're in place before the connection starts processing
                    // inbound packets - otherwise a peer that replies the instant the connection is
                    // up could have its first message delivered (and discarded, for lack of a
                    // subscriber) before connect() ever returns.
                    return this.connector.connect(host, port, this.connectOptions, connection -> trackOutbound(key, connection));
                } catch (IOException e) {
                    throw new UncheckedConnectException(e);
                }
            });
        } catch (UncheckedConnectException e) {
            throw (IOException) e.getCause();
        }
    }

    private void trackOutbound(HostPort key, OftConnection connection) {
        connection.addReceivedListener(this::onMessageReceived);
        connection.addDisconnectedListener(exception -> this.outboundConnections.remove(key, connection));
    }

    private void onInboundConnectionEstablished(OftConnection connection) {
        this.inboundConnections.add(connection);
        connection.addReceivedListener(this::onMessageReceived);
        connection.addDisconnectedListener(exception -> this.inboundConnections.remove(connection));
    }

    private void onMessageReceived(OftConnection connection, byte[] data) {
        for (OftReceivedListener listener : this.receivedListeners) {
            listener.onReceived(connection, data);
        }
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
        // partially reassembled. It's only a candidate once all of its data has been acknowledged.
        Set<OftConnection> toDisconnect = ConcurrentHashMap.newKeySet();
        for (OftConnection connection : tracked) {
            if (connection.hasPendingData()) {
                continue;
            }

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
            tracked.stream()
                    .filter(connection -> !toDisconnect.contains(connection) && !connection.hasPendingData())
                    .sorted((a, b) -> a.getConnectedAt().compareTo(b.getConnectedAt()))
                    .limit(excess)
                    .forEach(toDisconnect::add);
        }

        for (OftConnection connection : toDisconnect) {
            connection.disconnect();
        }
    }
}
