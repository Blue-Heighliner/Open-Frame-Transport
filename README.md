# Open Frame Transport (OFT)

Open Frame Transport (OFT) is an application-layer protocol that runs on top of TCP and TLS. It
provides:

- **Framing** — a byte stream is broken into discrete, length-delimited protobuf messages.
- **Acknowledgement** — every frame that carries application intent is individually acknowledged
  before the next one is sent, giving the connection simple, deterministic flow control.
- **Priority interruption** — an application can have many messages in flight logically at once, and
  a high-priority message can interrupt the transmission of a lower-priority one, which resumes
  automatically once the interruption is finished.
- **Cancellation** — an application can abandon a message it previously queued to send at any time
  before it completes.
- **Security modes** — each connection chooses one of four TLS modes, from no TLS at all up through
  mutual authentication.
- **TLS rekeying** — a connection's TLS session can be rekeyed in place, manually or automatically,
  without reconnecting.
- **Polling** — idle connections are still verified alive on a fixed interval.

Full protocol specification: **[Docs/OFT.md](Docs/OFT.md)**. Implementation architecture and
components: **[Docs/Architecture.md](Docs/Architecture.md)**.

## Implementations

| Language | Location | Docs |
|---|---|---|
| C# (.NET) — reference implementation | [`Core/`](Core) | [Docs/CSharp.md](Docs/CSharp.md) |
| Java | [`Ports/Java/`](Ports/Java) | [Docs/Java.md](Docs/Java.md) |
| C | [`Ports/C/`](Ports/C) | [Docs/C.md](Docs/C.md) |

All three implement the same wire protocol and the same three-component API shape (a connector, a
hoster/listener, and a peer-to-peer convenience layer), verified against each other via real
loopback TCP/TLS tests. Each is idiomatic to its language — see
[Docs/Architecture.md](Docs/Architecture.md) for a full breakdown of what's shared and what's
adapted per language.

An Avalonia sample app demonstrating peer-to-peer messaging with simulated network lag lives under
[`Sample/`](Sample).

## API flow

1. **Server side**: host a listener on a local endpoint, and register a handler for accepted
   connections — inside which you register a handler for received messages on that connection.
2. **Client side**: connect to a remote host/port, and register a handler for received messages on
   the returned connection.
3. **Either side** sends messages on any connection it holds, with an optional priority.
4. **Peer-to-peer**: create a peer, optionally have it also accept inbound connections, and send to
   a host/port directly on the peer — it connects (and caches the connection) the first time and
   reuses it afterward. Messages from every connection the peer holds, inbound or outbound, arrive
   through one handler registered on the peer itself.

**Note:** C#'s events buffer anything raised before their first subscriber, so registering a
received-message handler any time after getting a connection is always safe. Java and C instead
guarantee this via an explicit callback invoked *before* a new connection starts processing inbound
packets — use it (rather than registering afterward) if a peer might reply the instant a connection
is up. See [Docs/Architecture.md](Docs/Architecture.md#where-a-ports-flow-differs-from-this) for
this and other per-port flow differences (blocking vs. async calls, single- vs. multi-subscriber
received notifications).

## Getting started

### Client/server (C#)

```csharp
using OpenFrameTransport;

// Server
IOftListener listener = await new OftHoster().Host(
    new IPEndPoint(IPAddress.Any, 5000),
    new OftHostOptions { Info = "my-server", SecurityMode = OftSecurityMode.Secure });

listener.Connected += (_, e) =>
    e.Connection.Received += (_, msg) => Console.WriteLine(Encoding.UTF8.GetString(msg.Data.Span));

// Client
await using IOftConnection connection = await new OftConnector().Connect(
    "127.0.0.1", 5000,
    new OftConnectOptions { Info = "my-client", SecurityMode = OftSecurityMode.Secure });

await connection.Send(Encoding.UTF8.GetBytes("hello"));
```

### Peer-to-peer (C#)

```csharp
using OpenFrameTransport;

IOftPeer peer = new OftPeerFactory(new OftConnector(), new OftHoster())
    .Create(new OftPeerOptions { Info = "my-peer", SecurityMode = OftSecurityMode.Secure });

peer.Received += (_, msg) => Console.WriteLine(Encoding.UTF8.GetString(msg.Data.Span));

await peer.Open(new IPEndPoint(IPAddress.Any, 5001)); // optional: also accept inbound connections
await peer.Send("127.0.0.1", 5001, Encoding.UTF8.GetBytes("hello"));
```

See [Docs/CSharp.md](Docs/CSharp.md), [Docs/Java.md](Docs/Java.md), and [Docs/C.md](Docs/C.md) for
the equivalent examples in Java and C, plus security-mode configuration, cancellation, rekeying,
memory ownership, and more.

## Repository layout

- [`Core/`](Core) — C# reference implementation.
- [`Ports/Java/`](Ports/Java), [`Ports/C/`](Ports/C) — Java and C ports.
- [`Sample/`](Sample) — Avalonia peer-to-peer sample app (C#).
- [`Tests/`](Tests) — C# test suite; each port also has its own tests (`Ports/Java/src/test`,
  `Ports/C/tests`).
- [`Docs/`](Docs) — protocol specification, architecture, and per-language API reference.

See [`AGENTS.md`](AGENTS.md) for the coding conventions used throughout the implementation,
including the policy that all three ports' APIs stay aligned as much as practical.
