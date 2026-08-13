# Open Frame Transport (OFT)

[![NuGet](https://img.shields.io/nuget/v/BlueHeighliner.OpenFrameTransport.svg?label=NuGet)](https://www.nuget.org/packages/BlueHeighliner.OpenFrameTransport)
[![License: MIT](https://img.shields.io/github/license/Blue-Heighliner/Open-Frame-Transport.svg)](LICENSE)
[![C#](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/csharp.yml/badge.svg)](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/csharp.yml)
[![Java](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/java.yml/badge.svg)](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/java.yml)
[![C](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/c.yml/badge.svg)](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/c.yml)
[![Rust](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/rust.yml/badge.svg)](https://github.com/Blue-Heighliner/Open-Frame-Transport/actions/workflows/rust.yml)

Open Frame Transport (OFT) is an application-layer protocol that runs on top of TCP and TLS. It
provides:

- **Framing** — a byte stream is broken into discrete messages.
- **Acknowledgement** — every packet is individually acknowledged before the next one is sent, giving the connection simple, deterministic flow control.
- **Priority interruption** — an application can have many messages in flight logically at once, and
  a high-priority message can interrupt the transmission of a lower-priority one, which resumes
  automatically once the interruption is finished.
- **Cancellation** — an application can cancel a message it previously queued to send at any time
  before it completes.
- **Security modes** — each connection chooses one of four TLS modes, from no TLS at all up to
  mutual authentication.
- **TLS rekeying** — a connection's TLS session can be rekeyed in place, manually or automatically,
  without reconnecting.
- **Polling** — idle connections are verified alive on a fixed interval.

Full protocol specification: **[Docs/OFT.md](Docs/OFT.md)**. Implementation architecture and
components: **[Docs/Architecture.md](Docs/Architecture.md)**.

## Implementations

| Language | Location | Docs |
|---|---|---|
| C# (.NET) — reference implementation | [`Core/`](Core) | [Docs/CSharp.md](Docs/CSharp.md) |
| Java | [`Ports/Java/`](Ports/Java) | [Docs/Java.md](Docs/Java.md) |
| C | [`Ports/C/`](Ports/C) | [Docs/C.md](Docs/C.md) |
| Rust | [`Ports/Rust/`](Ports/Rust) | [Docs/Rust.md](Docs/Rust.md) |

All four implement the same wire protocol and the same three-component API shape (a connector, a
hoster/listener, and a peer-to-peer convenience layer), verified against each other via real
loopback TCP/TLS tests. Each is idiomatic to its language — see
[Docs/Architecture.md](Docs/Architecture.md) for a full breakdown of what's shared and what's
adapted per language.

An Avalonia sample app demonstrating peer-to-peer messaging with simulated network lag lives under
[`Sample/`](Sample).

## API flow

1. **Server side**: host a listener on a local endpoint, and assign a callback for accepted
   connections — inside which you assign a callback for received messages on that connection.
2. **Client side**: connect to a remote host/port, and assign a callback for received messages on
   the returned connection.
3. **Either side** sends messages on any connection it holds, with an optional priority.
4. **Peer-to-peer**: create a peer, optionally have it also accept inbound connections, and send to
   a host/port directly on the peer — it connects (and caches the connection) the first time and
   reuses it afterward. Messages from every connection the peer holds, inbound or outbound, arrive
   through one callback assigned on the peer itself.

All four ports use the same shape: a connection has an independent, single-slot callback for
received messages and one for disconnection; a listener has one for newly accepted connections; a
peer has one for messages received on any connection it holds (identifying which one) — a plain
`Action<T>` property in C#, a `set*Handler(Consumer<T>)` method in Java, a `oft_*_set_*_callback()`
function taking a function pointer in C, a `set_*_handler(Option<Arc<dyn Fn(...)>>)` method in Rust.
Assigning a new callback always replaces any previous one, so exactly one recipient is ever notified
of a given message, and each notification kind is assigned independently of the others. A peer
deliberately has no disconnected or connected callback of its own — connection lifecycle is its own
implementation detail, transparently managed behind its send method.

**Note:** all four ports buffer anything raised before a connection/listener/peer's first callback
is assigned, so assigning one (received, disconnected, or connected) any time after getting a
connection or listener is always safe, even if a peer replies the instant a connection is up. See
[Docs/Architecture.md](Docs/Architecture.md#buffered-notifications-prevent-a-connectdisconnectreceive-message-loss-race)
for details, and for other per-port flow differences (mainly blocking vs. async calls).

## Getting started

### CSharp

#### Client/server

```csharp
using BlueHeighliner.OpenFrameTransport;

// Server
IOftListener listener = await new OftHoster().Host(5000);
listener.ConnectedHandler = connection => connection.ReceivedHandler = data => Console.WriteLine(Encoding.UTF8.GetString(data.Memory.Span));

// Client
using IOftConnection connection = await new OftConnector().Connect("127.0.0.1", 5000);
await connection.Send(Encoding.UTF8.GetBytes("hello"));
```

#### Peer-to-peer

```csharp
using BlueHeighliner.OpenFrameTransport;

IOftPeer peer = new OftPeerFactory().Create();
peer.ReceivedHandler = (identity, data) => Console.WriteLine(Encoding.UTF8.GetString(data.Memory.Span));

await peer.Listen(new IPEndPoint(IPAddress.Any, 5001)); // optional: also accept inbound connections
await peer.Send("127.0.0.1", 5001, Encoding.UTF8.GetBytes("hello"));
```

### Java

#### Client/server

```java
import org.blueheighliner.openframetransport.*;

// Server
OftListener listener = OftHoster.create().host(5000).get();
listener.setConnectedHandler(connection -> connection.setReceivedHandler(data -> System.out.println(new String(data))));

// Client
OftConnection connection = OftConnector.create().connect("127.0.0.1", 5000).get();
connection.send("hello".getBytes(), 0, null);
```

#### Peer-to-peer

```java
import org.blueheighliner.openframetransport.*;

OftPeer peer = OftPeer.create(OftPeerOptions.builder().build());
peer.setReceivedHandler((identity, data) -> System.out.println(new String(data)));

peer.listen(new InetSocketAddress("0.0.0.0", 5001)).get(); // optional: also accept inbound connections
peer.send("127.0.0.1", 5001, "hello".getBytes(), 0, null);
```

### C

#### Client/server

```c
#include "oft/oft.h"

static void on_received(oft_connection *connection, uint8_t *data, size_t length, void *user_data) {
    printf("%.*s\n", (int)length, data);
    free(data); // ownership passes to this callback
}

static void on_connected(oft_listener *listener, oft_connection *connection, void *user_data) {
    oft_connection_set_received_callback(connection, on_received, NULL);
}

char error_buffer[256];

// Server
oft_listener *listener = oft_host("0.0.0.0", 5000, NULL, NULL, error_buffer, sizeof(error_buffer));
oft_listener_set_connected_callback(listener, on_connected, NULL);

// Client
oft_connection *connection = oft_connect("127.0.0.1", 5000, NULL, NULL, error_buffer, sizeof(error_buffer));

uint64_t message_id;
oft_connection_send(connection, (const uint8_t *)"hello", 5, 0, NULL, &message_id);
```

#### Peer-to-peer

```c
#include "oft/oft_peer.h"

static void on_peer_received(const oft_identity *identity, uint8_t *data, size_t length, void *user_data) {
    printf("%.*s\n", (int)length, data);
    free(data); // ownership passes to this callback
}

oft_peer_options options = {0};
oft_peer *peer = oft_peer_create(&options);
oft_peer_set_received_callback(peer, on_peer_received, NULL);

char error_buffer[256];
oft_peer_listen(peer, "0.0.0.0", 5001, error_buffer, sizeof(error_buffer)); // optional: also accept inbound connections

uint64_t message_id;
oft_peer_send(peer, "127.0.0.1", 5001, (const uint8_t *)"hello", 5, 0, NULL, NULL, &message_id, error_buffer, sizeof(error_buffer));
```

### Rust

#### Client/server

```rust
use oft::{connect, host};
use std::sync::Arc;

// Server
let listener = host("0.0.0.0", 5000, None)?;
listener.set_connected_handler(Some(Arc::new(|connection: oft::Connection| {
    connection.set_received_handler(Some(Arc::new(|data: Vec<u8>| println!("{}", String::from_utf8_lossy(&data)))));
})));

// Client
let connection = connect("127.0.0.1", 5000, None)?;
connection.send(b"hello".to_vec(), 0, None).wait()?;
# Ok::<(), Box<dyn std::error::Error>>(())
```

#### Peer-to-peer

```rust
use oft::Peer;
use std::sync::Arc;

let peer = Peer::new(None)?;
peer.set_received_handler(Some(Arc::new(|identity: oft::Identity, data: Vec<u8>| println!("{}", String::from_utf8_lossy(&data)))));

peer.listen("0.0.0.0", 5001)?; // optional: also accept inbound connections
peer.send("127.0.0.1", 5001, b"hello".to_vec(), 0, None)?.wait()?;
# Ok::<(), Box<dyn std::error::Error>>(())
```

See [Docs/CSharp.md](Docs/CSharp.md), [Docs/Java.md](Docs/Java.md), [Docs/C.md](Docs/C.md), and
[Docs/Rust.md](Docs/Rust.md) for more detail on each of these examples, plus security-mode
configuration, cancellation, rekeying, memory ownership, and more.

## Repository layout

- [`Core/`](Core) — C# reference implementation.
- [`Ports/Java/`](Ports/Java), [`Ports/C/`](Ports/C), [`Ports/Rust/`](Ports/Rust) — Java, C, and
  Rust ports.
- [`Sample/`](Sample) — Avalonia peer-to-peer sample app (C#).
- [`Tests/`](Tests) — C# test suite; each port also has its own tests (`Ports/Java/src/test`,
  `Ports/C/tests`, `Ports/Rust/tests`).
- [`Docs/`](Docs) — protocol specification, architecture, and per-language API reference.
- [`.github/workflows/`](.github/workflows) — per-port CI (build/test on every push and PR) and
  release-triggered publishing: C#/Java to GitHub Packages (NuGet/Maven), C/Rust as release assets
  (a tarball and a `.crate` archive, respectively — GitHub Packages has no registry type for either).

See [`AGENTS.md`](AGENTS.md) for the coding conventions used throughout the implementation,
including the policy that all ports' APIs stay aligned as much as practical.

## License

[MIT](LICENSE)
