package org.blueheighliner.openframetransport;

import org.blueheighliner.openframetransport.proto.Packet;

/**
 * The result of {@link OftFrameStream#readPacketOrPoll()}.
 */
final class PacketRead {
    enum Kind {
        /** The stream ended cleanly on a message boundary. */
        CLOSED,

        /** A zero-length frame was read: a {@code Poll} (see README.md &sect;10). */
        POLL,

        /** A {@link Packet} was read; see {@link #packet}. */
        MESSAGE,
    }

    static final PacketRead CLOSED = new PacketRead(Kind.CLOSED, null);
    static final PacketRead POLL = new PacketRead(Kind.POLL, null);

    private final Kind kind;
    private final Packet packet;

    private PacketRead(Kind kind, Packet packet) {
        this.kind = kind;
        this.packet = packet;
    }

    static PacketRead of(Packet packet) {
        return new PacketRead(Kind.MESSAGE, packet);
    }

    Kind kind() {
        return this.kind;
    }

    Packet packet() {
        return this.packet;
    }
}
