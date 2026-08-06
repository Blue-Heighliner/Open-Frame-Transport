namespace OpenFrameTransport;

/// <summary>
/// Creates <see cref="IOftPeer"/> instances. Registered for dependency injection by convention
/// (see <see cref="OpenFrameTransportServiceCollectionExtensions"/>); construct
/// <see cref="OftPeerFactory"/> directly (with an <see cref="OftConnector"/> and
/// <see cref="OftHoster"/>) when not using an IoC container.
/// </summary>
public interface IOftPeerFactory
{
    /// <summary>
    /// Creates a peer configured with the given options.
    /// </summary>
    /// <param name="options">
    /// The peer's options. When <see langword="null"/>, options with default values (and an empty
    /// <see cref="OftPeerOptions.Info"/>) are used.
    /// </param>
    /// <returns>The new peer.</returns>
    IOftPeer Create(OftPeerOptions? options = null);
}

/// <summary>
/// <inheritdoc cref="IOftPeerFactory" />
/// </summary>
public sealed class OftPeerFactory : IOftPeerFactory
{
    private readonly IOftConnector connector;
    private readonly IOftHoster hoster;

    /// <summary>
    /// Creates a peer factory that builds its peers' outbound/inbound connections with the given
    /// connector/hoster.
    /// </summary>
    /// <param name="connector">The connector used to make each peer's outbound connections.</param>
    /// <param name="hoster">The hoster used to accept each peer's inbound connections, if any.</param>
    public OftPeerFactory(IOftConnector connector, IOftHoster hoster)
    {
        this.connector = connector;
        this.hoster = hoster;
    }

    /// <inheritdoc />
    public IOftPeer Create(OftPeerOptions? options = null)
    {
        options ??= new OftPeerOptions { Info = string.Empty };

        OftConnectOptions connectOptions = new()
        {
            Info = options.Info,
            MaxPacketDataSize = options.MaxPacketDataSize,
            RekeyInterval = options.RekeyInterval,
            SecurityMode = options.SecurityMode,
            PollInterval = options.PollInterval,
            PollTimeout = options.PollTimeout,
            ClientCertificates = options.ClientCertificates,
            ServerCertificateValidation = options.CertificateValidation,
        };

        OftHostOptions hostOptions = new()
        {
            Info = options.Info,
            MaxPacketDataSize = options.MaxPacketDataSize,
            RekeyInterval = options.RekeyInterval,
            SecurityMode = options.SecurityMode,
            PollInterval = options.PollInterval,
            PollTimeout = options.PollTimeout,
            ServerCertificate = options.ServerCertificate,
            ClientCertificateValidation = options.CertificateValidation,
        };

        return new OftPeer(options, this.connector, connectOptions, this.hoster, hostOptions);
    }
}
