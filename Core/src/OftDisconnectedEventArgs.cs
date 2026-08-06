namespace OpenFrameTransport;

/// <summary>
/// Event data raised when a connection closes, whether cleanly or due to an error.
/// </summary>
public sealed class OftDisconnectedEventArgs : EventArgs
{
    /// <summary>
    /// The exception that caused the connection to close, or <see langword="null"/> if it closed
    /// cleanly (e.g. because <see cref="IOftConnection.Disconnect"/> was called).
    /// </summary>
    public Exception? Exception { get; init; }
}
