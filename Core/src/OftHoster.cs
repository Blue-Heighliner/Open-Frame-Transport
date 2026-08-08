namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Hosts inbound OFT listeners.
/// </summary>
public interface IOftHoster
{
    /// <summary>
    /// Starts listening for inbound OFT connections on <paramref name="listenEndPoint"/>.
    /// </summary>
    /// <param name="listenEndPoint">The local endpoint to listen for incoming TCP connections on.</param>
    /// <param name="options">
    /// The options used to accept every connection. When <see langword="null"/>, options with
    /// default values (and an empty <see cref="OftConnectionOptions.Info"/>) are used.
    /// </param>
    /// <param name="cancellationToken">A token that stops the resulting listener when cancelled.</param>
    /// <returns>The new listener.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="OftConnectionOptions.Certificate"/> was not set and
    /// <see cref="OftConnectionOptions.SecurityMode"/> requires one (see
    /// <see cref="OftSecurityMode.ServerAuthentication"/>/<see cref="OftSecurityMode.DualAuthentication"/>).
    /// </exception>
    Task<IOftListener> Host(IPEndPoint listenEndPoint, OftConnectionOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <inheritdoc cref="IOftHoster" />
/// </summary>
public sealed class OftHoster : IOftHoster
{
    /// <inheritdoc />
    public Task<IOftListener> Host(IPEndPoint listenEndPoint, OftConnectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OftConnectionOptions { Info = string.Empty };

        if (options.SecurityMode is OftSecurityMode.ServerAuthentication or OftSecurityMode.DualAuthentication && options.Certificate is null)
        {
            throw new ArgumentException(
                $"{nameof(OftConnectionOptions.Certificate)} is required when {nameof(OftConnectionOptions.SecurityMode)} is " +
                $"{nameof(OftSecurityMode.ServerAuthentication)} or {nameof(OftSecurityMode.DualAuthentication)}.",
                nameof(options));
        }

        IOftListener listener = OftListener.Start(options, listenEndPoint, cancellationToken);
        return Task.FromResult(listener);
    }
}

/// <summary>
/// Extension methods for <see cref="IOftHoster"/>.
/// </summary>
public static class OftHosterExtensions
{
    /// <summary>
    /// Starts listening for inbound OFT connections on <paramref name="port"/>, on any local IP
    /// address. Equivalent to calling
    /// <see cref="IOftHoster.Host(IPEndPoint, OftConnectionOptions?, CancellationToken)"/> with
    /// <c>new IPEndPoint(IPAddress.Any, port)</c>.
    /// </summary>
    /// <param name="hoster">The hoster to start listening with.</param>
    /// <param name="port">The local port to listen for incoming TCP connections on.</param>
    /// <param name="options">
    /// The options used to accept every connection. When <see langword="null"/>, options with
    /// default values (and an empty <see cref="OftConnectionOptions.Info"/>) are used.
    /// </param>
    /// <param name="cancellationToken">A token that stops the resulting listener when cancelled.</param>
    /// <returns>The new listener.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="OftConnectionOptions.Certificate"/> was not set and
    /// <see cref="OftConnectionOptions.SecurityMode"/> requires one (see
    /// <see cref="OftSecurityMode.ServerAuthentication"/>/<see cref="OftSecurityMode.DualAuthentication"/>).
    /// </exception>
    public static Task<IOftListener> Host(this IOftHoster hoster, int port, OftConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        hoster.Host(new IPEndPoint(IPAddress.Any, port), options, cancellationToken);
}
