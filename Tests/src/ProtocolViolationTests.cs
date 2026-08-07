namespace BlueHeighliner.OpenFrameTransport.Tests;

/// <summary>
/// Drives the OFT wire protocol directly (bypassing <see cref="IOftConnector"/>) to exercise how a
/// connection reacts to a misbehaving peer.
/// </summary>
public sealed class ProtocolViolationTests
{
    private static OftHostOptions CreateServerOptions() => new()
    {
        Info = "server",
        ServerCertificate = TestCertificate.Create(),
    };

    [Fact]
    public async Task IncompatibleHailVersion_ClosesConnectionWithoutEstablishing()
    {
        await using TrackedListener listener = await TrackedListener.Start(new IPEndPoint(IPAddress.Loopback, 0), CreateServerOptions());
        bool established = false;
        listener.OnConnectedExtra = _ => established = true;

        (TcpClient tcpClient, SslStream sslStream, OftFrameStream frameStream) = await OftTestHarness.RawConnect(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        using (tcpClient)
        await using (sslStream)
        {
            await frameStream.Write(new Hail { Version = "oft/999", Info = "rogue" }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
        }

        await OftTestHarness.WaitUntil(() => listener.Connections.Count == 0, OftTestHarness.DefaultTimeout);
        Assert.False(established);
    }

    [Fact]
    public async Task PeerClosesBeforeSendingHail_ServerDoesNotEstablish()
    {
        await using TrackedListener listener = await TrackedListener.Start(new IPEndPoint(IPAddress.Loopback, 0), CreateServerOptions());
        bool established = false;
        listener.OnConnectedExtra = _ => established = true;

        (TcpClient tcpClient, SslStream sslStream, _) = await OftTestHarness.RawConnect(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        sslStream.Dispose();
        tcpClient.Dispose();

        await Task.Delay(300);
        Assert.False(established);
        Assert.Empty(listener.Connections);
    }

    [Fact]
    public async Task OrphanCompletionPacket_ClosesConnectionWithException()
    {
        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), CreateServerOptions());
        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectedHandler = connection => serverConnectionSource.TrySetResult(connection);

        (TcpClient tcpClient, SslStream sslStream, OftFrameStream frameStream) = await OftTestHarness.RawConnect(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        using (tcpClient)
        await using (sslStream)
        {
            await frameStream.Write(new Hail { Version = "oft/1", Info = "rogue" }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            Hail? serverHail = await frameStream.ReadHail(CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            Assert.NotNull(serverHail);

            IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
            TaskCompletionSource<Exception?> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            serverConnection.DisconnectedHandler = exception => closedSource.TrySetResult(exception);

            // Control 2 (Completion) with nothing in flight on any priority channel: a protocol
            // violation the receiver must detect (see Docs/OFT.md §4.4).
            await frameStream.Write(new Packet { Control = 2, Data = ByteString.Empty }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);

            Exception? closedException = await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
            Assert.IsType<InvalidOperationException>(closedException);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenDisconnectedHandlerThrows_DoesNotPropagate()
    {
        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), CreateServerOptions());
        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectedHandler = connection => serverConnectionSource.TrySetResult(connection);

        (TcpClient tcpClient, SslStream sslStream, OftFrameStream frameStream) = await OftTestHarness.RawConnect(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        using (tcpClient)
        await using (sslStream)
        {
            await frameStream.Write(new Hail { Version = "oft/1", Info = "rogue" }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            await frameStream.ReadHail(CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);

            IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

            TaskCompletionSource<bool> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            serverConnection.DisconnectedHandler = _ =>
            {
                closedSource.TrySetResult(true);
                throw new InvalidOperationException("Disconnected handler misbehaving on purpose.");
            };

            // The protocol violation below makes the receive loop call Close(exception) itself; the
            // misbehaving handler registered above then makes that specific call - and therefore the
            // receive loop's own task - fault. Disposing the connection afterward must not let that
            // fault escape.
            await frameStream.Write(new Packet { Control = 2, Data = ByteString.Empty }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);

            await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

            await serverConnection.DisposeAsync().AsTask().WaitAsync(OftTestHarness.DefaultTimeout);
        }
    }

    [Fact]
    public async Task OrphanCancellationPacket_ClosesConnectionWithException()
    {
        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), CreateServerOptions());
        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectedHandler = connection => serverConnectionSource.TrySetResult(connection);

        (TcpClient tcpClient, SslStream sslStream, OftFrameStream frameStream) = await OftTestHarness.RawConnect(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        using (tcpClient)
        await using (sslStream)
        {
            await frameStream.Write(new Hail { Version = "oft/1", Info = "rogue" }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            await frameStream.ReadHail(CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);

            IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
            TaskCompletionSource<Exception?> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            serverConnection.DisconnectedHandler = exception => closedSource.TrySetResult(exception);

            // Control 3 (Cancellation) with nothing in flight: also a protocol violation.
            await frameStream.Write(new Packet { Control = 3, Data = ByteString.Empty }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);

            Exception? closedException = await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
            Assert.IsType<InvalidOperationException>(closedException);
        }
    }
}
