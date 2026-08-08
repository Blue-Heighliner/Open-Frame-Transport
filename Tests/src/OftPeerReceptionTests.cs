namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftPeerReceptionTests
{
    private sealed class TrackedMemoryOwner : IMemoryOwner<byte>
    {
        public TrackedMemoryOwner(byte[] data)
        {
            this.Memory = data;
        }

        public Memory<byte> Memory { get; }

        public int DisposeCount { get; private set; }

        public void Dispose() => this.DisposeCount++;
    }

    [Fact]
    public void Data_ReflectsUnderlyingOwnerMemory()
    {
        byte[] payload = "hello"u8.ToArray();
        TrackedMemoryOwner owner = new(payload);
        OftIdentity identity = new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 5000), Certificate = null, Info = "peer" };

        using OftPeerReception reception = new(owner, identity);

        Assert.Equal(payload, reception.Data.ToArray());
        Assert.Same(identity, reception.Identity);
    }

    [Fact]
    public void Dispose_DisposesUnderlyingOwner()
    {
        TrackedMemoryOwner owner = new("hello"u8.ToArray());
        OftIdentity identity = new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 5000), Certificate = null, Info = "peer" };
        OftPeerReception reception = new(owner, identity);

        reception.Dispose();

        Assert.Equal(1, owner.DisposeCount);
    }
}
