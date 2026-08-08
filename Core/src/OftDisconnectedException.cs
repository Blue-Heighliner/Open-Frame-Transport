namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Thrown by <see cref="IOftConnection.Send(ReadOnlyMemory{byte}, int, CancellationToken)"/>,
/// <see cref="IOftConnection.Rekey"/>,
/// <see cref="IOftPeer.Send(string, int, ReadOnlyMemory{byte}, int, CancellationToken)"/>, and
/// <see cref="IOftPeer.Rekey"/> when the connection or peer they were called on is no longer
/// connected (see <see cref="IOftConnection.IsConnected"/>/<see cref="IOftPeer.IsConnected"/>) —
/// whether because of a local <c>Disconnect()</c>/<see cref="IDisposable.Dispose"/> call, the
/// remote side disconnecting, or an unrecoverable error.
/// </summary>
public sealed class OftDisconnectedException : Exception
{
    /// <summary>
    /// Creates an exception with a default message describing the disconnected state.
    /// </summary>
    public OftDisconnectedException()
        : base("No longer connected.")
    {
    }

    /// <summary>
    /// Creates an exception with the given message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public OftDisconnectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception with the given message and inner exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public OftDisconnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
