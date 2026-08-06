namespace OpenFrameTransport.Internal;

/// <summary>
/// The OFT protocol version spoken by this implementation, sent as <see cref="Hail.Version"/> in
/// this side's hail (see Docs/OFT.md §3).
/// </summary>
internal static class OftProtocolVersion
{
    /// <summary>
    /// The current OFT protocol version string.
    /// </summary>
    public const string Current = "oft/1";
}
