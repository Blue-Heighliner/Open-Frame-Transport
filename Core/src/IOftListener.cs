namespace OpenFrameTransport;

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
    /// Raised whenever a new inbound connection completes its TLS handshake and hail exchange.
    /// Nothing raised before the first subscriber ever attaches is lost — it's delivered to that
    /// first subscriber as soon as it attaches (see README.md and <c>OftBufferedEvent</c>), since
    /// this listener may accept and establish connections before a caller has had a chance to
    /// subscribe.
    /// </summary>
    event EventHandler<OftConnectedEventArgs>? Connected;
}
