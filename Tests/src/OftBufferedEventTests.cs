namespace OpenFrameTransport.Tests;

public sealed class OftBufferedEventTests
{
    private sealed class TestEventArgs : EventArgs
    {
        public required int Value { get; init; }
    }

    private sealed class DisposableEventArgs : EventArgs, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }

    [Fact]
    public void Raise_BeforeAnySubscriber_IsBufferedNotLost()
    {
        object sender = new();
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(sender);

        bufferedEvent.Raise(new TestEventArgs { Value = 1 });

        List<int> received = [];
        bufferedEvent.Subscribe((_, args) => received.Add(args.Value));

        Assert.Equal([1], received);
    }

    [Fact]
    public void Raise_MultipleBeforeFirstSubscriber_FlushedInOrder()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());

        bufferedEvent.Raise(new TestEventArgs { Value = 1 });
        bufferedEvent.Raise(new TestEventArgs { Value = 2 });
        bufferedEvent.Raise(new TestEventArgs { Value = 3 });

        List<int> received = [];
        bufferedEvent.Subscribe((_, args) => received.Add(args.Value));

        Assert.Equal([1, 2, 3], received);
    }

    [Fact]
    public void Subscribe_FirstSubscriber_ReceivesSenderPassedToConstructor()
    {
        object sender = new();
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(sender);
        bufferedEvent.Raise(new TestEventArgs { Value = 1 });

        object? observedSender = null;
        bufferedEvent.Subscribe((s, _) => observedSender = s);

        Assert.Same(sender, observedSender);
    }

    [Fact]
    public void Raise_AfterFirstSubscriber_DeliveredLiveNotBuffered()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());

        List<int> received = [];
        bufferedEvent.Subscribe((_, args) => received.Add(args.Value));

        bufferedEvent.Raise(new TestEventArgs { Value = 1 });

        Assert.Equal([1], received);
    }

    [Fact]
    public void Subscribe_SecondSubscriber_DoesNotReceiveEarlierBacklog()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());
        bufferedEvent.Raise(new TestEventArgs { Value = 1 });

        List<int> firstReceived = [];
        bufferedEvent.Subscribe((_, args) => firstReceived.Add(args.Value));

        List<int> secondReceived = [];
        bufferedEvent.Subscribe((_, args) => secondReceived.Add(args.Value));

        Assert.Equal([1], firstReceived);
        Assert.Empty(secondReceived);
    }

    [Fact]
    public void Subscribe_MultipleSubscribers_AllReceiveSubsequentRaises()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());

        List<int> firstReceived = [];
        List<int> secondReceived = [];
        bufferedEvent.Subscribe((_, args) => firstReceived.Add(args.Value));
        bufferedEvent.Subscribe((_, args) => secondReceived.Add(args.Value));

        bufferedEvent.Raise(new TestEventArgs { Value = 5 });

        Assert.Equal([5], firstReceived);
        Assert.Equal([5], secondReceived);
    }

    [Fact]
    public void Subscribe_NullHandler_IsANoOp()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());
        bufferedEvent.Raise(new TestEventArgs { Value = 1 });

        // Must not throw, and must not consume the "first subscriber" flush.
        bufferedEvent.Subscribe(null);

        List<int> received = [];
        bufferedEvent.Subscribe((_, args) => received.Add(args.Value));
        Assert.Equal([1], received);
    }

    [Fact]
    public void Unsubscribe_NullHandler_IsANoOp()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());
        bufferedEvent.Unsubscribe(null);
    }

    [Fact]
    public void Unsubscribe_RemovesHandlerFromFutureRaises()
    {
        OftBufferedEvent<TestEventArgs> bufferedEvent = new(new object());

        List<int> received = [];
        EventHandler<TestEventArgs> handler = (_, args) => received.Add(args.Value);
        bufferedEvent.Subscribe(handler);
        bufferedEvent.Unsubscribe(handler);

        bufferedEvent.Raise(new TestEventArgs { Value = 1 });

        Assert.Empty(received);
    }

    [Fact]
    public void DisposeBuffered_DisposesEveryStillBufferedArgs()
    {
        OftBufferedEvent<DisposableEventArgs> bufferedEvent = new(new object());

        DisposableEventArgs first = new();
        DisposableEventArgs second = new();
        bufferedEvent.Raise(first);
        bufferedEvent.Raise(second);

        bufferedEvent.DisposeBuffered();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public void DisposeBuffered_ClearsBacklogSoNoSubscriberEverReceivesIt()
    {
        OftBufferedEvent<DisposableEventArgs> bufferedEvent = new(new object());
        bufferedEvent.Raise(new DisposableEventArgs());

        bufferedEvent.DisposeBuffered();

        List<DisposableEventArgs> received = [];
        bufferedEvent.Subscribe((_, args) => received.Add(args));

        Assert.Empty(received);
    }

    [Fact]
    public void DisposeBuffered_NoBufferedRaises_DoesNotThrow()
    {
        OftBufferedEvent<DisposableEventArgs> bufferedEvent = new(new object());
        bufferedEvent.DisposeBuffered();
    }
}
