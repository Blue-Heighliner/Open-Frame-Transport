namespace OpenFrameTransport.Internal;

/// <summary>
/// Wraps a stream that only really supports synchronous I/O — like the one BouncyCastle's TLS
/// protocol objects expose (see Docs/OFT.md §1) — so it can still be awaited like a normal stream,
/// without deadlocking.
/// </summary>
/// <remarks>
/// The problem this avoids: <see cref="Stream"/>'s own default <c>ReadAsync</c>/<c>WriteAsync</c>
/// fallback, used by any subclass (like BouncyCastle's) that doesn't provide a true async
/// implementation, serializes *all* reads and writes on the stream through one shared internal
/// semaphore — not just reads against reads, or writes against writes, but reads against writes too.
/// That's fine for a stream only ever used one call at a time, but this codebase always has a read
/// pending continuously (the receive loop immediately starts its next read after handling each
/// packet) concurrently with occasional writes (the send loop, and the receive loop's own
/// <c>Receipt</c> replies) — so the very first read that has to wait for more data than is
/// immediately available holds that shared semaphore for as long as it's pending, which, since it's
/// continuously re-issued, permanently blocks every write attempted afterward. Both sides of a
/// connection hang this way simultaneously: neither can write because its own pending read holds the
/// semaphore, so neither read ever receives anything to complete on.
/// <para/>
/// The fix is to never go through that fallback at all. This wrapper's <c>ReadAsync</c>/
/// <c>WriteAsync</c> call the inner stream's synchronous <c>Read</c>/<c>Write</c> directly and return
/// an already-completed <see cref="ValueTask{TResult}"/>/<see cref="ValueTask"/> — safe here
/// specifically because every caller in this codebase only ever calls into a connection's stream from
/// a dedicated, non-thread-pool thread (see <c>OftConnection.StartProcessing</c>), so blocking that
/// thread for the call's duration has no effect on shared thread pool capacity. BouncyCastle's own
/// internal locking (a dedicated write-side lock, and independent per-direction cipher state) is what
/// actually keeps a concurrent read and write safe against each other; this wrapper doesn't add or
/// need any synchronization of its own.
/// </remarks>
internal sealed class OftBlockingStream : Stream
{
    private readonly Stream inner;

    /// <param name="inner">
    /// The stream to wrap. Not owned by this instance and never disposed by it.
    /// </param>
    public OftBlockingStream(Stream inner)
    {
        this.inner = inner;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => this.inner.Read(buffer);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(this.inner.Read(buffer.Span));

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => this.inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => this.inner.Write(buffer);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.inner.Write(buffer.Span);
        return default;
    }

    /// <inheritdoc />
    public override void Flush() => this.inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        this.inner.Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}
