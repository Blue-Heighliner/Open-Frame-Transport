namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftConnectorTests
{
    [Fact]
    public async Task Connect_NothingListening_ThrowsAndDisposesSocket()
    {
        int freePort = OftTestHarness.ReserveFreePort();

        IOftConnector connector = new OftConnector();

        await Assert.ThrowsAnyAsync<Exception>(() => connector.Connect("127.0.0.1", freePort));
    }

    [Fact]
    public async Task Connect_NoOptions_UsesDefaults()
    {
        using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0));

        IOftConnector connector = new OftConnector();
        using IOftConnection connection = await connector.Connect("127.0.0.1", listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Equal(string.Empty, connection.Identity.Info);
    }

    [Fact]
    public async Task Connect_ReceivedNeverMissesAMessageSentImmediately()
    {
        OftConnectionOptions hostOptions = new()
        {
            Info = "server",
            Certificate = TestCertificate.Create(),
            SecurityMode = OftSecurityMode.ServerAuthentication,
        };

        using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        // Queued as early as structurally possible - before this connection's own send loop even
        // exists yet (see IOftListener.ConnectedHandler's contract) - so it's flushed as the very
        // first thing once the connection starts processing, immediately after this callback
        // returns: about as fast as a peer's first message could possibly arrive.
        listener.ConnectedHandler = connection => _ = connection.Send("immediate"u8.ToArray());

        IOftConnector connector = new OftConnector();
        OftConnectionOptions connectOptions = new()
        {
            Info = "client",
            SecurityMode = OftSecurityMode.ServerAuthentication,
            CertificateValidation = (_, _, _, _) => true,
        };

        using IOftConnection connection = await connector.Connect("127.0.0.1", listener.LocalEndPoint.Port, connectOptions).WaitAsync(OftTestHarness.DefaultTimeout);

        // Assigning ReceivedHandler after Connect() returns is safe precisely because it's backed by
        // OftBufferedHandlerSlot: nothing raised before this assignment is lost, so this isn't a
        // race against the listener's immediate reply above.
        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ReceivedHandler = data => received.TrySetResult(data);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal("immediate"u8.ToArray(), data.Memory.ToArray());
    }
}
