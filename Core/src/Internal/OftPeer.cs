namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// <inheritdoc cref="IOftPeer" />
/// </summary>
internal sealed class OftPeer : IOftPeer
{
    private readonly OftPeerOptions options;
    private readonly IOftConnector connector;
    private readonly OftConnectionOptions connectOptions;
    private readonly IOftHoster hoster;
    private readonly OftConnectionOptions hostOptions;

    private readonly Dictionary<(string Host, int Port), Task<IOftConnection>> outboundConnections = new();
    private readonly object outboundLock = new();

    private readonly List<IOftConnection> inboundConnections = [];
    private readonly object inboundLock = new();

    private readonly OftBufferedHandlerSlot<Action<IOftPeerReception>> receivedSlot = new();

    /// <summary>
    /// How long a connection must have had no pending data (see
    /// <see cref="IOftConnection.HasPendingData"/>) before it becomes eligible for automatic
    /// eviction (idle, lifetime, or excess-count based) at all — a fixed value, not configurable,
    /// giving the underlying TLS/TCP layers time to actually flush and acknowledge everything after
    /// the last application-level message completes, rather than evicting the instant
    /// <see cref="IOftConnection.HasPendingData"/> turns <see langword="false"/>.
    /// </summary>
    private static readonly TimeSpan EvictionGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often <see cref="RunEviction"/> checks connections against
    /// <see cref="OftPeerOptions.IdleTimeout"/>, <see cref="OftPeerOptions.MaxConnectionLifetime"/>,
    /// and <see cref="OftPeerOptions.MaxConnectionCount"/> — a fixed value, not configurable (see
    /// <see cref="IOftPeer"/>'s own doc comment). Since eviction only ever runs on this cadence,
    /// neither of those two duration-based options can take effect any sooner than this floor.
    /// </summary>
    private static readonly TimeSpan EvictionCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When each tracked connection was first observed, by <see cref="RunEviction"/>, to have no
    /// pending data (see <see cref="IOftConnection.HasPendingData"/>) — cleared again the moment a
    /// connection is next observed with pending data, so a connection that resumes sending after a
    /// quiet period gets a fresh <see cref="EvictionGracePeriod"/> once it finishes again. A
    /// connection isn't a candidate for any automatic eviction (idle, lifetime, or excess-count)
    /// until this has aged past <see cref="EvictionGracePeriod"/>.
    /// </summary>
    private readonly Dictionary<IOftConnection, DateTimeOffset> pendingDataClearedAt = new();
    private readonly object pendingDataClearedAtLock = new();

    private IOftListener? listener;
    private readonly object listenerLock = new();

    private readonly Timer evictionTimer;

    private readonly object disconnectLock = new();
    private bool disconnected;

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
    public OftPeer(OftPeerOptions options, IOftConnector connector, OftConnectionOptions connectOptions, IOftHoster hoster, OftConnectionOptions hostOptions)
    {
        this.options = options;
        this.connector = connector;
        this.connectOptions = connectOptions;
        this.hoster = hoster;
        this.hostOptions = hostOptions;

        this.evictionTimer = new Timer(_ => this.RunEviction(), null, EvictionCheckInterval, EvictionCheckInterval);
    }

    /// <inheritdoc />
    public bool IsConnected => !this.disconnected;

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
    public Action<IOftPeerReception>? ReceivedHandler
    {
        get => this.receivedSlot.Handler;
        set => this.receivedSlot.Handler = value;
    }

    /// <inheritdoc />
    public async Task Listen(IPEndPoint listenEndPoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disconnected, this);

        IOftListener opened = await this.hoster.Host(listenEndPoint, this.hostOptions, cancellationToken).ConfigureAwait(false);
        opened.ConnectedHandler = connection =>
        {
            lock (this.inboundLock)
            {
                this.inboundConnections.Add(connection);
            }

            this.TrackConnection(connection, tracked =>
            {
                lock (this.inboundLock)
                {
                    this.inboundConnections.Remove(tracked);
                }
            });
        };

