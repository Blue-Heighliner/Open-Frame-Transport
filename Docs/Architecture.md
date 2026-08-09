# Architecture

This document describes how the [OFT protocol](OFT.md) is implemented: the components every port
exposes, and the concurrency/memory model each language uses to implement the same wire behavior.
For language-specific API examples, see [CSharp.md](CSharp.md), [Java.md](Java.md), [C.md](C.md),
and [Rust.md](Rust.md).

## Implementations

| | Language | Location | TLS library |
|---|---|---|---|
| Reference implementation | C# (.NET) | [`Core/`](../Core) | BouncyCastle (`Org.BouncyCastle.Tls`) |
| Port | Java | [`Ports/Java/`](../Ports/Java) | JSSE (`javax.net.ssl`) |
| Port | C | [`Ports/C/`](../Ports/C) | OpenSSL |
| Port | Rust | [`Ports/Rust/`](../Ports/Rust) | rustls |

All four implement the same wire protocol and the same three-component API shape, verified against
each other over real loopback TCP/TLS (no mocked sockets). Per [`AGENTS.md`](../AGENTS.md), method
names, semantics, and option shapes stay aligned across ports as much as practical, adapted only
where a language's idioms genuinely require it — this document calls out where and why each one
differs.

## Components

Every port exposes the same three entry points, named per each language's own conventions (see the
per-language docs for exact names):

- **A connector** — dials a host:port, performs the TLS handshake and hail exchange, and returns an
  established connection. Stateless: it doesn't track or own the connections it creates.
- **A hoster** — starts listening on a local endpoint and returns a listener that notifies the
  caller of each accepted connection. Also stateless and one-shot: closing a listener only stops
  accepting new connections (already-accepted ones keep running), and a closed listener can't be
  reopened.
- **A peer** — a connection-pooling layer over one connector and one hoster. Sending to a `host:port`
  reuses a cached outbound connection or creates one; it may also listen for inbound connections into
  the same pool. It exposes no way to enumerate or address individual connections — rekey/drop act on
  all of them at once, and a single received-message notification covers every connection, tagged
  with which one it arrived on (an `Identity`/`OftIdentity` value in C#/Java/Rust, a connection
  argument in C). It has no connected/disconnected notification of its own: connection lifecycle
  (establishing, reconnecting, evicting) is entirely internal. Idle, expired, or excess-count
  connections are auto-disconnected (oldest first), except any with unacknowledged outbound or
  partially-reassembled inbound data, which is never evicted, so in-flight data is never dropped.

A **connection**, whether returned by the connector or handed to a listener's accepted-connection
callback, is the same type either way and exposes:

- Send (payload + priority, see [OFT.md §5-§7](OFT.md#5-priority)).
- Manual rekey (see [OFT.md §8](OFT.md#8-rekeying)), plus an optional automatic interval.
- Received-message and disconnected notifications.
- Metadata: the remote side's identity (endpoint, certificate if any, hail `info`) and
  connect/last-sent/last-received timestamps. C#/Java/Rust bundle identity into one
  `OftIdentity`/`Identity` value; C exposes separate accessors.
- Manual disconnect.

## General API flow

1. **Server**: host a listener, assign a connected-connection callback, and inside it assign a
   received-message callback per connection.
2. **Client**: connect, then assign a received-message callback on the returned connection.
3. **Either side** sends on any connection it holds, with an optional priority.
4. **Peer-to-peer**: create a peer, optionally listen for inbound connections, and send to a
   `host:port` — it connects and caches the connection the first time, reusing it after. One
   received-message callback on the peer itself covers every connection it holds.

Every notification is one independent, single-slot callback — no bundled handler-object interface,
no multicast event/listener-list:

| Notification | C# | Java | C | Rust |
|---|---|---|---|---|
| Connection received | `IOftConnection.ReceivedHandler` (`Action<T>`) | `OftConnection.setReceivedHandler` (`Consumer<T>`) | `oft_connection_set_received_callback` | `Connection::set_received_handler` |
| Connection disconnected | `.DisconnectedHandler` | `.setDisconnectedHandler` | `oft_connection_set_disconnected_callback` | `.set_disconnected_handler` |
| Connection send delivery status (tagged) | `.DeliveryStatusHandler` | `.setDeliveryStatusHandler` | `oft_connection_set_delivery_status_callback` | `.set_delivery_status_handler` |
| Listener accepted a connection | `IOftListener.ConnectedHandler` | `OftListener.setConnectedHandler` | `oft_listener_set_connected_callback` | `Listener::set_connected_handler` |
| Peer received (any held connection) | `IOftPeer.ReceivedHandler` | `OftPeer.setReceivedHandler` | `oft_peer_set_received_callback` | `Peer::set_received_handler` |
| Peer send delivery status (tagged) | `IOftPeer.DeliveryStatusHandler` | `OftPeer.setDeliveryStatusHandler` | `oft_peer_set_delivery_status_callback` | `Peer::set_delivery_status_handler` |

A peer has no connected/disconnected slot of its own (see [Components](#components)). Each slot is
assigned independently of the others, and assigning a new value (including `null`/`NULL`/`None`)
always fully replaces the previous one — at most one recipient per notification, which is what makes
data ownership unambiguous (see [Memory ownership](#memory-ownership)).

C#'s callbacks are plain `Action<T>` properties; Java mirrors this with `Consumer<T>`/`BiConsumer<T,
U>` setters; Rust mirrors it with `set_*_handler(Option<Arc<dyn Fn(...)>>)`. C, lacking both
interfaces and lambdas, uses a function pointer plus `void *user_data`. A peer's received callback
identifies the source connection the same way in all four: an identity argument alongside the
payload. C#/Java/Rust's identity value is self-contained and safe to keep after the callback
returns; C's `const oft_identity *` is borrowed from the connection and valid only for that one
call — copy out what you need if it must outlive it.

Tagged-send delivery status (`send`'s `tag` parameter and the delivery-status handler it's later
reported through) is mirrored identically across all four, at both the connection and peer level,
with one exception — see below. Every tagged send is reported through the same lifecycle: `Queued` →
`Sending` → optionally any number of `Interrupted`/`Resumed` pairs (a higher-priority send preempting
it, see [OFT.md §6](OFT.md#6-interruption)) → either `Cancelled` or `Sent` followed by `Acknowledged`.
At the peer level, the handler deliberately carries no identity, unlike the received notification:
the caller already knows which connection a send went out on, since it's the same caller that made
that `send` call.

### Buffered notifications prevent a connect/disconnect/receive message-loss race

A connection can start receiving packets the instant its handshake finishes — potentially before the
caller has had a chance to assign a received-message callback. Without precaution, a peer that
replies (or disconnects) immediately on connecting could have that first message or disconnection
silently lost.

All four ports solve this the same way, for every notification kind except `DeliveryStatusHandler`
(see below): each slot buffers everything raised before a callback is first assigned, then flushes
that backlog to it, in order, before going live for anything raised afterward. C#'s
`OftBufferedHandlerSlot<TDelegate>`, Java's `BufferedHandlerSlot`, C's `oft_event_buffer`, and Rust's
`BufferedSlot<T>` all implement this. Only the *first-ever* non-null assignment to a slot triggers a
flush — reassigning later (including to null, which drops future notifications) never re-triggers
one, and each slot buffers/flushes independently of the others. A second callback assigned to the
same slot later only sees live notifications from that point on, not the earlier backlog.

This means a caller can get a connection back and assign a received/disconnected callback afterward,
in any order, with nothing ever silently lost — and the same guarantee applies to a listener's
connected notification.

`DeliveryStatusHandler` is deliberately **not** buffered: unlike the other notifications, its only
trigger is the caller's own tagged `send()` call, so there's no autonomous-timing race to guard
against — even though it can fire many times for that one call (once per status), every one of those
firings still traces back to that same caller-controlled starting point. All four ports implement it
as a plain unbuffered field — assign it before the send you want notified about, not after.

### Where a port's flow differs from this

#### Blocking vs. asynchronous calls

- **C#** is fully `async`/`Task`-based throughout.
- **Java** returns `CompletableFuture` for everything that does real background work — `connect`,
  `host`, `OftPeer.listen`/`.stopListening`/`.drop`/`.rekey`, and send/rekey completion via
  `OftSendHandle.completion()` — so a caller can block (`.get()`) or compose asynchronously. The
  work underneath still runs synchronously on a dedicated per-call thread (no genuinely non-blocking
  JDK API for it exists — see [Java.md](Java.md#async-capable-apis)), not true overlapped I/O.
  `close()` on `OftConnection`/`OftListener`/`OftPeer` stays a plain blocking `void` method on all
  three, since `AutoCloseable.close()` must return `void` to support try-with-resources.
- **C** is blocking throughout; completion (delivered, cancelled, closed) is reported via explicit
  `wait` calls or return codes, not futures.
- **Rust** is blocking throughout for the same reason as C — no bundled async runtime to hand a
  future back on — except `SendHandle`, which also implements `std::future::Future`, so it can be
  `.await`ed under whatever executor an application already uses (see
  [Rust.md](Rust.md#waiting-for-delivery-and-cancellation)).

#### Tracking and cancelling a sent message

Every `send` queues a message the same way, but exposes waiting/cancelling differently, following
from the blocking/async split above:

| | Mechanism |
|---|---|
| C# | `Send` takes a `CancellationToken` and returns the `Task` to await — no separate handle needed. |
| Java | `send` returns an `OftSendHandle` (`completion(): CompletableFuture<Void>`, `cancel()`), since a blocking call can't hand back both a result and a cancel handle any other way. |
| C | `oft_connection_send` writes a message id to an out-param, used with separate `oft_connection_wait()`/`_cancel()` calls (idiomatic C has no object to return a handle on). |
| Rust | `send` returns a `SendHandle` shaped like Java's (`.wait()`/`.wait_timeout()`, `.cancel()`), which also implements `Future` — a third, async option alongside the two blocking ones. |

#### Teardown: two speeds in C#/Java/Rust, one in C, with a peer-level nuance

Connection/listener teardown has an immediate, non-waiting form and a graceful, waiting form in
C#/Java/Rust: `Dispose()`/`DisposeAsync()` in C# (`IDisposable`/`IAsyncDisposable`),
`disconnect()`/`close()` in Java and Rust — the graceful form waits for background work
(receive/send loops, etc.) to finish, the immediate one doesn't. C mirrors this with
`oft_connection_disconnect()`/`_close()`. `OftListener` has only one teardown method per port
(`Dispose()`, `close()`, `oft_listener_close()`, `Listener::close()` — closing has no in-flight work
worth a non-blocking alternative) — though C#'s deliberately doesn't wait for its accept loop (see
[Rekeying and thread safety](#rekeying-and-thread-safety)'s note on disposing synchronization
primitives out from under a running operation), while Java's/C's/Rust's do.

At the peer level, all four distinguish "drop this peer's held connections, leaving the peer usable"
(`Drop()`/`drop()`/`oft_peer_drop()`/`Peer::drop()`) from "permanently retire the peer"
(`Dispose()`/`.DisposeAsync()`; `close()`; `oft_peer_close()`; `Peer::close()`). After the latter,
`isConnected()` is permanently false and every other member fails: an
`ObjectDisposedException`/`IllegalStateException` for lifecycle calls, `OftDisconnectedException`
for send/rekey (C returns `OFT_ERROR`; Rust returns `Result<_, OftError>`, with
`OftError::Disconnected` covering both "explicitly retired" and "simply lost its last connection").
C has no peer-level `is_connected()` at all — `oft_peer_close()` frees the struct immediately, so
there's no "closed but not freed" state to query, unlike its per-connection equivalent.

Whether `drop`/`close` wait for each held connection's background work to finish also varies: C#'s
`Drop()`/`DisposeAsync()` both use the graceful, waiting teardown. Java's `drop()`/`close()` both use
the immediate one — safe only because Java connections are garbage-collected regardless. C keeps
`oft_peer_close()` on the graceful, thread-joining `oft_connection_close()` (`oft_peer_drop()` stays
immediate) purely because `oft_connection_disconnect()` never frees memory — only
`oft_connection_close()` does, so using the immediate form for `close()` would leak. Rust follows
C's split, not Java's: `Peer::drop()` is immediate, `Peer::close()` is graceful, since a Rust
`Connection` isn't garbage-collected and `close()` should leave nothing still winding down after it
returns.

#### Post-handshake connection validation

All four ports expose an optional callback, invoked once per connection after the OFT hail exchange
completes, that can reject a connection for more than just its certificate in isolation. Its shape
follows each language's own TLS API rather than a shared struct:

| | Shape |
|---|---|
| C# | `async Task<bool>`; passes `OftIdentity`/`X509Certificate2?`/`X509Chain?`/`SslPolicyErrors` — the chain/errors come from extending the same BouncyCastle validation `CertificateValidation` already does mid-handshake. |
| Java | blocking `boolean`; passes `OftIdentity`/`Certificate[]` (peer's chain, leaf first)/`SSLSession` — no `sslErrors` equivalent, since a `TrustManager` either accepts a chain or aborts outright. |
| C | blocking `int`; passes `oft_identity *`/`X509 *`/`STACK_OF(X509) *` (borrowed, call-duration only)/`long verify_result` (OpenSSL's `SSL_get_verify_result()`). |
| Rust | blocking `Arc<dyn Fn(&Identity) -> bool>`; passes only `&Identity` — `rustls` exposes no chain beyond the leaf (already in `Identity.certificate`) and no separate verify-result, the simplest of the four. |

All four default to "accept every connection" and fail establishment if the callback rejects one;
each was designed against what its own TLS stack exposes at this point, not mirrored from another.

## Concurrency model

Every connection owns background work for its whole lifetime: a **receive loop** (reads and
dispatches inbound frames), a **send loop** (drains the outbound priority queues, see
[OFT.md §5-§6](OFT.md#5-priority), waiting for each packet's `Receipt` before sending the next — see
[OFT.md §4.1](OFT.md#41-acknowledgement-and-flow-control)), a **liveness task** (periodic `Poll` +
watchdog, see [OFT.md §10](OFT.md#10-liveness-polling)), and an optional **rekey timer**. A listener
runs its own accept loop so a slow handshake never delays accepting the next connection; a peer runs
its own eviction-check loop.

C# implements these as thread-pool `Task`s; Java as daemon `Thread`s plus
`ScheduledExecutorService`s for timers; C as POSIX `pthread`s. Rust is the exception:

### Rust: a single I/O thread per connection

Rust uses one background thread owning the connection's socket and `rustls` state exclusively,
instead of 2-4 separate threads. This isn't a simplification for its own sake: `rustls::Connection`
isn't `Sync`, and its read/write paths share internal state, so separate threads would need a mutex
spanning the whole I/O path — which would let a thread blocked in a read stall an urgent write (like
a `Receipt`) indefinitely. Instead, the single thread does a short-timeout blocking read each
iteration, using the gaps to drain a work queue that `send()`/`rekey()`/`disconnect()` push onto from
any thread, service the liveness/poll timers, and send the next scheduled packet. See
[Rust.md](Rust.md#concurrency-model) for the full design, including how `disconnect()` uses a cloned
socket handle to unblock that read immediately rather than waiting for its next timeout tick.

### Rekeying and thread safety

C#/Java/C trigger a TLS `KeyUpdate` (manual or automatic) only from the connection's own receive
thread, never an arbitrary caller thread, funneling requests through a queue the receive loop
drains. This is required for correctness: a locally initiated `KeyUpdate` and an inbound one
(triggered by an ordinary read) both touch the same read/write state, and none of BouncyCastle,
JSSE, or OpenSSL guard that against concurrent access on their own — observed directly in Java as a
`bad_record_mac` alert when both peers rekeyed at nearly the same moment during development. Running
the update on the receive thread guarantees it can't race the read path, and it's wrapped in the
same write-serialization primitive as ordinary packet writes so it can't interleave with a send
either. Rust satisfies this by construction: since one thread owns the whole connection, every read,
write, and `refresh_traffic_keys()` call is already serialized against every other one — there's no
second thread to race.

`rustls::ConnectionCommon::refresh_traffic_keys()` makes this a safe, public operation, so Rust's
`rekey()` genuinely rekeys the connection like every other port's does — no TLS library used in this
repository lacks a way to initiate a `KeyUpdate` from the application side.

## Security modes

[OFT.md §9](OFT.md#9-security-modes) defines four security modes (`Trusted`/`Secure`/
`ServerAuthentication`/`DualAuthentication`); each port exposes the same four as an enum
(`OftSecurityMode` in C#/Java, `enum oft_security_mode` in C, `SecurityMode` in Rust).
`ServerAuthentication` is rejected outright by every peer constructor — a peer has no
client/server delineation, so it can't express one-sided authentication; use `DualAuthentication`
instead.

Under `Secure` mode, the accepting side generates its own throwaway certificate rather than using
one the caller supplies. It's **resolved once per listener, not once per accepted connection** —
RSA-2048 keygen is expensive enough that doing it per connection would meaningfully slow down or
destabilize connection establishment under load.

Rust's `rustls::ClientConfig`/`ServerConfig` are separate types; a `DualAuthentication` peer needs
both and builds each independently from the same shared `identity`/`root_certificates` options,
rather than one bundled role-specific context the way C#'s certificate-plus-callback pairing or
Java's single `SSLContext` do.

## Memory ownership

- **C#** delivers received data as a pooled `IMemoryOwner<byte>` (`MemoryPool<byte>.Shared`) — the
  callback owns it and should dispose it for prompt reuse. `Send` either copies
  (`ReadOnlyMemory<byte>` overload) or takes ownership (`IMemoryOwner<byte>` overload, disposed once
  the send completes) — see [`AGENTS.md`](../AGENTS.md)'s pooled-memory conventions.
- **Java** delivers an ordinary `byte[]`, garbage-collected once the callback is done with it.
  `send(byte[], ...)` copies its input.
- **C** delivers a `malloc()`-allocated `uint8_t *`/`length`; the callback must `free()` it.
  `oft_connection_send()` copies its input.
- **Rust** delivers an owned `Vec<u8>` with ordinary Rust ownership, no release step needed. `send`
  takes ownership of the `Vec<u8>` passed to it (moved, not copied).

A peer's received callback delivers this same payload, alongside a separate identity argument (a
borrowed, call-duration-only pointer in C; an owned value in the other three).

## See also

- [OFT.md](OFT.md) — the wire protocol specification.
- [CSharp.md](CSharp.md), [Java.md](Java.md), [C.md](C.md), [Rust.md](Rust.md) — per-language API
  reference and examples.
- [`AGENTS.md`](../AGENTS.md) — coding conventions and the cross-port alignment policy.
