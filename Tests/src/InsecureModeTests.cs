namespace OpenFrameTransport.Tests;

public sealed class InsecureModeTests
{
    [Fact]
    public async Task Insecure_ConnectionEstablishesAndExchangesMessages()
    {
        await using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.Insecure);

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = "hello over plain tcp"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Host_AuthenticationModeWithoutServerCertificate_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new OftHoster().Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftHostOptions { Info = "server", SecurityMode = OftSecurityMode.Authentication }));
    }

    [Fact]
    public async Task Host_InsecureWithoutServerCertificate_Succeeds()
    {
        await using IOftListener listener = await new OftHoster().Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftHostOptions { Info = "server", SecurityMode = OftSecurityMode.Insecure });

        Assert.NotNull(listener);
    }

    [Fact]
    public async Task Rekey_OnInsecureConnection_IsNoOp()
    {
        await using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.Insecure);

        await pair.ClientConnection.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Insecure_HailIsExchangedDirectlyOverRawTcp()
    {
        OftHostOptions hostOptions = new()
        {
            Info = "server",
            SecurityMode = OftSecurityMode.Insecure,
        };

        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Connected += (_, args) => serverConnectionSource.TrySetResult(args.Connection);

        (TcpClient tcpClient, OftFrameStream frameStream) = await OftTestHarness.RawConnectInsecure(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
        using (tcpClient)
        {
            // No TLS handshake happened above: the hail is written as the very first bytes on the
            // raw TCP stream, immediately after connecting.
            await frameStream.Write(new Hail { Version = OftProtocolVersion.Current, Info = "raw-client" }, CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            Hail? serverHail = await frameStream.ReadHail(CancellationToken.None).WaitAsync(OftTestHarness.DefaultTimeout);
            Assert.NotNull(serverHail);
            Assert.Equal(OftProtocolVersion.Current, serverHail!.Version);

            IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);
            Assert.Equal("raw-client", serverConnection.RemoteInfo);
        }
    }
}
