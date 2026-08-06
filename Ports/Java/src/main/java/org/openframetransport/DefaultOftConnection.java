package org.openframetransport;

import com.google.protobuf.ByteString;
import org.openframetransport.proto.Hail;
import org.openframetransport.proto.Packet;

import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.NavigableMap;
import java.util.TreeMap;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Consumer;

/**
 * {@inheritDoc}
 *
 * <p>Mirrors the design of the C# reference implementation's connection engine, including rekeying
 * via a TLS 1.3 {@code KeyUpdate} triggered in place on the existing session (see README.md
 * &sect;8) rather than by layering a new TLS session over the socket.
 */
final class DefaultOftConnection implements OftConnection {
    private final Socket rawSocket;
    private final Socket socket;
    private final SSLSocket sslSocket;
    private final boolean insecure;
    private final int maxPacketDataSize;
    private final Duration rekeyInterval;
    private final Duration pollInterval;
    private final Duration pollTimeout;

    private final OftFrameStream frameStream;
    private volatile ScheduledExecutorService pollScheduler;

    /**
     * When the connection last received anything at all - a {@code Poll} packet or any other kind
     * - used exclusively by the liveness watchdog (see README.md &sect;10). Deliberately tracked
     * separately from {@link #lastReceivedAtMillis} (which only {@code Poll} leaves untouched): an
     * {@link OftPeer}'s idle-eviction relies on {@link #getLastReceivedAt()} reflecting application
     * activity only, and automatic {@code Poll} traffic would otherwise mask a connection an
     * application never actually uses as perpetually "active".
     */
    private final AtomicLong lastInboundActivityMillis = new AtomicLong();

    private final Object outboundLock = new Object();
    private final NavigableMap<Integer, Deque<PendingMessage>> outboundQueues = new TreeMap<>();
    private final Semaphore sendSignal = new Semaphore(0);

    /**
     * Held by whichever of the send loop or the receive loop is currently entitled to write the
     * next Unit/Data/Completion/Cancellation/Receipt packet, so a {@code Receipt} written from the
     * receive loop can never interleave with a partially written message from the send loop.
     */
    private final Semaphore writePermit = new Semaphore(1);
    private final AtomicReference<CompletableFuture<Void>> outstandingReceipt = new AtomicReference<>();

    /** Only ever touched by the receive thread; no synchronization needed. */
    private final Map<Integer, List<byte[]>> inboundBuffers = new HashMap<>();

    /**
     * Mirrors {@code !inboundBuffers.isEmpty()} for {@link #hasPendingData()} to read from any
     * thread without synchronizing with the receive loop. Written only by the receive loop,
     * immediately after every mutation of {@link #inboundBuffers}.
     */
    private volatile boolean hasInProgressInboundMessage;

    /**
     * Rekey requests queued by {@link #rekey()}, drained only by the receive loop (see
     * {@link #processPendingRekeys()}). This isn't just an implementation convenience - it's the
     * only thing that makes calling {@link SSLSocket#startHandshake()} to trigger a TLS 1.3
     * {@code KeyUpdate} actually safe here: JSSE's {@code SSLSocketImpl} guards a locally initiated
     * {@code startHandshake()} and the receive path's processing of an inbound post-handshake
     * message (which itself may need to write a reciprocal {@code KeyUpdate}) with two separate,
     * non-exclusive locks, so calling {@code startHandshake()} from a thread other than the one
     * already reading this socket can corrupt the connection (observed as a spurious
     * {@code bad_record_mac} when both peers happen to rekey at nearly the same moment). Running it
     * on the receive thread instead guarantees the two never execute concurrently, since one thread
     * can't do both at once.
     */
    private final Deque<CompletableFuture<Void>> pendingRekeys = new ArrayDeque<>();
    private ScheduledExecutorService rekeyScheduler;

    private final AtomicBoolean closed = new AtomicBoolean(false);

    private volatile long connectedAtMillis;
    private final AtomicLong lastSentAtMillis = new AtomicLong();
    private final AtomicLong lastReceivedAtMillis = new AtomicLong();
    private volatile String remoteInfo = "";

    private Thread receiveThread;
    private Thread sendThread;

    private final List<OftReceivedListener> receivedListeners = new CopyOnWriteArrayList<>();
    private final List<Consumer<Throwable>> disconnectedListeners = new CopyOnWriteArrayList<>();

