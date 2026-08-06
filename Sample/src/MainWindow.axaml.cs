namespace OpenFrameTransport.Sample;

/// <summary>
/// The sample's single window: hosts an <see cref="IOftListener"/> for inbound connections and uses
/// an <see cref="IOftConnector"/> to dial a fresh outbound connection for each sent message,
/// optionally routed through a <see cref="LagRelay"/> that artificially slows down delivery so
/// priority interruption (see Docs/OFT.md §6) is easy to see happen.
/// </summary>
internal sealed partial class MainWindow : Window
{
    private readonly IOftHoster hoster = new OftHoster();
    private readonly IOftConnector connector = new OftConnector();
    private readonly OftHostOptions hostOptions;
    private readonly OftConnectOptions connectOptions;
    private readonly LagRelayManager lagRelayManager;
    private readonly ObservableCollection<string> logEntries = [];

    private IOftListener? listener;
    private volatile int lagMilliseconds;

    /// <summary>
    /// Creates the window and wires up the UI. Listening on an OS-assigned loopback port starts
    /// once the window opens (see <see cref="OnWindowOpened"/>).
    /// </summary>
    public MainWindow()
    {
        this.InitializeComponent();

        this.LogList.ItemsSource = this.logEntries;
        this.lagRelayManager = new LagRelayManager(() => TimeSpan.FromMilliseconds(this.lagMilliseconds));

        string info = $"oft-sample-{Environment.ProcessId}";

        // The sample only ever talks to other instances of itself using throwaway self-signed
        // certificates, so certificate validation is intentionally disabled here. A real
        // application should validate the peer's certificate.
        RemoteCertificateValidationCallback acceptAnyCertificate = (_, _, _, _) => true;

        // Small on purpose: it makes messages split into several packets at ordinary message
        // sizes, which combined with simulated lag makes priority interruption easy to observe.
        const int maxPacketDataSize = 512;

        this.hostOptions = new OftHostOptions
        {
            Info = info,
            SecurityMode = OftSecurityMode.Authentication,
            ServerCertificate = SampleCertificate.Create(),
            ClientCertificateValidation = acceptAnyCertificate,
            MaxPacketDataSize = maxPacketDataSize,
        };

        this.connectOptions = new OftConnectOptions
        {
            Info = info,
            SecurityMode = OftSecurityMode.Authentication,
            ServerCertificateValidation = acceptAnyCertificate,
            MaxPacketDataSize = maxPacketDataSize,
        };

        this.Opened += this.OnWindowOpened;
        this.Closed += this.OnWindowClosed;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        this.listener = await this.hoster.Host(new IPEndPoint(IPAddress.Loopback, 0), this.hostOptions).ConfigureAwait(true);
        this.listener.Connected += this.OnInboundConnected;
        this.ListenAddressText.Text = $"Listening on: {this.listener.LocalEndPoint}";
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        await this.lagRelayManager.DisposeAsync().ConfigureAwait(false);

        if (this.listener is not null)
        {
            await this.listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnLagSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        this.lagMilliseconds = (int)e.NewValue;
        this.LagValueText.Text = $"{this.lagMilliseconds} ms";
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        string host = this.HostBox.Text?.Trim() ?? string.Empty;
        string message = this.MessageBox.Text ?? string.Empty;
        int priority = (int)(this.PriorityBox.Value ?? 0);
        int padToSize = (int)(this.PadToSizeBox.Value ?? 0);

        if (host.Length == 0 || !int.TryParse(this.PortBox.Text, out int port))
        {
            this.AppendLog("Enter a valid host and port before sending.");
            return;
        }

        byte[] payload = BuildPayload(message, padToSize);

        try
        {
            (string RelayHost, int RelayPort) relay = this.lagRelayManager.GetRelayEndpoint(host, port);
            this.AppendLog($"Sending {payload.Length} byte(s) to {host}:{port} at priority {priority} (lag {this.lagMilliseconds} ms)...");

            // A fresh connection per send, closed again once it's done - the connector itself
            // caches nothing (see IOftConnector), unlike the connection-reusing IOftPeer this
            // sample used to send through.
            await using IOftConnection connection = await this.connector.Connect(relay.RelayHost, relay.RelayPort, this.connectOptions).ConfigureAwait(true);
            await connection.Send(payload, priority).ConfigureAwait(true);

            this.AppendLog($"Sent {payload.Length} byte(s) to {host}:{port} at priority {priority}.");
        }
        catch (Exception exception)
        {
            this.AppendLog($"Failed to send to {host}:{port}: {exception.Message}");
        }
    }

    private void OnInboundConnected(object? sender, OftConnectedEventArgs e)
    {
        e.Connection.Received += this.OnConnectionReceived;
    }

    private void OnConnectionReceived(object? sender, OftReceivedEventArgs e)
    {
        string preview = DescribePayload(e.Data.Span);

        Dispatcher.UIThread.Post(() => this.AppendLog($"Received {e.Data.Length} byte(s): {preview}"));
    }

    private void AppendLog(string entry)
    {
        this.logEntries.Add($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {entry}");

        if (this.logEntries.Count > 0)
        {
            this.LogList.ScrollIntoView(this.logEntries[^1]);
        }
    }

    private static byte[] BuildPayload(string message, int padToSize)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(message);
        if (padToSize <= textBytes.Length)
        {
            return textBytes;
        }

        byte[] payload = new byte[padToSize];
        textBytes.CopyTo(payload, 0);
        return payload;
    }

    private static string DescribePayload(ReadOnlySpan<byte> data)
    {
        const int previewLength = 80;
        ReadOnlySpan<byte> visible = data.Length > previewLength ? data[..previewLength] : data;
        string text = Encoding.UTF8.GetString(visible).Replace('\0', ' ').TrimEnd();
        return data.Length > previewLength ? $"{text}…" : text;
    }
}
