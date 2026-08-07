namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class HandshakeTests
{
    [Fact]
    public async Task Establish_ExchangesInfoAsHail()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        Assert.Equal("server", pair.ClientConnection.RemoteInfo);
        Assert.Equal("client", pair.ServerConnection.RemoteInfo);
    }

    [Fact]
    public async Task Establish_RecordsConnectionTimestamps()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await using OftPair pair = await OftTestHarness.Establish();
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.InRange(pair.ClientConnection.ConnectedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.InRange(pair.ServerConnection.ConnectedAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task Establish_RemoteEndPointMatchesTheOtherSide()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        Assert.Equal(pair.Listener.LocalEndPoint.Port, pair.ClientConnection.RemoteEndPoint.Port);
        Assert.True(pair.ServerConnection.RemoteEndPoint.Port > 0);
    }

    [Fact]
    public async Task Connect_CancelledMidHandshake_ThrowsOperationCanceled()
    {
        // A raw listener that accepts the TCP connection but never speaks TLS at all, so the
        // client's TLS handshake blocks forever - giving a deterministic window to cancel it,
        // regardless of how fast this machine happens to run.
        TcpListener rawListener = new(IPAddress.Loopback, 0);
        rawListener.Start();
        try
        {
            Task<TcpClient> acceptTask = rawListener.AcceptTcpClientAsync();

            IOftConnector connector = new OftConnector();
            OftConnectOptions options = new()
            {
                Info = "client",
                SecurityMode = OftSecurityMode.ServerAuthentication,
                ServerCertificateValidation = (_, _, _, _) => true,
            };

            using CancellationTokenSource cts = new();
            Task<IOftConnection> connectTask = connector.Connect(
                "127.0.0.1", ((IPEndPoint)rawListener.LocalEndpoint).Port, options, cts.Token);

            using TcpClient accepted = await acceptTask.WaitAsync(OftTestHarness.DefaultTimeout);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask).WaitAsync(OftTestHarness.DefaultTimeout);
        }
        finally
        {
            rawListener.Stop();
        }
    }
}
