namespace BlueHeighliner.OpenFrameTransport.Sample;

/// <summary>
/// Creates and caches one <see cref="LagRelay"/> per distinct send target, so repeated sends to the
/// same host/port reuse the same simulated-lag relay instead of opening a new one each time.
/// </summary>
internal sealed class LagRelayManager : IAsyncDisposable
{
    private readonly Dictionary<(string Host, int Port), LagRelay> relays = [];
    private readonly object relaysLock = new();
    private readonly Func<TimeSpan> getLag;

    /// <summary>
    /// Creates a manager whose relays all share the same live lag setting.
    /// </summary>
    /// <param name="getLag">Invoked to determine the current artificial delay for every relay this manager creates.</param>
    public LagRelayManager(Func<TimeSpan> getLag)
    {
        this.getLag = getLag;
    }

    /// <summary>
    /// Gets the loopback host/port a caller should connect to in order to reach
    /// <paramref name="targetHost"/>:<paramref name="targetPort"/> through a lag relay, starting a
    /// new relay for that target if one doesn't already exist.
    /// </summary>
    /// <param name="targetHost">The real host to eventually reach.</param>
    /// <param name="targetPort">The real port to eventually reach.</param>
    /// <returns>The loopback host and port to connect to instead.</returns>
    public (string Host, int Port) GetRelayEndpoint(string targetHost, int targetPort)
    {
        lock (this.relaysLock)
        {
            (string, int) key = (targetHost, targetPort);
            if (!this.relays.TryGetValue(key, out LagRelay? relay))
            {
                relay = LagRelay.Start(targetHost, targetPort, this.getLag);
                this.relays[key] = relay;
            }

            return ("127.0.0.1", relay.Port);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<LagRelay> toDispose;
        lock (this.relaysLock)
        {
            toDispose = [.. this.relays.Values];
            this.relays.Clear();
        }

        foreach (LagRelay relay in toDispose)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
    }
}
