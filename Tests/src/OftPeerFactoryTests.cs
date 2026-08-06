namespace OpenFrameTransport.Tests;

public sealed class OftPeerFactoryTests
{
    [Fact]
    public async Task Open_AuthenticationModeWithoutServerCertificate_Throws()
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());

        await using IOftPeer peer = factory.Create(new OftPeerOptions { Info = "peer", SecurityMode = OftSecurityMode.Authentication });

        await Assert.ThrowsAsync<ArgumentException>(() => peer.Open(new IPEndPoint(IPAddress.Loopback, 0)));
    }

    [Fact]
    public async Task Create_NoServerCertificate_DoesNotRequireOne()
    {
        OftPeerFactory factory = new(new OftConnector(), new OftHoster());

        await using IOftPeer peer = factory.Create(new OftPeerOptions { Info = "peer" });

        Assert.Null(peer.LocalEndPoint);
    }
}
