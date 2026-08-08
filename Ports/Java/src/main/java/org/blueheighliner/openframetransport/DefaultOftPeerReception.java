package org.blueheighliner.openframetransport;

/**
 * {@inheritDoc}
 */
record DefaultOftPeerReception(byte[] data, OftIdentity identity) implements OftPeerReception {
}
