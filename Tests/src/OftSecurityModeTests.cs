namespace OpenFrameTransport.Tests;

public sealed class OftSecurityModeTests
{
    [Fact]
    public async Task Secure_NoCertificatesConfigured_ConnectionEstablishesAndExchangesMessages()
    {
        // Secure mode needs no certificates from either side: the host generates its own
        // throwaway certificate internally, and the connecting side accepts it unconditionally.
        await using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.Secure);

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = "hello under secure mode"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Secure_ConfiguredServerCertificateIsIgnored()
    {
        // A caller-supplied ServerCertificate is meaningless under Secure mode (nothing validates
        // it), so hosting must succeed even though this certificate is never actually presented.
        X509Certificate2 unusedCertificate = TestCertificate.Create();

        await using IOftListener listener = await new OftHoster().Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftHostOptions { Info = "server", SecurityMode = OftSecurityMode.Secure, ServerCertificate = unusedCertificate });

        Assert.NotNull(listener);
    }

    [Fact]
    public async Task DualAuthentication_ConnectWithoutClientCertificates_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new OftConnector().Connect(
            "127.0.0.1",
            OftTestHarness.ReserveFreePort(),
            new OftConnectOptions { Info = "client", SecurityMode = OftSecurityMode.DualAuthentication }));
    }

    [Fact]
    public async Task DualAuthentication_BothSidesPresentCertificates_ConnectionEstablishesAndExchangesMessages()
    {
        OftHostOptions hostOptions = new()
        {
            Info = "server",
            SecurityMode = OftSecurityMode.DualAuthentication,
            ServerCertificate = TestCertificate.Create(),
            ClientCertificateValidation = (_, _, _, _) => true,
        };

        await using IOftListener listener = await new OftHoster().Host(new IPEndPoint(IPAddress.Loopback, 0), hostOptions);

        TaskCompletionSource<IOftConnection> serverConnectionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Connected += (_, args) => serverConnectionSource.TrySetResult(args.Connection);

        OftConnectOptions connectOptions = new()
        {
            Info = "client",
            SecurityMode = OftSecurityMode.DualAuthentication,
            ClientCertificates = [TestCertificate.Create()],
            ServerCertificateValidation = (_, _, _, _) => true,
        };

        await using IOftConnection clientConnection = await new OftConnector()
            .Connect("127.0.0.1", listener.LocalEndPoint.Port, connectOptions)
            .WaitAsync(OftTestHarness.DefaultTimeout);
        await using IOftConnection serverConnection = await serverConnectionSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = "hello under mutual tls"u8.ToArray();
        await clientConnection.Send(payload).WaitAsync(OftTestHarness.DefaultTimeout);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }
}
