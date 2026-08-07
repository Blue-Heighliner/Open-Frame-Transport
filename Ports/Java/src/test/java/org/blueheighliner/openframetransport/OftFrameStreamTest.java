package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.blueheighliner.openframetransport.proto.Hail;
import org.blueheighliner.openframetransport.proto.Packet;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

/** Direct, connection-free coverage of {@link OftFrameStream}'s wire codec, including edge cases a live connection never reaches. */
final class OftFrameStreamTest {
    @Test
    void write_thenReadHail_roundTrips() throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        OftFrameStream writer = new OftFrameStream(new ByteArrayInputStream(new byte[0]), output);
        writer.write(Hail.newBuilder().setVersion("oft/1").setInfo("hello").build());

        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(output.toByteArray()), new ByteArrayOutputStream());
        Hail hail = reader.readHail();

        assertEquals("oft/1", hail.getVersion());
        assertEquals("hello", hail.getInfo());
    }

    @Test
    void readHail_cleanEndOfStream_returnsNull() throws Exception {
        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(new byte[0]), new ByteArrayOutputStream());
        assertNull(reader.readHail());
    }

    @Test
    void write_thenReadPacket_roundTrips() throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        OftFrameStream writer = new OftFrameStream(new ByteArrayInputStream(new byte[0]), output);
        writer.write(Packet.newBuilder().setControl(1).build());

        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(output.toByteArray()), new ByteArrayOutputStream());
        Packet packet = reader.readPacket();

        assertEquals(1, packet.getControl());
    }

    @Test
    void readPacket_cleanEndOfStream_returnsNull() throws Exception {
        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(new byte[0]), new ByteArrayOutputStream());
        assertNull(reader.readPacket());
    }

    @Test
    void readVarint32_truncatedMidLengthPrefix_throwsEofException() {
        // Continuation bit set (0x80) but no further bytes: a truncated varint.
        byte[] data = {(byte) 0x80};
        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(data), new ByteArrayOutputStream());

        assertThrows(EOFException.class, reader::readHail);
    }

    @Test
    void readVarint32_overlong_throwsIoException() {
        // 5 continuation bytes exceeds MAX_VARINT_BYTES.
        byte[] data = {(byte) 0xFF, (byte) 0xFF, (byte) 0xFF, (byte) 0xFF, (byte) 0xFF};
        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(data), new ByteArrayOutputStream());

        IOException exception = assertThrows(IOException.class, reader::readHail);
        assertEquals("Message length prefix exceeded the maximum varint size.", exception.getMessage());
    }

    @Test
    void readExact_truncatedMidPayload_throwsEofException() {
        // Varint length prefix of 10, but only 2 payload bytes actually follow.
        byte[] data = {10, 1, 2};
        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(data), new ByteArrayOutputStream());

        assertThrows(EOFException.class, reader::readHail);
    }

    @Test
    void writeVarint32_valueRequiringMultipleBytes_roundTrips() throws Exception {
        // A payload >= 128 bytes forces its varint length prefix past a single byte.
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        OftFrameStream writer = new OftFrameStream(new ByteArrayInputStream(new byte[0]), output);

        Packet largePacket = Packet.newBuilder()
                .setControl(1)
                .setData(com.google.protobuf.ByteString.copyFrom(new byte[200]))
                .build();
        writer.write(largePacket);

        OftFrameStream reader = new OftFrameStream(new ByteArrayInputStream(output.toByteArray()), new ByteArrayOutputStream());
        Packet read = reader.readPacket();

        assertEquals(200, read.getData().size());
    }
}
