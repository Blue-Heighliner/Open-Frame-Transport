namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class TrustedModeTests
{
    [Fact]
    public async Task Trusted_ConnectionEstablishesAndExchangesMessages()
    {
        await using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.Trusted);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "hello over plain tcp"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Host_ServerAuthenticationModeWithoutServerCertificate_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new OftHoster().Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftHostOptions { Info = "server", SecurityMode = OftSecurityMode.ServerAuthentication }));
    }

    [Fact]
    public async Task Host_TrustedWithoutServerCertificate_Succeeds()
    {
        await using IOftListener listener = await new OftHoster().Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftHostOptions { Info = "server", SecurityMode = OftSecurityMode.Trusted });

        Assert.NotNull(listener);
    }

    [Fact]
    public async Task Rekey_OnTrustedConnection_IsNoOp()
    {
        await using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.Trusted);

        await pair.ClientConnection.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Trusted_HailIsExchangedDirectlyOverRawTcp()
    {
        OftHostOptions hostOptions = new()
        {
            Info = "server",
            SecurityMode = OftSecurityMode.Trusted,
        };

        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectedHandler = connection => serverConnectionSource.TrySetResult(connection);

        (TcpClient tcpClient, OftFrameStream frameStream) = await OftTestHarness.RawConnectTrusted(listener.LocalEndPoint.Port).WaitAsync(OftTestHarness.DefaultTimeout);
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
