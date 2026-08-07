namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// Wraps a pooled <see cref="IMemoryOwner{T}"/> whose rented buffer may be larger than the data it
/// actually holds (pools commonly round a rental up to a bucket size), exposing only the valid
/// prefix via <see cref="Memory"/> while still forwarding <see cref="Dispose"/> to the underlying
/// rental so the whole buffer is returned to its pool.
/// </summary>
internal sealed class OftSlicedMemoryOwner : IMemoryOwner<byte>
{
    private readonly IMemoryOwner<byte> owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="OftSlicedMemoryOwner"/> class.
    /// </summary>
    /// <param name="owner">The pooled rental this instance takes ownership of.</param>
    /// <param name="length">The number of bytes, from the start of <paramref name="owner"/>'s memory, that are actually valid.</param>
    public OftSlicedMemoryOwner(IMemoryOwner<byte> owner, int length)
    {
        this.owner = owner;
        this.Memory = owner.Memory[..length];
    }

    /// <inheritdoc />
    public Memory<byte> Memory { get; }

    /// <inheritdoc />
    public void Dispose() => this.owner.Dispose();
}
