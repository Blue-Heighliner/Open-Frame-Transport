package org.blueheighliner.openframetransport;

/**
 * The OFT protocol version spoken by this implementation, sent in this side's hail (see
 * README.md &sect;3).
 */
final class OftProtocolVersion {
    /** The current OFT protocol version string. */
    static final String CURRENT = "oft/1";

    private OftProtocolVersion() {
    }
}
