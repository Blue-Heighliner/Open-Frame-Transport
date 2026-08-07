namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftBufferedHandlerSlotTests
{
    private sealed class DisposablePayload : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }

    [Fact]
    public void Raise_BeforeAnyHandlerAssigned_IsBufferedNotLost()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();

        slot.Raise(handler => handler(1));

        List<int> received = [];
        slot.Handler = received.Add;

        Assert.Equal([1], received);
    }

    [Fact]
    public void Raise_MultipleBeforeFirstAssignment_FlushedInOrder()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();

        slot.Raise(handler => handler(1));
        slot.Raise(handler => handler(2));
        slot.Raise(handler => handler(3));

        List<int> received = [];
        slot.Handler = received.Add;

        Assert.Equal([1, 2, 3], received);
    }

    [Fact]
    public void Raise_AfterFirstAssignment_DeliveredLiveNotBuffered()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();

        List<int> received = [];
        slot.Handler = received.Add;

        slot.Raise(handler => handler(1));

        Assert.Equal([1], received);
    }

    [Fact]
    public void Handler_AssignedAfterFirst_DoesNotReceiveEarlierBacklog()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();
        slot.Raise(handler => handler(1));

        List<int> first = [];
        slot.Handler = first.Add;

        List<int> second = [];
        slot.Handler = second.Add;

        Assert.Equal([1], first);
        Assert.Empty(second);
    }

    [Fact]
    public void Handler_Get_ReturnsCurrentlyAssignedHandler()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();
        Assert.Null(slot.Handler);

        Action<int> handler = _ => { };
        slot.Handler = handler;

        Assert.Same(handler, slot.Handler);
    }

    [Fact]
    public void Handler_AssignedNull_IsANoOpAndDoesNotConsumeTheFirstAssignment()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();
        slot.Raise(handler => handler(1));

        // Must not throw, and must not consume the "first non-null assignment" flush.
        slot.Handler = null;

        List<int> received = [];
        slot.Handler = received.Add;
        Assert.Equal([1], received);
    }

    [Fact]
    public void Handler_ReassignedToNull_IgnoresFutureRaises()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();

        List<int> received = [];
        slot.Handler = received.Add;
        slot.Handler = null;

        slot.Raise(handler => handler(1));

        Assert.Empty(received);
    }

    [Fact]
    public void Handler_ReassignedAfterNull_ResumesLiveDelivery()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();

        List<int> first = [];
        slot.Handler = first.Add;
        slot.Handler = null;
        slot.Raise(handler => handler(1));

        List<int> second = [];
        slot.Handler = second.Add;
        slot.Raise(handler => handler(2));

        Assert.Empty(first);
        Assert.Equal([2], second);
    }

    [Fact]
    public void DisposeBuffered_DisposesEveryStillBufferedDisposable()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();

        DisposablePayload first = new();
        DisposablePayload second = new();
        slot.Raise(_ => { }, discardedDisposable: first);
        slot.Raise(_ => { }, discardedDisposable: second);

        slot.DisposeBuffered();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public void DisposeBuffered_ClearsBacklogSoNoHandlerEverReceivesIt()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();
        slot.Raise(handler => handler(1));

        slot.DisposeBuffered();

        List<int> received = [];
        slot.Handler = received.Add;

        Assert.Empty(received);
    }

    [Fact]
    public void DisposeBuffered_NoBufferedRaises_DoesNotThrow()
    {
        OftBufferedHandlerSlot<Action<int>> slot = new();
        slot.DisposeBuffered();
    }
}