    private DefaultOftConnection(
            Socket rawSocket,
            Socket socket,
            SSLSocket sslSocket,
            boolean insecure,
            int maxPacketDataSize,
            Duration rekeyInterval,
            Duration pollInterval,
            Duration pollTimeout,
            String info) throws IOException {
        this.rawSocket = rawSocket;
        this.socket = socket;
        this.sslSocket = sslSocket;
        this.insecure = insecure;
        this.maxPacketDataSize = maxPacketDataSize;
        this.rekeyInterval = rekeyInterval;
        this.pollInterval = pollInterval;
        this.pollTimeout = pollTimeout;
        this.frameStream = new OftFrameStream(socket.getInputStream(), socket.getOutputStream());
        completeHandshake(info);
    }

    /**
     * Dials {@code rawSocket}, performs the client-side TLS handshake (unless {@code options} has
     * {@link OftSecurityMode#INSECURE} set) and hail exchange against it, and returns the resulting
     * established connection.
     */
    static DefaultOftConnection establishAsClient(Socket rawSocket, String targetHost, OftConnectOptions options) throws IOException {
        if (options.securityMode() == OftSecurityMode.INSECURE) {
            return new DefaultOftConnection(
                    rawSocket, rawSocket, null, true,
                    options.maxPacketDataSize(), options.rekeyInterval(),
                    options.pollInterval(), options.pollTimeout(), options.info());
        }

        SSLContext sslContext = resolveClientSslContext(options);
        SSLSocketFactory sslSocketFactory = sslContext.getSocketFactory();
        SSLSocket sslSocket = (SSLSocket) sslSocketFactory.createSocket(rawSocket, targetHost, rawSocket.getPort(), false);
        sslSocket.setUseClientMode(true);
        restrictToTls13(sslSocket);
        sslSocket.startHandshake();

        return new DefaultOftConnection(
                rawSocket, sslSocket, sslSocket, false,
                options.maxPacketDataSize(), options.rekeyInterval(),
                options.pollInterval(), options.pollTimeout(), options.info());
    }

    /**
     * Resolves the {@link SSLContext} the connecting side hands off to, per
     * {@link OftSecurityMode}: {@link OftSecurityMode#SECURE} trusts whatever certificate the
     * accepting side presents unconditionally (there's nothing meaningful to validate an ephemeral
     * certificate against); {@link OftSecurityMode#AUTHENTICATION} falls back to the JVM's default
     * trust store when {@code options.sslContext()} isn't set (mirroring the C# reference
     * implementation's default certificate-chain validation when no callback is supplied);
     * {@link OftSecurityMode#DUAL_AUTHENTICATION} requires a caller-supplied context (there's no
     * sane default for this side's own identity certificate).
     */
    private static SSLContext resolveClientSslContext(OftConnectOptions options) throws IOException {
        try {
            if (options.securityMode() == OftSecurityMode.SECURE) {
                return OftEphemeralSslContext.trustAllContext();
            }

            if (options.sslContext() != null) {
                return options.sslContext();
            }

            if (options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION) {
                throw new IllegalArgumentException("sslContext is required when securityMode is DUAL_AUTHENTICATION.");
            }

            return SSLContext.getDefault();
        } catch (IOException | RuntimeException e) {
            throw e;
        } catch (Exception e) {
            throw new IOException(e);
        }
    }

    /**
     * Accepts {@code rawSocket}, performs the server-side TLS handshake (unless {@code options} has
     * {@link OftSecurityMode#INSECURE} set) and hail exchange against it, and returns the resulting
     * established connection.
     */
    static DefaultOftConnection establishAsServer(Socket rawSocket, OftHostOptions options) throws IOException {
        if (options.securityMode() == OftSecurityMode.INSECURE) {
            return new DefaultOftConnection(
                    rawSocket, rawSocket, null, true,
                    options.maxPacketDataSize(), options.rekeyInterval(),
                    options.pollInterval(), options.pollTimeout(), options.info());
        }

        // By this point sslContext is always resolved for SECURE mode: IOftListener.host has
        // already replaced it with a listener-lifetime ephemeral context; for
        // AUTHENTICATION/DUAL_AUTHENTICATION, it's the caller-supplied one, already validated
        // non-null by OftHoster.host before this is ever reached.
        SSLSocketFactory sslSocketFactory = options.sslContext().getSocketFactory();
        SSLSocket sslSocket = (SSLSocket) sslSocketFactory.createSocket(rawSocket, null, rawSocket.getPort(), false);
        sslSocket.setUseClientMode(false);
        if (options.securityMode() == OftSecurityMode.DUAL_AUTHENTICATION) {
            sslSocket.setNeedClientAuth(true);
        }
        restrictToTls13(sslSocket);
        sslSocket.startHandshake();

        return new DefaultOftConnection(
                rawSocket, sslSocket, sslSocket, false,
                options.maxPacketDataSize(), options.rekeyInterval(),
                options.pollInterval(), options.pollTimeout(), options.info());
    }