        lock (this.listenerLock)
        {
            this.listener = opened;
        }
    }

    /// <inheritdoc />
    public Task StopListening()
    {
        ObjectDisposedException.ThrowIf(this.disconnected, this);

        IOftListener? currentListener;
        lock (this.listenerLock)
        {
            currentListener = this.listener;
            this.listener = null;
        }

        currentListener?.Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Send(string host, int port, ReadOnlyMemory<byte> data, int priority = 0, CancellationToken cancellationToken = default)
    {
        if (this.disconnected)
        {
            throw new OftDisconnectedException("This peer is no longer connected.");
        }

        IOftConnection connection = await this.GetOrCreateConnection(host, port, cancellationToken).ConfigureAwait(false);
        await connection.Send(data, priority, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Send(string host, int port, IMemoryOwner<byte> data, int priority = 0, CancellationToken cancellationToken = default)
    {
        if (this.disconnected)
        {
            throw new OftDisconnectedException("This peer is no longer connected.");
        }

        IOftConnection connection = await this.GetOrCreateConnection(host, port, cancellationToken).ConfigureAwait(false);
        await connection.Send(data, priority, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Rekey(CancellationToken cancellationToken = default)
    {
        if (this.disconnected)
        {
            throw new OftDisconnectedException("This peer is no longer connected.");
        }

        return Task.WhenAll(this.GetTrackedConnections().Select(connection => connection.Rekey(cancellationToken)));
    }

    /// <inheritdoc />
    public Task Drop()
    {
        ObjectDisposedException.ThrowIf(this.disconnected, this);
        return Task.WhenAll(this.GetTrackedConnections().Select(connection => connection.Disconnect()));
    }

    /// <inheritdoc />
    public async Task Disconnect()
    {
        if (!this.TryBeginDisconnect())
        {
            return;
        }

        this.evictionTimer.Dispose();

        IOftListener? currentListener;
        lock (this.listenerLock)
        {
            currentListener = this.listener;
            this.listener = null;
        }

        currentListener?.Dispose();

        await Task.WhenAll(this.GetTrackedConnections().Select(connection => connection.Disconnect())).ConfigureAwait(false);

        this.receivedSlot.DisposeBuffered();
    }

    /// <summary>
    /// Immediately and synchronously puts this peer into a disconnected state: stops listening (if
    /// applicable), immediately terminates every connection it currently holds without waiting for
    /// any of their background work to finish, and releases every other resource it owns. Call
    /// <see cref="Disconnect"/> instead for a graceful, awaitable teardown.
    /// </summary>
    public void Dispose()
    {
        if (!this.TryBeginDisconnect())
        {
            return;
        }

        this.evictionTimer.Dispose();

        IOftListener? currentListener;
        lock (this.listenerLock)
        {
            currentListener = this.listener;
            this.listener = null;
        }

        currentListener?.Dispose();

        foreach (IOftConnection connection in this.GetTrackedConnections())
        {
            connection.Dispose();
        }

        this.receivedSlot.DisposeBuffered();
    }

    /// <summary>
    /// Atomically transitions this peer into a disconnected state exactly once: returns
    /// <see langword="true"/> the first time this is called (by either <see cref="Disconnect"/> or
    /// <see cref="Dispose"/>), and <see langword="false"/> every time after, for both of them - so
    /// whichever is called first performs the actual teardown, and any subsequent call to either is
    /// a no-op.
    /// </summary>
    private bool TryBeginDisconnect()
    {
        lock (this.disconnectLock)
        {
            if (this.disconnected)
            {
                return false;
            }

            this.disconnected = true;
            return true;
        }
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
        // Assigning ReceivedHandler/DisconnectedHandler after this.connector.Connect returns is safe:
        // they're backed by OftBufferedHandlerSlot, so nothing the connection raised in the meantime
        // is lost (see README.md and OftBufferedHandlerSlot's own doc comment).
        IOftConnection connection = await this.connector.Connect(host, port, this.connectOptions, cancellationToken).ConfigureAwait(false);

        this.TrackConnection(connection, tracked =>
        {
            lock (this.outboundLock)
            {
                if (this.outboundConnections.TryGetValue((host, port), out Task<IOftConnection>? current) &&
                    current.IsCompletedSuccessfully && current.Result == tracked)
                {
                    this.outboundConnections.Remove((host, port));
                }
            }
        });

        return connection;
    }

    /// <summary>
    /// Forwards a tracked connection's received messages to this peer's own
    /// <see cref="ReceivedHandler"/>, and runs <paramref name="onDisconnectedTrackingCleanup"/> when
    /// it disconnects to untrack it (from <see cref="outboundConnections"/> or
    /// <see cref="inboundConnections"/> as appropriate) — this peer has no external disconnected
    /// notification of its own to forward to (see <see cref="IOftPeer.ReceivedHandler"/>'s own doc
    /// comment for why).
    /// </summary>
    private void TrackConnection(IOftConnection connection, Action<IOftConnection> onDisconnectedTrackingCleanup)
    {
        connection.ReceivedHandler = data =>
        {
            OftPeerReception reception = new(data, connection.Identity);
            this.receivedSlot.Raise(callback => callback(reception), discardedDisposable: reception);
        };
        connection.DisconnectedHandler = _ =>
        {
            onDisconnectedTrackingCleanup(connection);

            lock (this.pendingDataClearedAtLock)
            {
                this.pendingDataClearedAt.Remove(connection);
            }
        };
    }

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
        // partially reassembled. It's only a candidate once all of its data has been acknowledged,
        // and even then, only once EvictionGracePeriod has passed since that happened - giving the
        // underlying TLS/TCP layers time to actually flush and acknowledge everything rather than
        // evicting the instant HasPendingData turns false.
        List<IOftConnection> evictionCandidates = [];
        lock (this.pendingDataClearedAtLock)
        {
            foreach (IOftConnection connection in tracked)
            {
                if (connection.HasPendingData)
                {
                    this.pendingDataClearedAt.Remove(connection);
                    continue;
                }

                if (!this.pendingDataClearedAt.TryGetValue(connection, out DateTimeOffset clearedAt))
                {
                    clearedAt = now;
                    this.pendingDataClearedAt[connection] = clearedAt;
                }

                if (now - clearedAt >= EvictionGracePeriod)
                {
                    evictionCandidates.Add(connection);
                }
            }
        }

        HashSet<IOftConnection> toDisconnect = [];
        foreach (IOftConnection connection in evictionCandidates)
        {
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
            foreach (IOftConnection connection in evictionCandidates
                .Where(connection => !toDisconnect.Contains(connection))
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
