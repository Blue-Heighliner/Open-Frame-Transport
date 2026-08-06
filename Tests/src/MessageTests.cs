namespace OpenFrameTransport.Tests;

public sealed class MessageTests
{
    [Fact]
    public async Task Send_Small_DeliveredAsUnit()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = "hello"u8.ToArray();
        await pair.ClientConnection.Send(payload, priority: 7);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Send_EmptyPayload_DeliveredAsEmptyMessage()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        await pair.ClientConnection.Send(ReadOnlyMemory<byte>.Empty);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(0, args.Data.Length);
    }

    [Fact]
    public async Task Send_LargerThanPacketSize_SplitAndReassembled()
    {
        await using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];
        await pair.ClientConnection.Send(payload, priority: 3);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Send_ExactlyOnePacketSize_DeliveredAsUnit()
    {
        await using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        await pair.ClientConnection.Send(payload, priority: 2);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Send_IsBidirectional()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] payload = "from server"u8.ToArray();
        await pair.ServerConnection.Send(payload);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, args.Data.ToArray());
    }

    [Fact]
    public async Task Send_HigherPriorityInterruptsLowerPriority()
    {
        await using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        List<int> receivedOrder = [];
        TaskCompletionSource<bool> bothReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) =>
        {
            lock (receivedOrder)
            {
                receivedOrder.Add(args.Data.Length);
                if (receivedOrder.Count == 2)
                {
                    bothReceived.TrySetResult(true);
                }
            }
        };

        byte[] lowPriorityPayload = [.. Enumerable.Repeat((byte)1, 500)];
        byte[] highPriorityPayload = [.. Enumerable.Repeat((byte)2, 24)];

        Task lowSend = pair.ClientConnection.Send(lowPriorityPayload, priority: 0);
        await Task.Delay(20);
        Task highSend = pair.ClientConnection.Send(highPriorityPayload, priority: 5);

        await bothReceived.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        await Task.WhenAll(lowSend, highSend).WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Equal([highPriorityPayload.Length, lowPriorityPayload.Length], receivedOrder);
    }

    [Fact]
    public async Task Send_CancelledBeforeStart_NeverDelivered()
    {
        await using OftPair pair = await OftTestHarness.Establish();

        using CancellationTokenSource cts = new();
        TaskCompletionSource<bool> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, _) => received.TrySetResult(true);

        cts.Cancel();
        Task sendTask = pair.ClientConnection.Send("should not arrive"u8.ToArray(), cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        Task delay = Task.Delay(200);
        Task completed = await Task.WhenAny(received.Task, delay);
        Assert.Same(delay, completed);
    }

    [Fact]
    public async Task Send_CancelledAfterStart_SendsCancellationAndConnectionStaysHealthy()
    {
        await using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        using CancellationTokenSource cts = new();
        byte[] payload = [.. Enumerable.Repeat((byte)9, 400)];

        Task sendTask = pair.ClientConnection.Send(payload, cancellationToken: cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        TaskCompletionSource<OftReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.Received += (_, args) => received.TrySetResult(args);

        byte[] followUp = "still alive"u8.ToArray();
        await pair.ClientConnection.Send(followUp);

        OftReceivedEventArgs args = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(followUp, args.Data.ToArray());
    }
}
