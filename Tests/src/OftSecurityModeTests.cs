namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftSecurityModeTests
{
    [Fact]
    public async Task Secure_NoCertificatesConfigured_ConnectionEstablishesAndExchangesMessages()
    {
        // Secure mode needs no certificates from either side: the host generates its own
        // throwaway certificate internally, and the connecting side accepts it unconditionally.
        using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.Secure);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "hello under secure mode"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Secure_ConfiguredCertificateIsIgnored()
    {
        // A caller-supplied Certificate is meaningless under Secure mode (nothing validates it), so
        // hosting must succeed even though this certificate is never actually presented.
        X509Certificate2 unusedCertificate = TestCertificate.Create();

        using IOftListener listener = await new OftHoster().Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftConnectionOptions { Info = "server", SecurityMode = OftSecurityMode.Secure, Certificate = unusedCertificate });

        Assert.NotNull(listener);
    }

    [Fact]
    public async Task DualAuthentication_ConnectWithoutClientCertificate_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new OftConnector().Connect(
            "127.0.0.1",
            OftTestHarness.ReserveFreePort(),
            new OftConnectionOptions { Info = "client", SecurityMode = OftSecurityMode.DualAuthentication }));
    }

    [Fact]
    public async Task DualAuthentication_BothSidesPresentCertificates_ConnectionEstablishesAndExchangesMessages()
    {
        OftConnectionOptions hostOptions = new()
        {
            Info = "server",
            SecurityMode = OftSecurityMode.DualAuthentication,
            Certificate = TestCertificate.Create(),
            CertificateValidation = (_, _, _, _) => true,
        };

        using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectedHandler = connection => serverConnectionSource.TrySetResult(connection);

        OftConnectionOptions connectOptions = new()
        {
            Info = "client",
            SecurityMode = OftSecurityMode.DualAuthentication,
            Certificate = TestCertificate.Create(),
            CertificateValidation = (_, _, _, _) => true,
        };

        using IOftConnection clientConnection = await new OftConnector()
            .Connect("127.0.0.1", listener.LocalEndPoint.Port, connectOptions)
            .WaitAsync(OftTestHarness.DefaultTimeout);
        using IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "hello under mutual tls"u8.ToArray();
        await clientConnection.Send(payload).WaitAsync(OftTestHarness.DefaultTimeout);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }
}
