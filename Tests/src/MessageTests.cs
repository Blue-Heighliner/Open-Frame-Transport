namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class MessageTests
{
    [Fact]
    public async Task Send_Small_DeliveredAsUnit()
    {
        using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "hello"u8.ToArray();
        await pair.ClientConnection.Send(payload, priority: 7);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Send_EmptyPayload_DeliveredAsEmptyMessage()
    {
        using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        await pair.ClientConnection.Send(ReadOnlyMemory<byte>.Empty);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(0, data.Memory.Length);
    }

    [Fact]
    public async Task Send_LargerThanPacketSize_SplitAndReassembled()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];
        await pair.ClientConnection.Send(payload, priority: 3);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Send_ExactlyOnePacketSize_DeliveredAsUnit()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        await pair.ClientConnection.Send(payload, priority: 2);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Send_OneByteOverPacketSize_SplitWithMinimalFinalChunk()
    {
        // The smallest possible split: one full Data chunk plus a 1-byte Completion chunk. This is
        // the boundary case the Completion-carries-the-proto3-default-control-value design (Docs/OFT.md
        // §4) depends on - a Completion packet's data must never be empty, and this is as close to
        // empty as a real one can get.
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = [.. Enumerable.Range(0, 17).Select(i => (byte)i)];
        await pair.ClientConnection.Send(payload, priority: 1);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Send_IsBidirectional()
    {
        using OftPair pair = await OftTestHarness.Establish();

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] payload = "from server"u8.ToArray();
        await pair.ServerConnection.Send(payload);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(payload, data.Memory.ToArray());
    }

    [Fact]
    public async Task Send_HigherPriorityInterruptsLowerPriority()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        List<int> receivedOrder = [];
        TaskCompletionSource<bool> bothReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data =>
        {
            using (data)
            {
                lock (receivedOrder)
                {
                    receivedOrder.Add(data.Memory.Length);
                    if (receivedOrder.Count == 2)
                    {
                        bothReceived.TrySetResult(true);
                    }
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
        using OftPair pair = await OftTestHarness.Establish();

        using CancellationTokenSource cts = new();
        TaskCompletionSource<bool> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data =>
        {
            data.Dispose();
            received.TrySetResult(true);
        };

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
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        using CancellationTokenSource cts = new();
        byte[] payload = [.. Enumerable.Repeat((byte)9, 400)];

        Task sendTask = pair.ClientConnection.Send(payload, cancellationToken: cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        byte[] followUp = "still alive"u8.ToArray();
        await pair.ClientConnection.Send(followUp);

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal(followUp, data.Memory.ToArray());
    }

    [Fact]
    public async Task Send_WithTag_RaisesDeliveryStatusHandlerEndingInAcknowledged()
    {
        using OftPair pair = await OftTestHarness.Establish();

        object tag = new();
        List<OftDeliveryStatus> statuses = [];
        TaskCompletionSource<object> acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.DeliveryStatusHandler = (raisedTag, status) =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }

            if (status == OftDeliveryStatus.Acknowledged)
            {
                acknowledged.TrySetResult(raisedTag);
            }
        };

        await pair.ClientConnection.Send("hello"u8.ToArray(), tag: tag);

        object acknowledgedTag = await acknowledged.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Same(tag, acknowledgedTag);
        Assert.Equal([OftDeliveryStatus.Queued, OftDeliveryStatus.Sending, OftDeliveryStatus.Sent, OftDeliveryStatus.Acknowledged], statuses);
    }

    [Fact]
    public async Task Send_WithTagLargerThanPacketSize_RaisesAcknowledgedOnlyAfterFinalCompletion()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 16);

        object tag = new();
        TaskCompletionSource<object> acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.DeliveryStatusHandler = (raisedTag, status) =>
        {
            if (status == OftDeliveryStatus.Acknowledged)
            {
                acknowledged.TrySetResult(raisedTag);
            }
        };

        byte[] payload = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];
        Task sendTask = pair.ClientConnection.Send(payload, tag: tag);

        object acknowledgedTag = await acknowledged.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Same(tag, acknowledgedTag);
        await sendTask.WaitAsync(OftTestHarness.DefaultTimeout);
    }

    [Fact]
    public async Task Send_WithNullTag_NeverRaisesDeliveryStatusHandler()
    {
        using OftPair pair = await OftTestHarness.Establish();

        bool deliveryStatusHandlerRaised = false;
        pair.ClientConnection.DeliveryStatusHandler = (_, _) => deliveryStatusHandlerRaised = true;

        TaskCompletionSource<IMemoryOwner<byte>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerConnection.ReceivedHandler = data => received.TrySetResult(data);

        await pair.ClientConnection.Send("hello"u8.ToArray());

        using IMemoryOwner<byte> data = await received.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal("hello"u8.ToArray(), data.Memory.ToArray());
        Assert.False(deliveryStatusHandlerRaised);
    }

    [Fact]
    public async Task Send_CancelledBeforeStartWithTag_RaisesOnlyCancelled()
    {
        using OftPair pair = await OftTestHarness.Establish();

        List<OftDeliveryStatus> statuses = [];
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.DeliveryStatusHandler = (_, status) =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }

            if (status == OftDeliveryStatus.Cancelled)
            {
                cancelled.TrySetResult(true);
            }
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();
        Task sendTask = pair.ClientConnection.Send("should not arrive"u8.ToArray(), tag: new object(), cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        // Cancelled before the send loop ever picked it up, so no Queued/Sending is expected either -
        // it's removed from the queue synchronously by the cancellation callback itself.
        await cancelled.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        Assert.Equal([OftDeliveryStatus.Cancelled], statuses);
    }

    [Fact]
    public async Task Send_HigherPriorityInterruptsLowerPriority_RaisesInterruptedAndResumedForLowPriorityMessage()
    {
        using OftPair pair = await OftTestHarness.Establish(maxPacketDataSize: 8);

        object lowPriorityTag = new();
        List<OftDeliveryStatus> lowPriorityStatuses = [];
        TaskCompletionSource<bool> lowPriorityAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ClientConnection.DeliveryStatusHandler = (tag, status) =>
        {
            if (ReferenceEquals(tag, lowPriorityTag))
            {
                lock (lowPriorityStatuses)
                {
                    lowPriorityStatuses.Add(status);
                }

                if (status == OftDeliveryStatus.Acknowledged)
                {
                    lowPriorityAcknowledged.TrySetResult(true);
                }
            }
        };

        TaskCompletionSource<bool> bothReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int receivedCount = 0;
        pair.ServerConnection.ReceivedHandler = data =>
        {
            data.Dispose();
            if (Interlocked.Increment(ref receivedCount) == 2)
            {
                bothReceived.TrySetResult(true);
            }
        };

        byte[] lowPriorityPayload = [.. Enumerable.Repeat((byte)1, 500)];
        byte[] highPriorityPayload = [.. Enumerable.Repeat((byte)2, 24)];

        Task lowSend = pair.ClientConnection.Send(lowPriorityPayload, priority: 0, tag: lowPriorityTag);
        await Task.Delay(20);
        Task highSend = pair.ClientConnection.Send(highPriorityPayload, priority: 5);

        await bothReceived.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        await lowPriorityAcknowledged.Task.WaitAsync(OftTestHarness.DefaultTimeout);
        await Task.WhenAll(lowSend, highSend).WaitAsync(OftTestHarness.DefaultTimeout);

        Assert.Contains(OftDeliveryStatus.Interrupted, lowPriorityStatuses);
        Assert.Contains(OftDeliveryStatus.Resumed, lowPriorityStatuses);
        Assert.Equal(OftDeliveryStatus.Acknowledged, lowPriorityStatuses[^1]);
    }
}
