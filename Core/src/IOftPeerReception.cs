namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// A message received via <see cref="IOftPeer.ReceivedHandler"/>: its payload, plus the identity of
/// the connection it arrived on. Owns the payload's pooled memory — the callback must dispose this
/// instance (returning that memory to its pool) once done with it, e.g. via a
/// <see langword="using"/> statement. Instances are produced by <see cref="IOftPeer"/>, never
/// constructed directly.
/// </summary>
public interface IOftPeerReception : IDisposable
{
    /// <summary>The identity of the connection the message arrived on.</summary>
    OftIdentity Identity { get; }
    
    /// <summary>The received message's payload.</summary>
    ReadOnlyMemory<byte> Data { get; }
}
