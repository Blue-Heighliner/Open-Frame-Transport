namespace OpenFrameTransport.Internal;

/// <summary>
/// Reads and writes protobuf messages on a stream using a standard protobuf varint length prefix,
/// as described in Docs/OFT.md §2: the first message ever read is a <see cref="Hail"/>, and every
/// message after it is a <see cref="Packet"/>. Writes are serialized against concurrent callers so
/// that a <c>Receipt</c> written from the receive loop can never interleave with a partially
/// written message from the send loop.
/// </summary>
internal sealed class OftFrameStream
{
    private const int MaxVarintBytes = 5;

    private readonly Stream stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly byte[] readVarintBuffer = new byte[1];

    /// <summary>
    /// Reused across every <see cref="Write"/> call rather than allocated per call: safe because
    /// <see cref="writeLock"/> already serializes all writes on this instance.
    /// </summary>
    private readonly byte[] writeLengthBuffer = new byte[MaxVarintBytes];

    /// <summary>
    /// Creates a frame stream wrapping the given underlying stream. The underlying stream is not
    /// owned by this instance and is never disposed by it.
    /// </summary>
    /// <param name="stream">The stream messages are read from and written to.</param>
    public OftFrameStream(Stream stream)
    {
        this.stream = stream;
    }

    /// <summary>
    /// Serializes and writes a single message, prefixed with its varint-encoded length. Safe to
    /// call concurrently from multiple callers; writes are serialized internally.
    /// </summary>
    /// <param name="message">The message to write.</param>
    /// <param name="cancellationToken">A token used to cancel the write.</param>
    public async Task Write(IMessage message, CancellationToken cancellationToken)
    {
        int payloadSize = message.CalculateSize();
        byte[] payload = ArrayPool<byte>.Shared.Rent(payloadSize);
        try
        {
            CodedOutputStream codedOutput = new(payload);
            message.WriteTo(codedOutput);
            codedOutput.Flush();

            int lengthBytes = EncodeVarint32((uint)payloadSize, this.writeLengthBuffer);

            await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await this.stream.WriteAsync(this.writeLengthBuffer.AsMemory(0, lengthBytes), cancellationToken).ConfigureAwait(false);
                await this.stream.WriteAsync(payload.AsMemory(0, payloadSize), cancellationToken).ConfigureAwait(false);
                await this.stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>
    /// Reads a single <see cref="Hail"/> message, or <see langword="null"/> if the stream ended
    /// cleanly on a message boundary. Intended for exactly one call per TLS session, as the first
    /// read on it.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the read.</param>
    public Task<Hail?> ReadHail(CancellationToken cancellationToken) =>
        this.ReadLengthDelimited(Hail.Parser, cancellationToken);

    /// <summary>
    /// Reads a single <see cref="Packet"/> message, or <see langword="null"/> if the stream ended
    /// cleanly on a message boundary. Intended for every read on a TLS session after the initial
    /// <see cref="ReadHail"/> call.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the read.</param>
    public Task<Packet?> ReadPacket(CancellationToken cancellationToken) =>
        this.ReadLengthDelimited(Packet.Parser, cancellationToken);

    /// <summary>
    /// Reads a single frame after the initial <see cref="ReadHail"/> call and classifies it, per
    /// Docs/OFT.md §10: a zero-length frame is a <c>Poll</c> — deliberately not a dedicated
    /// <see cref="Packet"/> control value, since protobuf's proto3 wire format never emits any bytes
    /// for a message with every field at its default value, so an all-default <see cref="Packet"/>
    /// (and only that) already serializes to zero bytes with no encoding changes needed. Any other
    /// frame is parsed as a <see cref="Packet"/>. A clean end-of-stream at a message boundary is
    /// reported as closed.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the read.</param>
    public async Task<OftPacketRead> ReadPacketOrPoll(CancellationToken cancellationToken)
    {
        int? length = await this.ReadVarint32(cancellationToken).ConfigureAwait(false);
        if (length is null)
        {
            return OftPacketRead.Closed;
        }

        if (length.Value == 0)
        {
            return OftPacketRead.Poll;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length.Value);
        try
        {
            await this.ReadExact(buffer.AsMemory(0, length.Value), cancellationToken).ConfigureAwait(false);
            Packet packet = Packet.Parser.ParseFrom(new ReadOnlySpan<byte>(buffer, 0, length.Value));
            return OftPacketRead.Of(packet);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a single length-delimited message, parsing it from a pooled buffer that never outlives
    /// this call: the parser copies whatever it needs into the returned message, so the raw wire
    /// bytes don't need to be retained afterward.
    /// </summary>
    private async Task<T?> ReadLengthDelimited<T>(MessageParser<T> parser, CancellationToken cancellationToken)
        where T : class, IMessage<T>
    {
        int? length = await this.ReadVarint32(cancellationToken).ConfigureAwait(false);
        if (length is null)
        {
            return null;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length.Value);
        try
        {
            await this.ReadExact(buffer.AsMemory(0, length.Value), cancellationToken).ConfigureAwait(false);
            return parser.ParseFrom(new ReadOnlySpan<byte>(buffer, 0, length.Value));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<int?> ReadVarint32(CancellationToken cancellationToken)
    {
        uint result = 0;
        int shift = 0;

        for (int i = 0; i < MaxVarintBytes; i++)
        {
            int read = await this.stream.ReadAsync(this.readVarintBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (i == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("Stream ended in the middle of a message length prefix.");
            }

            byte current = this.readVarintBuffer[0];
            result |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return (int)result;
            }

            shift += 7;
        }

        throw new InvalidDataException("Message length prefix exceeded the maximum varint size.");
    }

    private async Task ReadExact(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await this.stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Stream ended in the middle of a message payload.");
            }

            totalRead += read;
        }
    }

    private static int EncodeVarint32(uint value, byte[] buffer)
    {
        int index = 0;
        while (value >= 0x80)
        {
            buffer[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[index++] = (byte)value;
        return index;
    }
}
