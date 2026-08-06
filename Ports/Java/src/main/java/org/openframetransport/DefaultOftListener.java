package org.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;

/**
 * {@inheritDoc}
 */
final class DefaultOftListener implements OftListener {
    private final OftHostOptions options;
    private final ServerSocket serverSocket;
    private final Thread acceptThread;
    private final List<Consumer<OftConnection>> connectedListeners = new CopyOnWriteArrayList<>();
    private final ExecutorService acceptExecutor = Executors.newCachedThreadPool(runnable -> {
        Thread thread = new Thread(runnable, "oft-listener-handshake");
        thread.setDaemon(true);
        return thread;
    });

    private volatile boolean closed;

    private DefaultOftListener(OftHostOptions options, ServerSocket serverSocket) {
        this.options = options;
        this.serverSocket = serverSocket;
        this.acceptThread = new Thread(this::acceptLoop, "oft-listener-accept");
        this.acceptThread.setDaemon(true);
        this.acceptThread.start();
    }

    static DefaultOftListener start(InetSocketAddress listenEndpoint, OftHostOptions options) throws IOException {
        ServerSocket serverSocket = new ServerSocket();
        serverSocket.bind(listenEndpoint);
        return new DefaultOftListener(options, serverSocket);
    }

    @Override
    public InetSocketAddress getLocalEndpoint() {
        return (InetSocketAddress) this.serverSocket.getLocalSocketAddress();
    }

    @Override
    public void addConnectedListener(Consumer<OftConnection> listener) {
        this.connectedListeners.add(listener);
    }

    @Override
    public void removeConnectedListener(Consumer<OftConnection> listener) {
        this.connectedListeners.remove(listener);
    }

    @Override
    public synchronized void close() {
        if (this.closed) {
            return;
        }

        this.closed = true;

        try {
            this.serverSocket.close();
        } catch (IOException ignored) {
            // Expected to unblock the pending accept() call.
        }

        try {
            this.acceptThread.join();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }

        this.acceptExecutor.shutdown();
    }

    private void acceptLoop() {
        while (true) {
            Socket accepted;
            try {
                accepted = this.serverSocket.accept();
            } catch (IOException e) {
                // Expected when close() closes the listening socket.
                return;
            }

            this.acceptExecutor.submit(() -> handleAccepted(accepted));
        }
    }

    private void handleAccepted(Socket accepted) {
        try {
            DefaultOftConnection connection = DefaultOftConnection.establishAsServer(accepted, this.options);

            // Safe to start processing immediately, before notifying connectedListeners: unlike the
            // C# reference implementation's buffered events, this port still relies on listeners
            // being attached synchronously within a connected listener - see OftListener's own doc
            // comment and DefaultOftConnection#startProcessing.
            for (Consumer<OftConnection> listener : this.connectedListeners) {
                listener.accept(connection);
            }

            connection.startProcessing();
        } catch (Exception e) {
            try {
                accepted.close();
            } catch (IOException ignored) {
                // Best-effort cleanup.
            }
        }
    }
}
