namespace OpenFrameTransport.Internal;

/// <summary>
/// <inheritdoc cref="IOftPeer" />
/// </summary>
internal sealed class OftPeer : IOftPeer
{
    private readonly OftPeerOptions options;
    private readonly IOftConnector connector;
    private readonly OftConnectOptions connectOptions;
    private readonly IOftHoster hoster;
    private readonly OftHostOptions hostOptions;

    private readonly Dictionary<(string Host, int Port), Task<IOftConnection>> outboundConnections = new();
    private readonly object outboundLock = new();

    private readonly List<IOftConnection> inboundConnections = [];
    private readonly object inboundLock = new();

    private readonly OftBufferedEvent<OftReceivedEventArgs> receivedEvent;

    private IOftListener? listener;
    private readonly object listenerLock = new();

    private readonly Timer evictionTimer;
    private bool disposed;

    /// <summary>
    /// Creates a peer using the given options and the connector/hoster it delegates to. Constructed
    /// by <see cref="OftPeerFactory"/>, which builds <paramref name="connectOptions"/>/
    /// <paramref name="hostOptions"/> from an <see cref="OftPeerOptions"/>.
    /// </summary>
    /// <param name="options">The peer's options.</param>
    /// <param name="connector">The connector used to make outbound connections.</param>
    /// <param name="connectOptions">The options used for every outbound connection this peer makes.</param>
    /// <param name="hoster">The hoster used to accept inbound connections.</param>
    /// <param name="hostOptions">The options used to accept every inbound connection.</param>
    public OftPeer(OftPeerOptions options, IOftConnector connector, OftConnectOptions connectOptions, IOftHoster hoster, OftHostOptions hostOptions)
    {
        this.options = options;
        this.connector = connector;
        this.connectOptions = connectOptions;
        this.hoster = hoster;
        this.hostOptions = hostOptions;
        this.receivedEvent = new OftBufferedEvent<OftReceivedEventArgs>(this);

        this.evictionTimer = new Timer(_ => this.RunEviction(), null, options.EvictionCheckInterval, options.EvictionCheckInterval);
    }

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint
    {
        get
        {
            lock (this.listenerLock)
            {
                return this.listener?.LocalEndPoint;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<OftReceivedEventArgs>? Received
    {
        add => this.receivedEvent.Subscribe(value);
        remove => this.receivedEvent.Unsubscribe(value);
    }

    /// <inheritdoc />
    public async Task Open(IPEndPoint listenEndPoint, CancellationToken cancellationToken = default)
    {
        IOftListener opened = await this.hoster.Host(listenEndPoint, this.hostOptions, cancellationToken).ConfigureAwait(false);
        opened.Connected += this.OnInboundConnected;

        lock (this.listenerLock)
        {
            this.listener = opened;
        }
    }

    /// <inheritdoc />
    public async Task Close()
    {
        IOftListener? currentListener;
        lock (this.listenerLock)
        {
            currentListener = this.listener;
            this.listener = null;
        }

        if (currentListener is not null)
        {
            currentListener.Connected -= this.OnInboundConnected;
            await currentListener.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task Send(string host, int port, ReadOnlyMemory<byte> data, int priority = 0, CancellationToken cancellationToken = default)
    {
        IOftConnection connection = await this.GetOrCreateConnection(host, port, cancellationToken).ConfigureAwait(false);
        await connection.Send(data, priority, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Send(string host, int port, IMemoryOwner<byte> data, int priority = 0, CancellationToken cancellationToken = default)
    {
        IOftConnection connection = await this.GetOrCreateConnection(host, port, cancellationToken).ConfigureAwait(false);
        await connection.Send(data, priority, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Rekey(CancellationToken cancellationToken = default) =>
        Task.WhenAll(this.GetTrackedConnections().Select(connection => connection.Rekey(cancellationToken)));

    /// <inheritdoc />
    public Task Disconnect() =>
        Task.WhenAll(this.GetTrackedConnections().Select(connection => connection.Disconnect()));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        await this.evictionTimer.DisposeAsync().ConfigureAwait(false);
        await this.Close().ConfigureAwait(false);

        this.receivedEvent.DisposeBuffered();
    }

    private Task<IOftConnection> GetOrCreateConnection(string host, int port, CancellationToken cancellationToken)
    {
        (string Host, int Port) key = (host, port);

        Task<IOftConnection>? connectTask;
        lock (this.outboundLock)
        {
            if (!this.outboundConnections.TryGetValue(key, out connectTask))
            {
                connectTask = this.Connect(host, port, cancellationToken);
                this.outboundConnections[key] = connectTask;
            }
        }

        return this.AwaitAndUntrackOnFailure(key, connectTask);
    }

    private async Task<IOftConnection> AwaitAndUntrackOnFailure((string Host, int Port) key, Task<IOftConnection> connectTask)
    {
        try
        {
            return await connectTask.ConfigureAwait(false);
        }
        catch
        {
            lock (this.outboundLock)
            {
                if (this.outboundConnections.TryGetValue(key, out Task<IOftConnection>? current) && current == connectTask)
                {
                    this.outboundConnections.Remove(key);
                }
            }

            throw;
        }
    }

    private async Task<IOftConnection> Connect(string host, int port, CancellationToken cancellationToken)
    {
        // Subscribing after this.connector.Connect returns is safe here, unlike the old
        // onEstablished-callback workaround it replaced: Received/Disconnected are backed by
        // OftBufferedEvent, so nothing the connection raised in the meantime is lost (see
        // README.md and OftBufferedEvent's own doc comment).
        IOftConnection connection = await this.connector.Connect(host, port, this.connectOptions, cancellationToken).ConfigureAwait(false);

        connection.Received += this.OnMessageReceived;
        connection.Disconnected += (_, _) =>
        {
            lock (this.outboundLock)
            {
                if (this.outboundConnections.TryGetValue((host, port), out Task<IOftConnection>? current) &&
                    current.IsCompletedSuccessfully && current.Result == connection)
                {
                    this.outboundConnections.Remove((host, port));
                }
            }
        };

        return connection;
    }

    private void OnInboundConnected(object? sender, OftConnectedEventArgs args)
    {
        lock (this.inboundLock)
        {
            this.inboundConnections.Add(args.Connection);
        }

        args.Connection.Received += this.OnMessageReceived;
        args.Connection.Disconnected += (_, _) =>
        {
            lock (this.inboundLock)
            {
                this.inboundConnections.Remove(args.Connection);
            }
        };
    }

    private void OnMessageReceived(object? sender, OftReceivedEventArgs args) => this.receivedEvent.Raise(args);

    /// <summary>
    /// Every connection this peer currently holds, both outbound (only those that have finished
    /// connecting successfully) and inbound.
    /// </summary>
    private List<IOftConnection> GetTrackedConnections()
    {
        List<IOftConnection> tracked = [];

        lock (this.outboundLock)
        {
            foreach (Task<IOftConnection> connectTask in this.outboundConnections.Values)
            {
                if (connectTask.IsCompletedSuccessfully)
                {
                    tracked.Add(connectTask.Result);
                }
            }
        }

        lock (this.inboundLock)
        {
            tracked.AddRange(this.inboundConnections);
        }

        return tracked;
    }

    private void RunEviction()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<IOftConnection> tracked = this.GetTrackedConnections();

        // A connection with pending/unacknowledged data (see IOftConnection.HasPendingData) is
        // never auto-disconnected here, regardless of which eviction condition it would otherwise
        // meet: doing so could silently drop a message that's still queued, in flight, or only
        // partially reassembled. It's only a candidate once all of its data has been acknowledged.
        HashSet<IOftConnection> toDisconnect = [];
        foreach (IOftConnection connection in tracked)
        {
            if (connection.HasPendingData)
            {
                continue;
            }

            DateTimeOffset lastActivity = connection.LastSentAt > connection.LastReceivedAt ? connection.LastSentAt : connection.LastReceivedAt;
            if (now - lastActivity > this.options.IdleTimeout || now - connection.ConnectedAt > this.options.MaxConnectionLifetime)
            {
                toDisconnect.Add(connection);
            }
        }

        int remainingCount = tracked.Count - toDisconnect.Count;
        if (remainingCount > this.options.MaxConnectionCount)
        {
            int excess = remainingCount - this.options.MaxConnectionCount;
            foreach (IOftConnection connection in tracked
                .Where(connection => !toDisconnect.Contains(connection) && !connection.HasPendingData)
                .OrderBy(connection => connection.ConnectedAt)
                .Take(excess))
            {
                toDisconnect.Add(connection);
            }
        }

        foreach (IOftConnection connection in toDisconnect)
        {
            _ = connection.Disconnect();
        }
    }
}
