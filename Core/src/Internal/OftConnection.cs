namespace OpenFrameTransport.Internal;

/// <summary>
/// <inheritdoc cref="IOftConnection" />
/// </summary>
internal sealed class OftConnection : IOftConnection
{
    private readonly TcpClient tcpClient;
    private readonly NetworkStream networkStream;

    /// <summary>
    /// <see langword="null"/> for an insecure connection, which never rekeys since it has no TLS
    /// session to rekey. Otherwise the BouncyCastle TLS 1.3 protocol object backing this connection,
    /// used only to request a <c>KeyUpdate</c> (see Docs/OFT.md §8) — everything else about it is
    /// accessed through <see cref="plaintextStream"/>/<see cref="frameStream"/> instead.
    /// </summary>
    private readonly IOftTlsRekeyableProtocol? tlsProtocol;

    private readonly OftConnectionOptions options;

    private readonly object outboundLock = new();
    private readonly Dictionary<int, Queue<PendingOutboundMessage>> outboundQueues = new();
    private readonly SemaphoreSlim sendSignal = new(0);

    /// <summary>
    /// Held by whichever of the send loop or the receive loop is currently entitled to write the
    /// next Unit/Data/Completion/Cancellation packet, so the connection never has more than one such
    /// packet in flight at a time (see Docs/OFT.md §4.1). Rekeying (see Docs/OFT.md §8) is a TLS-layer
    /// operation, invisible to this packet-level turn-taking, and never touches this permit.
    /// </summary>
    private readonly SemaphoreSlim writePermit = new(1, 1);
    private TaskCompletionSource<bool>? outstandingReceipt;

    /// <summary>
    /// Serializes calls into <see cref="IOftTlsRekeyableProtocol.RequestKeyUpdate"/>: BouncyCastle's
    /// TLS 1.3 traffic-secret rotation is not safe to enter concurrently with itself on the same
    /// connection (two overlapping calls can race on the same secret object, one extracting/replacing
    /// it out from under the other). Unrelated to <see cref="writePermit"/> — this only ever guards
    /// <see cref="Rekey"/> against itself, not against packet writes.
    /// </summary>
    private readonly SemaphoreSlim rekeyPermit = new(1, 1);

    /// <summary>
    /// Each chunk is stored as a zero-copy view over its parsed <see cref="Packet.Data"/>
    /// <see cref="ByteString"/> (already privately owned and immutable once parsed), rather than a
    /// fresh copy, until the message is fully reassembled.
    /// </summary>
    private readonly Dictionary<int, List<ReadOnlyMemory<byte>>> inboundBuffers = new();

    /// <summary>
    /// Mirrors <c>inboundBuffers.Count > 0</c> for <see cref="HasPendingData"/> to read from any
    /// thread without synchronizing with the receive loop, which is otherwise the only thread that
    /// ever touches <see cref="inboundBuffers"/>. Written only by the receive loop, immediately
    /// after every mutation of <see cref="inboundBuffers"/>.
    /// </summary>
    private volatile bool hasInProgressInboundMessage;

    private Timer? rekeyTimer;
    private Timer? pollTimer;

    /// <summary>
    /// When the connection last received anything at all — a <c>Poll</c> packet or any other kind —
    /// used exclusively by the liveness watchdog (see Docs/OFT.md §10). Deliberately tracked
    /// separately from <see cref="lastReceivedAtTicks"/> (which only <c>Poll</c> leaves untouched):
    /// an <see cref="IOftPeer"/>'s idle-eviction relies on <see cref="LastReceivedAt"/> reflecting
    /// application activity only, and automatic <c>Poll</c> traffic would otherwise mask a
    /// connection an application never actually uses as perpetually "active".
    /// </summary>
    private long lastInboundActivityTicks;

    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly object closeLock = new();
    private bool closed;

    private long connectedAtTicks;
    private long lastSentAtTicks;
    private long lastReceivedAtTicks;

    private readonly Stream plaintextStream;
    private readonly OftFrameStream frameStream;
    private Task? receiveLoopTask;
    private Task? sendLoopTask;

