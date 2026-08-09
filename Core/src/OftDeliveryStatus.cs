namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// A lifecycle stage of a tagged send, reported via
/// <see cref="IOftConnection.DeliveryStatusHandler"/>/<see cref="IOftPeer.DeliveryStatusHandler"/>.
/// Every tagged send passes through <see cref="Queued"/>, <see cref="Sending"/>, then either
/// <see cref="Cancelled"/> or <see cref="Sent"/> followed by <see cref="Acknowledged"/>;
/// <see cref="Interrupted"/>/<see cref="Resumed"/> pairs may occur any number of times in between
/// <see cref="Sending"/> and <see cref="Sent"/>, for a multi-packet send that a higher-priority send
/// preempts (see Docs/OFT.md §6) — a single-packet send can never be interrupted, since there is
/// nothing between its first and only packet for another send to interleave with.
/// <see cref="Cancelled"/> can only occur before <see cref="Sent"/>: once a send's final packet has
/// actually been written, cancelling it can no longer prevent delivery, so it always proceeds to
/// <see cref="Sent"/>/<see cref="Acknowledged"/> instead.
/// </summary>
public enum OftDeliveryStatus
{
    /// <summary>
    /// The send has been queued and is waiting its turn — reported once a send call returns, not
    /// necessarily synchronously before it does.
    /// </summary>
    Queued,

    /// <summary>
    /// The send's first packet has started transmitting.
    /// </summary>
    Sending,

    /// <summary>
    /// A higher-priority send has preempted this one before it finished (see Docs/OFT.md §6); it
    /// remains queued and eventually resumes.
    /// </summary>
    Interrupted,

    /// <summary>
    /// Transmission has resumed after an <see cref="Interrupted"/> preemption.
    /// </summary>
    Resumed,

    /// <summary>
    /// The send's final packet has been written, but not yet acknowledged.
    /// </summary>
    Sent,

    /// <summary>
    /// The send's final packet has been acknowledged (a <c>Receipt</c> — see Docs/OFT.md §4.1): the
    /// send is now fully delivered. This is the terminal status for a send that isn't cancelled.
    /// </summary>
    Acknowledged,

    /// <summary>
    /// The send was cancelled (see Docs/OFT.md §7) before its final packet was written. This is the
    /// terminal status for a cancelled send.
    /// </summary>
    Cancelled,
}
