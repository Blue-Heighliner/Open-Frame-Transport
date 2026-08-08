namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftConnectionTests
{
    [Fact]
    public async Task Rekey_CalledTwiceOnSameConnection_BothComplete()
    {
        using OftPair pair = await OftTestHarness.Establish();

        Task rekey1 = pair.ClientConnection.Rekey();
        Task rekey2 = pair.ClientConnection.Rekey();

        await Task.WhenAll(rekey1, rekey2).WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Disconnect_WithQueuedUnsentMessages_CancelsThem()
    {
        using OftPair pair = await OftTestHarness.Establish();

        Task task1 = pair.ClientConnection.Send("a"u8.ToArray());
        Task task2 = pair.ClientConnection.Send("b"u8.ToArray());
        Task task3 = pair.ClientConnection.Send("c"u8.ToArray());

        await pair.ClientConnection.Disconnect();

        await Assert.ThrowsAnyAsync<Exception>(() => task1);
        await Assert.ThrowsAnyAsync<Exception>(() => task2);
        await Assert.ThrowsAnyAsync<Exception>(() => task3);
    }

    [Fact]
    public async Task Send_NegativePriority_Throws()
    {
        using OftPair pair = await OftTestHarness.Establish();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pair.ClientConnection.Send("hi"u8.ToArray(), priority: -1));
    }

    [Fact]
    public async Task Send_AfterDisconnect_Throws()
    {
        using OftPair pair = await OftTestHarness.Establish();
        await pair.ClientConnection.Disconnect();

        await Assert.ThrowsAsync<OftDisconnectedException>(() => pair.ClientConnection.Send("hi"u8.ToArray()));
    }

    [Fact]
    public async Task Disconnect_CalledTwice_IsIdempotent()
    {
        using OftPair pair = await OftTestHarness.Establish();

        await pair.ClientConnection.Disconnect();
        await pair.ClientConnection.Disconnect();
    }

    [Fact]
    public async Task Rekey_AfterDisconnect_Throws()
    {
        using OftPair pair = await OftTestHarness.Establish();
        await pair.ClientConnection.Disconnect();

        await Assert.ThrowsAsync<OftDisconnectedException>(() => pair.ClientConnection.Rekey());
    }

    [Fact]
    public async Task IsConnected_TrueUntilDisconnected()
    {
        using OftPair pair = await OftTestHarness.Establish();

        Assert.True(pair.ClientConnection.IsConnected);

        await pair.ClientConnection.Disconnect();

        Assert.False(pair.ClientConnection.IsConnected);
    }

    [Fact]
    public async Task IsConnected_FalseAfterRemoteDisconnect()
    {
        using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<bool> disconnectedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.DisconnectedHandler = _ => disconnectedSource.TrySetResult(true);

        await pair.ServerConnection.Disconnect();
        await disconnectedSource.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.False(pair.ClientConnection.IsConnected);
    }

    [Fact]
    public async Task Received_HandlerReassignedToNull_IgnoresFutureNotifications()
    {
        using OftPair pair = await OftTestHarness.Establish();

        int invocationCount = 0;
        pair.ServerConnection.ReceivedHandler = _ => invocationCount++;
        pair.ServerConnection.ReceivedHandler = null;

        await pair.ClientConnection.Send("ignored"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        await pair.ClientConnection.Send("after"u8.ToArray()).WaitAsync(OftTestHarness.DefaultTimeout);
        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Equal("after"u8.ToArray(), data.Memory.ToArray());
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task Disconnected_HandlerReassignedToNull_IgnoresNotification()
    {
        using OftPair pair = await OftTestHarness.Establish();

        int invocationCount = 0;
        pair.ServerConnection.DisconnectedHandler = _ => invocationCount++;
        pair.ServerConnection.DisconnectedHandler = null;

        await pair.ServerConnection.Disconnect();

        Assert.Equal(0, invocationCount);
    }
}
