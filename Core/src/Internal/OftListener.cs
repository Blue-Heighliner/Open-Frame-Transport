namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// <inheritdoc cref="IOftListener" />
/// </summary>
internal sealed class OftListener : IOftListener
{
    private readonly OftConnectionOptions options;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource acceptLoopCts;
    private readonly OftBufferedHandlerSlot<Action<IOftConnection>> connectedSlot = new();
    private bool disposed;

    private OftListener(OftConnectionOptions options, TcpListener listener, CancellationTokenSource acceptLoopCts)
    {
        this.options = options;
        this.listener = listener;
        this.acceptLoopCts = acceptLoopCts;
        _ = Task.Run(this.AcceptLoop, CancellationToken.None);
    }

    /// <summary>
    /// Starts listening on <paramref name="listenEndPoint"/> and returns the resulting listener.
    /// </summary>
    /// <param name="options">The options used to accept every connection.</param>
    /// <param name="listenEndPoint">The local endpoint to listen for incoming TCP connections on.</param>
    /// <param name="cancellationToken">A token that stops the listener when cancelled.</param>
    internal static OftListener Start(OftConnectionOptions options, IPEndPoint listenEndPoint, CancellationToken cancellationToken)
    {
        // Resolved once per listener rather than per accepted connection: nothing validates this
        // certificate under Secure mode, so one throwaway certificate reused for the listener's
        // whole lifetime is both correct and far cheaper than generating a fresh RSA keypair on
        // every single inbound connection.
        if (options.SecurityMode == OftSecurityMode.Secure)
        {
            options = options with { Certificate = OftEphemeralCertificate.Create() };
        }

        TcpListener listener = new(listenEndPoint);
        listener.Start();

        CancellationTokenSource acceptLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new OftListener(options, listener, acceptLoopCts);
    }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint => (IPEndPoint)this.listener.LocalEndpoint;

    /// <inheritdoc />
    public Action<IOftConnection>? ConnectedHandler
    {
        get => this.connectedSlot.Handler;
        set => this.connectedSlot.Handler = value;
    }

    /// <summary>
    /// Immediately and synchronously stops listening and releases this listener's resources, without
    /// waiting for its background accept loop to actually finish running, which happens shortly
    /// afterward on its own.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        this.acceptLoopCts.Cancel();
        this.listener.Stop();

        // acceptLoopCts is deliberately never disposed here: AcceptLoop may still be unwinding (see
        // its own catch blocks) and could still be touching its token by the time this returns, so
        // disposing it here could race with that - it's cheap enough to just leave for the GC.

        // Nobody will ever assign a callback to a disposed listener after this point, so a connected
        // notification still buffered for lack of one would otherwise be held onto forever - though,
        // unlike OftConnection's received notifications, this owns nothing disposable; this is just
        // consistent cleanup.
        this.connectedSlot.DisposeBuffered();
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
            // Expected when Dispose() closes the listener while a call to AcceptTcpClientAsync is pending.
        }
        catch (SocketException)
        {
            // Expected when Dispose() closes the listener while a call to AcceptTcpClientAsync is pending.
        }
    }

    private async Task HandleAccepted(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        try
        {
            OftConnection connection = await OftConnection.EstablishAsServer(tcpClient, this.options, cancellationToken).ConfigureAwait(false);

            // Safe to start processing immediately, before raising the connected notification: that
            // notification is backed by OftBufferedHandlerSlot, so a received/disconnected
            // notification this connection raises before a caller reacting to it gets a chance to
            // assign its own callbacks is buffered rather than lost (see README.md and
            // OftBufferedHandlerSlot's own doc comment).
            connection.StartProcessing();
            this.connectedSlot.Raise(callback => callback(connection));
        }
        catch
        {
            tcpClient.Dispose();
        }
    }
}