    /** Restricts the socket to TLS 1.3, the only version OFT ever negotiates (see README.md &sect;1). */
    private static void restrictToTls13(SSLSocket socket) {
        socket.setEnabledProtocols(new String[] {"TLSv1.3"});
    }

    private void completeHandshake(String info) throws IOException {
        Hail ourHail = Hail.newBuilder().setVersion(OftProtocolVersion.CURRENT).setInfo(info).build();

        AtomicReference<IOException> writeFailure = new AtomicReference<>();
        Thread hailWriter = new Thread(() -> {
            try {
                this.frameStream.write(ourHail);
            } catch (IOException e) {
                writeFailure.set(e);
            }
        }, "oft-hail-writer");
        hailWriter.start();

        Hail received;
        try {
            received = this.frameStream.readHail();
        } finally {
            try {
                hailWriter.join();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }

        if (writeFailure.get() != null) {
            throw writeFailure.get();
        }

        if (received == null) {
            throw new IOException("Connection closed before completing the OFT hail handshake.");
        }

        if (!received.getVersion().equals(OftProtocolVersion.CURRENT)) {
            throw new IllegalStateException("Incompatible OFT protocol version '" + received.getVersion() + "'.");
        }

        this.remoteInfo = received.getInfo();
        long now = System.currentTimeMillis();
        this.connectedAtMillis = now;
        this.lastSentAtMillis.set(now);
        this.lastReceivedAtMillis.set(now);
        this.lastInboundActivityMillis.set(now);
    }

    /**
     * Starts this connection's background threads: the receive loop (which begins delivering
     * inbound packets to registered listeners), the send loop, the {@code Poll} timer (see
     * README.md &sect;10), and (if configured, and not {@link #insecure}) the automatic rekey
     * timer. Deliberately not part of the constructor: {@link DefaultOftHoster}'s accept loop and
     * {@link DefaultOftConnector#connect} call this only after {@code onEstablished} (if any) has
     * had a chance to run, so that no inbound packet can be delivered before every listener a
     * caller registers in reaction to establishment has had a chance to attach to this connection.
     */
    void startProcessing() {
        String threadSuffix = String.valueOf(this.rawSocket.getRemoteSocketAddress());
        this.receiveThread = new Thread(this::receiveLoop, "oft-receive-" + threadSuffix);
        this.receiveThread.setDaemon(true);
        this.receiveThread.start();

        this.sendThread = new Thread(this::sendLoop, "oft-send-" + threadSuffix);
        this.sendThread.setDaemon(true);
        this.sendThread.start();

        // Rekeying requires a TLS session to rekey, so the timer is never started for an insecure
        // connection, even if rekeyInterval happens to be set.
        if (!this.insecure && this.rekeyInterval != null) {
            this.rekeyScheduler = Executors.newSingleThreadScheduledExecutor(runnable -> {
                Thread thread = new Thread(runnable, "oft-rekey-timer-" + threadSuffix);
                thread.setDaemon(true);
                return thread;
            });
            long millis = this.rekeyInterval.toMillis();
            this.rekeyScheduler.scheduleAtFixedRate(this::rekey, millis, millis, TimeUnit.MILLISECONDS);
        }

        this.pollScheduler = Executors.newSingleThreadScheduledExecutor(runnable -> {
            Thread thread = new Thread(runnable, "oft-poll-timer-" + threadSuffix);
            thread.setDaemon(true);
            return thread;
        });
        long pollMillis = this.pollInterval.toMillis();
        this.pollScheduler.scheduleAtFixedRate(this::onPollTick, pollMillis, pollMillis, TimeUnit.MILLISECONDS);
    }

    /**
     * Fires on every {@link #pollInterval} tick (see README.md &sect;10): sends a best-effort
     * {@code Poll} packet, then closes the connection if nothing at all has been received from the
     * peer within {@link #pollTimeout}.
     */
    private void onPollTick() {
        // Only sent when writePermit is immediately available (never waited on): skipping a tick
        // when busy is harmless, since real application traffic already keeps the peer's watchdog
        // satisfied whenever the permit is in heavy use, and an otherwise-idle connection always has
        // the permit free.
        if (this.writePermit.tryAcquire()) {
            try {
                // An all-default Packet (every field at its zero value) serializes to zero bytes
                // under proto3's default-value-omission rule - exactly the zero-length frame
                // readPacketOrPoll treats as a Poll. No dedicated control value needed.
                this.frameStream.write(Packet.newBuilder().build());
            } catch (IOException ignored) {
                // Best-effort: a single failed poll write isn't itself fatal - the watchdog check
                // below is what detects a genuinely dead connection, and the next tick tries again.
            } finally {
                this.writePermit.release();
            }
        }

        long elapsedMillis = System.currentTimeMillis() - this.lastInboundActivityMillis.get();
        if (elapsedMillis > this.pollTimeout.toMillis()) {
            close(new java.util.concurrent.TimeoutException(
                    "No poll or message was received from the peer within " + this.pollTimeout + "."));
        }
    }

    @Override
    public InetSocketAddress getRemoteEndpoint() {
        return (InetSocketAddress) this.rawSocket.getRemoteSocketAddress();
    }

    @Override
    public String getRemoteInfo() {
        return this.remoteInfo;
    }

    @Override
    public Instant getConnectedAt() {
        return Instant.ofEpochMilli(this.connectedAtMillis);
    }

    @Override
    public Instant getLastSentAt() {
        return Instant.ofEpochMilli(this.lastSentAtMillis.get());
    }

    @Override
    public Instant getLastReceivedAt() {
        return Instant.ofEpochMilli(this.lastReceivedAtMillis.get());
    }

    @Override
    public boolean hasPendingData() {
        synchronized (this.outboundLock) {
            for (Deque<PendingMessage> queue : this.outboundQueues.values()) {
                if (!queue.isEmpty()) {
                    return true;
                }
            }
        }

        return this.hasInProgressInboundMessage;
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
    public void addDisconnectedListener(Consumer<Throwable> listener) {
        this.disconnectedListeners.add(listener);
    }

    @Override
    public void removeDisconnectedListener(Consumer<Throwable> listener) {
        this.disconnectedListeners.remove(listener);
    }

    @Override
    public OftSendHandle send(byte[] data, int priority) {
        if (priority < 0) {
            throw new IllegalArgumentException("priority must not be negative");
        }

        if (this.closed.get()) {
            throw new IllegalStateException("The connection is closed.");
        }

        CompletableFuture<Void> future = new CompletableFuture<>();
        PendingMessage message = new PendingMessage(data, priority, future);

        synchronized (this.outboundLock) {
            this.outboundQueues.computeIfAbsent(priority, key -> new ArrayDeque<>()).addLast(message);
        }

        this.sendSignal.release();

        return new OftSendHandle() {
            @Override
            public CompletableFuture<Void> completion() {
                return future;
            }

            @Override
            public void cancel() {
                requestCancellation(message);
            }
        };
    }

    @Override
    public CompletableFuture<Void> rekey() {
        // No-op: an insecure (non-TLS) connection has no TLS session to rekey.
        if (this.insecure) {
            return CompletableFuture.completedFuture(null);
        }

        CompletableFuture<Void> future = new CompletableFuture<>();
        synchronized (this.pendingRekeys) {
            this.pendingRekeys.addLast(future);
        }

        return future;
    }

    @Override
    public void disconnect() {
        close(null);
    }

    @Override
    public void close() {
        close(null);
        try {
            if (this.receiveThread != null) {
                this.receiveThread.join();
            }

            if (this.sendThread != null) {
                this.sendThread.join();
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    private void receiveLoop() {
        try {
            while (true) {
                processPendingRekeys();

                PacketRead read = this.frameStream.readPacketOrPoll();
                if (read.kind() == PacketRead.Kind.CLOSED) {
                    close(null);
                    return;
                }

                long now = System.currentTimeMillis();
                this.lastInboundActivityMillis.set(now);

                // Poll deliberately doesn't count as lastReceivedAt activity - see
                // lastInboundActivityMillis - so it can't mask an otherwise-unused connection as
                // active to an OftPeer's idle-eviction.
                if (read.kind() == PacketRead.Kind.POLL) {
                    continue;
                }

                this.lastReceivedAtMillis.set(now);
                handlePacket(read.packet());
            }
        } catch (Exception exception) {
            if (!this.closed.get()) {
                close(exception);
            }
        }
    }

    /**
     * Drains every rekey request queued by {@link #rekey()} and requests a TLS 1.3
     * {@code KeyUpdate} for each, in place on the existing session (see README.md &sect;8). Only
     * ever called from the receive loop - see {@link #pendingRekeys} for why that's required for
     * correctness, not just convenience.
     */
    private void processPendingRekeys() {
        while (true) {
            CompletableFuture<Void> future;
            synchronized (this.pendingRekeys) {
                future = this.pendingRekeys.pollFirst();
            }

            if (future == null) {
                return;
            }

            try {
                this.sslSocket.startHandshake();
                future.complete(null);
            } catch (Exception e) {
                future.completeExceptionally(e);
            }
        }
    }

    private void sendLoop() {
        try {
            while (!this.closed.get()) {
                this.sendSignal.acquire();

                while (true) {
                    PendingMessage message;
                    synchronized (this.outboundLock) {
                        message = pickNextMessage();
                    }

                    if (message == null) {
                        break;
                    }

                    this.writePermit.acquire();
                    sendNextPacket(message);
                    this.writePermit.release();
                }
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (Exception exception) {
            if (!this.closed.get()) {
                close(exception);
            }
        }
    }

    private PendingMessage pickNextMessage() {
        for (Map.Entry<Integer, Deque<PendingMessage>> entry : this.outboundQueues.descendingMap().entrySet()) {
            if (!entry.getValue().isEmpty()) {
                return entry.getValue().peekFirst();
            }
        }

        return null;
    }

    private void sendNextPacket(PendingMessage message) throws IOException, InterruptedException {
        Packet packet;
        boolean finishesMessage;

        if (message.cancelRequested && message.started) {
            packet = Packet.newBuilder().setControl(3).setData(ByteString.EMPTY).build();
            finishesMessage = true;
        } else if (!message.started && message.data.length <= this.maxPacketDataSize) {
            packet = Packet.newBuilder().setControl(1).setData(ByteString.copyFrom(message.data)).build();
            message.started = true;
            finishesMessage = true;
        } else {
            message.started = true;
            int remaining = message.data.length - message.bytesSent;
            int chunkSize = Math.min(remaining, this.maxPacketDataSize);
            boolean isLast = message.bytesSent + chunkSize >= message.data.length;

            packet = Packet.newBuilder()
                    .setControl(isLast ? 2 : message.priority + 4)
                    .setData(ByteString.copyFrom(message.data, message.bytesSent, chunkSize))
                    .build();
            message.bytesSent += chunkSize;
            finishesMessage = isLast;
        }

        CompletableFuture<Void> receiptFuture = new CompletableFuture<>();
        this.outstandingReceipt.set(receiptFuture);

        this.frameStream.write(packet);
        this.lastSentAtMillis.set(System.currentTimeMillis());

        try {
            receiptFuture.get();
        } catch (ExecutionException e) {
            throw new IOException(e.getCause());
        }

        if (finishesMessage) {
            synchronized (this.outboundLock) {
                this.outboundQueues.get(message.priority).pollFirst();
            }

            if (message.cancelRequested) {
                message.future.completeExceptionally(new CancellationException("Message was cancelled."));
            } else {
                message.future.complete(null);
            }
        }
    }

    private void requestCancellation(PendingMessage message) {
        synchronized (this.outboundLock) {
            if (!message.started) {
                Deque<PendingMessage> queue = this.outboundQueues.get(message.priority);
                if (queue != null && queue.remove(message)) {
                    message.future.completeExceptionally(new CancellationException("Message was cancelled."));
                    return;
                }
            }

            message.cancelRequested = true;
        }

        this.sendSignal.release();
    }

    private void handlePacket(Packet packet) throws IOException {
        if (packet.getControl() == 0) {
            CompletableFuture<Void> receipt = this.outstandingReceipt.getAndSet(null);
            if (receipt != null) {
                receipt.complete(null);
            }

            return;
        }

        switch (packet.getControl()) {
            case 1:
                raiseReceived(packet.getData().toByteArray());
                break;
            case 2:
                completeInboundMessage(packet.getData().toByteArray(), false);
                break;
            case 3:
                completeInboundMessage(new byte[0], true);
                break;
            default:
                int priority = packet.getControl() - 4;
                this.inboundBuffers.computeIfAbsent(priority, key -> new ArrayList<>()).add(packet.getData().toByteArray());
                this.hasInProgressInboundMessage = !this.inboundBuffers.isEmpty();
                break;
        }

        this.frameStream.write(Packet.newBuilder().setControl(0).setData(ByteString.EMPTY).build());
    }

    private void completeInboundMessage(byte[] finalChunk, boolean cancelled) {
        Integer highestPriority = null;
        for (int priority : this.inboundBuffers.keySet()) {
            if (highestPriority == null || priority > highestPriority) {
                highestPriority = priority;
            }
        }

        if (highestPriority == null) {
            throw new IllegalStateException("Received a " + (cancelled ? "cancellation" : "completion")
                    + " packet with no pending message on any priority channel.");
        }

        List<byte[]> buffer = this.inboundBuffers.remove(highestPriority);
        this.hasInProgressInboundMessage = !this.inboundBuffers.isEmpty();

        if (cancelled) {
            return;
        }

        if (finalChunk.length > 0) {
            buffer.add(finalChunk);
        }

        int totalLength = 0;
        for (byte[] chunk : buffer) {
            totalLength += chunk.length;
        }

        byte[] message = new byte[totalLength];
        int offset = 0;
        for (byte[] chunk : buffer) {
            System.arraycopy(chunk, 0, message, offset, chunk.length);
            offset += chunk.length;
        }

        raiseReceived(message);
    }

    private void raiseReceived(byte[] data) {
        for (OftReceivedListener listener : this.receivedListeners) {
            listener.onReceived(this, data);
        }
    }

    private void close(Throwable exception) {
        if (!this.closed.compareAndSet(false, true)) {
            return;
        }

        if (this.rekeyScheduler != null) {
            this.rekeyScheduler.shutdownNow();
        }

        if (this.pollScheduler != null) {
            this.pollScheduler.shutdownNow();
        }

        List<CompletableFuture<Void>> cancelledRekeys;
        synchronized (this.pendingRekeys) {
            cancelledRekeys = new ArrayList<>(this.pendingRekeys);
            this.pendingRekeys.clear();
        }

        for (CompletableFuture<Void> future : cancelledRekeys) {
            future.completeExceptionally(
                    exception != null ? exception : new IllegalStateException("The connection was disposed."));
        }

        synchronized (this.outboundLock) {
            for (Deque<PendingMessage> queue : this.outboundQueues.values()) {
                PendingMessage message;
                while ((message = queue.pollFirst()) != null) {
                    message.future.completeExceptionally(
                            exception != null ? exception : new IllegalStateException("The connection was disposed."));
                }
            }
        }

        try {
            this.socket.close();
        } catch (IOException ignored) {
            // Best-effort cleanup.
        }

        try {
            this.rawSocket.close();
        } catch (IOException ignored) {
            // Best-effort cleanup.
        }

        if (this.receiveThread != null) {
            this.receiveThread.interrupt();
        }

        if (this.sendThread != null) {
            this.sendThread.interrupt();
        }

        for (Consumer<Throwable> listener : this.disconnectedListeners) {
            listener.accept(exception);
        }
    }

    private static final class PendingMessage {
        final byte[] data;
        final int priority;
        final CompletableFuture<Void> future;
        volatile boolean cancelRequested;
        volatile boolean started;
        int bytesSent;

        PendingMessage(byte[] data, int priority, CompletableFuture<Void> future) {
            this.data = data;
            this.priority = priority;
            this.future = future;
        }
    }
}
