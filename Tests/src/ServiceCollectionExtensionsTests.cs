namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOpenFrameTransport_RegistersFactoriesByConvention()
    {
        ServiceCollection services = new();
        services.AddOpenFrameTransport();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<OftConnector>(provider.GetRequiredService<IOftConnector>());
        Assert.IsType<OftHoster>(provider.GetRequiredService<IOftHoster>());
        Assert.IsType<OftPeerFactory>(provider.GetRequiredService<IOftPeerFactory>());
    }
}
