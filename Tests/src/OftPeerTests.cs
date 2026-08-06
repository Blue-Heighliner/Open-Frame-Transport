namespace OpenFrameTransport.Tests;

public sealed class OftPeerTests
{
    private static async Task<(TrackedListener Listener, TaskCompletionSource<IOftConnection> ConnectionSource)> StartRemoteServer()
    {
        OftHostOptions serverOptions = new()
        {
            Info = "remote",
            ServerCertificate = TestCertificate.Create(),
        };

        TrackedListener listener = await TrackedListener.Start(new IPEndPoint(IPAddress.Loopback, 0), serverOptions);
        TaskCompletionSource<IOftConnection> connectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Connected += (_, args) => connectionSource.TrySetResult(args.Connection);
        return (listener, connectionSource);
    }

    private static IOftPeer CreatePeer(
        TimeSpan? idleTimeout = null,
        TimeSpan? maxConnectionLifetime = null,
        int? maxConnectionCount = null,
        TimeSpan? evictionCheckInterval = null)
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        OftPeerOptions options = new()
        {
            Info = "peer",
            CertificateValidation = (_, _, _, _) => true,
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5),
            MaxConnectionLifetime = maxConnectionLifetime ?? TimeSpan.FromHours(1),
            MaxConnectionCount = maxConnectionCount ?? 128,
            EvictionCheckInterval = evictionCheckInterval ?? TimeSpan.FromSeconds(30),
        };

        return factory.Create(options);
    }

    private static IOftPeer CreateListeningPeer(
        TimeSpan? idleTimeout = null,
        TimeSpan? evictionCheckInterval = null)
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        return factory.Create(new OftPeerOptions
        {
            Info = "listener",
            ServerCertificate = TestCertificate.Create(),
            CertificateValidation = (_, _, _, _) => true,
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5),
            EvictionCheckInterval = evictionCheckInterval ?? TimeSpan.FromSeconds(30),
        });
    }

    [Fact]
    public async Task Send_ReusesConnectionAcrossCalls()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        await using TrackedListener listener = remoteListener;
        await using IOftPeer peer = CreatePeer();
        int port = remoteListener.LocalEndPoint.Port;

        await peer.Send("127.0.0.1", port, "first"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(remoteListener.Connections);

        await peer.Send("127.0.0.1", port, "second"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);

        // Still exactly one inbound connection: the second send reused the cached outbound
        // connection rather than dialing a new one.
        Assert.Single(remoteListener.Connections);
    }

    [Fact]
    public async Task IdleConnections_AreAutomaticallyDisconnected()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        await using TrackedListener listener = remoteListener;
        await using IOftPeer peer = CreatePeer(idleTimeout: TimeSpan.FromMilliseconds(100), evictionCheckInterval: TimeSpan.FromMilliseconds(50));
        int port = remoteListener.LocalEndPoint.Port;

        await peer.Send("127.0.0.1", port, "hi"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(remoteListener.Connections);

        await OftTestHarness.WaitUntil(() => remoteListener.Connections.Count == 0, OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task IdleConnections_InboundAreAutomaticallyDisconnected()
    {
        await using IOftPeer listeningPeer = CreateListeningPeer(idleTimeout: TimeSpan.FromMilliseconds(100), evictionCheckInterval: TimeSpan.FromMilliseconds(50));
        await listeningPeer.Open(new IPEndPoint(IPAddress.Loopback, 0));

        IOftConnector connector = new OftConnector();
        OftConnectOptions clientOptions = new()
        {
            Info = "client",
            ServerCertificateValidation = (_, _, _, _) => true,
        };

        await using IOftConnection connection = await connector.Connect("127.0.0.1", listeningPeer.LocalEndPoint!.Port, clientOptions).WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<bool> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += (_, _) => closedSource.TrySetResult(true);

        await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task MaxConnectionCount_EvictsOldestConnectionFirst()
    {
        (TrackedListener listener1, TaskCompletionSource<IOftConnection> connection1Source) = await StartRemoteServer();
        (TrackedListener listener2, TaskCompletionSource<IOftConnection> connection2Source) = await StartRemoteServer();
        await using TrackedListener l1 = listener1;
        await using TrackedListener l2 = listener2;
        await using IOftPeer peer = CreatePeer(maxConnectionCount: 1, evictionCheckInterval: TimeSpan.FromMilliseconds(50));

        await peer.Send("127.0.0.1", listener1.LocalEndPoint.Port, "a"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connection1Source.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(listener1.Connections);

        await Task.Delay(50);

        await peer.Send("127.0.0.1", listener2.LocalEndPoint.Port, "b"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connection2Source.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        await OftTestHarness.WaitUntil(() => listener1.Connections.Count == 0, OftTestHarness.DefaultTimeout);
        Assert.Single(listener2.Connections);
    }

    [Fact]
    public async Task Received_RaisedForInboundConnections()
    {
        await using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Open(new IPEndPoint(IPAddress.Loopback, 0));

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listeningPeer.Received += (_, args) => received.TrySetResult(args);

        await using IOftPeer caller = CreatePeer();
        byte[] payload = "hello listener"u8.ToArray();
        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, payload).WaitAsync(OftTestHarness.DefaultTimeout);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Close_OutboundOnlyPeer_IsNoOp()
    {
        await using IOftPeer peer = CreatePeer();
        await peer.Close();
    }

    [Fact]
    public async Task Close_ListeningPeer_StopsAcceptingNewConnections()
    {
        await using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Open(new IPEndPoint(IPAddress.Loopback, 0));
        int port = listeningPeer.LocalEndPoint!.Port;

        await listeningPeer.Close();

        await using IOftPeer caller = CreatePeer();
        await Assert.ThrowsAnyAsync<Exception>(() => caller.Send("127.0.0.1", port, "hi"u8.ToArray()));
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        IOftPeer peer = CreatePeer();
        await peer.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task Send_ConnectFailure_ThrowsAndCanBeRetried()
    {
        await using IOftPeer peer = CreatePeer();
        int freePort = OftTestHarness.ReserveFreePort();

        await Assert.ThrowsAnyAsync<Exception>(() => peer.Send("127.0.0.1", freePort, "hi"u8.ToArray()));

        // A failed attempt must not have cached a permanently faulted connection: retrying is a
        // fresh attempt, not an instant replay of the same failure.
        await Assert.ThrowsAnyAsync<Exception>(() => peer.Send("127.0.0.1", freePort, "hi"u8.ToArray()));
    }

    [Fact]
    public async Task PendingData_PreventsAutomaticDisconnectionUntilAcknowledged()
    {
        OftHostOptions receiverOptions = new()
        {
            Info = "receiver",
            ServerCertificate = TestCertificate.Create(),
        };

        await using TrackedListener receiverListener = await TrackedListener.Start(new IPEndPoint(IPAddress.Loopback, 0), receiverOptions);

        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        await using IOftPeer sender = factory.Create(new OftPeerOptions
        {
            Info = "sender",
            CertificateValidation = (_, _, _, _) => true,
            MaxPacketDataSize = 8,
            IdleTimeout = TimeSpan.FromMilliseconds(50),
            EvictionCheckInterval = TimeSpan.FromMilliseconds(20),
        });

        // ~50 acknowledged round trips (one packet in flight at a time), which comfortably outlasts
        // the 50ms idle timeout above: if eviction ignored pending data, the connection would
        // already be gone well before this send finishes.
        byte[] payload = [.. Enumerable.Repeat((byte)7, 400)];
        Task sendTask = sender.Send("127.0.0.1", receiverListener.LocalEndPoint.Port, payload).WaitAsync(OftTestHarness.DefaultTimeout);

        await Task.Delay(150);
        Assert.False(sendTask.IsCompleted);
        Assert.Single(receiverListener.Connections);

        await sendTask;

        await OftTestHarness.WaitUntil(() => receiverListener.Connections.Count == 0, OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Rekey_RekeysOutboundAndInboundConnections()
    {
        await using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Open(new IPEndPoint(IPAddress.Loopback, 0));

        // Subscribed before any message is ever sent: Received buffers every raise until the first
        // subscriber attaches (see OftBufferedEvent), so subscribing here rather than after the
        // "hello" send below avoids seeing that earlier, unrelated message instead of the
        // post-rekey one this test actually cares about.
        byte[] payload = "post-rekey"u8.ToArray();
        TaskCompletionSource<OftReceivedEventArgs> receivedPostRekey = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listeningPeer.Received += (_, args) =>
        {
            if (args.Data.Span.SequenceEqual(payload))
            {
                receivedPostRekey.TrySetResult(args);
            }
        };

        await using IOftPeer caller = CreatePeer();
        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, "hello"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);

        await caller.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);
        await listeningPeer.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);

        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, payload).WaitAsync(OftTestHarness.DefaultTimeout);

        OftReceivedEventArgs args = await receivedPostRekey.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Rekey_NoConnections_CompletesImmediately()
    {
        await using IOftPeer peer = CreatePeer();
        await peer.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Disconnect_DisconnectsOutboundAndInboundConnections()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        await using TrackedListener listener = remoteListener;
        await using IOftPeer peer = CreatePeer();

        await peer.Send("127.0.0.1", remoteListener.LocalEndPoint.Port, "hi"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        IOftConnection inboundOnServer = await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(remoteListener.Connections);

        TaskCompletionSource<bool> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        inboundOnServer.Disconnected += (_, _) => closedSource.TrySetResult(true);

        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Empty(remoteListener.Connections);
    }

    [Fact]
    public async Task Disconnect_PeerRemainsUsableAfterward()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        await using TrackedListener listener = remoteListener;
        await using IOftPeer peer = CreatePeer();
        int port = remoteListener.LocalEndPoint.Port;

        await peer.Send("127.0.0.1", port, "first"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        await peer.Send("127.0.0.1", port, "second"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Disconnect_NoConnections_CompletesImmediately()
    {
        await using IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);
    }
}
