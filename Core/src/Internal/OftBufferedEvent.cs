namespace OpenFrameTransport.Internal;

/// <summary>
/// Backs a public <c>event EventHandler&lt;TEventArgs&gt;</c> with custom add/remove accessors so
/// that no raise is ever lost for lack of a subscriber: every raise that happens before the first
/// subscriber ever attaches is buffered, then flushed, in order, to that first subscriber before it
/// starts receiving live raises. Once at least one subscriber has ever attached, this behaves exactly
/// like an ordinary multicast event — a subscriber added afterward only sees raises from then on, not
/// the earlier backlog.
/// </summary>
/// <remarks>
/// This exists because <see cref="OftConnection"/>'s background processing — and therefore its first
/// opportunity to raise <c>Received</c>/<c>Disconnected</c>, and an <see cref="OftListener"/>'s first
/// opportunity to raise <c>Connected</c> — can start the instant a connection is established, before
/// whoever just created or was handed that connection/listener has had a chance to attach a listener
/// of their own. Buffering until the first subscriber removes the need for any of the callers in this
/// codebase to carefully sequence "start processing" after "give the caller a chance to subscribe" —
/// every one of them can just start processing immediately, since nothing raised before a subscriber
/// attaches is ever discarded.
/// </remarks>
/// <typeparam name="TEventArgs">The event's argument type.</typeparam>
internal sealed class OftBufferedEvent<TEventArgs>
    where TEventArgs : EventArgs
{
    private readonly object sender;
    private readonly object gate = new();
    private readonly List<TEventArgs> buffered = [];
    private EventHandler<TEventArgs>? handlers;
    private bool everSubscribed;

    /// <param name="sender">The object passed as every raised event's <c>sender</c> argument.</param>
    public OftBufferedEvent(object sender)
    {
        this.sender = sender;
    }

    /// <summary>
    /// Raises the event with <paramref name="args"/>: delivered immediately to every current
    /// subscriber once at least one has ever attached, or buffered for later delivery to the first
    /// one to attach otherwise.
    /// </summary>
    /// <param name="args">The event data to raise.</param>
    public void Raise(TEventArgs args)
    {
        EventHandler<TEventArgs>? toInvoke;
        lock (this.gate)
        {
            if (!this.everSubscribed)
            {
                this.buffered.Add(args);
                return;
            }

            toInvoke = this.handlers;
        }

        toInvoke?.Invoke(this.sender, args);
    }

    /// <summary>
    /// Adds <paramref name="handler"/> as a subscriber. If this is the first subscriber this
    /// instance has ever had, it is first synchronously delivered every raise that was buffered
    /// while there was no subscriber, in the order they were raised.
    /// </summary>
    public void Subscribe(EventHandler<TEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        List<TEventArgs>? toFlush = null;
        lock (this.gate)
        {
            if (!this.everSubscribed)
            {
                this.everSubscribed = true;
                if (this.buffered.Count > 0)
                {
                    toFlush = [.. this.buffered];
                    this.buffered.Clear();
                }
            }

            this.handlers += handler;
        }

        if (toFlush is not null)
        {
            foreach (TEventArgs args in toFlush)
            {
                handler(this.sender, args);
            }
        }
    }

    /// <summary>Removes a previously added subscriber. Does not affect buffering state.</summary>
    public void Unsubscribe(EventHandler<TEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (this.gate)
        {
            this.handlers -= handler;
        }
    }

    /// <summary>
    /// Disposes and discards every raise still buffered for lack of a subscriber (relevant only for
    /// a <typeparamref name="TEventArgs"/> that owns disposable resources, e.g. pooled memory) —
    /// called when the object that owns this event is being torn down, so that a raise nobody ever
    /// subscribed to isn't held onto forever instead of being released.
    /// </summary>
    public void DisposeBuffered()
    {
        List<TEventArgs> toDispose;
        lock (this.gate)
        {
            toDispose = [.. this.buffered];
            this.buffered.Clear();
        }

        foreach (TEventArgs args in toDispose)
        {
            (args as IDisposable)?.Dispose();
        }
    }
}
