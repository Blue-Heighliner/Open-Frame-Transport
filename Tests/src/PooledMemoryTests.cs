namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class PooledMemoryTests
{
    /// <summary>
    /// Tracks whether it was disposed, so tests can assert the connection actually returns
    /// caller-owned memory once it's done with it, without depending on <see cref="MemoryPool{T}"/>
    /// internals.
    /// </summary>
    private sealed class TrackedMemoryOwner : IMemoryOwner<byte>
    {
        public TrackedMemoryOwner(byte[] data)
        {
            this.Memory = data;
        }

        public Memory<byte> Memory { get; }

        public int DisposeCount { get; private set; }

        public void Dispose() => this.DisposeCount++;
    }

    [Fact]
    public async Task Send_WithMemoryOwner_Small_DeliveredAndOwnerDisposed()
    {
        using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "hello"u8.ToArray();
        TrackedMemoryOwner owner = new(payload);

        await pair.ClientConnection.Send(owner, priority: 1);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Send_WithMemoryOwner_LargerThanPacketSize_DeliveredAndOwnerDisposed()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];
        TrackedMemoryOwner owner = new(payload);

        await pair.ClientConnection.Send(owner, priority: 4);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Send_WithMemoryOwner_CancelledBeforeStart_OwnerStillDisposed()
    {
        using OftPair pair = await OftTestHarness.Establish();

        using CancellationTokenSource cts = new();
        byte[] payload = "should not arrive"u8.ToArray();
        TrackedMemoryOwner owner = new(payload);

        cts.Cancel();
        Task sendTask = pair.ClientConnection.Send(owner, cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Send_WithMemoryOwner_CancelledAfterStart_OwnerStillDisposed()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        using CancellationTokenSource cts = new();
        byte[] payload = [.. Enumerable.Repeat((byte)9, 400)];
        TrackedMemoryOwner owner = new(payload);

        Task sendTask = pair.ClientConnection.Send(owner, cancellationToken: cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Send_WithMemoryOwner_QueuedThenConnectionCloses_OwnerStillDisposed()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        byte[] first = [.. Enumerable.Repeat((byte)1, 400)];
        TrackedMemoryOwner firstOwner = new(first);
        byte[] second = "queued"u8.ToArray();
        TrackedMemoryOwner secondOwner = new(second);

        // Whichever of these is still mid-send or still queued when the connection closes below
        // completes exceptionally; either way completing at all (successfully, cancelled, or
        // faulted) must dispose its owner exactly once, so the outcome itself isn't asserted here.
        Task firstSend = pair.ClientConnection.Send(firstOwner);
        Task secondSend = pair.ClientConnection.Send(secondOwner);

        pair.ClientConnection.Dispose();
        pair.ServerConnection.Dispose();

        try
        {
            await Task.WhenAll(firstSend, secondSend);
        }
        catch
        {
            // Expected: at least one of these was interrupted by the connection closing.
        }

        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(1, secondOwner.DisposeCount);
    }

    [Fact]
    public async Task ReceivedData_Dispose_IsSafeAndIdempotent()
    {
        using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "hello"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        byte[] copy = data.Memory.ToArray();

        data.Dispose();
        data.Dispose();

        Assert.Equal(payload, copy);
    }

    [Fact]
    public async Task Peer_Send_WithMemoryOwner_DeliveredAndOwnerDisposed()
    {
        // Subscribed at the peer level so it covers every connection the peer ever holds, rather
        // than a specific IOftConnection obtained via ConnectedHandler after the fact.
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        using IOftPeer listeningPeer = factory.Create(new OftPeerOptions
        {
            Info = "listener",
            Certificate = TestCertificate.Create(),
            CertificateValidation = (_, _, _, _) => true,
        });
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));

        TaskCompletionSource<IOftPeerReception> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listeningPeer.ReceivedHandler = reception => received.TrySetResult(reception);

        using IOftPeer caller = factory.Create(new OftPeerOptions
        {
            Info = "caller",
            CertificateValidation = (_, _, _, _) => true,
        });

        byte[] payload = "hello peer"u8.ToArray();
        TrackedMemoryOwner owner = new(payload);

        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, owner).WaitAsync(OftTestHarness.DefaultTimeout);

        using IOftPeerReception reception = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, reception.Data.ToArray());
        Assert.Equal(1, owner.DisposeCount);
    }
}
