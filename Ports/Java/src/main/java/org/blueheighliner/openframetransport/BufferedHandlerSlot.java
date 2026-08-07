package org.blueheighliner.openframetransport;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;

/**
 * Backs a single-handler-slot property (e.g. {@link OftConnection#setHandler}) so that no raise is
 * ever lost for lack of a handler: every raise that happens before the first non-null handler is
 * ever assigned is buffered, then flushed, in order, to that first handler before it becomes the
 * live target for anything raised afterward. Unlike a multi-subscriber listener list, there is only
 * ever one live target - assigning a new handler (including {@code null}) always replaces the
 * previous one; only the very first non-null assignment ever triggers a backlog flush.
 *
 * <p>Mirrors the C# reference implementation's {@code OftBufferedHandlerSlot<THandler>}.
 *
 * @param <H> the handler type
 */
final class BufferedHandlerSlot<H> {
    private final Object gate = new Object();
    private final List<Consumer<H>> buffered = new ArrayList<>();
    private H handler;
    private boolean everAssigned;

    H getHandler() {
        synchronized (this.gate) {
            return this.handler;
        }
    }

    void setHandler(H value) {
        List<Consumer<H>> toFlush = null;
        synchronized (this.gate) {
            if (!this.everAssigned && value != null) {
                this.everAssigned = true;
                if (!this.buffered.isEmpty()) {
                    toFlush = new ArrayList<>(this.buffered);
                    this.buffered.clear();
                }
            }

            this.handler = value;
        }

        if (toFlush != null) {
            for (Consumer<H> invoke : toFlush) {
                invoke.accept(value);
            }
        }
    }

    /**
     * Raises {@code invoke}: called immediately with the current handler once one has ever been
     * assigned (a no-op if the current handler happens to be {@code null} at that point), or
     * buffered for later delivery to the first handler ever assigned otherwise.
     */
    void raise(Consumer<H> invoke) {
        H current;
        synchronized (this.gate) {
            if (!this.everAssigned) {
                this.buffered.add(invoke);
                return;
            }

            current = this.handler;
        }

        if (current != null) {
            invoke.accept(current);
        }
    }

    /**
     * Discards anything still buffered for lack of a handler ever being assigned - called when the
     * object that owns this slot is being torn down, so a raise nobody ever handled isn't held onto
     * forever.
     */
    void clear() {
        synchronized (this.gate) {
            this.buffered.clear();
        }
    }
}
