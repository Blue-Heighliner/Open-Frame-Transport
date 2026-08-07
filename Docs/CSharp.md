# Open Frame Transport — C# (reference implementation)

The [`Core/`](../Core) project is the C# reference implementation of [OFT](OFT.md). See
[Architecture.md](Architecture.md) for how its components relate to the other ports; this document
covers the C#-specific API in detail, with examples.

## Types

- `IOftConnector` / `OftConnector` — dials outbound connections.
- `IOftHoster` / `OftHoster` — hosts inbound listeners.
- `IOftListener` — a listener returned by `IOftHoster.Host`.
- `IOftConnection` — a single established connection, produced by either of the above.
- `IOftPeer` / `IOftPeerFactory` / `OftPeerFactory` — the peer-to-peer convenience layer.
- `OftConnectOptions`, `OftHostOptions`, `OftPeerOptions` — per-role options records, all deriving
  shared settings (`Info`, `MaxPacketDataSize`, `RekeyInterval`, `SecurityMode`, `PollInterval`,
  `PollTimeout`) from `OftConnectionOptions`.
- `OftSecurityMode` — `Trusted` / `Secure` / `ServerAuthentication` / `DualAuthentication` (see
  [OFT.md §9](OFT.md#9-security-modes)). `ServerAuthentication` is rejected by
  `IOftPeerFactory.Create` — a peer has no client/server delineation, so use `DualAuthentication`
  instead.
- `IOftConnection.ReceivedHandler`/`.DisconnectedHandler`, `IOftListener.ConnectedHandler`,
  `IOftPeer.ReceivedHandler` — single-slot `Action<T>` properties assigned directly (no handler-object
  interface to implement), one per notification kind. Assigning a new value (including `null`) always
  replaces any previous one, and each notification kind is assigned independently of the others.
  `IOftPeer` has no `DisconnectedHandler`/`ConnectedHandler` of its own — see its own type doc comment
  for why.

## Client/server example

```csharp
using BlueHeighliner.OpenFrameTransport;

// --- Server side ---
IOftHoster hoster = new OftHoster();

OftHostOptions hostOptions = new()
{
    Info = "my-server",
    SecurityMode = OftSecurityMode.Secure, // no certificate needed for this example
};

IOftListener listener = await hoster.Host(new IPEndPoint(IPAddress.Any, 5000), hostOptions);

listener.ConnectedHandler = connection =>
{
    connection.ReceivedHandler = data =>
    {
        string text = Encoding.UTF8.GetString(data.Memory.Span);
        Console.WriteLine($"Received: {text}");
    };
};

// --- Client side ---
IOftConnector connector = new OftConnector();

OftConnectOptions connectOptions = new()
{
    Info = "my-client",
    SecurityMode = OftSecurityMode.Secure,
};

await using IOftConnection connection = await connector.Connect("127.0.0.1", 5000, connectOptions);
await connection.Send(Encoding.UTF8.GetBytes("hello"), priority: 0);
```

`options` is optional on both `Connect` and `Host` — omit it (or pass `null`) to use defaults
(`SecurityMode = Secure`, `Info = string.Empty`, 1 KiB max packet size, 1s/5s poll interval/timeout).

## Peer-to-peer example

```csharp
using BlueHeighliner.OpenFrameTransport;

IOftPeerFactory peerFactory = new OftPeerFactory();

IOftPeer peer = peerFactory.Create(new OftPeerOptions
{
    Info = "my-peer",
    SecurityMode = OftSecurityMode.Secure,
});

peer.ReceivedHandler = (connection, data) =>
{
    string text = Encoding.UTF8.GetString(data.Memory.Span);
    Console.WriteLine($"Received: {text}");
};

// Optional: also accept inbound connections into the same pool.
await peer.Open(new IPEndPoint(IPAddress.Any, 5001));

// Sending to a host:port transparently reuses a cached connection or creates and caches a new one.
await peer.Send("127.0.0.1", 5001, Encoding.UTF8.GetBytes("hello"), priority: 0);

await peer.DisposeAsync();
```

`IOftPeer.ReceivedHandler`'s `connection` argument is only for replying on the same connection a
message arrived on — a peer deliberately exposes no other way to enumerate, look up, or be notified
about the individual connections it holds (there is no `IOftPeer.DisconnectedHandler`/
`ConnectedHandler`): connection lifecycle is the peer's own implementation detail, transparently
managed (reconnecting, evicting, etc.) behind `Send`.

## Sending with pooled memory

`IOftConnection.Send` has two overloads: one that copies a `ReadOnlyMemory<byte>` (used above), and
one that takes ownership of an `IMemoryOwner<byte>` (e.g. rented from `MemoryPool<byte>.Shared`),
avoiding a copy for callers already using pooled buffers:

```csharp
IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(payload.Length);
payload.CopyTo(owner.Memory.Span);

// The connection disposes `owner` once the send completes, is cancelled, or the connection closes -
// do not use or dispose it yourself afterward.
await connection.Send(owner, priority: 5);
```

`ReceivedHandler`/`IOftPeer.ReceivedHandler` deliver received data as an `IMemoryOwner<byte>`
directly — it's pooled, and the callback owns it: disposing it promptly returns the memory to its
pool, though this is optional (skipping it just means the memory isn't reused, with no correctness
impact):

```csharp
connection.ReceivedHandler = data =>
{
    using (data)
    {
        Process(data.Memory.Span);
    }
};
```

## Cancellation

Every `Send` overload takes a `CancellationToken`. Cancelling it before the message has started
sending abandons it immediately; cancelling after it has started sending a multi-packet message
sends a `Cancellation` packet (see [OFT.md §7](OFT.md#7-cancellation-from-the-applications-perspective)):

```csharp
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
try
{
    await connection.Send(payload, priority: 0, cts.Token);
}
catch (OperationCanceledException)
{
    // The message was abandoned or a Cancellation packet was sent for it.
}
```

## Rekeying

```csharp
// Manual, on either side, at any time:
await connection.Rekey();

// Or automatic, via options:
OftConnectOptions options = new() { Info = "my-client", RekeyInterval = TimeSpan.FromMinutes(10) };
```

`Rekey()` is a no-op (returns a completed task immediately) if the connection was established with
`OftSecurityMode.Trusted` — there's no TLS session to rekey. `IOftPeer.Rekey()`/`.Disconnect()` act
on every connection the peer currently holds, both inbound and outbound, at once.

## Security modes

```csharp
// ServerAuthentication (one-way TLS): the server presents a real certificate.
OftHostOptions hostOptions = new()
{
    Info = "my-server",
    SecurityMode = OftSecurityMode.ServerAuthentication,
    ServerCertificate = myServerCertificate, // X509Certificate2
};

OftConnectOptions connectOptions = new()
{
    Info = "my-client",
    SecurityMode = OftSecurityMode.ServerAuthentication,
    ServerCertificateValidation = (sender, cert, chain, errors) => /* custom validation */ true,
};

// DualAuthentication (mutual TLS): the client also presents a certificate. The only authenticating
// mode IOftPeer supports — ServerAuthentication above is only valid for IOftConnector/IOftHoster.
OftConnectOptions mutualOptions = new()
{
    Info = "my-client",
    SecurityMode = OftSecurityMode.DualAuthentication,
    ClientCertificates = new X509CertificateCollection { myClientCertificate },
};
```

`ServerCertificateValidation`/`ClientCertificateValidation` are only consulted under
`ServerAuthentication`/`DualAuthentication`; under `Secure`, the peer's ephemeral certificate is
accepted unconditionally regardless of any callback supplied. See
[OFT.md §9](OFT.md#9-security-modes) for the full semantics of each mode.

## Dependency injection

`AddOpenFrameTransport()` registers `IOftConnector`, `IOftHoster`, and `IOftPeerFactory` into an
`IServiceCollection` by convention (every public `IThing` in the assembly resolves to the public
`Thing` that implements it — no explicit registration needed):

```csharp
using Microsoft.Extensions.DependencyInjection;
using BlueHeighliner.OpenFrameTransport;

ServiceCollection services = new();
services.AddOpenFrameTransport();
ServiceProvider provider = services.BuildServiceProvider();

IOftConnector connector = provider.GetRequiredService<IOftConnector>();
IOftPeerFactory peerFactory = provider.GetRequiredService<IOftPeerFactory>();
```

Without an IoC container, `new OftPeerFactory()` (no arguments) builds a factory backed by a plain
`OftConnector`/`OftHoster`, equivalent to `new OftPeerFactory(new OftConnector(), new OftHoster())`.

## Testing and coverage

Tests use xUnit against real loopback TCP/TLS connections (`Tests/`). Coverage is collected with
`coverlet.collector` and reported with `reportgenerator`:

```
dotnet test Tests/OpenFrameTransport.Tests.csproj --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory Tests/TestResults
dotnet tool run reportgenerator -reports:"Tests/TestResults/**/coverage.cobertura.xml" -targetdir:Tests/TestResults/report -reporttypes:Html
```

See [`AGENTS.md`](../AGENTS.md) for the coding conventions used throughout this project.
