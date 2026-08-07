namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Connects out to remote OFT endpoints.
/// </summary>
public interface IOftConnector
{
    /// <summary>
    /// Dials <paramref name="host"/>:<paramref name="port"/>, performs the TLS handshake and hail
    /// exchange (see Docs/OFT.md §1-§3), and returns the resulting established connection.
    /// </summary>
    /// <param name="host">The remote host to connect to.</param>
    /// <param name="port">The remote port to connect to.</param>
    /// <param name="options">
    /// The options used for this connection. When <see langword="null"/>, options with default
    /// values (and an empty <see cref="OftConnectionOptions.Info"/>) are used.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel connecting.</param>
    /// <returns>The established connection.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="OftConnectOptions.ClientCertificates"/> was not set and
    /// <see cref="OftConnectionOptions.SecurityMode"/> is <see cref="OftSecurityMode.DualAuthentication"/>.
    /// </exception>
    Task<IOftConnection> Connect(string host, int port, OftConnectOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <inheritdoc cref="IOftConnector" />
/// </summary>
public sealed class OftConnector : IOftConnector
{
    /// <inheritdoc />
    public async Task<IOftConnection> Connect(string host, int port, OftConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OftConnectOptions { Info = string.Empty };

        if (options.SecurityMode == OftSecurityMode.DualAuthentication && (options.ClientCertificates is null || options.ClientCertificates.Count == 0))
        {
            throw new ArgumentException(
                $"{nameof(OftConnectOptions.ClientCertificates)} is required when {nameof(OftConnectionOptions.SecurityMode)} is {nameof(OftSecurityMode.DualAuthentication)}.",
                nameof(options));
        }

        TcpClient tcpClient = new();
        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            OftConnection connection = await OftConnection.EstablishAsClient(tcpClient, host, options, cancellationToken).ConfigureAwait(false);

            // Safe to start processing immediately: Handler is backed by OftBufferedHandlerSlot, so
            // nothing raised before the caller gets this connection back and assigns a handler is
            // lost (see README.md and OftBufferedHandlerSlot's own doc comment) - there's no ordering
            // requirement to satisfy here.
            connection.StartProcessing();
            return connection;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }
}
