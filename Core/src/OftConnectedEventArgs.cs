namespace OpenFrameTransport;

/// <summary>
/// Event data raised when an <see cref="IOftListener"/> establishes a new inbound connection.
/// </summary>
public sealed class OftConnectedEventArgs : EventArgs
{
    /// <summary>
    /// The newly established connection.
    /// </summary>
    public required IOftConnection Connection { get; init; }
}
