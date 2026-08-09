package org.blueheighliner.openframetransport;

import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;

/**
 * Runs a blocking operation on its own dedicated daemon thread and exposes its outcome as a
 * {@link CompletableFuture}, so a caller can treat {@code connect()}/{@code host()} as
 * non-blocking even though the socket/TLS work underneath is not - matching the C# reference
 * implementation's own use of {@code TaskCreationOptions.LongRunning} for exactly the same reason
 * (see {@code Core/src/Internal/OftConnection.cs}'s own comment on it): this work has no truly
 * non-blocking variant available in the JDK APIs this port uses, so running it on a shared pool
 * would tie up one of that pool's threads for the operation's entire (potentially seconds-long)
 * duration - a handful of concurrent {@code connect()}/{@code host()} calls would be enough to
 * starve it for every other unrelated piece of async work in the process. A dedicated thread per
 * call has no such effect on shared pool capacity.
 */
final class OftBlocking {
    private OftBlocking() {
    }

    static <T> CompletableFuture<T> supplyAsync(String threadName, Callable<T> operation) {
        CompletableFuture<T> future = new CompletableFuture<>();
        Thread thread = new Thread(() -> {
            try {
                future.complete(operation.call());
            } catch (Throwable t) {
                future.completeExceptionally(t);
            }
        }, threadName);
        thread.setDaemon(true);
        thread.start();
        return future;
    }
}
