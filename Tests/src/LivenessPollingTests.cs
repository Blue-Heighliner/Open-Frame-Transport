namespace OpenFrameTransport.Tests;

public sealed class LivenessPollingTests
{
    [Fact]
    public async Task Poll_KeepsIdleConnectionAliveBeyondPollTimeout()
    {
        await using OftPair pair = await OftTestHarness.Establish(
            pollInterval: TimeSpan.FromMilliseconds(50),
            pollTimeout: TimeSpan.FromMilliseconds(200));

        bool serverClosed = false;
        pair.ServerConnection.Disconnected += (_, _) => serverClosed = true;

        // No application traffic at all in either direction for well beyond PollTimeout: if the
        // background Poll packets weren't keeping the connection alive, the watchdog would have
        // already closed it.
        await Task.Delay(500);

        Assert.False(serverClosed);
    }

    [Fact]
    public async Task Poll_ClosesConnectionWhenPeerGoesSilent()
    {
        OftHostOptions hostOptions = new()
        {
            Info = "server",
            SecurityMode = OftSecurityMode.Insecure,
            PollInterval = TimeSpan.FromMilliseconds(50),
            PollTimeout = TimeSpan.FromMilliseconds(200),
        };

        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Connected += (_, args) => serverConnectionSource.TrySetResult(args.Connection);

        (TcpClient tcpClient, OftFrameStream frameStream) = await OftTestHarness.RawConnectInsecure(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        using (tcpClient)
        {
            await frameStream.Write(new Hail { Version = OftProtocolVersion.Current, Info = "silent-client" }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            await frameStream.ReadHail(CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);

            IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

            TaskCompletionSource<OftDisconnectedEventArgs> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            serverConnection.Disconnected += (_, args) => closedSource.TrySetResult(args);

            // The raw client above never sends another byte (no Poll, nothing) after the hail: the
            // server side must notice via its liveness watchdog and close on its own.
            OftDisconnectedEventArgs closedArgs = await closedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
            Assert.IsType<TimeoutException>(closedArgs.Exception);
        }
    }
}
