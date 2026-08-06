namespace OpenFrameTransport.Tests;

public sealed class OftFrameStreamTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsHailThenPackets()
    {
        using MemoryStream stream = new();
        OftFrameStream writer = new(stream);

        await writer.Write(new Hail { Version = "oft/1", Info = "app" }, CancellationToken.None);
        await writer.Write(new Packet { Control = 1, Data = ByteString.CopyFromUtf8("hello") }, CancellationToken.None);
        await writer.Write(new Packet { Control = 0, Data = ByteString.Empty }, CancellationToken.None);

        stream.Position = 0;
        OftFrameStream reader = new(stream);

        Hail? hail = await reader.ReadHail(CancellationToken.None);
        Assert.NotNull(hail);
        Assert.Equal("oft/1", hail!.Version);
        Assert.Equal("app", hail.Info);

        Packet? unit = await reader.ReadPacket(CancellationToken.None);
        Assert.NotNull(unit);
        Assert.Equal(1u, unit!.Control);
        Assert.Equal("hello", unit.Data.ToStringUtf8());

        Packet? receipt = await reader.ReadPacket(CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal(0u, receipt!.Control);

        Packet? end = await reader.ReadPacket(CancellationToken.None);
        Assert.Null(end);
    }

    [Fact]
    public async Task ReadPacket_LargePayload_RoundTrips()
    {
        using MemoryStream stream = new();
        OftFrameStream writer = new(stream);

        byte[] payload = [.. Enumerable.Range(0, 70000).Select(i => (byte)i)];
        await writer.Write(new Packet { Control = 5, Data = ByteString.CopyFrom(payload) }, CancellationToken.None);

        stream.Position = 0;
        OftFrameStream reader = new(stream);

        Packet? packet = await reader.ReadPacket(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.Equal(payload, packet!.Data.ToByteArray());
    }

    [Fact]
    public async Task ReadPacket_StreamEndsMidLengthPrefix_ThrowsEndOfStream()
    {
        // A single byte with the varint continuation bit set, but nothing after it.
        using MemoryStream stream = new([0x80]);
        OftFrameStream reader = new(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadPacket(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPacket_StreamEndsMidPayload_ThrowsEndOfStream()
    {
        using MemoryStream stream = new();
        OftFrameStream writer = new(stream);
        await writer.Write(new Packet { Control = 1, Data = ByteString.CopyFromUtf8("hello") }, CancellationToken.None);

        // Truncate the stream so the varint length prefix promises more payload bytes than exist.
        byte[] truncated = stream.ToArray()[..^2];
        using MemoryStream truncatedStream = new(truncated);
        OftFrameStream reader = new(truncatedStream);

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadPacket(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPacket_LengthPrefixExceedsMaxVarintSize_ThrowsInvalidData()
    {
        // Five bytes, all with the continuation bit set: never terminates within the max varint size.
        using MemoryStream stream = new([0x80, 0x80, 0x80, 0x80, 0x80]);
        OftFrameStream reader = new(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPacket(CancellationToken.None));
    }
}
