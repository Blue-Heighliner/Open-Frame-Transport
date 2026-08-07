namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftListenerTests
{
    private static OftHostOptions CreateOptions() => new()
    {
        Info = "server",
        ServerCertificate = TestCertificate.Create(),
        SecurityMode = OftSecurityMode.ServerAuthentication,
    };

    private static IPEndPoint LoopbackEndPoint() => new(IPAddress.Loopback, 0);

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        IOftListener listener = await new OftHoster().Host(LoopbackEndPoint(), CreateOptions());
        await listener.DisposeAsync();
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotAffectAlreadyAcceptedConnections()
    {
        IOftListener listener = await new OftHoster().Host(LoopbackEndPoint(), CreateOptions());

        TaskCompletionSource<IOftConnection> connectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectedHandler = connection => connectionSource.TrySetResult(connection);

        IOftConnector connector = new OftConnector();
        await using IOftConnection clientConnection = await connector.Connect(
                "127.0.0.1",
                listener.LocalEndPoint.Port,
                new OftConnectOptions { Info = "client", SecurityMode = OftSecurityMode.ServerAuthentication, ServerCertificateValidation = (_, _, _, _) => true })
            .WaitAsync(OftTestHarness.DefaultTimeout);

        await using IOftConnection serverConnection = await connectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        // The listener doesn't track the connections it has accepted (see IOftListener's own doc
        // comment), so disposing it only stops the accept loop - it must leave an already-accepted
        // connection fully alive and usable.
        await listener.DisposeAsync();

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "still alive"u8.ToArray();
        await clientConnection.Send(payload).WaitAsync(OftTestHarness.DefaultTimeout);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task HandleAccepted_MalformedClient_DoesNotAffectListener()
    {
        await using IOftListener listener = await new OftHoster().Host(LoopbackEndPoint(), CreateOptions());
        bool established = false;
        listener.ConnectedHandler = _ => established = true;

        using (TcpClient rogue = new())
        {
            await rogue.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
            await rogue.GetStream().WriteAsync(new byte[] { 1, 2, 3, 4, 5 }).AsTask().WaitAsync(OftTestHarness.DefaultTimeout);
        }

        await Task.Delay(300);
        Assert.False(established);

        // The listener is still healthy afterward: a real client can still connect.
        IOftConnector connector = new OftConnector();
        await using IOftConnection connection = await connector.Connect(
                "127.0.0.1",
                listener.LocalEndPoint.Port,
                new OftConnectOptions { Info = "client", SecurityMode = OftSecurityMode.ServerAuthentication, ServerCertificateValidation = (_, _, _, _) => true })
            .WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Host_WithPort_ListensOnAnyAddressAtTheGivenPort()
    {
        int port = OftTestHarness.ReserveFreePort();

        await using IOftListener listener = await new OftHoster().Host(port);

        Assert.Equal(IPAddress.Any, listener.LocalEndPoint.Address);
        Assert.Equal(port, listener.LocalEndPoint.Port);
    }
}
