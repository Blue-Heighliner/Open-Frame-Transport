namespace OpenFrameTransport.Tests;

/// <summary>
/// A connected client/server pair, both established against each other, for use in tests.
/// </summary>
internal sealed class OftPair : IAsyncDisposable
{
    public required IOftListener Listener { get; init; }

    public required IOftConnection ServerConnection { get; init; }

    public required IOftConnection ClientConnection { get; init; }

    public async ValueTask DisposeAsync()
    {
        await this.ClientConnection.DisposeAsync();
        await this.ServerConnection.DisposeAsync();
        await this.Listener.DisposeAsync();
    }
}

/// <summary>
/// Wraps an <see cref="IOftListener"/>, additionally tracking every connection it has accepted that
/// hasn't disconnected yet — since <see cref="IOftListener"/> itself doesn't (see its own doc
/// comment), tests that need to observe accepted-connection counts do it this way instead.
/// </summary>
internal sealed class TrackedListener : IAsyncDisposable
{
    private readonly IOftListener listener;
    private readonly List<IOftConnection> connections = [];
    private readonly object connectionsLock = new();

    private TrackedListener(IOftListener listener)
    {
        this.listener = listener;
        listener.Connected += this.OnConnected;
    }

    public static async Task<TrackedListener> Start(IPEndPoint listenEndPoint, OftHostOptions? options = null, CancellationToken cancellationToken = default) =>
        new(await new OftHoster().Host(listenEndPoint, options, cancellationToken).ConfigureAwait(false));

    public IPEndPoint LocalEndPoint => this.listener.LocalEndPoint;

    public IReadOnlyCollection<IOftConnection> Connections
    {
        get
        {
            lock (this.connectionsLock)
            {
                return this.connections.ToArray();
            }
        }
    }

    public event EventHandler<OftConnectedEventArgs>? Connected
    {
        add => this.listener.Connected += value;
        remove => this.listener.Connected -= value;
    }

    private void OnConnected(object? sender, OftConnectedEventArgs args)
    {
        lock (this.connectionsLock)
        {
            this.connections.Add(args.Connection);
        }

        args.Connection.Disconnected += (_, _) =>
        {
            lock (this.connectionsLock)
            {
                this.connections.Remove(args.Connection);
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        this.listener.Connected -= this.OnConnected;
        await this.listener.DisposeAsync();
    }
}

/// <summary>
/// Shared setup for tests that need a live, established OFT connection over real TCP/TLS on the
/// loopback interface.
/// </summary>
internal static class OftTestHarness
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static async Task<OftPair> Establish(
        int maxPacketDataSize = 16384,
        TimeSpan? rekeyInterval = null,
        OftSecurityMode securityMode = OftSecurityMode.Authentication,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null)
    {
        bool needsServerCertificate = securityMode is OftSecurityMode.Authentication or OftSecurityMode.DualAuthentication;

        OftHostOptions hostOptions = new()
        {
            Info = "server",
            ServerCertificate = needsServerCertificate ? TestCertificate.Create() : null,
            ClientCertificateValidation = (_, _, _, _) => true,
            MaxPacketDataSize = maxPacketDataSize,
            RekeyInterval = rekeyInterval,
            SecurityMode = securityMode,
            PollInterval = pollInterval ?? TimeSpan.FromSeconds(1),
            PollTimeout = pollTimeout ?? TimeSpan.FromSeconds(5),
        };

        IOftHoster hoster = new OftHoster();
        IOftListener listener = await hoster.Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Connected += (_, args) => serverConnectionSource.TrySetResult(args.Connection);

        OftConnectOptions connectOptions = new()
        {
            Info = "client",
            MaxPacketDataSize = maxPacketDataSize,
            RekeyInterval = rekeyInterval,
            SecurityMode = securityMode,
            PollInterval = pollInterval ?? TimeSpan.FromSeconds(1),
            PollTimeout = pollTimeout ?? TimeSpan.FromSeconds(5),
            ClientCertificates = securityMode == OftSecurityMode.DualAuthentication ? [TestCertificate.Create()] : null,
            ServerCertificateValidation = (_, _, _, _) => true,
        };

        IOftConnector connector = new OftConnector();

        IOftConnection clientConnection = await connector.Connect("127.0.0.1", listener.LocalEndPoint.Port, connectOptions).WaitAsync(DefaultTimeout);
        IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(DefaultTimeout);

        return new OftPair
        {
            Listener = listener,
            ServerConnection = serverConnection,
            ClientConnection = clientConnection,
        };
    }

    public static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Binds and immediately releases a loopback port, so a test can dial a port with high
    /// confidence nothing is listening on it.
    /// </summary>
    public static int ReserveFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Dials and TLS-handshakes a raw connection to a local OFT server, without performing the hail
    /// exchange, so a test can drive the OFT wire protocol directly and exercise the server's
    /// handling of malformed or hostile input.
    /// </summary>
    public static async Task<(TcpClient TcpClient, SslStream SslStream, OftFrameStream FrameStream)> RawConnect(int port, CancellationToken cancellationToken = default)
    {
        TcpClient tcpClient = new();
        await tcpClient.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
        SslStream sslStream = new(tcpClient.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" }, cancellationToken).ConfigureAwait(false);
        return (tcpClient, sslStream, new OftFrameStream(sslStream));
    }

    /// <summary>
    /// Dials a raw, TLS-less connection to a local insecure OFT server, so a test can drive the OFT
    /// wire protocol directly over plain TCP.
    /// </summary>
    public static async Task<(TcpClient TcpClient, OftFrameStream FrameStream)> RawConnectInsecure(int port, CancellationToken cancellationToken = default)
    {
        TcpClient tcpClient = new();
        await tcpClient.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
        return (tcpClient, new OftFrameStream(tcpClient.GetStream()));
    }
}
