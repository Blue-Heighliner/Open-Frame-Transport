namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Listens for and accepts inbound OFT connections on one endpoint. Instances are produced by
/// <see cref="IOftHoster.Host(IPEndPoint, OftHostOptions?, CancellationToken)"/>, never constructed
/// directly. There is no way to stop listening short of disposing the listener entirely — call
/// <see cref="IOftHoster.Host(IPEndPoint, OftHostOptions?, CancellationToken)"/> again for a fresh
/// listener if needed.
/// </summary>
public interface IOftListener : IAsyncDisposable
{
    /// <summary>
    /// The endpoint being listened on. Useful for discovering which port was chosen when the
    /// endpoint passed to <see cref="IOftHoster.Host(IPEndPoint, OftHostOptions?, CancellationToken)"/>
    /// specified port 0.
    /// </summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Called whenever a new inbound connection completes its TLS handshake and hail exchange, or
    /// <see langword="null"/> if no callback is currently assigned. There is only ever one callback
    /// at a time — assigning a new value here always replaces any previous one. The first time this
    /// is ever assigned a non-null value, it is synchronously delivered, in order, every connection
    /// accepted before that assignment (see README.md), since this listener may accept and establish
    /// connections before a caller has had a chance to assign a callback. Assigning
    /// <see langword="null"/> afterward simply discards any connection accepted while no callback is
    /// assigned (it is not automatically closed, unlike a discarded received message — the caller may
    /// still reach it later, e.g. by enumerating a peer's tracked connections).
    /// </summary>
    Action<IOftConnection>? ConnectedHandler { get; set; }
}
