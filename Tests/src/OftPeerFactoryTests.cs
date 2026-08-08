namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftPeerFactoryTests
{
    [Fact]
    public void Create_ServerAuthenticationMode_Throws()
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());

        Assert.Throws<ArgumentException>(() => factory.Create(new OftPeerOptions { Info = "peer", SecurityMode = OftSecurityMode.ServerAuthentication }));
    }

    [Fact]
    public async Task Open_DualAuthenticationWithoutServerCertificate_Throws()
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());

        using IOftPeer peer = factory.Create(new OftPeerOptions { Info = "peer", SecurityMode = OftSecurityMode.DualAuthentication });

        await Assert.ThrowsAsync<ArgumentException>(() => peer.Listen(new IPEndPoint(IPAddress.Loopback, 0)));
    }

    [Fact]
    public async Task Create_NoServerCertificate_DoesNotRequireOne()
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());

        using IOftPeer peer = factory.Create(new OftPeerOptions { Info = "peer" });

        Assert.Null(peer.LocalEndPoint);
    }

    [Fact]
    public async Task ParameterlessConstructor_CreatesAUsablePeer()
    {
        OftPeerFactory factory = new();

        using IOftPeer peer = factory.Create(new OftPeerOptions { Info = "peer" });

        Assert.Null(peer.LocalEndPoint);
    }
}