    private readonly OftBufferedEvent<OftReceivedEventArgs> receivedEvent;
    private readonly OftBufferedEvent<OftDisconnectedEventArgs> disconnectedEvent;

    private OftConnection(TcpClient tcpClient, NetworkStream networkStream, Stream plaintextStream, IOftTlsRekeyableProtocol? tlsProtocol, OftConnectionOptions options)
    {
        this.tcpClient = tcpClient;
        this.networkStream = networkStream;
        this.plaintextStream = plaintextStream;
        this.tlsProtocol = tlsProtocol;
        this.frameStream = new OftFrameStream(plaintextStream);
        this.options = options;
        this.RemoteInfo = string.Empty;
        this.receivedEvent = new OftBufferedEvent<OftReceivedEventArgs>(this);
        this.disconnectedEvent = new OftBufferedEvent<OftDisconnectedEventArgs>(this);
    }

    /// <inheritdoc />
    public IPEndPoint RemoteEndPoint => (IPEndPoint)this.tcpClient.Client.RemoteEndPoint!;

    /// <inheritdoc />
    public string RemoteInfo { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset ConnectedAt => new(Interlocked.Read(ref this.connectedAtTicks), TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset LastSentAt => new(Interlocked.Read(ref this.lastSentAtTicks), TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset LastReceivedAt => new(Interlocked.Read(ref this.lastReceivedAtTicks), TimeSpan.Zero);

    /// <inheritdoc />
    public bool HasPendingData
    {
        get
        {
            lock (this.outboundLock)
            {
                foreach (Queue<PendingOutboundMessage> queue in this.outboundQueues.Values)
                {
                    if (queue.Count > 0)
                    {
                        return true;
                    }
                }
            }

            return this.hasInProgressInboundMessage;
        }
    }

    /// <inheritdoc />
    public event EventHandler<OftReceivedEventArgs>? Received
    {
        add => this.receivedEvent.Subscribe(value);
        remove => this.receivedEvent.Unsubscribe(value);
    }

    /// <inheritdoc />
    public event EventHandler<OftDisconnectedEventArgs>? Disconnected
    {
        add => this.disconnectedEvent.Subscribe(value);
        remove => this.disconnectedEvent.Unsubscribe(value);
    }

    /// <summary>
    /// Dials <paramref name="tcpClient"/>, performs the client-side TLS 1.3 handshake (unless
    /// <see cref="OftConnectionOptions.SecurityMode"/> is <see cref="OftSecurityMode.Insecure"/>) and
    /// hail exchange against it, and returns the resulting established connection.
    /// </summary>
    internal static async Task<OftConnection> EstablishAsClient(TcpClient tcpClient, string targetHost, OftConnectOptions options, CancellationToken cancellationToken)
    {
        NetworkStream networkStream = tcpClient.GetStream();

        if (options.SecurityMode == OftSecurityMode.Insecure)
        {
            OftConnection insecureConnection = new(tcpClient, networkStream, networkStream, tlsProtocol: null, options);
            await insecureConnection.CompleteHandshake(cancellationToken).ConfigureAwait(false);
            return insecureConnection;
        }

        BcTlsCrypto crypto = new();
        bool skipServerCertificateValidation = options.SecurityMode == OftSecurityMode.Secure;
        OftTlsClient tlsClient = new(crypto, targetHost, skipServerCertificateValidation, options.ClientCertificates, options.ServerCertificateValidation);
        OftTlsClientProtocol protocol = new(networkStream);

        await RunBlockingTlsOperation(() => protocol.Connect(tlsClient), tcpClient, cancellationToken).ConfigureAwait(false);

        OftConnection connection = new(tcpClient, networkStream, new OftBlockingStream(protocol.Stream), protocol, options);
        await connection.CompleteHandshake(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Accepts <paramref name="tcpClient"/>, performs the server-side TLS 1.3 handshake (unless
    /// <see cref="OftConnectionOptions.SecurityMode"/> is <see cref="OftSecurityMode.Insecure"/>) and
    /// hail exchange against it, and returns the resulting established connection.
    /// </summary>
    internal static async Task<OftConnection> EstablishAsServer(TcpClient tcpClient, OftHostOptions options, CancellationToken cancellationToken)
    {
        NetworkStream networkStream = tcpClient.GetStream();

        if (options.SecurityMode == OftSecurityMode.Insecure)
        {
            OftConnection insecureConnection = new(tcpClient, networkStream, networkStream, tlsProtocol: null, options);
            await insecureConnection.CompleteHandshake(cancellationToken).ConfigureAwait(false);
            return insecureConnection;
        }

        // By this point ServerCertificate is always resolved: for Secure mode, IOftListener.Start
        // has already replaced it with a listener-lifetime ephemeral certificate; for
        // Authentication/DualAuthentication, IOftHoster.Host has already validated the caller
        // supplied a real one.
        X509Certificate2 serverCertificate = options.ServerCertificate!;

        BcTlsCrypto crypto = new();
        bool requireClientCertificate = options.SecurityMode == OftSecurityMode.DualAuthentication;
        OftTlsServer tlsServer = new(crypto, serverCertificate, requireClientCertificate, options.ClientCertificateValidation);
        OftTlsServerProtocol protocol = new(networkStream);

        await RunBlockingTlsOperation(() => protocol.Accept(tlsServer), tcpClient, cancellationToken).ConfigureAwait(false);

        OftConnection connection = new(tcpClient, networkStream, new OftBlockingStream(protocol.Stream), protocol, options);
        await connection.CompleteHandshake(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> — a blocking BouncyCastle TLS call, which has no
    /// cancellation support of its own — on a dedicated (not thread-pool) thread, and makes it
    /// respond to <paramref name="cancellationToken"/> anyway by disposing <paramref name="tcpClient"/>
    /// if the token fires before the operation finishes: the operation's own blocking socket I/O then
    /// fails, unblocking its thread, and that failure is surfaced here as a clean
    /// <see cref="OperationCanceledException"/> instead of whatever I/O exception disposal caused.
    /// </summary>
    private static async Task RunBlockingTlsOperation(Action operation, TcpClient tcpClient, CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(tcpClient.Dispose);
        try
        {
            await Task.Factory.StartNew(operation, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task Send(ReadOnlyMemory<byte> data, int priority = 0, CancellationToken cancellationToken = default) =>
        this.EnqueueMessage(data, owner: null, priority, cancellationToken);

    /// <inheritdoc />
    public Task Send(IMemoryOwner<byte> data, int priority = 0, CancellationToken cancellationToken = default) =>
        this.EnqueueMessage(data.Memory, owner: data, priority, cancellationToken);

    private Task EnqueueMessage(ReadOnlyMemory<byte> data, IMemoryOwner<byte>? owner, int priority, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.closed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(priority);

        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingOutboundMessage message = new()
        {
            Data = data,
            Owner = owner,
            Priority = priority,
            CompletionSource = completionSource,
            CancellationToken = cancellationToken,
        };

        lock (this.outboundLock)
        {
            if (!this.outboundQueues.TryGetValue(priority, out Queue<PendingOutboundMessage>? queue))
            {
                queue = new Queue<PendingOutboundMessage>();
                this.outboundQueues[priority] = queue;
            }

            queue.Enqueue(message);
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => this.RequestCancellation(message));
        }

        this.sendSignal.Release();
        return completionSource.Task;
    }

    /// <inheritdoc />
    public async Task Rekey(CancellationToken cancellationToken = default)
    {
        if (this.tlsProtocol is null)
        {
            // No-op: an insecure (non-TLS) connection has no TLS session to rekey.
            return;
        }

        // A TLS 1.3 KeyUpdate is a record on the same continuous encrypted stream, safe to interleave
        // with application data by construction (see Docs/OFT.md §8) - there's nothing at the OFT
        // packet level to coordinate, so this just asks the TLS layer directly and returns once that
        // request has been sent. rekeyPermit only guards against overlapping with another concurrent
        // Rekey call on this same connection (see its declaration) - unrelated to writePermit.
        await this.rekeyPermit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(this.tlsProtocol.RequestKeyUpdate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.rekeyPermit.Release();
        }
    }

    /// <inheritdoc />
    public async Task Disconnect()
    {
        await this.Close(null).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.Close(null).ConfigureAwait(false);

        if (this.receiveLoopTask is not null)
        {
            try
            {
                await this.receiveLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // The receive loop's failure is already surfaced via the Disconnected event.
            }
        }

        if (this.sendLoopTask is not null)
        {
            try
            {
                await this.sendLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // The send loop's failure is already surfaced via the Disconnected event.
            }
        }
    }

    private async Task CompleteHandshake(CancellationToken cancellationToken)
    {
        Task sendHail = this.frameStream.Write(
            new Hail { Version = OftProtocolVersion.Current, Info = this.options.Info },
            cancellationToken);

        Hail? received = await this.frameStream.ReadHail(cancellationToken).ConfigureAwait(false);
        if (received is null)
        {
            throw new IOException("Connection closed before completing the OFT hail handshake.");
        }

        await sendHail.ConfigureAwait(false);

        if (received.Version != OftProtocolVersion.Current)
        {
            throw new InvalidOperationException($"Incompatible OFT protocol version '{received.Version}'.");
        }

        this.RemoteInfo = received.Info;
        Interlocked.Exchange(ref this.connectedAtTicks, NowTicks());
        this.UpdateLastSentAt();
        this.UpdateLastReceivedAt();
        Interlocked.Exchange(ref this.lastInboundActivityTicks, NowTicks());
    }

    /// <summary>
    /// Starts this connection's background work: the receive loop (which begins delivering inbound
    /// activity to <see cref="Received"/> subscribers), the send loop, and (if configured) the
    /// automatic rekey timer. Safe to call immediately after establishment, regardless of whether a
    /// caller has subscribed to <see cref="Received"/>/<see cref="Disconnected"/> yet: both are
    /// backed by <see cref="OftBufferedEvent{TEventArgs}"/>, which buffers any raise that happens
    /// before the first subscriber attaches rather than discarding it (see its own doc comment), so
    /// there is no ordering requirement between starting processing and a caller subscribing.
    /// </summary>
    internal void StartProcessing()
    {
        // LongRunning, rather than the default thread-pool scheduling: BouncyCastle's TLS Stream has
        // no true async I/O of its own (see Docs/OFT.md §1) - its ReadAsync/WriteAsync are the base
        // Stream class's default wrappers around a blocking synchronous call, each occupying a real
        // thread pool thread for the call's whole duration. Since the receive loop's read is *always*
        // pending (it immediately starts the next one after handling each packet), running it on the
        // thread pool would permanently pin one pool thread per secure connection for that
        // connection's entire lifetime - a handful of connections would already be enough to starve
        // the pool for every other unrelated piece of async work in the process. A dedicated thread
        // per loop (what LongRunning requests) has no such effect on shared pool capacity. Insecure
        // connections don't strictly need this (NetworkStream's async I/O is real), but there's no
        // downside to treating them the same way.
        this.receiveLoopTask = Task.Factory.StartNew(this.ReceiveLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        this.sendLoopTask = Task.Factory.StartNew(this.SendLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        // Rekeying requires a TLS session to rekey, so the timer is never started for an insecure
        // connection, even if RekeyInterval happens to be set.
        if (this.tlsProtocol is not null && this.options.RekeyInterval is { } interval)
        {
            this.rekeyTimer = new Timer(_ => _ = this.Rekey(CancellationToken.None), null, interval, interval);
        }

        this.pollTimer = new Timer(_ => _ = this.OnPollTimerTick(), null, this.options.PollInterval, this.options.PollInterval);
    }

    private async Task SendLoop()
    {
        try
        {
            while (!this.lifetimeCts.IsCancellationRequested)
            {
                await this.sendSignal.WaitAsync(this.lifetimeCts.Token).ConfigureAwait(false);

                while (true)
                {
                    PendingOutboundMessage? message;
                    lock (this.outboundLock)
                    {
                        message = this.PickNextMessage();
                    }

                    if (message is null)
                    {
                        break;
                    }

                    await this.writePermit.WaitAsync(this.lifetimeCts.Token).ConfigureAwait(false);
                    await this.SendNextPacket(message).ConfigureAwait(false);
                    this.writePermit.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            await this.Close(exception).ConfigureAwait(false);
        }
    }

    private PendingOutboundMessage? PickNextMessage()
    {
        int? bestPriority = null;
        foreach (KeyValuePair<int, Queue<PendingOutboundMessage>> entry in this.outboundQueues)
        {
            if (entry.Value.Count > 0 && (bestPriority is null || entry.Key > bestPriority))
            {
                bestPriority = entry.Key;
            }
        }

        return bestPriority is null ? null : this.outboundQueues[bestPriority.Value].Peek();
    }

    private async Task SendNextPacket(PendingOutboundMessage message)
    {
        Packet packet;
        bool finishesMessage;

        if (message.CancelRequested && message.Started)
        {
            packet = new Packet { Control = 3, Data = ByteString.Empty };
            finishesMessage = true;
        }
        else if (!message.Started && message.Data.Length <= this.options.MaxPacketDataSize)
        {
            packet = new Packet { Control = 1, Data = ToByteString(message.Data, message.Owner is not null) };
            message.Started = true;
            finishesMessage = true;
        }
        else
        {
            message.Started = true;
            int remaining = message.Data.Length - message.BytesSent;
            int chunkSize = Math.Min(remaining, this.options.MaxPacketDataSize);
            ReadOnlyMemory<byte> chunk = message.Data.Slice(message.BytesSent, chunkSize);
            bool isLast = message.BytesSent + chunkSize >= message.Data.Length;

            packet = new Packet
            {
                Control = isLast ? 2u : (uint)(message.Priority + 4),
                Data = ToByteString(chunk, message.Owner is not null),
            };
            message.BytesSent += chunkSize;
            finishesMessage = isLast;
        }

        TaskCompletionSource<bool> receiptSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        this.outstandingReceipt = receiptSource;

        await this.frameStream.Write(packet, this.lifetimeCts.Token).ConfigureAwait(false);
        this.UpdateLastSentAt();

        await receiptSource.Task.WaitAsync(this.lifetimeCts.Token).ConfigureAwait(false);

        if (finishesMessage)
        {
            lock (this.outboundLock)
            {
                this.outboundQueues[message.Priority].Dequeue();
            }

            message.Owner?.Dispose();

            if (message.CancelRequested)
            {
                message.CompletionSource.TrySetCanceled(message.CancellationToken);
            }
            else
            {
                message.CompletionSource.TrySetResult();
            }
        }
    }

    /// <summary>
    /// When <paramref name="owned"/>, wraps <paramref name="data"/> into a <see cref="ByteString"/>
    /// without copying it — safe only because the connection owns that memory exclusively for the
    /// message's whole lifetime (see <see cref="Send(IMemoryOwner{byte}, int, CancellationToken)"/>)
    /// and the packet built from it is serialized and written before the next chunk is touched.
    /// Otherwise copies, since the caller retains ownership and may reuse or mutate the buffer.
    /// </summary>
    private static ByteString ToByteString(ReadOnlyMemory<byte> data, bool owned) =>
        owned ? UnsafeByteOperations.UnsafeWrap(data) : ByteString.CopyFrom(data.Span);

    private void RequestCancellation(PendingOutboundMessage message)
    {
        lock (this.outboundLock)
        {
            if (!message.Started && this.outboundQueues.TryGetValue(message.Priority, out Queue<PendingOutboundMessage>? queue) && queue.Contains(message))
            {
                Queue<PendingOutboundMessage> rebuilt = new(queue.Where(candidate => candidate != message));
                this.outboundQueues[message.Priority] = rebuilt;
                message.Owner?.Dispose();
                message.CompletionSource.TrySetCanceled(message.CancellationToken);
                return;
            }

            message.CancelRequested = true;
        }

        this.sendSignal.Release();
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (true)
            {
                OftPacketRead read = await this.frameStream.ReadPacketOrPoll(this.lifetimeCts.Token).ConfigureAwait(false);
                if (read.Kind == OftPacketReadKind.Closed)
                {
                    await this.Close(null).ConfigureAwait(false);
                    return;
                }

                Interlocked.Exchange(ref this.lastInboundActivityTicks, NowTicks());
                if (read.Kind == OftPacketReadKind.Poll)
                {
                    continue;
                }

                this.UpdateLastReceivedAt();
                await this.HandlePacket(read.Packet!).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            await this.Close(exception).ConfigureAwait(false);
        }
    }

    private async Task HandlePacket(Packet packet)
    {
        if (packet.Control == 0)
        {
            TaskCompletionSource<bool>? receipt = Interlocked.Exchange(ref this.outstandingReceipt, null);
            receipt?.TrySetResult(true);
            return;
        }

        switch (packet.Control)
        {
            case 1:
                (ReadOnlyMemory<byte> unitData, IMemoryOwner<byte>? unitOwner) = RentAndCopy(packet.Data.Span);
                this.RaiseReceived(unitData, unitOwner);
                break;
            case 2:
                this.CompleteInboundMessage(packet.Data.Memory, cancelled: false);
                break;
            case 3:
                this.CompleteInboundMessage(ReadOnlyMemory<byte>.Empty, cancelled: true);
                break;
            default:
                int priority = (int)(packet.Control - 4);
                if (!this.inboundBuffers.TryGetValue(priority, out List<ReadOnlyMemory<byte>>? buffer))
                {
                    buffer = [];
                    this.inboundBuffers[priority] = buffer;
                }

                buffer.Add(packet.Data.Memory);
                this.hasInProgressInboundMessage = this.inboundBuffers.Count > 0;
                break;
        }

        await this.frameStream.Write(new Packet { Control = 0, Data = ByteString.Empty }, this.lifetimeCts.Token).ConfigureAwait(false);
    }

    private void CompleteInboundMessage(ReadOnlyMemory<byte> finalChunk, bool cancelled)
    {
        int? highestPriority = null;
        foreach (int priority in this.inboundBuffers.Keys)
        {
            if (highestPriority is null || priority > highestPriority)
            {
                highestPriority = priority;
            }
        }

        if (highestPriority is null)
        {
            throw new InvalidOperationException($"Received a {(cancelled ? "cancellation" : "completion")} packet with no pending message on any priority channel.");
        }

        List<ReadOnlyMemory<byte>> buffer = this.inboundBuffers[highestPriority.Value];
        this.inboundBuffers.Remove(highestPriority.Value);
        this.hasInProgressInboundMessage = this.inboundBuffers.Count > 0;

        if (cancelled)
        {
            return;
        }

        if (finalChunk.Length > 0)
        {
            buffer.Add(finalChunk);
        }

        int totalLength = buffer.Sum(chunk => chunk.Length);
        IMemoryOwner<byte>? owner = totalLength > 0 ? MemoryPool<byte>.Shared.Rent(totalLength) : null;
        Memory<byte> message = owner is null ? Memory<byte>.Empty : owner.Memory[..totalLength];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> chunk in buffer)
        {
            chunk.Span.CopyTo(message.Span[offset..]);
            offset += chunk.Length;
        }

        this.RaiseReceived(message, owner);
    }

    /// <summary>
    /// Rents pooled memory sized to <paramref name="source"/> and copies it in, so the caller can
    /// hand ownership of the copy off to <see cref="OftReceivedEventArgs"/> without holding onto (or
    /// needing to keep alive) whatever <paramref name="source"/> was a view over.
    /// </summary>
    private static (ReadOnlyMemory<byte> Data, IMemoryOwner<byte>? Owner) RentAndCopy(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
        {
            return (ReadOnlyMemory<byte>.Empty, null);
        }

        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(owner.Memory.Span);
        return (owner.Memory[..source.Length], owner);
    }

    private void RaiseReceived(ReadOnlyMemory<byte> data, IMemoryOwner<byte>? owner) =>
        this.receivedEvent.Raise(new OftReceivedEventArgs { Data = data, Owner = owner });

    /// <summary>
    /// Fires on every <see cref="OftConnectionOptions.PollInterval"/> tick (see Docs/OFT.md §10):
    /// sends a best-effort <c>Poll</c> packet, then closes the connection if nothing at all has
    /// been received from the peer within <see cref="OftConnectionOptions.PollTimeout"/>. Rekeying
    /// (see Docs/OFT.md §8) is a TLS-layer operation, invisible to and never coordinated with this
    /// packet-level write — unlike the old session-replacing rekey design, there's no window during
    /// which writing a packet here could corrupt anything, so this never needs to wait for
    /// <see cref="writePermit"/>.
    /// </summary>
    private async Task OnPollTimerTick()
    {
        try
        {
            // An all-default Packet (every field at its zero value) serializes to zero bytes under
            // proto3's default-value-omission rule — exactly the zero-length frame ReadPacketOrPoll
            // treats as a Poll. No dedicated control value needed.
            await this.frameStream.Write(new Packet(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a single failed poll write isn't itself fatal — the watchdog check below
            // is what detects a genuinely dead connection, and the next tick tries again.
        }

        long elapsedTicks = NowTicks() - Interlocked.Read(ref this.lastInboundActivityTicks);
        if (elapsedTicks > this.options.PollTimeout.Ticks)
        {
            await this.Close(new TimeoutException($"No poll or message was received from the peer within {this.options.PollTimeout}.")).ConfigureAwait(false);
        }
    }

    private async Task Close(Exception? exception)
    {
        lock (this.closeLock)
        {
            if (this.closed)
            {
                return;
            }

            this.closed = true;
        }

        await this.lifetimeCts.CancelAsync().ConfigureAwait(false);
        this.rekeyTimer?.Dispose();
        this.pollTimer?.Dispose();

        lock (this.outboundLock)
        {
            foreach (Queue<PendingOutboundMessage> queue in this.outboundQueues.Values)
            {
                while (queue.Count > 0)
                {
                    PendingOutboundMessage message = queue.Dequeue();
                    message.Owner?.Dispose();
                    message.CompletionSource.TrySetException(exception ?? new ObjectDisposedException(nameof(OftConnection)));
                }
            }
        }

        try
        {
            this.plaintextStream.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        try
        {
            this.networkStream.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        try
        {
            this.tcpClient.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        this.disconnectedEvent.Raise(new OftDisconnectedEventArgs { Exception = exception });

        // Nobody will ever subscribe to a closed connection's events after this point, so any raise
        // still buffered for lack of a subscriber (most relevantly a Received carrying pooled
        // memory) would otherwise be held onto forever instead of being released.
        this.receivedEvent.DisposeBuffered();
        this.disconnectedEvent.DisposeBuffered();
    }

    private void UpdateLastSentAt() => Interlocked.Exchange(ref this.lastSentAtTicks, NowTicks());

    private void UpdateLastReceivedAt() => Interlocked.Exchange(ref this.lastReceivedAtTicks, NowTicks());

    private static long NowTicks() => DateTimeOffset.UtcNow.UtcTicks;

    private sealed class PendingOutboundMessage
    {
        public required ReadOnlyMemory<byte> Data { get; init; }

        /// <summary>
        /// Owns <see cref="Data"/>'s backing memory when the caller transferred ownership via
        /// <see cref="Send(IMemoryOwner{byte}, int, CancellationToken)"/>, or
        /// <see langword="null"/> when <see cref="Data"/> is caller-owned (and was therefore copied
        /// into each packet's payload rather than referenced directly). Disposed exactly once, at
        /// whichever of send completion, cancellation, or connection close happens first.
        /// </summary>
        public IMemoryOwner<byte>? Owner { get; init; }

        public required int Priority { get; init; }

        public required TaskCompletionSource CompletionSource { get; init; }

        public required CancellationToken CancellationToken { get; init; }

        public int BytesSent { get; set; }

        public bool Started { get; set; }

        public bool CancelRequested { get; set; }
    }
}
