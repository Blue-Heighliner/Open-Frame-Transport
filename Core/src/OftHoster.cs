namespace OpenFrameTransport;

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
    /// <see cref="OftHostOptions.ServerCertificate"/> was not set and
    /// <see cref="OftConnectionOptions.SecurityMode"/> requires one (see
    /// <see cref="OftSecurityMode.Authentication"/>/<see cref="OftSecurityMode.DualAuthentication"/>).
    /// </exception>
    Task<IOftListener> Host(IPEndPoint listenEndPoint, OftHostOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <inheritdoc cref="IOftHoster" />
/// </summary>
public sealed class OftHoster : IOftHoster
{
    /// <inheritdoc />
    public Task<IOftListener> Host(IPEndPoint listenEndPoint, OftHostOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OftHostOptions { Info = string.Empty };

        if (options.SecurityMode is OftSecurityMode.Authentication or OftSecurityMode.DualAuthentication && options.ServerCertificate is null)
        {
            throw new ArgumentException(
                $"{nameof(OftHostOptions.ServerCertificate)} is required when {nameof(OftConnectionOptions.SecurityMode)} is " +
                $"{nameof(OftSecurityMode.Authentication)} or {nameof(OftSecurityMode.DualAuthentication)}.",
                nameof(options));
        }

        IOftListener listener = OftListener.Start(options, listenEndPoint, cancellationToken);
        return Task.FromResult(listener);
    }
}
