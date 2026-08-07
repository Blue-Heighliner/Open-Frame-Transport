namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Creates <see cref="IOftPeer"/> instances. Registered for dependency injection by convention
/// (see <see cref="OpenFrameTransportServiceCollectionExtensions"/>); construct
/// <see cref="OftPeerFactory"/> directly — with its parameterless constructor, or with a custom
/// <see cref="IOftConnector"/>/<see cref="IOftHoster"/> — when not using an IoC container.
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
    /// <exception cref="ArgumentException">
    /// <see cref="OftPeerOptions.SecurityMode"/> is <see cref="OftSecurityMode.ServerAuthentication"/> — not a
    /// valid mode for a peer, which has no client/server delineation and so cannot express a
    /// one-sided authentication requirement (use <see cref="OftSecurityMode.DualAuthentication"/> instead).
    /// </exception>
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
    /// Creates a peer factory that builds its peers' outbound/inbound connections with a plain
    /// <see cref="OftConnector"/>/<see cref="OftHoster"/> — equivalent to
    /// <see cref="OftPeerFactory(IOftConnector, IOftHoster)"/> called with
    /// <c>new OftConnector()</c>/<c>new OftHoster()</c>, for callers that don't need a custom
    /// connector/hoster (e.g. via an IoC container) and would rather not construct them by hand.
    /// </summary>
    public OftPeerFactory()
        : this(new OftConnector(), new OftHoster())
    {
    }

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

        if (options.SecurityMode == OftSecurityMode.ServerAuthentication)
        {
            throw new ArgumentException(
                $"{nameof(OftSecurityMode.ServerAuthentication)} is not a valid {nameof(OftConnectionOptions.SecurityMode)} for an " +
                $"{nameof(IOftPeer)}: a peer has no client/server delineation, so it cannot express a one-sided authentication " +
                $"requirement. Use {nameof(OftSecurityMode.DualAuthentication)} instead.",
                nameof(options));
        }

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
