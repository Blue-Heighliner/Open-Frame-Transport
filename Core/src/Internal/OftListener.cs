namespace OpenFrameTransport.Internal;

/// <summary>
/// <inheritdoc cref="IOftListener" />
/// </summary>
internal sealed class OftListener : IOftListener
{
    private readonly OftHostOptions options;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource acceptLoopCts;
    private readonly Task acceptLoopTask;
    private readonly OftBufferedEvent<OftConnectedEventArgs> connectedEvent;
    private bool disposed;

    private OftListener(OftHostOptions options, TcpListener listener, CancellationTokenSource acceptLoopCts)
    {
        this.options = options;
        this.listener = listener;
        this.acceptLoopCts = acceptLoopCts;
        this.connectedEvent = new OftBufferedEvent<OftConnectedEventArgs>(this);
        this.acceptLoopTask = Task.Run(this.AcceptLoop, CancellationToken.None);
    }

    /// <summary>
    /// Starts listening on <paramref name="listenEndPoint"/> and returns the resulting listener.
    /// </summary>
    /// <param name="options">The options used to accept every connection.</param>
    /// <param name="listenEndPoint">The local endpoint to listen for incoming TCP connections on.</param>
    /// <param name="cancellationToken">A token that stops the listener when cancelled.</param>
    internal static OftListener Start(OftHostOptions options, IPEndPoint listenEndPoint, CancellationToken cancellationToken)
    {
        // Resolved once per listener rather than per accepted connection: nothing validates this
        // certificate under Secure mode, so one throwaway certificate reused for the listener's
        // whole lifetime is both correct and far cheaper than generating a fresh RSA keypair on
        // every single inbound connection.
        if (options.SecurityMode == OftSecurityMode.Secure)
        {
            options = options with { ServerCertificate = OftEphemeralCertificate.Create() };
        }

        TcpListener listener = new(listenEndPoint);
        listener.Start();

        CancellationTokenSource acceptLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new OftListener(options, listener, acceptLoopCts);
    }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint => (IPEndPoint)this.listener.LocalEndpoint;

    /// <inheritdoc />
    public event EventHandler<OftConnectedEventArgs>? Connected
    {
        add => this.connectedEvent.Subscribe(value);
        remove => this.connectedEvent.Unsubscribe(value);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        await this.acceptLoopCts.CancelAsync().ConfigureAwait(false);
        this.listener.Stop();

        try
        {
            await this.acceptLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Expected during shutdown.
        }

        this.acceptLoopCts.Dispose();

        // Nobody will ever subscribe to a disposed listener's events after this point, so a
        // Connected still buffered for lack of a subscriber would otherwise be held onto forever -
        // though, unlike OftConnection's Received, OftConnectedEventArgs owns nothing disposable;
        // this is just consistent cleanup.
        this.connectedEvent.DisposeBuffered();
    }

    private async Task AcceptLoop()
    {
        CancellationToken cancellationToken = this.acceptLoopCts.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient tcpClient = await this.listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = this.HandleAccepted(tcpClient, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Expected when DisposeAsync() closes the listener while a call to AcceptTcpClientAsync is pending.
        }
        catch (SocketException)
        {
            // Expected when DisposeAsync() closes the listener while a call to AcceptTcpClientAsync is pending.
        }
    }

    private async Task HandleAccepted(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        try
        {
            OftConnection connection = await OftConnection.EstablishAsServer(tcpClient, this.options, cancellationToken).ConfigureAwait(false);

            // Safe to start processing immediately, before raising Connected: Connected is backed by
            // OftBufferedEvent, so a Received/Disconnected this connection raises before a caller
            // reacting to Connected gets a chance to subscribe is buffered rather than lost (see
            // README.md and OftBufferedEvent's own doc comment).
            connection.StartProcessing();
            this.connectedEvent.Raise(new OftConnectedEventArgs { Connection = connection });
        }
        catch
        {
            tcpClient.Dispose();
        }
    }
}
