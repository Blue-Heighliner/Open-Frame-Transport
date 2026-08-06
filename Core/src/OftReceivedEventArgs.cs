namespace OpenFrameTransport;

/// <summary>
/// Event data raised when a complete application message has been received on a connection.
/// </summary>
public sealed class OftReceivedEventArgs : EventArgs, IDisposable
{
    private bool disposed;

    /// <summary>
    /// The received message payload. Backed by pooled memory (see <see cref="Dispose"/>); valid
    /// until this instance is disposed.
    /// </summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// Owns the pooled memory <see cref="Data"/> is a view over, if any. Not part of the public
    /// surface: set by the connection that raises this event, released via <see cref="Dispose"/>.
    /// </summary>
    internal IMemoryOwner<byte>? Owner { get; init; }

    /// <summary>
    /// Returns <see cref="Data"/>'s backing memory to its pool, if it was pooled. Optional: safe to
    /// skip if the caller doesn't care about returning pooled memory promptly (the memory is simply
    /// not reused by the pool in that case, with no correctness impact). Safe to call more than
    /// once. <see cref="Data"/> must not be used afterward.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.Owner?.Dispose();
    }
}
