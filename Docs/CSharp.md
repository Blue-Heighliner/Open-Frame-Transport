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
- `OftConnectionOptions` — options for an individual connection, used both to connect
  (`IOftConnector.Connect`) and to host (`IOftHoster.Host`): `Info`, `Certificate`,
  `CertificateValidation`, `ConnectionValidation`, `MaxPacketDataSize`, `RekeyInterval`,
  `SecurityMode`, `PollInterval`, `PollTimeout`.
- `OftPeerOptions` — options for an `IOftPeer`, covering both its outbound and inbound connections
  at once; extends `OftConnectionOptions` with peer-specific eviction settings (`IdleTimeout`,
  `MaxConnectionLifetime`, `MaxConnectionCount`). A connection with pending data
  (`IOftConnection.HasPendingData`) is never auto-evicted for any of these reasons, and even once
  clear, must stay clear for a fixed 30-second grace period (not configurable — see `IOftPeer`'s own
  doc comment) before it becomes eligible; eviction itself is also only ever checked on a fixed,
  non-configurable 30-second interval, so `IdleTimeout`/`MaxConnectionLifetime` can't effectively take
  effect any sooner than that combined ~30-60 second floor.
- `OftSecurityMode` — `Trusted` / `Secure` / `ServerAuthentication` / `DualAuthentication` (see
  [OFT.md §9](OFT.md#9-security-modes)). `ServerAuthentication` is rejected by
  `IOftPeerFactory.Create` — a peer has no client/server delineation, so use `DualAuthentication`
  instead.
- `IOftConnection.Identity` — an `OftIdentity` record describing the connection's remote side:
  `EndPoint`, `Certificate` (an `OftCertificateIdentity?`, present only if the remote side presented a
  TLS certificate), and `Info` (the opaque hail data).
- `OftCertificateIdentity` — `Name`/`Issuer` (the Common Name of a certificate's subject/issuer) and
  `AlternativeNames`, extracted from an `X509Certificate2` via `OftCertificateIdentity.FromCertificate`.
- `IOftPeerReception` — the type delivered to `IOftPeer.ReceivedHandler`: `Data` (the message payload)
  and `Identity` (the sending connection's `OftIdentity`), backed by pooled memory the callback must
  dispose.
- `IOftConnection.ReceivedHandler`/`.DisconnectedHandler`, `IOftListener.ConnectedHandler`,
  `IOftPeer.ReceivedHandler` — single-slot `Action<T>` properties assigned directly (no handler-object
  interface to implement), one per notification kind. Assigning a new value (including `null`) always
  replaces any previous one, and each notification kind is assigned independently of the others.
  `IOftPeer` has no `DisconnectedHandler`/`ConnectedHandler` of its own — see its own type doc comment
  for why.
- `IOftConnection`/`IOftListener`/`IOftPeer` implement `IDisposable`, not `IAsyncDisposable`:
  `Dispose()` immediately terminates whatever it's called on and releases its resources, without
  waiting for any background work to finish. Each has its own separate `async` method for a graceful,
  awaitable teardown instead — see [Disposal vs. graceful teardown](#disposal-vs-graceful-teardown).
- `IOftConnection.IsConnected`/`IOftPeer.IsConnected` — `true` until permanently disconnected, after
  which `Send`/`Rekey` throw `OftDisconnectedException`. See
  [Disposal vs. graceful teardown](#disposal-vs-graceful-teardown) for the full state machine and
  `IOftPeer.Drop`, which disconnects a peer's held connections without affecting `IsConnected`.

## Client/server example

```csharp
using BlueHeighliner.OpenFrameTransport;

// --- Server side ---
IOftHoster hoster = new OftHoster();

OftConnectionOptions hostOptions = new()
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

OftConnectionOptions connectOptions = new()
{
    Info = "my-client",
    SecurityMode = OftSecurityMode.Secure,
};

using IOftConnection connection = await connector.Connect("127.0.0.1", 5000, connectOptions);
await connection.Send(Encoding.UTF8.GetBytes("hello"), priority: 0);
```

`options` is optional on both `Connect` and `Host` — omit it (or pass `null`) to use defaults
(`SecurityMode = Secure`, `Info = string.Empty`, 1 KiB max packet size, 1s/5s poll interval/timeout).

## Remote identity

```csharp
OftIdentity identity = connection.Identity;
Console.WriteLine($"Remote endpoint: {identity.EndPoint}");
Console.WriteLine($"Hail info: {identity.Info}");

if (identity.Certificate is { } certificate)
{
    Console.WriteLine($"Certificate subject CN: {certificate.Name}");
    Console.WriteLine($"Certificate issuer CN: {certificate.Issuer}");
    Console.WriteLine($"Certificate SANs: {string.Join(", ", certificate.AlternativeNames)}");
}
```

`Identity.Certificate` is `null` for a connection established with `OftSecurityMode.Trusted` (no TLS
at all), and also `null` on the accepting side of a connection established under a mode that never
requests a certificate from the connecting side (see `OftSecurityMode.DualAuthentication`).

## Peer-to-peer example

```csharp
using BlueHeighliner.OpenFrameTransport;

IOftPeerFactory peerFactory = new OftPeerFactory();

IOftPeer peer = peerFactory.Create(new OftPeerOptions
{
    Info = "my-peer",
    SecurityMode = OftSecurityMode.Secure,
});

peer.ReceivedHandler = reception =>
{
    using (reception)
    {
        string text = Encoding.UTF8.GetString(reception.Data.Span);
        Console.WriteLine($"Received from {reception.Identity.EndPoint}: {text}");
    }
};

// Optional: also accept inbound connections into the same pool.
await peer.Listen(new IPEndPoint(IPAddress.Any, 5001));

// Sending to a host:port transparently reuses a cached connection or creates and caches a new one.
await peer.Send("127.0.0.1", 5001, Encoding.UTF8.GetBytes("hello"), priority: 0);

peer.Dispose();
```

`IOftPeer.ReceivedHandler` delivers an `IOftPeerReception` — its `Identity` (an `OftIdentity`) is only
for identifying which connection a message arrived on, e.g. to decide how to respond via `Send`; a
peer deliberately exposes no other way to enumerate, look up, or be notified about the individual
connections it holds (there is no `IOftPeer.DisconnectedHandler`/`ConnectedHandler`): connection
lifecycle is the peer's own implementation detail, transparently managed (reconnecting, evicting,
etc.) behind `Send`.

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

`IOftConnection.ReceivedHandler` delivers received data as an `IMemoryOwner<byte>` directly — it's
pooled, and the callback owns it: disposing it promptly returns the memory to its pool, though this
is optional (skipping it just means the memory isn't reused, with no correctness impact):

```csharp
connection.ReceivedHandler = data =>
{
    using (data)
    {
        Process(data.Memory.Span);
    }
};
```

`IOftPeer.ReceivedHandler` delivers the same pooled memory wrapped in an `IOftPeerReception` instead
(see the peer-to-peer example above) — its `Data` property exposes the payload, and disposing the
`IOftPeerReception` itself (rather than `Data` directly, which has no `Dispose` of its own) returns the
underlying memory to its pool.

## Disposal vs. graceful teardown

`IOftConnection`, `IOftListener`, and `IOftPeer` all implement `IDisposable`, not
`IAsyncDisposable`: `Dispose()` immediately terminates whatever it's called on — cancelling background
work, releasing sockets and other resources — without waiting for that background work to actually
finish running, which happens shortly afterward on its own. `IOftConnection`/`IOftPeer` additionally
expose their own `async Disconnect()` method for a graceful, awaitable teardown that does wait; both
`Dispose()` and `Disconnect()` immediately and synchronously put their target into a permanently
disconnected state (`IsConnected` becomes `false`) before any of that background work actually
finishes, not just once it does:

```csharp
// Immediate: IsConnected is already false by the time this returns; background work stops shortly after.
connection.Dispose();

// Graceful: also immediate about IsConnected, but doesn't return until background work has fully stopped.
await connection.Disconnect();
```

`IOftListener` has no separate graceful-teardown method — stopping a listener has no in-flight work of
its own to wait for, so `Dispose()` alone is already a complete, immediate teardown.

`IOftPeer.Disconnect()`/`Dispose()` follow the same immediate-vs-graceful split as
`IOftConnection`, but at the peer level: both stop listening (if applicable), disconnect every
connection the peer currently holds, and permanently set `IsConnected` to `false` — after which every
other member throws (`Listen`/`StopListening`/`Drop` throw `ObjectDisposedException`; `Send`/`Rekey` throw
`OftDisconnectedException`, since those two can also fail for the unremarkable reason that the peer
was never disconnected locally but simply lost its last connection). Both are idempotent — calling
either again after the first is a no-op.

`IOftPeer.Drop()` is different: it disconnects every connection the peer currently holds - the same
work `Disconnect()`/`Dispose()` do - but leaves the peer itself usable, `IsConnected` still `true`.
Use `Drop()` to force every cached connection to be re-established from scratch (e.g. after a network
change) without tearing the peer down; use `Disconnect()`/`Dispose()` to actually retire the peer.

```csharp
await peer.Drop(); // Forces reconnection on the next Send; the peer itself remains usable.

await peer.Disconnect(); // Permanently retires the peer; every member but IsConnected now throws.
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
OftConnectionOptions options = new() { Info = "my-client", RekeyInterval = TimeSpan.FromMinutes(10) };
```

`Rekey()` is a no-op (returns a completed task immediately) if the connection was established with
`OftSecurityMode.Trusted` — there's no TLS session to rekey. `IOftPeer.Rekey()`/`.Drop()` act
on every connection the peer currently holds, both inbound and outbound, at once.

## Security modes

```csharp
// ServerAuthentication (one-way TLS): the server presents a real certificate.
OftConnectionOptions hostOptions = new()
{
    Info = "my-server",
    SecurityMode = OftSecurityMode.ServerAuthentication,
    Certificate = myServerCertificate, // X509Certificate2
};

OftConnectionOptions connectOptions = new()
{
    Info = "my-client",
    SecurityMode = OftSecurityMode.ServerAuthentication,
    CertificateValidation = (sender, cert, chain, errors) => /* custom validation */ true,
};

// DualAuthentication (mutual TLS): the client also presents a certificate. The only authenticating
// mode IOftPeer supports — ServerAuthentication above is only valid for IOftConnector/IOftHoster.
OftConnectionOptions mutualOptions = new()
{
    Info = "my-client",
    SecurityMode = OftSecurityMode.DualAuthentication,
    Certificate = myClientCertificate, // X509Certificate2
};
```

`CertificateValidation` is only consulted under `ServerAuthentication`/`DualAuthentication` (on
`OftConnectionOptions` used to connect) or `DualAuthentication` (on `OftConnectionOptions` used to
host); under `Secure`, the peer's ephemeral certificate is accepted unconditionally regardless of any
callback supplied. See [OFT.md §9](OFT.md#9-security-modes) for the full semantics of each mode.

`OftConnectionOptions` also has a `ConnectionValidation` option: an optional, `async`
`OftConnectionValidationCallback` invoked once the OFT hail exchange completes, for every security
mode (including `Trusted` and `Secure`, where its `certificate`/`chain` parameters are always `null`)
- unlike `CertificateValidation`, which only runs during the TLS handshake itself and never sees the
connection's `OftIdentity`:

```csharp
OftConnectionOptions connectOptions = new()
{
    Info = "my-client",
    ConnectionValidation = async (identity, certificate, chain, sslErrors) =>
    {
        // certificate/chain are already accepted by CertificateValidation (or the default .NET
        // chain validation) by the time this runs; sslErrors mirrors what that validation found.
        return true; // or false to reject the connection
    },
};
```

`null` (the default) accepts every connection; returning `false` fails `Connect`/`Host` with an
`AuthenticationException`.

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
