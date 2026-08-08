namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftPeerTests
{
    private static async Task<(TrackedListener Listener, TaskCompletionSource<IOftConnection> ConnectionSource)> StartRemoteServer()
    {
        OftConnectionOptions serverOptions = new()
        {
            Info = "remote",
            Certificate = TestCertificate.Create(),
        };

        TrackedListener listener = await TrackedListener.Start(new IPEndPoint(IPAddress.Loopback, 0), serverOptions);
        TaskCompletionSource<IOftConnection> connectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnConnectedExtra = connection => connectionSource.TrySetResult(connection);
        return (listener, connectionSource);
    }

    private static IOftPeer CreatePeer(
        TimeSpan? idleTimeout = null,
        TimeSpan? maxConnectionLifetime = null,
        int? maxConnectionCount = null)
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        OftPeerOptions options = new()
        {
            Info = "peer",
            CertificateValidation = (_, _, _, _) => true,
            IdleTimeout = idleTimeout ?? TimeSpan.FromHours(2),
            MaxConnectionLifetime = maxConnectionLifetime ?? TimeSpan.FromDays(1),
            MaxConnectionCount = maxConnectionCount ?? 16,
        };

        return factory.Create(options);
    }

    private static IOftPeer CreateListeningPeer(TimeSpan? idleTimeout = null)
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        return factory.Create(new OftPeerOptions
        {
            Info = "listener",
            Certificate = TestCertificate.Create(),
            CertificateValidation = (_, _, _, _) => true,
            IdleTimeout = idleTimeout ?? TimeSpan.FromHours(2),
        });
    }

    [Fact]
    public async Task Send_ReusesConnectionAcrossCalls()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        using TrackedListener listener = remoteListener;
        using IOftPeer peer = CreatePeer();
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
        using TrackedListener listener = remoteListener;
        using IOftPeer peer = CreatePeer(idleTimeout: TimeSpan.FromMilliseconds(100));
        int port = remoteListener.LocalEndPoint.Port;

        await peer.Send("127.0.0.1", port, "hi"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(remoteListener.Connections);

        await OftTestHarness.WaitUntil(() => remoteListener.Connections.Count == 0, OftTestHarness.EvictionTimeout);
    }

    [Fact]
    public async Task IdleConnections_InboundAreAutomaticallyDisconnected()
    {
        using IOftPeer listeningPeer = CreateListeningPeer(idleTimeout: TimeSpan.FromMilliseconds(100));
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));

        IOftConnector connector = new OftConnector();
        OftConnectionOptions clientOptions = new()
        {
            Info = "client",
            CertificateValidation = (_, _, _, _) => true,
        };

        using IOftConnection connection = await connector.Connect("127.0.0.1", listeningPeer.LocalEndPoint!.Port, clientOptions).WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<bool> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DisconnectedHandler = _ => closedSource.TrySetResult(true);

        await closedSource.Task.WaitAsync(OftTestHarness.EvictionTimeout);
    }

    [Fact]
    public async Task MaxConnectionCount_EvictsOldestConnectionFirst()
    {
        (TrackedListener listener1, TaskCompletionSource<IOftConnection> connection1Source) = await StartRemoteServer();
        (TrackedListener listener2, TaskCompletionSource<IOftConnection> connection2Source) = await StartRemoteServer();
        using TrackedListener l1 = listener1;
        using TrackedListener l2 = listener2;
        using IOftPeer peer = CreatePeer(maxConnectionCount: 1);

        await peer.Send("127.0.0.1", listener1.LocalEndPoint.Port, "a"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connection1Source.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(listener1.Connections);

        await Task.Delay(50);

        await peer.Send("127.0.0.1", listener2.LocalEndPoint.Port, "b"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connection2Source.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        // EvictionTimeout rather than DefaultTimeout: connection1 only becomes an eviction candidate
        // once IOftPeer's fixed, non-configurable 30-second grace period has elapsed since it
        // finished sending (see IOftPeer's own doc comment).
        await OftTestHarness.WaitUntil(() => listener1.Connections.Count == 0, OftTestHarness.EvictionTimeout);
        Assert.Single(listener2.Connections);
    }

    [Fact]
    public async Task Received_RaisedForInboundConnections()
    {
        using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));

        TaskCompletionSource<(OftIdentity Identity, IMemoryOwner<byte> Data)> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listeningPeer.ReceivedHandler = (identity, data) => received.TrySetResult((identity, data));

        using IOftPeer caller = CreatePeer();
        byte[] payload = "hello listener"u8.ToArray();
        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, payload).WaitAsync(OftTestHarness.DefaultTimeout);

        (OftIdentity identity, IMemoryOwner<byte> data) = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        using (data)
        {
            Assert.Equal(payload, data.Memory.ToArray());
            Assert.Equal("peer", identity.Info);
        }
    }

    [Fact]
    public async Task Send_WithTag_RaisesAcknowledgedHandlerWithIdentityAndTag()
    {
        using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));

        object tag = new();
        TaskCompletionSource<(OftIdentity Identity, object Tag)> acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IOftPeer caller = CreatePeer();
        caller.AcknowledgedHandler = (identity, acknowledgedTag) => acknowledged.TrySetResult((identity, acknowledgedTag));

        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, "hello listener"u8.ToArray(), tag: tag).WaitAsync(OftTestHarness.DefaultTimeout);

        (OftIdentity identity, object acknowledgedTag) = await acknowledged.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Same(tag, acknowledgedTag);
        Assert.Equal("listener", identity.Info);
    }

    [Fact]
    public async Task Send_WithoutTag_NeverRaisesAcknowledgedHandler()
    {
        using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));

        bool acknowledgedHandlerRaised = false;
        using IOftPeer caller = CreatePeer();
        caller.AcknowledgedHandler = (_, _) => acknowledgedHandlerRaised = true;

        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, "hello listener"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);

        await Task.Delay(200);
        Assert.False(acknowledgedHandlerRaised);
    }

    [Fact]
    public async Task StopListening_OutboundOnlyPeer_IsNoOp()
    {
        using IOftPeer peer = CreatePeer();
        await peer.StopListening();
    }

    [Fact]
    public async Task StopListening_ListeningPeer_StopsAcceptingNewConnections()
    {
        using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));
        int port = listeningPeer.LocalEndPoint!.Port;

        await listeningPeer.StopListening();

        using IOftPeer caller = CreatePeer();
        await Assert.ThrowsAnyAsync<Exception>(() => caller.Send("127.0.0.1", port, "hi"u8.ToArray()));
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        IOftPeer peer = CreatePeer();
        peer.Dispose();
        peer.Dispose();
    }

    [Fact]
    public async Task Send_ConnectFailure_ThrowsAndCanBeRetried()
    {
        using IOftPeer peer = CreatePeer();
        int freePort = OftTestHarness.ReserveFreePort();

        await Assert.ThrowsAnyAsync<Exception>(() => peer.Send("127.0.0.1", freePort, "hi"u8.ToArray()));

        // A failed attempt must not have cached a permanently faulted connection: retrying is a
        // fresh attempt, not an instant replay of the same failure.
        await Assert.ThrowsAnyAsync<Exception>(() => peer.Send("127.0.0.1", freePort, "hi"u8.ToArray()));
    }

    [Fact]
    public async Task PendingData_PreventsAutomaticDisconnectionUntilAcknowledged()
    {
        OftConnectionOptions receiverOptions = new()
        {
            Info = "receiver",
            Certificate = TestCertificate.Create(),
        };

        using TrackedListener receiverListener = await TrackedListener.Start(new IPEndPoint(IPAddress.Loopback, 0), receiverOptions);

        OftPeerFactory factory = new(new OftConnector(), new OftHoster());
        using IOftPeer sender = factory.Create(new OftPeerOptions
        {
            Info = "sender",
            CertificateValidation = (_, _, _, _) => true,
            MaxPacketDataSize = 8,
            IdleTimeout = TimeSpan.FromMilliseconds(50),
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

        await OftTestHarness.WaitUntil(() => receiverListener.Connections.Count == 0, OftTestHarness.EvictionTimeout);
    }

    [Fact]
    public async Task Rekey_RekeysOutboundAndInboundConnections()
    {
        using IOftPeer listeningPeer = CreateListeningPeer();
        await listeningPeer.Listen(new IPEndPoint(IPAddress.Loopback, 0));

        // Assigned before any message is ever sent: ReceivedHandler buffers every raise until the
        // first non-null assignment (see OftBufferedHandlerSlot), so assigning here rather than
        // after the "hello" send below avoids seeing that earlier, unrelated message instead of the
        // post-rekey one this test actually cares about.
        byte[] payload = "post-rekey"u8.ToArray();
        TaskCompletionSource<IMemoryOwner<byte>> receivedPostRekey = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listeningPeer.ReceivedHandler = (_, data) =>
        {
            if (data.Memory.Span.SequenceEqual(payload))
            {
                receivedPostRekey.TrySetResult(data);
            }
            else
            {
                data.Dispose();
            }
        };

        using IOftPeer caller = CreatePeer();
        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, "hello"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);

        await caller.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);
        await listeningPeer.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);

        await caller.Send("127.0.0.1", listeningPeer.LocalEndPoint!.Port, payload).WaitAsync(OftTestHarness.DefaultTimeout);

        using IMemoryOwner<byte> data = await receivedPostRekey.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Rekey_NoConnections_CompletesImmediately()
    {
        using IOftPeer peer = CreatePeer();
        await peer.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Drop_DisconnectsOutboundAndInboundConnections()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        using TrackedListener listener = remoteListener;
        using IOftPeer peer = CreatePeer();

        await peer.Send("127.0.0.1", remoteListener.LocalEndPoint.Port, "hi"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        IOftConnection inboundOnServer = await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(remoteListener.Connections);

        TaskCompletionSource<bool> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnConnectionDisconnectedExtra = (connection, _) =>
        {
            if (connection == inboundOnServer)
            {
                closedSource.TrySetResult(true);
            }
        };

        await peer.Drop().WaitAsync(OftTestHarness.DefaultTimeout);

        await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Empty(remoteListener.Connections);
    }

    [Fact]
    public async Task Drop_PeerRemainsUsableAfterward()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        using TrackedListener listener = remoteListener;
        using IOftPeer peer = CreatePeer();
        int port = remoteListener.LocalEndPoint.Port;

        await peer.Send("127.0.0.1", port, "first"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        await peer.Drop().WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.True(peer.IsConnected);
        await peer.Send("127.0.0.1", port, "second"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Drop_NoConnections_CompletesImmediately()
    {
        using IOftPeer peer = CreatePeer();
        await peer.Drop().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Drop_AfterDisconnect_Throws()
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => peer.Drop());
    }

    [Fact]
    public void IsConnected_TrueUntilDisconnected()
    {
        using IOftPeer peer = CreatePeer();
        Assert.True(peer.IsConnected);
    }

    [Fact]
    public async Task Disconnect_PutsIntoDisconnectedState()
    {
        IOftPeer peer = CreatePeer();

        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.False(peer.IsConnected);
    }

    [Fact]
    public async Task Disconnect_DisconnectsOutboundAndInboundConnections()
    {
        (TrackedListener remoteListener, TaskCompletionSource<IOftConnection> connectionSource) = await StartRemoteServer();
        using TrackedListener listener = remoteListener;
        IOftPeer peer = CreatePeer();

        await peer.Send("127.0.0.1", remoteListener.LocalEndPoint.Port, "hi"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        IOftConnection inboundOnServer = await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Single(remoteListener.Connections);

        TaskCompletionSource<bool> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnConnectionDisconnectedExtra = (connection, _) =>
        {
            if (connection == inboundOnServer)
            {
                closedSource.TrySetResult(true);
            }
        };

        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Empty(remoteListener.Connections);
    }

    [Fact]
    public async Task Disconnect_NoConnections_CompletesImmediately()
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Disconnect_CalledTwice_IsIdempotent()
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Disconnect_ThenDispose_IsIdempotent()
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);
        peer.Dispose();
    }

    [Theory]
    [InlineData(nameof(IOftPeer.StopListening))]
    [InlineData(nameof(IOftPeer.Listen))]
    [InlineData(nameof(IOftPeer.Drop))]
    public async Task MemberCalledAfterDisconnect_ThrowsObjectDisposedException(string memberName)
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        Func<Task> call = memberName switch
        {
            nameof(IOftPeer.StopListening) => () => peer.StopListening(),
            nameof(IOftPeer.Listen) => () => peer.Listen(new IPEndPoint(IPAddress.Loopback, 0)),
            nameof(IOftPeer.Drop) => () => peer.Drop(),
            _ => throw new InvalidOperationException(),
        };

        await Assert.ThrowsAsync<ObjectDisposedException>(call);
    }

    [Fact]
    public async Task RekeyCalledAfterDisconnect_ThrowsOftDisconnectedException()
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        await Assert.ThrowsAsync<OftDisconnectedException>(() => peer.Rekey());
    }

    [Fact]
    public async Task SendCalledAfterDisconnect_ThrowsOftDisconnectedException()
    {
        IOftPeer peer = CreatePeer();
        await peer.Disconnect().WaitAsync(OftTestHarness.DefaultTimeout);

        await Assert.ThrowsAsync<OftDisconnectedException>(() => peer.Send("127.0.0.1", 12345, "hi"u8.ToArray()));
    }

    [Fact]
    public void IsConnected_FalseAfterDispose()
    {
        IOftPeer peer = CreatePeer();
        peer.Dispose();

        Assert.False(peer.IsConnected);
    }

    [Fact]
    public async Task SendCalledAfterDispose_ThrowsOftDisconnectedException()
    {
        IOftPeer peer = CreatePeer();
        peer.Dispose();

        await Assert.ThrowsAsync<OftDisconnectedException>(() => peer.Send("127.0.0.1", 12345, "hi"u8.ToArray()));
    }
}
