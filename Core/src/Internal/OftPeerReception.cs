namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// <inheritdoc cref="IOftPeerReception" />
/// </summary>
internal sealed class OftPeerReception : IOftPeerReception
{
    private readonly IMemoryOwner<byte> owner;

    /// <summary>
    /// Creates a reception wrapping <paramref name="owner"/>'s memory.
    /// </summary>
    /// <param name="owner">The pooled rental backing <see cref="Data"/>. Ownership transfers to this instance.</param>
    /// <param name="identity">The identity of the connection the message arrived on.</param>
    public OftPeerReception(IMemoryOwner<byte> owner, OftIdentity identity)
    {
        this.owner = owner;
        this.Identity = identity;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Data => this.owner.Memory;

    /// <inheritdoc />
    public OftIdentity Identity { get; }

    /// <inheritdoc />
    public void Dispose() => this.owner.Dispose();
}
