package org.blueheighliner.openframetransport;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
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
    private final BufferedHandlerSlot<Consumer<OftConnection>> connectedSlot = new BufferedHandlerSlot<>();
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
    public void setConnectedHandler(Consumer<OftConnection> handler) {
        this.connectedSlot.setHandler(handler);
    }

    @Override
    public Consumer<OftConnection> getConnectedHandler() {
        return this.connectedSlot.getHandler();
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

        // Nobody will ever assign a callback to a closed listener afterward, so a connected
        // notification still buffered for lack of one would otherwise be held onto forever - just
        // consistent cleanup, since an OftConnection reference owns nothing that needs disposal.
        this.connectedSlot.clear();
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

            // Safe to start processing immediately, before notifying the connected callback: it's
            // backed by BufferedHandlerSlot, so a connection's own received/disconnected
            // notifications raised in the meantime are never lost even if a caller reacting to this
            // connected notification hasn't assigned its own callbacks yet (see BufferedHandlerSlot's
            // own doc comment).
            connection.startProcessing();
            this.connectedSlot.raise(handler -> handler.accept(connection));
        } catch (Exception e) {
            try {
                accepted.close();
            } catch (IOException ignored) {
                // Best-effort cleanup.
            }
        }
    }
}
