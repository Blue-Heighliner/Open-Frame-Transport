namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// Requests a TLS 1.3 post-handshake <c>KeyUpdate</c> (see Docs/OFT.md §8) on an established
/// connection. Implemented by <see cref="OftTlsClientProtocol"/> and
/// <see cref="OftTlsServerProtocol"/>, which both just forward to their base class's
/// <c>Send13KeyUpdate</c> — protected there since BouncyCastle otherwise only triggers it
/// automatically (in response to the peer's own request, or its internal key-usage limits), never in
/// response to an explicit application request.
/// </summary>
internal interface IOftTlsRekeyableProtocol
{
    /// <summary>
    /// Sends a <c>KeyUpdate</c> requesting the peer update its own keys too, deriving fresh traffic
    /// keys for both directions without a new handshake. A no-op if called before the connection's
    /// application-data phase has begun.
    /// </summary>
    void RequestKeyUpdate();
}

/// <inheritdoc cref="IOftTlsRekeyableProtocol" />
internal sealed class OftTlsClientProtocol : TlsClientProtocol, IOftTlsRekeyableProtocol
{
    public OftTlsClientProtocol(Stream stream)
        : base(stream)
    {
    }

    public void RequestKeyUpdate() => this.Send13KeyUpdate(true);
}

/// <inheritdoc cref="IOftTlsRekeyableProtocol" />
internal sealed class OftTlsServerProtocol : TlsServerProtocol, IOftTlsRekeyableProtocol
{
    public OftTlsServerProtocol(Stream stream)
        : base(stream)
    {
    }

    public void RequestKeyUpdate() => this.Send13KeyUpdate(true);
}
