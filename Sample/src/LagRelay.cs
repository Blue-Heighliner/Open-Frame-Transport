namespace OpenFrameTransport.Sample;

/// <summary>
/// A local TCP relay that forwards raw bytes to a single fixed remote target, inserting an
/// artificial delay before forwarding each chunk it reads in either direction. Used by the sample
/// to simulate network lag without needing any support from OFT itself: OFT (and the TLS session
/// underneath it) never knows the relay is there, since it operates below TLS as a dumb byte pipe.
/// </summary>
internal sealed class LagRelay : IAsyncDisposable
{
    private const int BufferSize = 8192;

    private readonly TcpListener listener;
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly Func<TimeSpan> getLag;
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptLoopTask;

    private LagRelay(TcpListener listener, string targetHost, int targetPort, Func<TimeSpan> getLag)
    {
        this.listener = listener;
        this.targetHost = targetHost;
        this.targetPort = targetPort;
        this.getLag = getLag;
        this.acceptLoopTask = Task.Run(this.AcceptLoop);
    }

    /// <summary>
    /// The loopback port callers should connect to in order to reach the relay's target through it.
    /// </summary>
    public int Port => ((IPEndPoint)this.listener.LocalEndpoint).Port;

    /// <summary>
    /// Starts a new relay listening on an OS-assigned loopback port, forwarding every connection it
    /// accepts to <paramref name="targetHost"/>:<paramref name="targetPort"/>.
    /// </summary>
    /// <param name="targetHost">The real host to forward connections to.</param>
    /// <param name="targetPort">The real port to forward connections to.</param>
    /// <param name="getLag">
    /// Invoked before forwarding each chunk of bytes, in either direction, to determine the current
    /// artificial delay. Read live on every chunk, so changing the value affects transfers already
    /// in progress.
    /// </param>
    public static LagRelay Start(string targetHost, int targetPort, Func<TimeSpan> getLag)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new LagRelay(listener, targetHost, targetPort, getLag);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.cts.CancelAsync().ConfigureAwait(false);
        this.listener.Stop();

        try
        {
            await this.acceptLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Expected during shutdown.
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!this.cts.IsCancellationRequested)
            {
                TcpClient inbound = await this.listener.AcceptTcpClientAsync(this.cts.Token).ConfigureAwait(false);
                _ = this.RelayConnection(inbound);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Expected when DisposeAsync stops the listener while AcceptTcpClientAsync is pending.
        }
    }

    private async Task RelayConnection(TcpClient inbound)
    {
        using TcpClient inboundClient = inbound;
        using TcpClient outboundClient = new();

        try
        {
            await outboundClient.ConnectAsync(this.targetHost, this.targetPort, this.cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        NetworkStream inboundStream = inboundClient.GetStream();
        NetworkStream outboundStream = outboundClient.GetStream();

        Task toTarget = this.Pump(inboundStream, outboundStream);
        Task toCaller = this.Pump(outboundStream, inboundStream);
        await Task.WhenAny(toTarget, toCaller).ConfigureAwait(false);
    }

    private async Task Pump(NetworkStream source, NetworkStream destination)
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, this.cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                TimeSpan lag = this.getLag();
                if (lag > TimeSpan.Zero)
                {
                    await Task.Delay(lag, this.cts.Token).ConfigureAwait(false);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), this.cts.Token).ConfigureAwait(false);
                await destination.FlushAsync(this.cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // The connection closed, or the relay is shutting down.
        }
    }
}
