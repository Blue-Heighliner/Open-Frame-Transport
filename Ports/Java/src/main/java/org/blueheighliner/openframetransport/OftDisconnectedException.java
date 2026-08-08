package org.blueheighliner.openframetransport;

/**
 * Thrown by {@link OftConnection#send}, {@link OftConnection#rekey}, {@link OftPeer#send}, and
 * {@link OftPeer#rekey} when the connection or peer they were called on is no longer connected
 * (see {@link OftConnection#isConnected()}/{@link OftPeer#isConnected()}) - whether because of a
 * local {@code disconnect()}/{@code close()} call, the remote side disconnecting, or an
 * unrecoverable error.
 */
public final class OftDisconnectedException extends RuntimeException {
    /**
     * Creates an exception with the given message.
     *
     * @param message the exception message
     */
    public OftDisconnectedException(String message) {
        super(message);
    }

    /**
     * Creates an exception with the given message and cause.
     *
     * @param message the exception message
     * @param cause   the exception that caused this one
     */
    public OftDisconnectedException(String message, Throwable cause) {
        super(message, cause);
    }
}
