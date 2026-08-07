namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class RekeyTests
{
    [Fact]
    public async Task Rekey_ConnectionStillWorksAfterward()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        await pair.ClientConnection.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "post-rekey"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Rekey_InitiatedFromServerSide_Works()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        await pair.ServerConnection.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "post-rekey-from-server"u8.ToArray();
        await pair.ServerConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Rekey_InitiatedSimultaneouslyFromBothSides_DoesNotDeadlock()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        Task clientRekey = pair.ClientConnection.Rekey();
        Task serverRekey = pair.ServerConnection.Rekey();

        await Task.WhenAll(clientRekey, serverRekey).WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "after simultaneous rekey"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Rekey_WhileMessageInFlight_StillDeliversMessage()
    {
        await using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = [.. Enumerable.Repeat((byte)7, 300)];
        Task sendTask = pair.ClientConnection.Send(payload, priority: 1);

        await Task.Delay(20);
        await pair.ClientConnection.Rekey().WaitAsync(OftTestHarness.DefaultTimeout);

        await sendTask.WaitAsync(OftTestHarness.DefaultTimeout);
        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task RekeyInterval_AutomaticallyRekeysWithoutBreakingConnection()
    {
        await using OftPair pair = await OftTestHarness.Establish(rekeyInterval: TimeSpan.FromMilliseconds(150));

        await Task.Delay(500);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "still here"u8.ToArray();
        await pair.ClientConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }
}
