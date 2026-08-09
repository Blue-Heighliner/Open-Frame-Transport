package org.blueheighliner.openframetransport;

/**
 * A lifecycle stage of a tagged send, reported via {@link OftConnection#setDeliveryStatusHandler}/
 * {@link OftPeer#setDeliveryStatusHandler}. Every tagged send passes through {@link #QUEUED},
 * {@link #SENDING}, then either {@link #CANCELLED} or {@link #SENT} followed by
 * {@link #ACKNOWLEDGED}; {@link #INTERRUPTED}/{@link #RESUMED} pairs may occur any number of times
 * in between {@link #SENDING} and {@link #SENT}, for a multi-packet send that a higher-priority send
 * preempts (see README.md &sect;6) - a single-packet send can never be interrupted, since there is
 * nothing between its first and only packet for another send to interleave with. {@link #CANCELLED}
 * can only occur before {@link #SENT}: once a send's final packet has actually been written,
 * cancelling it can no longer prevent delivery, so it always proceeds to
 * {@link #SENT}/{@link #ACKNOWLEDGED} instead.
 */
public enum OftDeliveryStatus {
    /**
     * The send has been queued and is waiting its turn - reported once a send call returns, not
     * necessarily synchronously before it does.
     */
    QUEUED,

    /** The send's first packet has started transmitting. */
    SENDING,

    /**
     * A higher-priority send has preempted this one before it finished (see README.md &sect;6); it
     * remains queued and eventually resumes.
     */
    INTERRUPTED,

    /** Transmission has resumed after an {@link #INTERRUPTED} preemption. */
    RESUMED,

    /** The send's final packet has been written, but not yet acknowledged. */
    SENT,

    /**
     * The send's final packet has been acknowledged (a {@code Receipt} - see README.md
     * &sect;4.1): the send is now fully delivered. This is the terminal status for a send that
     * isn't cancelled.
     */
    ACKNOWLEDGED,

    /**
     * The send was cancelled (see README.md &sect;7) before its final packet was written. This is
     * the terminal status for a cancelled send.
     */
    CANCELLED,
}
