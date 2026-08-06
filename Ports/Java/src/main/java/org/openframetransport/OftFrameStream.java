package org.openframetransport;

import com.google.protobuf.MessageLite;
import org.openframetransport.proto.Hail;
import org.openframetransport.proto.Packet;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Reads and writes protobuf messages on a stream using a standard protobuf varint length prefix,
 * as described in README.md &sect;2: the first message ever read is a {@link Hail}, and every
 * message after it is a {@link Packet}. Writes are serialized against concurrent callers so that a
 * {@code Receipt} written from the receive loop can never interleave with a partially written
 * message from the send loop.
 */
final class OftFrameStream {
    private static final int MAX_VARINT_BYTES = 5;

    private final InputStream input;
    private final OutputStream output;
    private final Object writeLock = new Object();

    OftFrameStream(InputStream input, OutputStream output) {
        this.input = input;
        this.output = output;
    }

    /**
     * Serializes and writes a single message, prefixed with its varint-encoded length. Safe to call
     * concurrently from multiple callers; writes are serialized internally.
     */
    void write(MessageLite message) throws IOException {
        byte[] payload = message.toByteArray();
        synchronized (this.writeLock) {
            writeVarint32(payload.length);
            this.output.write(payload);
            this.output.flush();
        }
    }

    /**
     * Reads a single {@link Hail} message, or {@code null} if the stream ended cleanly on a message
     * boundary. Intended for exactly one call per TLS session, as the first read on it.
     */
    Hail readHail() throws IOException {
        byte[] payload = readLengthDelimited();
        return payload == null ? null : Hail.parseFrom(payload);
    }

    /**
     * Reads a single {@link Packet} message, or {@code null} if the stream ended cleanly on a
     * message boundary. Intended for every read on a TLS session after the initial
     * {@link #readHail()} call.
     */
    Packet readPacket() throws IOException {
        byte[] payload = readLengthDelimited();
        return payload == null ? null : Packet.parseFrom(payload);
    }

    /**
     * Reads a single frame after the initial {@link #readHail()} call and classifies it, per
     * README.md &sect;10: a zero-length frame is a {@code Poll} - deliberately not a dedicated
     * {@link Packet} control value, since protobuf's proto3 wire format never emits any bytes for a
     * message with every field at its default value, so an all-default {@link Packet} (and only
     * that) already serializes to zero bytes with no encoding changes needed. Any other frame is
     * parsed as a {@link Packet}. A clean end-of-stream at a message boundary is reported as closed.
     */
    PacketRead readPacketOrPoll() throws IOException {
        int length = readVarint32();
        if (length < 0) {
            return PacketRead.CLOSED;
        }

        if (length == 0) {
            return PacketRead.POLL;
        }

        byte[] payload = new byte[length];
        readExact(payload);
        return PacketRead.of(Packet.parseFrom(payload));
    }

    private byte[] readLengthDelimited() throws IOException {
        int length = readVarint32();
        if (length < 0) {
            return null;
        }

        byte[] payload = new byte[length];
        readExact(payload);
        return payload;
    }

    private int readVarint32() throws IOException {
        int result = 0;
        int shift = 0;

        for (int i = 0; i < MAX_VARINT_BYTES; i++) {
            int current = this.input.read();
            if (current == -1) {
                if (i == 0) {
                    return -1;
                }

                throw new EOFException("Stream ended in the middle of a message length prefix.");
            }

            result |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0) {
                return result;
            }

            shift += 7;
        }

        throw new IOException("Message length prefix exceeded the maximum varint size.");
    }

    private void readExact(byte[] buffer) throws IOException {
        int totalRead = 0;
        while (totalRead < buffer.length) {
            int read = this.input.read(buffer, totalRead, buffer.length - totalRead);
            if (read == -1) {
                throw new EOFException("Stream ended in the middle of a message payload.");
            }

            totalRead += read;
        }
    }

    private void writeVarint32(int value) throws IOException {
        while ((value & ~0x7F) != 0) {
            this.output.write((value & 0x7F) | 0x80);
            value >>>= 7;
        }

        this.output.write(value);
    }
}
