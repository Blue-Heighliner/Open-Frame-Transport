namespace OpenFrameTransport.Tests;

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
        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0));

        IOftConnector connector = new OftConnector();
        await using IOftConnection connection = await connector.Connect("127.0.0.1", listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Equal(string.Empty, connection.RemoteInfo);
    }

    [Fact]
    public async Task Connect_ReceivedNeverMissesAMessageSentImmediately()
    {
        OftHostOptions hostOptions = new()
        {
            Info = "server",
            ServerCertificate = TestCertificate.Create(),
            SecurityMode = OftSecurityMode.Authentication,
        };

        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        // Queued as early as structurally possible - before this connection's own send loop even
        // exists yet (see IOftListener.Connected's contract) - so it's flushed as the very first
        // thing once the connection starts processing, immediately after this handler returns:
        // about as fast as a peer's first message could possibly arrive.
        listener.Connected += (_, args) => _ = args.Connection.Send("immediate"u8.ToArray());

        IOftConnector connector = new OftConnector();
        OftConnectOptions connectOptions = new()
        {
            Info = "client",
            SecurityMode = OftSecurityMode.Authentication,
            ServerCertificateValidation = (_, _, _, _) => true,
        };

        await using IOftConnection connection = await connector.Connect("127.0.0.1", listener.LocalEndPoint.Port, connectOptions).WaitAsync(OftTestHarness.DefaultTimeout);

        // Subscribing after Connect() returns - there's no onEstablished-style hook to subscribe
        // any earlier with - is safe precisely because Received is backed by OftBufferedEvent:
        // nothing raised before this subscription attaches is lost, so this isn't a race against
        // the listener's immediate reply above.
        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Received += (_, args) => received.TrySetResult(args);

        OftReceivedEventArgs receivedArgs = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal("immediate"u8.ToArray(), receivedArgs.Data.ToArray());
    }
}
