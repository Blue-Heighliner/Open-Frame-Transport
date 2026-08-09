# Open Frame Transport — Rust

A Rust implementation of [OFT](OFT.md) under [`Ports/Rust/`](../Ports/Rust). See
[Architecture.md](Architecture.md) for how its components relate to the other ports (notably the
blocking call style, shared with Java/C); this document covers the Rust-specific API in detail,
with examples.

This port depends on [`rustls`](https://docs.rs/rustls) (a pure-Rust, memory-safe TLS
implementation) and [`rcgen`](https://docs.rs/rcgen) (ephemeral certificate generation under
`SecurityMode::Secure`); [`rustls-native-certs`](https://docs.rs/rustls-native-certs) supplies the
platform trust store fallback for `SecurityMode::ServerAuthentication`. It hand-rolls its own
Protocol Buffers wire codec for `Hail`/`Packet` (see [`OFT.proto`](OFT.proto)) rather than
depending on a third-party protobuf runtime or `protoc`, matching the C port's own approach — both
messages are small and fixed (two fields each), so this keeps the crate's dependencies limited to
what the TLS layer genuinely needs.

## Types

- `connect(host, port, options)` — dials outbound connections. `options: Option<ConnectionOptions>`.
- `host(bind_host, bind_port, options)` — starts an inbound listener.
- `Listener` — a listener returned by `host()`.
- `Connection` — a single established connection, produced by either of the above. A cheap `Clone`
  handle (internally `Arc`-backed) over the connection's actual state.
- `Peer` — the peer-to-peer convenience layer. `Peer::new(options)`.
- `ConnectionOptions` — options for an individual connection, used both to connect and to host:
  `info`, `security_mode`, `identity`, `root_certificates`, `max_packet_data_size`,
  `rekey_interval`, `poll_interval`, `poll_timeout`, `connection_validation`. A plain, `Clone`
  struct constructed with `ConnectionOptions { field: value, ..Default::default() }`.
- `PeerOptions` — wraps a `ConnectionOptions` (its `connection` field) plus peer-specific eviction
  settings: `idle_timeout`, `max_connection_lifetime`, `max_connection_count`. Implements
  `Deref`/`DerefMut` to `ConnectionOptions`, so `peer_options.info = "x".into()` works directly
  without going through `.connection`. A connection with pending data
  (`Connection::has_pending_data()`) is never auto-evicted for any of these reasons, and even once
  clear, must stay clear for a fixed 30-second grace period (not configurable) before it becomes
  eligible; eviction itself is also only ever checked on a fixed, non-configurable 30-second
  interval, so `idle_timeout`/`max_connection_lifetime` can't effectively take effect any sooner
  than that combined ~30-60 second floor.
- `SecurityMode` — an enum: `Trusted` / `Secure` / `ServerAuthentication` / `DualAuthentication`
  (see [OFT.md §9](OFT.md#9-security-modes)). `ServerAuthentication` is rejected by `Peer::new` — a
  peer has no client/server delineation, so use `DualAuthentication` instead.
- `Connection::identity()` — an `Identity` struct describing the connection's remote side:
  `address`, `certificate` (a `rustls_pki_types::CertificateDer<'static>`, present only if the
  remote side presented a TLS certificate), and `info` (the opaque hail data).
- `Connection::set_received_handler`/`set_disconnected_handler`/`set_acknowledged_handler`,
  `Listener::set_connected_handler`, `Peer::set_received_handler`/`set_acknowledged_handler` —
  single-slot callback setters taking `Option<Arc<dyn Fn(...) + Send + Sync>>`, one per
  notification kind. Assigning a new value (including `None`) always replaces any previous one, and
  each notification kind is assigned independently of the others. `Peer` has no
  `disconnected_handler`/`connected_handler` of its own — see its own docstring for why.
  `Peer::received_handler` takes `(Identity, Vec<u8>)` — the sending connection's identity plus the
  same payload `Connection::received_handler` delivers, as two separate arguments rather than one
  bundled type.
- `send()`'s `tag` parameter — `Option<Tag>` (`Tag = Box<dyn std::any::Any + Send>`), an optional,
  application-controlled value attached to a send, referenced later via `acknowledged_handler`: once
  that message is fully delivered and acknowledged (a `Receipt` for its `Unit` packet, or for its
  final `Completion` packet if split — see [OFT.md §4](OFT.md#4-packets)), `acknowledged_handler` is
  raised with the tag (`Connection`) or the identity and tag (`Peer`). A `None` tag never raises
  `acknowledged_handler`, and neither does a cancelled send. Unlike `received_handler`/
  `disconnected_handler`, `acknowledged_handler` is **not** buffered — see
  [Architecture.md](Architecture.md#buffered-notifications-prevent-a-connectdisconnectreceive-message-loss-race)
  for why that's safe.
- `SendHandle` — returned by `send()`: `.wait()` blocks until delivered/cancelled/disconnected,
  `.wait_timeout(duration)` is the non-blocking-forever variant, and `.cancel()`.
- `Connection::disconnect()`/`.close()`, `Peer::drop()`/`.close()` — see
  [Disposal vs. graceful teardown](#disposal-vs-graceful-teardown).
- `Connection::is_connected()`/`Peer::is_connected()` — `true` until permanently disconnected, after
  which `send()`/`rekey()` return `Err(OftError::Disconnected)`.
- `OftError` — an error enum: `Disconnected`, `Cancelled`, `ValidationRejected(String)`,
  `Io(std::io::Error)`, `Tls(rustls::Error)`. Implements `std::error::Error`.

## Client/server example

```rust
use oft::{connect, host, ConnectionOptions, SecurityMode};
use std::sync::Arc;

// --- Server side ---
let host_options = ConnectionOptions {
    info: "my-server".to_string(),
    security_mode: SecurityMode::Secure, // no certificate needed for this example
    ..Default::default()
};

let listener = host("0.0.0.0", 5000, Some(host_options))?;

listener.set_connected_handler(Some(Arc::new(|connection: oft::Connection| {
    connection.set_received_handler(Some(Arc::new(|data: Vec<u8>| {
        println!("Received: {}", String::from_utf8_lossy(&data));
    })));
})));

// --- Client side ---
let connect_options = ConnectionOptions {
    info: "my-client".to_string(),
    security_mode: SecurityMode::Secure,
    ..Default::default()
};

let connection = connect("127.0.0.1", 5000, Some(connect_options))?;
connection.send(b"hello".to_vec(), 0, None).wait()?;
# Ok::<(), Box<dyn std::error::Error>>(())
```

`options` is optional on both `connect()` and `host()` — pass `None` to use defaults
(`SecurityMode::Secure`, empty `info`, 1 KiB max packet size, 1s/5s poll interval/timeout).

## Remote identity

```rust
let identity = connection.identity();
println!("Remote endpoint: {}", identity.address);
println!("Hail info: {}", identity.info);

if let Some(certificate) = &identity.certificate {
    // certificate: &rustls_pki_types::CertificateDer<'static> - raw DER; parse with a crate like
    // `x509-parser` if you need subject/issuer fields.
    println!("Certificate DER length: {}", certificate.as_ref().len());
}
```

`identity.certificate` is `None` for a connection established with `SecurityMode::Trusted` (no TLS
at all), and also `None` on the accepting side of a connection established under a mode that never
requests a certificate from the connecting side (see `SecurityMode::DualAuthentication`).

## Peer-to-peer example

```rust
use oft::{Peer, PeerOptions, SecurityMode};
use std::sync::Arc;

let options = PeerOptions {
    connection: oft::ConnectionOptions {
        info: "my-peer".to_string(),
        security_mode: SecurityMode::Secure,
        ..Default::default()
    },
    ..Default::default()
};
let peer = Peer::new(Some(options))?;

peer.set_received_handler(Some(Arc::new(|identity: oft::Identity, data: Vec<u8>| {
    println!("Received from {}: {}", identity.address, String::from_utf8_lossy(&data));
})));

// Optional: also accept inbound connections into the same pool.
peer.listen("0.0.0.0", 5001)?;

// Sending to a host:port transparently reuses a cached connection or creates and caches a new one.
peer.send("127.0.0.1", 5001, b"hello".to_vec(), 0, None)?.wait()?;

peer.close();
# Ok::<(), Box<dyn std::error::Error>>(())
```

`Peer::received_handler`'s `identity` argument is only for identifying which connection a message
arrived on, e.g. to decide how to respond via `send()`; a peer deliberately exposes no other way to
enumerate, look up, or be notified about the individual connections it holds (there is no
`disconnected_handler`/`connected_handler`): connection lifecycle is the peer's own implementation
detail, transparently managed (reconnecting, evicting, etc.) behind `send()`.

## Waiting for delivery and cancellation

`send()` returns a `SendHandle`:

```rust
let handle = connection.send(payload, 0, None);

// Block until fully delivered, cancelled, or the connection closes:
match handle.wait() {
    Ok(()) => { /* delivered and acknowledged */ }
    Err(oft::SendFailure::Cancelled) => { /* cancelled */ }
    Err(oft::SendFailure::Disconnected) => { /* connection closed first */ }
}

// Or cancel it (see OFT.md §7): immediately if not yet started, or by sending a Cancellation
// packet if it has already begun.
handle.cancel();
```

## Rekeying

```rust
// Manual, on either side, at any time:
connection.rekey()?;

// Or configure an automatic interval via options:
let options = ConnectionOptions {
    rekey_interval: Some(std::time::Duration::from_secs(600)),
    ..Default::default()
};
```

This port's `rekey()` genuinely rekeys the connection: `rustls` exposes a public, safe API for it
directly — `rustls::ConnectionCommon::refresh_traffic_keys()` — called on the connection's own
background thread (see [Concurrency model](#concurrency-model)), matching every other port's rule
of only ever triggering a `KeyUpdate` from the thread that also performs that connection's reads
(Docs/OFT.md §8). Under `SecurityMode::Trusted` there's no TLS session, so `rekey()` only validates
the connection is still open and otherwise does nothing.

## Security modes

```rust
use oft::{connect, host, ConnectionOptions, SecurityMode};
use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use rustls::RootCertStore;
use std::sync::Arc;

// SERVER_AUTHENTICATION (one-way TLS): the server presents a real certificate chain + key.
let server_chain: Vec<CertificateDer<'static>> = /* ... */ vec![];
let server_key: PrivateKeyDer<'static> = /* ... */ todo!();
let host_options = ConnectionOptions {
    info: "my-server".to_string(),
    security_mode: SecurityMode::ServerAuthentication,
    identity: Some(Arc::new((server_chain, server_key))),
    ..Default::default()
};

// Client leaves root_certificates as None to fall back to the platform's native trust store
// (via rustls-native-certs), or supplies its own:
let mut roots = RootCertStore::empty();
// roots.add(ca_cert_der)?;
let connect_options = ConnectionOptions {
    info: "my-client".to_string(),
    security_mode: SecurityMode::ServerAuthentication,
    root_certificates: Some(Arc::new(roots)),
    ..Default::default()
};

// DUAL_AUTHENTICATION (mutual TLS): the client's options also carry `identity`. The only
// authenticating mode Peer supports - ServerAuthentication above is only valid for connect()/host().
```

`identity` is required (`Some`) for `SecurityMode::ServerAuthentication`/`DualAuthentication` on
`host()`'s options, and for `SecurityMode::DualAuthentication` on `connect()`'s options.
`root_certificates` is required for `SecurityMode::DualAuthentication` on `host()`'s options (to
validate the connecting side's certificate), and optional (falls back to the native trust store) for
`SecurityMode::ServerAuthentication` on `connect()`'s options. Under `SecurityMode::Secure`, both
fields are ignored — the accepting side generates its own throwaway identity (resolved once per
listener/peer, not once per connection), and the connecting side accepts whatever certificate it's
presented with unconditionally. See [OFT.md §9](OFT.md#9-security-modes) for the full semantics of
each mode.

`rustls::ClientConfig`/`ServerConfig` are two separate types; a `Peer` under `DualAuthentication`
needs both, and this port builds each independently from the same `identity`/`root_certificates`
option fields — used to build a `ClientConfig` for its outbound connections and a `ServerConfig`
for its inbound ones.

`ConnectionOptions` also has a `connection_validation` field: an optional callback,
`Arc<dyn Fn(&Identity) -> bool + Send + Sync>`, invoked once the OFT hail exchange completes, for
every security mode (including `Trusted` and `Secure`, where `identity.certificate` is always
`None`) - unlike the trust configuration in `root_certificates`, which only runs during the TLS
handshake itself and never sees the connection's `Identity`:

```rust
let connect_options = ConnectionOptions {
    connection_validation: Some(Arc::new(|identity: &oft::Identity| {
        // identity.certificate (if any) is already accepted by root_certificates' own
        // verification by the time this runs.
        true // or false to reject the connection
    })),
    ..Default::default()
};
```

`None` (the default) accepts every connection; returning `false` fails `connect()`/`host()` with
`OftError::ValidationRejected`. This is the simplest of the four ports' shapes for this callback —
`rustls` exposes no certificate chain beyond the leaf (already captured in `Identity.certificate`)
and no separate "policy errors" result distinct from the handshake simply failing outright, so
there's nothing further to pass here beyond what `Identity` already carries.

## Concurrency model

Each `Connection` owns a single background thread for its whole lifetime, unlike the other three
ports (which use 2-4 threads each: separate receive/send/poll loops). This is a deliberate
Rust-specific design, not a simplification for its own sake: `rustls::Connection` isn't `Sync` and
its read/write paths share internal state, so genuinely concurrent reads and writes from separate
threads on the same TLS connection would need a mutex spanning the whole I/O path — which would
mean a thread blocked in a read could stall a write (including an urgent one like a `Receipt`)
indefinitely. Instead, one thread owns the connection's socket and TLS state exclusively:

- It performs a blocking read with a short timeout (capped at 100ms), so it can also promptly
  service a small work queue that `send()`/`rekey()`/`disconnect()` push onto from any calling
  thread — this is how those calls get "into" a design where only one thread ever touches the
  connection, without callers ever blocking on the connection's own internal state directly.
- On each iteration it: processes any newly received frame(s) (dispatching to
  `received_handler`, replying with a `Receipt` inline, and updating the reassembly buffers per
  [OFT.md §4.4](OFT.md#44-identifying-which-channel-a-completioncancellation-belongs-to)); drains
  the work queue (starting/cancelling sends, running a manual or interval-triggered
  `refresh_traffic_keys()`); sends a `Poll` frame or evaluates the liveness timeout if due; and, if
  the single in-flight-packet slot is free, sends the next scheduled packet per
  [OFT.md §5-§6](OFT.md#5-priority).

Because every read, write, and `refresh_traffic_keys()` call happens on this one thread by
construction, this port satisfies OFT.md §8's "only ever trigger a `KeyUpdate` from the thread that
performs that connection's reads" rule automatically — there's no second thread it could race
against.

`disconnect()` doesn't wait for the I/O thread's next read-timeout tick to notice a stop request: it
also shuts down a cloned handle to the connection's own socket immediately, from the calling
thread, which reliably and near-instantly unblocks the I/O thread's blocking read — closing a
socket from a different thread than the one blocked reading it doesn't reliably interrupt that read
on Linux, but shutting down any clone of the same underlying socket does.

A `Listener` runs its own accept loop on a background thread, spawning a short-lived thread per
accepted connection to perform its handshake and hail exchange so a slow one never delays accepting
the next; a `Peer` runs its own eviction-check loop as well.

## Memory ownership

Received data is delivered as an owned, immutable `Vec<u8>` — ordinary Rust ownership: the callback
gets full ownership of the buffer with no need for a separate release step. `Connection::send`/
`Peer::send` take ownership of the `Vec<u8>` passed to them (moved, not copied) — the connection
copies out only what a given packet's chunk needs while chunking a multi-packet message, freeing
the caller from needing to keep the original buffer alive itself.

## Disposal vs. graceful teardown

`Connection::disconnect()` requests an immediate teardown and returns without waiting for the
connection's background I/O thread to finish, which happens shortly afterward on its own;
`.close()` waits for that work to fully stop before returning. Both immediately and synchronously
put the connection into a permanently disconnected state (`is_connected()` becomes `false`) before
any of that background work actually finishes, not just once it does. `Listener` has only one
teardown method (`close()`) — stopping a listener has no in-flight work of its own worth a
non-blocking alternative for.

`Peer::drop()`/`.close()` follow a similar split at the peer level: `drop()` disconnects every
connection the peer currently holds (the immediate teardown) but leaves the peer itself usable,
`is_connected()` still `true`; `close()` additionally stops listening (if applicable) and
permanently retires the peer using the *graceful*, thread-joining teardown for each connection it
holds (so `close()` genuinely waits for every connection's background thread to exit, leaving
nothing still running once it returns) — after which every other member returns
`Err(OftError::Disconnected)` (`is_connected()` itself is the only exception, permanently returning
`false`).

```rust
peer.drop()?;  // Forces reconnection on the next send(); the peer itself remains usable.
peer.close();  // Permanently retires the peer; every member but is_connected() now errors.
```

## Testing and coverage

Tests use real loopback TCP/TLS connections (`tests/`), with throwaway self-signed test
certificates generated via `rcgen`.

```
cargo test
```

See [`AGENTS.md`](../AGENTS.md) for the coding conventions used throughout this project.
