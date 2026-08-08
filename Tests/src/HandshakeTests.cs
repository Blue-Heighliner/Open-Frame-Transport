namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class HandshakeTests
{
    [Fact]
    public async Task Establish_ExchangesInfoAsHail()
    {
        using OftPair pair = await OftTestHarness.Establish();

        Assert.Equal("server", pair.ClientConnection.Identity.Info);
        Assert.Equal("client", pair.ServerConnection.Identity.Info);
    }

    [Fact]
    public async Task Establish_RecordsConnectionTimestamps()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        using OftPair pair = await OftTestHarness.Establish();
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.InRange(pair.ClientConnection.ConnectedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.InRange(pair.ServerConnection.ConnectedAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task Establish_IdentityEndPointMatchesTheOtherSide()
    {
        using OftPair pair = await OftTestHarness.Establish();

        Assert.Equal(pair.Listener.LocalEndPoint.Port, pair.ClientConnection.Identity.EndPoint.Port);
        Assert.True(pair.ServerConnection.Identity.EndPoint.Port > 0);
    }

    [Fact]
    public async Task Establish_ServerAuthentication_ClientSeesServerCertificateIdentity()
    {
        using OftPair pair = await OftTestHarness.Establish(securityMode: OftSecurityMode.ServerAuthentication);

        Assert.NotNull(pair.ClientConnection.Identity.Certificate);
        Assert.Equal("localhost", pair.ClientConnection.Identity.Certificate!.Name);

        // Server authentication only authenticates the server - the server never sees a client
        // certificate.
        Assert.Null(pair.ServerConnection.Identity.Certificate);
    }

    [Fact]
    public async Task Establish_ConnectionValidationNull_AllConnectionsAccepted()
    {
        using OftPair pair = await OftTestHarness.Establish();

        Assert.True(pair.ClientConnection.IsConnected);
        Assert.True(pair.ServerConnection.IsConnected);
    }

    [Fact]
    public async Task Establish_ServerAuthentication_ConnectionValidationSeesIdentityCertificateAndChain()
    {
        TaskCompletionSource<(OftIdentity Identity, X509Certificate2? Certificate, X509Chain? Chain, SslPolicyErrors SslErrors)> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        X509Certificate2 hostCertificate = TestCertificate.Create();

        IOftHoster hoster = new OftHoster();
        using IOftListener listener = await hoster.Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftConnectionOptions
            {
                Info = "server",
                Certificate = hostCertificate,
                SecurityMode = OftSecurityMode.ServerAuthentication,
                CertificateValidation = (_, _, _, _) => true,
            });

        IOftConnector connector = new OftConnector();
        using IOftConnection clientConnection = await connector.Connect(
            "127.0.0.1",
            listener.LocalEndPoint.Port,
            new OftConnectionOptions
            {
                Info = "client",
                SecurityMode = OftSecurityMode.ServerAuthentication,
                CertificateValidation = (_, _, _, _) => true,
                ConnectionValidation = (identity, certificate, chain, sslErrors) =>
                {
                    observed.TrySetResult((identity, certificate, chain, sslErrors));
                    return Task.FromResult(true);
                },
            }).WaitAsync(OftTestHarness.DefaultTimeout);

        (OftIdentity identity, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors _) = await observed.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Equal("server", identity.Info);
        Assert.NotNull(certificate);
        Assert.Equal(hostCertificate.Thumbprint, certificate!.Thumbprint);
        Assert.NotNull(chain);
    }

    [Fact]
    public async Task Establish_TrustedMode_ConnectionValidationSeesNoCertificateOrChain()
    {
        TaskCompletionSource<(X509Certificate2? Certificate, X509Chain? Chain, SslPolicyErrors SslErrors)> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IOftHoster hoster = new OftHoster();
        using IOftListener listener = await hoster.Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftConnectionOptions { Info = "server", SecurityMode = OftSecurityMode.Trusted });

        IOftConnector connector = new OftConnector();
        using IOftConnection clientConnection = await connector.Connect(
            "127.0.0.1",
            listener.LocalEndPoint.Port,
            new OftConnectionOptions
            {
                Info = "client",
                SecurityMode = OftSecurityMode.Trusted,
                ConnectionValidation = (_, certificate, chain, sslErrors) =>
                {
                    observed.TrySetResult((certificate, chain, sslErrors));
                    return Task.FromResult(true);
                },
            }).WaitAsync(OftTestHarness.DefaultTimeout);

        (X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslErrors) = await observed.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Null(certificate);
        Assert.Null(chain);
        Assert.Equal(SslPolicyErrors.None, sslErrors);
    }

    [Fact]
    public async Task Connect_ConnectionValidationReturnsFalse_ThrowsAuthenticationException()
    {
        IOftHoster hoster = new OftHoster();
        using IOftListener listener = await hoster.Host(
            new IPEndPoint(IPAddress.Loopback, 0),
            new OftConnectionOptions { Info = "server", SecurityMode = OftSecurityMode.Secure });

        IOftConnector connector = new OftConnector();
        await Assert.ThrowsAsync<AuthenticationException>(() => connector.Connect(
            "127.0.0.1",
            listener.LocalEndPoint.Port,
            new OftConnectionOptions
            {
                Info = "client",
                SecurityMode = OftSecurityMode.Secure,
                ConnectionValidation = (_, _, _, _) => Task.FromResult(false),
            })).WaitAsync(OftTestHarness.DefaultTimeout);
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
            OftConnectionOptions options = new()
            {
                Info = "client",
                SecurityMode = OftSecurityMode.ServerAuthentication,
                CertificateValidation = (_, _, _, _) => true,
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
