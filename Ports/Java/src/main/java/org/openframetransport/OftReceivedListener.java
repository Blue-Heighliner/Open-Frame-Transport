package org.openframetransport;

/**
 * Receives complete application messages from an {@link OftConnection}.
 */
@FunctionalInterface
public interface OftReceivedListener {
    /**
     * Called whenever a complete application message has been received.
     *
     * @param connection the connection the message arrived on
     * @param data       the received message payload
     */
    void onReceived(OftConnection connection, byte[] data);
}
