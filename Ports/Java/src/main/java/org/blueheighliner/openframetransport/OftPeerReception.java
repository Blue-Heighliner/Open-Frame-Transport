package org.blueheighliner.openframetransport;

/**
 * A message received via {@link OftPeer#setReceivedHandler}: its payload plus the identity of the
 * connection it arrived on. Instances are produced by {@link OftPeer}, never constructed directly.
 */
public interface OftPeerReception {
    /** The received message's payload. */
    byte[] data();

    /** The identity of the connection the message arrived on. */
    OftIdentity identity();
}
