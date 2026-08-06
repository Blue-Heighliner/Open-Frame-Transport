package org.openframetransport;

import java.util.concurrent.CompletableFuture;

/**
 * A handle to a message queued for sending via {@link OftConnection#send(byte[], int)}.
 */
public interface OftSendHandle {
    /**
     * A future that completes once the message has been fully delivered, or completes
     * exceptionally if it is cancelled or the connection closes first.
     */
    CompletableFuture<Void> completion();

    /**
     * Abandons the message (see README.md &sect;7): immediately if it has not yet started sending,
     * or by sending a {@code Cancellation} packet if it has.
     */
    void cancel();
}
