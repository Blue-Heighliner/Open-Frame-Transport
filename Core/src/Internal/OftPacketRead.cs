namespace BlueHeighliner.OpenFrameTransport.Internal;

/// <summary>
/// What <see cref="OftFrameStream.ReadPacketOrPoll"/> read.
/// </summary>
internal enum OftPacketReadKind
{
    /// <summary>The stream ended cleanly on a message boundary.</summary>
    Closed,

    /// <summary>A zero-length frame was read: a <c>Poll</c> (see Docs/OFT.md §10).</summary>
    Poll,

    /// <summary>A <see cref="Packet"/> was read; see <see cref="OftPacketRead.Packet"/>.</summary>
    Message,
}

/// <summary>
/// The result of <see cref="OftFrameStream.ReadPacketOrPoll"/>.
/// </summary>
internal readonly record struct OftPacketRead
{
    private OftPacketRead(OftPacketReadKind kind, Packet? packet)
    {
        this.Kind = kind;
        this.Packet = packet;
    }

    /// <summary>The stream ended cleanly on a message boundary.</summary>
    public static OftPacketRead Closed { get; } = new(OftPacketReadKind.Closed, null);

    /// <summary>A zero-length frame was read: a <c>Poll</c> (see Docs/OFT.md §10).</summary>
    public static OftPacketRead Poll { get; } = new(OftPacketReadKind.Poll, null);

    /// <summary>A <see cref="Packet"/> was read.</summary>
    /// <param name="packet">The packet that was read.</param>
    public static OftPacketRead Of(Packet packet) => new(OftPacketReadKind.Message, packet);

    /// <summary>Which of the three outcomes this is.</summary>
    public OftPacketReadKind Kind { get; }

    /// <summary>The packet that was read, when <see cref="Kind"/> is <see cref="OftPacketReadKind.Message"/>; otherwise <see langword="null"/>.</summary>
    public Packet? Packet { get; }
}
