namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// Backs a public single-callback-slot property (e.g. <see cref="IOftConnection.ReceivedHandler"/>)
/// so that no raise is ever lost for lack of a callback: every raise that happens before the first
/// non-null callback is ever assigned is buffered, then flushed, in order, to that first callback
/// before it becomes the live target for anything raised afterward. Unlike a multicast event, there
/// is only ever one live target — assigning a new callback (including <see langword="null"/>) always
/// replaces the previous one; only the very first non-null assignment ever triggers a backlog flush.
/// </summary>
/// <remarks>
/// This exists because a connection's/listener's background processing can start the instant it's
/// established, before whoever just created or was handed it has had a chance to assign a callback
/// of their own. Buffering until the first non-null assignment removes the need for any caller in
/// this codebase to carefully sequence "start processing" after "give the caller a chance to assign
/// a callback" — every one of them can just start processing immediately, since nothing raised
/// before a callback is assigned is ever discarded.
/// </remarks>
/// <typeparam name="TDelegate">The callback delegate type, e.g. <see cref="Action{T}"/>.</typeparam>
internal sealed class OftBufferedHandlerSlot<TDelegate>
    where TDelegate : Delegate
{
    private readonly object gate = new();
    private readonly List<(Action<TDelegate> Invoke, IDisposable? DiscardedDisposable)> buffered = [];
    private TDelegate? handler;
    private bool everAssigned;

    /// <summary>
    /// The currently assigned callback, or <see langword="null"/> if none is assigned. Setting this
    /// always replaces the previous value. If this is the first time it has ever been set to a
    /// non-null value, every raise buffered up to this point is delivered to it first, in order.
    /// </summary>
    public TDelegate? Handler
    {
        get
        {
            lock (this.gate)
            {
                return this.handler;
            }
        }

        set
        {
            List<Action<TDelegate>>? toFlush = null;
            lock (this.gate)
            {
                if (!this.everAssigned && value is not null)
                {
                    this.everAssigned = true;
                    if (this.buffered.Count > 0)
                    {
                        toFlush = [.. this.buffered.Select(item => item.Invoke)];
                        this.buffered.Clear();
                    }
                }

                this.handler = value;
            }

            if (toFlush is not null)
            {
                foreach (Action<TDelegate> invoke in toFlush)
                {
                    invoke(value!);
                }
            }
        }
    }

    /// <summary>
    /// Raises <paramref name="invoke"/>: called immediately with the current callback once one has
    /// ever been assigned (a no-op if the current callback happens to be <see langword="null"/> at
    /// that point), or buffered for later delivery to the first callback ever assigned otherwise.
    /// </summary>
    /// <param name="invoke">Invokes the callback with this raise's data.</param>
    /// <param name="discardedDisposable">
    /// Disposed if this raise is still buffered (never delivered to a callback) when
    /// <see cref="DisposeBuffered"/> is called — relevant only for a raise carrying disposable
    /// resources, e.g. pooled memory.
    /// </param>
    public void Raise(Action<TDelegate> invoke, IDisposable? discardedDisposable = null)
    {
        TDelegate? current;
        lock (this.gate)
        {
            if (!this.everAssigned)
            {
                this.buffered.Add((invoke, discardedDisposable));
                return;
            }

            current = this.handler;
        }

        if (current is not null)
        {
            invoke(current);
        }
    }

    /// <summary>
    /// Disposes and discards every raise still buffered for lack of a handler ever being assigned —
    /// called when the object that owns this slot is being torn down, so a raise nobody ever handled
    /// isn't held onto forever instead of being released.
    /// </summary>
    public void DisposeBuffered()
    {
        List<IDisposable?> toDispose;
        lock (this.gate)
        {
            toDispose = [.. this.buffered.Select(item => item.DiscardedDisposable)];
            this.buffered.Clear();
        }

        foreach (IDisposable? disposable in toDispose)
        {
            disposable?.Dispose();
        }
    }
}
