# Architecture

This document describes how the [OFT protocol](OFT.md) is implemented: the components every port
exposes, how they relate to each other, and the concurrency/memory model each language uses to
implement the same wire behavior. For language-specific API examples, see
[CSharp.md](CSharp.md), [Java.md](Java.md), [C.md](C.md), and [Rust.md](Rust.md).

## Implementations

| | Language | Location | TLS library |
|---|---|---|---|
| Reference implementation | C# (.NET) | [`Core/`](../Core) | BouncyCastle (`Org.BouncyCastle.Tls`) |
| Port | Java | [`Ports/Java/`](../Ports/Java) | JSSE (`javax.net.ssl`) |
| Port | C | [`Ports/C/`](../Ports/C) | OpenSSL |
| Port | Rust | [`Ports/Rust/`](../Ports/Rust) | rustls |

All four implement the same wire protocol and the same three-component API shape, verified against
each other via real loopback TCP/TLS tests (no mocked sockets). [`AGENTS.md`](../AGENTS.md) has an
explicit convention that the ports' APIs — method names, semantics, and option shapes — stay
aligned as much as is practical, adapted only where a language's idioms genuinely require it; this
document calls out where and why each one differs.

## Components

Every implementation exposes the same three entry points, under names adapted to each language's
conventions (see the per-language docs for exact type/method names):

- **A connector** — dials out to a remote host:port, performs the TLS handshake and hail exchange,
  and returns an established connection. Stateless: creating a connector holds no resources, and it
  doesn't track or own the connections it creates — the caller owns each one's lifetime.
- **A hoster** — starts listening on a local endpoint and returns a listener. Also stateless: each
  call to host a listener is a one-shot "start listening now" operation. The returned listener
  notifies the caller of each accepted, fully-established inbound connection, but doesn't track them
  itself — closing it only stops accepting new ones, leaving already-accepted connections running.
  There's no way to reopen a closed listener; host a fresh one instead.
- **A peer** — a connection-pooling convenience layer built on one connector and one hoster. Sending
  a message to a `host:port` transparently reuses an existing outbound connection or creates and
  caches a new one; it may optionally also listen for inbound connections, folding them into the same
  pool. It has no way to enumerate or individually address the connections it holds — rekey and drop
  act on all of them at once — and exposes only a single received-message notification covering every
  connection, identifying which one a message arrived on (C: via that notification's own connection
  argument, needed for replying directly; C#/Java/Rust: via the message's own `OftIdentity`/`Identity`
  value, not the connection itself). It has no disconnected or connected notification of its own: connection
  lifecycle (establishing, reconnecting, evicting) is the peer's own implementation detail,
  deliberately not surfaced, so a caller can never observe individual connections beyond what a
  received message reveals in passing. Idle, expired, or excess cached connections are disconnected
  automatically (configurable by time since last activity, maximum age, and maximum count, evicting
  the oldest first) — except a connection with unacknowledged outbound or not-yet-reassembled inbound
  data, which is never evicted regardless of these limits, so in-flight data is never silently
  dropped. (See [Where a port's flow differs from this](#where-a-ports-flow-differs-from-this) below
  for the distinction between dropping a peer's held connections and permanently retiring the peer
  itself.)

A **connection**, produced by either the connector or an accepted-connection notification from a
listener, is the same type either way and exposes:

- Message send, taking a payload and a priority (see [OFT.md §5-§7](OFT.md#5-priority)).
- Manual rekey (see [OFT.md §8](OFT.md#8-rekeying)), plus an optional automatic rekey interval
  configured on the connection's options.
- Notification of every fully-received application message.
- Notification of disconnection, with the exception (if any) that caused it.
- Metadata: the remote side's identity (endpoint, TLS certificate if any, and hail `info`) and
  connect/last-sent/last-received timestamps. C#/Java/Rust bundle the identity fields into one
  `OftIdentity`/`Identity` value (`IOftConnection.Identity`/`OftConnection.getIdentity()`/
  `Connection::identity()`); C exposes them as separate accessors — see [CSharp.md](CSharp.md),
  [Java.md](Java.md), [C.md](C.md), and [Rust.md](Rust.md) for the exact shape in each.
- Manual disconnect.

## General API flow

The typical flow, independent of language:

1. **Server side**: create a hoster, call its host method with a listen endpoint and options, assign
   a callback for accepted connections, and — inside that callback — assign a callback for received
   messages on each connection.
2. **Client side**: create a connector, call its connect method with a target host/port and options,
   and assign a received-message callback on the returned connection.
3. **Either side** sends messages on any connection it holds, at any point after it's established,
   with an optional priority.
4. **Peer-to-peer**: create a peer with options, optionally call its listen method to also accept
   inbound connections, and call its send method with a target host/port — it transparently connects
   (and caches the connection) the first time, and reuses the cached connection afterward. Assign a
   received-message callback on the peer itself (not on individual connections) to handle messages
   from every connection it holds, inbound or outbound.

Every notification is one independent, single-slot callback — there's no bundled "handler object"
implementing several notification methods at once, and no multicast event/listener-list:

| Notification | C# | Java | C | Rust |
|---|---|---|---|---|
| Connection received | `IOftConnection.ReceivedHandler` (`Action<T>`) | `OftConnection.setReceivedHandler` (`Consumer<T>`) | `oft_connection_set_received_callback` | `Connection::set_received_handler` |
| Connection disconnected | `.DisconnectedHandler` | `.setDisconnectedHandler` | `oft_connection_set_disconnected_callback` | `.set_disconnected_handler` |
| Connection send acknowledged (tagged) | `.AcknowledgedHandler` | `.setAcknowledgedHandler` | `oft_connection_set_acknowledged_callback` | `.set_acknowledged_handler` |
| Listener accepted a connection | `IOftListener.ConnectedHandler` | `OftListener.setConnectedHandler` | `oft_listener_set_connected_callback` | `Listener::set_connected_handler` |
| Peer received (any held connection) | `IOftPeer.ReceivedHandler` | `OftPeer.setReceivedHandler` | `oft_peer_set_received_callback` | `Peer::set_received_handler` |
| Peer send acknowledged (tagged) | `IOftPeer.AcknowledgedHandler` | `OftPeer.setAcknowledgedHandler` | `oft_peer_set_acknowledged_callback` | `Peer::set_acknowledged_handler` |

A peer deliberately has no disconnected/connected slot of its own — see [Components](#components)
above for why. Each slot on a given connection/listener/peer is assigned (or reassigned)
independently of the others — setting `ReceivedHandler` never disturbs `DisconnectedHandler` — and
assigning a new callback (including `null`/`NULL`) always fully replaces whatever was assigned
before, so there's always at most one recipient of a given notification. That's what makes it
unambiguous who owns any data the notification carries (see [Memory ownership](#memory-ownership)
below): unlike a multicast design, no two subscribers can both think they own the same data.

C#'s callbacks are plain `Action<T>` properties assigned a lambda or method group directly, no
interface to implement; Java mirrors this with `Consumer<T>`/`BiConsumer<T, U>` setter methods. C,
lacking both interfaces and lambdas, uses a function pointer plus a `void *user_data`. Rust mirrors
C#/Java's assignment style with `set_*_handler(Option<Arc<dyn Fn(...) + Send + Sync>>)` setter
methods. A connection's or listener's own callback passes a plain payload/connection argument in
every port. A peer's received callback identifies which connection a message arrived on the same
way in all four: an `OftIdentity`/`Identity`/`oft_identity *` argument alongside the payload, as two
separate arguments — the same shape a connection's own received callback already uses. C#'s
`OftIdentity` is a self-contained record, safe to keep referencing after the callback returns
(garbage-collected like any other object); Java's is likewise a self-contained record; Rust's
`Identity` is likewise self-contained, owned outright by whoever holds it (no separate GC needed,
just ordinary drop-when-last-owner-goes-out-of-scope); C's `const oft_identity *` is instead
borrowed from the underlying connection and valid only for the duration of that one call, matching
how every other borrowed-identity pointer in this port works (e.g. `oft_connection_identity()`'s
own return value) — a caller that needs it afterward must copy out whatever fields it cares about
itself.

Tagged-send acknowledgement (`Send`'s/`send`'s/`oft_connection_send()`'s/`send()`'s `tag` parameter,
and the `AcknowledgedHandler`/`setAcknowledgedHandler`/`oft_connection_set_acknowledged_callback`/
`set_acknowledged_handler` notification it's later reported through) is mirrored identically across
all four, at both the connection and peer level. See
[Buffered notifications](#buffered-notifications-prevent-a-connectdisconnectreceive-message-loss-race)
below for the one respect in which this notification deliberately behaves differently from every
other one.

### Buffered notifications prevent a connect/disconnect/receive message-loss race

A connection can start receiving packets the instant its TLS handshake and hail exchange finish —
potentially before the caller that just received it (from a connect call or a listener's
accepted-connection notification) has had a chance to assign a received-message callback. Without
precaution, a peer that replies (or disconnects) immediately on connecting could have its first
message, or its disconnection, silently lost: delivered to nothing, before the caller ever gets a
chance to assign a callback.

All four ports avoid this the same way, uniformly across every notification kind (received,
disconnected, and a listener's accepted-connection "connected" notification): each is backed by its
own buffering slot that holds onto everything raised before a callback is first assigned, then
delivers that backlog to it, in order, before it becomes the live target for anything raised
afterward. C#'s `OftBufferedHandlerSlot<TDelegate>` (`Core/src/Internal/OftBufferedHandlerSlot.cs`),
Java's `BufferedHandlerSlot` (`Ports/Java/.../BufferedHandlerSlot.java`), C's `oft_event_buffer`
(`Ports/C/src/oft_event_buffer.{h,c}`), and Rust's `BufferedSlot<T>` (`Ports/Rust/src/buffered_slot.rs`)
all share this guarantee, adapted to each language's callback shape. Only the very first
non-`null`/`NULL`/`None` assignment to a given slot ever triggers a flush — reassigning it
afterward (including to `null`/`NULL`/`None`, which drops future notifications) never re-triggers
one; every slot buffers and flushes completely independently.

This means a caller in any of the four languages can simply get the connection back from the
connect call (or receive it via the connected notification) and assign a received or disconnected
callback afterward, in any order, with no special API for it — nothing is ever silently lost between
establishment and assignment. The same guarantee applies symmetrically to a listener's connected
notification: a caller can assign it at any point after a hoster returns the listener, even after
connections have already been accepted, and still receive the backlog of any accepted before it did.

Because the backlog only ever flushes to the *first-ever* callback assigned to a slot, this is not a
general replay mechanism — a second callback assigned to the same slot later sees only live
notifications from that point on, not the earlier history. This matters for tests and application
code alike: assigning a callback after an earlier, unrelated send has already happened means seeing
that earlier message (delivered via the backlog) before whatever the caller actually intended to
observe next.

#### `AcknowledgedHandler` is deliberately *not* buffered

Every notification above needs this buffering because its trigger — an inbound packet, a connection
closing, a listener accepting — can happen autonomously, at any time, independent of anything the
caller does. `AcknowledgedHandler`/`setAcknowledgedHandler`/`oft_connection_set_acknowledged_callback`/
`set_acknowledged_handler` (and the peer-level equivalents) are different: the *only* way to ever
trigger one is the caller's own `Send`/`send`/`oft_connection_send()`/`send()` call with a
non-`null`/non-`NULL`/`Some` tag. Since the caller fully controls when that first happens, there is
no message-loss race to guard against, and all four ports implement this one as a plain, unbuffered
field — assigning it after the triggering send call (but before that send completes) simply misses
the notification for that specific send, by design; the caller is expected to assign it before
issuing a tagged send it cares about being notified for, not rely on any backlog to catch up later.

### Where a port's flow differs from this

#### Blocking vs. asynchronous calls

C#'s API is fully `async`/`Task`-based throughout. Java's API is blocking (ordinary method calls,
with `CompletableFuture` used only for results that complete in the background, like message
delivery/rekey completion) — Java call sites don't need `await`, but a call like connect blocks the
calling thread until the TCP connection and handshake finish. C's API is likewise blocking
throughout, with completion states (delivered, cancelled, or the connection closed) reported via
explicit `wait` calls or return codes rather than futures/promises. Rust's API is blocking too,
matching Java's overall shape most closely — ordinary method calls, with `SendHandle::wait()`
standing in for Java's `OftSendHandle::completion()`.

#### Tracking and cancelling a sent message

`Send`/`send`/`oft_connection_send` (and the peer-level equivalents) all queue a message the same
way, but expose waiting for delivery and cancelling differently, following from the blocking/async
split above:

- C#'s `Send` takes a `CancellationToken` directly and returns the `Task` to await for delivery — no
  separate handle needed, since C# already has a first-class cancellation primitive.
- Java's `send` returns an `OftSendHandle` (`completion(): CompletableFuture<Void>` to wait on,
  `cancel()` to abandon it), since a plain blocking call can't hand back "the eventual result" and "a
  way to cancel it" any other way.
- C's `oft_connection_send` writes the message's id to an `out_message_id` out-param instead of
  returning a handle object (idiomatic C has no object to return it on); that id is then passed to
  the separate `oft_connection_wait()`/`oft_connection_cancel()` calls.
- Rust's `send` returns a `SendHandle` shaped much like Java's `OftSendHandle` — `.wait()`/
  `.wait_timeout()` to block for delivery, `.cancel()` to abandon it — since Rust's blocking API has
  the same "plain call can't hand back both a result and a cancel handle" problem Java's does.

#### Teardown: two speeds in C#/Java/Rust, one in C, with one peer-level nuance

C#'s connection/listener/peer types implement `IDisposable`, not `IAsyncDisposable`: `Dispose()`
requests an immediate teardown and returns without waiting for background work (receive/send loops,
etc.) to finish, while a separate `async Disconnect()` (also on `IOftPeer`, alongside its own
`Listen()`/`StopListening()`/`Drop()`) waits for that work to fully stop before returning. Java's
`OftConnection` had this same two-speed split from the start under different names — `close()`
(`AutoCloseable`) blocks until its background threads finish, matching C#'s awaitable `Disconnect()`;
`disconnect()` doesn't wait, matching C#'s `Dispose()`. C's `oft_connection_close()`/
`oft_connection_disconnect()` mirror this identically. Rust's `Connection::close()`/`.disconnect()`
use the same names and split: `close()` waits for the connection's own background I/O thread to
fully stop before returning, `disconnect()` doesn't.

`OftListener` has only one teardown method in every port (`Dispose()`/`close()`/
`oft_listener_close()`/`Listener::close()`) — stopping a listener has no in-flight work worth a
non-blocking alternative for — though C#'s deliberately doesn't wait for its accept loop to finish
(see [Rekeying and thread safety](#rekeying-and-thread-safety)'s sibling concern about disposing
synchronization primitives out from under a still-running background operation), while Java's/C's/
Rust's do block on it.

At the peer level, all four ports distinguish "disconnect this peer's currently held connections,
leaving the peer itself usable" (`IOftPeer.Drop()`/`OftPeer.drop()`/`oft_peer_drop()`/`Peer::drop()`)
from "permanently retire the peer itself" (`IOftPeer.Dispose()`/`.Disconnect()`; `OftPeer.close()`;
`oft_peer_close()`; `Peer::close()`). After the latter, `IsConnected`/`isConnected()`/`is_connected()`
is permanently `false` and every other member throws/returns an error: an
`ObjectDisposedException`/`IllegalStateException` for lifecycle operations
(`Listen`/`StopListening`/`listen`/`stopListening`/`Drop`/`drop`), or an
`OftDisconnectedException` for `Send`/`Rekey`/`send`/`rekey` (which can also fail for the
unremarkable reason that the peer simply lost its last connection, not because it was explicitly
retired). C has no exception mechanism, so `oft_peer_send()`/`_rekey()` just return `OFT_ERROR`
either way; C also has no `is_connected()` accessor at the peer level at all, unlike its
per-connection `oft_connection_is_connected()`, since `oft_peer_close()` frees the peer struct
immediately rather than leaving it alive-but-disconnected the way a closed
`oft_connection`/`OftConnection`/`IOftConnection` remains queryable — there's no "closed but not yet
freed" peer state in C to report on. Rust, like C, has no exception mechanism, but unlike C it does
distinguish outcomes: every fallible member returns `Result<_, OftError>`, with
`OftError::Disconnected` covering both the "explicitly retired" and "simply lost its last
connection" cases uniformly, matching `OftDisconnectedException`'s own scope.

C#'s `IOftPeer.Dispose()`/`.Disconnect()` use the immediate and graceful per-connection teardown
respectively for every connection the peer holds — `.Disconnect()` genuinely waits for all of it to
finish, and `.Drop()` uses the same graceful, awaitable teardown `.Disconnect()` does. Java's
`OftPeer.close()`/`.drop()` both use the immediate per-connection teardown regardless of which
peer-level method is called, so neither waits — a deliberate divergence from C#, safe in Java only
because its connections are garbage-collected either way, unlike the manually-scoped resources this
distinction was designed around in C#. C keeps `oft_peer_close()` on the graceful, thread-joining
`oft_connection_close()` per connection (`oft_peer_drop()` still uses the immediate
`oft_connection_disconnect()`, unchanged): `oft_connection_disconnect()` frees nothing, only closes
the connection, so switching `oft_peer_close()` to it instead would leak every connection's memory —
a purely C-specific constraint, since `oft_connection_close()` is the *only* function that ever calls
`free()` on an `oft_connection`. Rust follows C's split rather than Java's: `Peer::drop()` uses the
immediate `Connection::disconnect()`, but `Peer::close()` uses the graceful, thread-joining
`Connection::close()` per connection — a Rust `Connection` isn't garbage-collected the way Java's
is, so `close()` (the operation meant to fully retire the peer, with nothing left running
afterward) waits for every one of its connections' background I/O threads to actually exit before
returning, rather than leaving some still winding down after `Peer::close()` itself has already
returned.

#### Post-handshake connection validation

All four ports expose an optional callback, invoked once per connection after the OFT hail exchange
completes, that can reject a connection based on more than just its certificate in isolation — but
the shape of what it's handed follows each language's own TLS API, not a common struct:

- C#'s `OftConnectionOptions.ConnectionValidation` (`OftConnectionValidationCallback`) is `async`
  (`Task<bool>`) and passes `OftIdentity`/certificate/chain/`sslErrors`
  (`X509Certificate2?`/`X509Chain?`/`SslPolicyErrors`), computed by extending the same
  BouncyCastle-based validation `CertificateValidation` already uses mid-handshake to also return its
  chain/policy-errors instead of discarding them.
- Java's `OftConnectOptions`/`OftHostOptions`/`OftPeerOptions`' `connectionValidation`
  (`OftConnectionValidationCallback`) is a blocking `boolean`, matching Java's overall call style, and
  passes `OftIdentity`/`Certificate[]` (the peer's chain, leaf first, from the negotiated
  `SSLSession`)/the `SSLSession` itself — there's no Java equivalent of `sslErrors` to report, since a
  `TrustManager` either accepts a chain or aborts the handshake outright, with no partial
  "accepted with these errors" result to surface afterward.
- C's `oft_connect_options`/`oft_host_options`/`oft_peer_options`' `connection_validation`
  (`oft_connection_validation_callback`) is likewise blocking (returns `int`), and passes
  `oft_identity *`/`X509 *`/`STACK_OF(X509) *` (borrowed from the connection's `SSL *`, valid only for
  the callback's duration)/`long verify_result` — OpenSSL's `SSL_get_verify_result()`, the closest
  actual equivalent to C#'s `sslErrors` this port has.
- Rust's `ConnectionOptions.connection_validation` (`Arc<dyn Fn(&Identity) -> bool + Send + Sync>`)
  is likewise blocking, and passes only `&Identity` — `rustls` exposes no certificate chain beyond
  the leaf (already captured in `Identity.certificate`) and no separate policy-errors/verify-result
  value distinct from the handshake either succeeding or failing outright, so this is the simplest
  of the four shapes.

All four default to `NULL`/`null`/`None` (accept every connection) and fail connection establishment
if the callback rejects one; none is mirrored from another — each was designed against what its own
TLS stack actually exposes at this point, with only the invocation point and accept/reject semantics
shared.

## Concurrency model

Every connection owns two background threads/tasks for its whole lifetime:

- A **receive loop**, continuously reading and dispatching inbound frames.
- A **send loop**, draining the outbound priority queues (see [OFT.md §5-§6](OFT.md#5-priority)) and
  writing packets, waiting for each one's `Receipt` before sending the next (see
  [OFT.md §4.1](OFT.md#41-acknowledgement-and-flow-control)).

A third background task per connection sends the periodic `Poll` frame and runs the liveness watchdog
check (see [OFT.md §10](OFT.md#10-liveness-polling)); a fourth, optional one drives an automatic
rekey interval if configured. A listener runs its own accept loop on a background thread/task,
dispatching each newly accepted connection's handshake so a slow one never delays accepting the next;
a peer runs its own eviction-check loop as well.

C# implements these as `Task`s on the thread pool; Java as daemon `Thread`s (plus
`ScheduledExecutorService`s for the timers); C as POSIX `pthread`s. Rust is the exception - see
below.

### Rust: a single I/O thread per connection

Rust's `Connection` deliberately deviates from the receive/send/poll-loop split above: it uses one
background thread that owns the connection's socket and `rustls` state exclusively, rather than
2-4 separate threads. This isn't a simplification for its own sake - `rustls::Connection` isn't
`Sync`, and its read and write paths share internal state, so letting separate threads read and
write it concurrently would require a mutex spanning the whole I/O path, which would let a thread
blocked in a read stall an urgent write (like a `Receipt`) indefinitely. The single thread instead
performs a short-timeout blocking read each iteration, using the gaps to also drain a small
work queue that `send()`/`rekey()`/`disconnect()` push onto from any calling thread, service the
liveness/poll timers, and send the next scheduled packet - see
[Docs/Rust.md](Rust.md#concurrency-model) for the full design, including how `disconnect()` uses a
cloned socket handle to unblock that thread's read immediately rather than waiting for its next
timeout tick.

### Rekeying and thread safety

C#/Java/C trigger a TLS `KeyUpdate` (manual or automatic) **only from the connection's own receive
thread/task**, never from an arbitrary caller thread, funneling rekey requests through a queue the
receive loop drains. This is required for correctness, not just convenience: a locally initiated
`KeyUpdate` and an inbound one requested by the peer (processed as a side effect of an ordinary
read, which may itself need to write a reciprocal `KeyUpdate`) both touch the same connection's
read and write state, and none of the three TLS libraries used (BouncyCastle, JSSE, OpenSSL) guard
that interaction against concurrent access on their own — observed directly in Java as a
`bad_record_mac` alert when both peers happened to rekey at nearly the same moment during
development. Running the update on the receive thread/task guarantees it never runs concurrently
with the read path, since one thread can't do both at once; it's additionally wrapped in the same
write-serialization primitive used for ordinary application packet writes, so it can't interleave
with a send-loop write either. Rust satisfies this same rule by construction rather than by
explicit coordination: since one thread owns the entire connection (see above), every read, write,
and `refresh_traffic_keys()` call it makes is already serialized against every other one - there's
no second thread it could race against in the first place.

`rustls::ConnectionCommon::refresh_traffic_keys()` is a safe, public method for exactly this, so
Rust's `rekey()` genuinely rekeys the connection like every other port's does - no TLS library used
by any port in this repository lacks a way to initiate a `KeyUpdate` from the application side.

## Security modes

[OFT.md §9](OFT.md#9-security-modes) describes the four security modes (`Trusted`/`Secure`/
`ServerAuthentication`/`DualAuthentication`) at the protocol level; each implementation exposes the
same four as an enum on its connection/host/peer options (`OftSecurityMode` in C#/Java,
`enum oft_security_mode` in C, `SecurityMode` in Rust). `ServerAuthentication` is rejected outright
by every peer component (`OftPeerFactory.Create()` in C#, `OftPeer.create()` in Java,
`oft_peer_create()` in C, `Peer::new()` in Rust) — a peer has no fixed client/server delineation, so
it can't express a one-sided authentication requirement; use `DualAuthentication` instead.

One implementation detail worth calling out: under `Secure` mode, the accepting side (a listener or a
listening peer) generates its own throwaway certificate rather than using one the caller supplies.
That certificate/context is **resolved once per listener, not once per accepted connection** —
generating a fresh keypair is expensive enough (RSA-2048 keygen) that doing it per connection would
meaningfully slow down or destabilize connection establishment under load. All four implementations
resolve it at host-time and reuse it for every connection that listener accepts afterward.

Rust's `rustls::ClientConfig` and `ServerConfig` are two separate types; a `Peer` under
`DualAuthentication` needs both, and this port builds each independently from the same shared
`identity`/`root_certificates` option fields rather than from one bundled, role-specific context
object the way C#'s `X509Certificate2`-plus-validation-callback pairing or Java's single
`SSLContext` do.

## Memory ownership

How a received message's payload is owned differs by language, following each one's own memory
model:

- **C#** delivers received data as an `IMemoryOwner<byte>` backed by pooled memory
  (`MemoryPool<byte>.Shared`) — the callback owns it, and disposing it (optional, but recommended for
  prompt reuse) returns the memory to its pool. Data passed to `Send` is either copied (plain
  `ReadOnlyMemory<byte>` overload) or ownership-transferred (`IMemoryOwner<byte>` overload, disposed
  by the connection once the send completes), matching this repository's general pooled-memory
  conventions (see [`AGENTS.md`](../AGENTS.md)). A peer's `ReceivedHandler` delivers this same pooled
  memory as its own argument, alongside a separate `OftIdentity` argument rather than one bundled type.
- **Java** delivers received data as an ordinary `byte[]`; the JVM garbage collector reclaims it once
  the received callback is done with it — no explicit release needed.
  `OftConnection.send(byte[], int, Object)` copies its input; the caller retains ownership of its own
  buffer. A peer's `setReceivedHandler` delivers this same `byte[]` as its own argument, alongside a
  separate `OftIdentity` argument, needing no explicit release either.
- **C** delivers received data as a `malloc()`-allocated `uint8_t *`/`length`; ownership passes to the
  received callback, which **must** `free()` it when done. `oft_connection_send()` copies the data
  it's given; the caller retains ownership of its own buffer. A peer's `oft_peer_received_callback`
  delivers this same `malloc()`-allocated payload directly, alongside a separate `const oft_identity *`
  argument — borrowed from the underlying connection, valid only for the duration of that one call,
  and never freed by the callee (unlike the payload, which it must `free()`).
- **Rust** delivers received data as an owned `Vec<u8>` — ordinary Rust ownership, the callback gets
  the buffer outright with no separate release step. `Connection::send`/`Peer::send` take ownership
  of the `Vec<u8>` passed to them (moved, not copied); a multi-packet send copies out only the bytes
  each chunk needs, so the caller never needs to keep the original buffer alive itself beyond the
  call. A peer's `received_handler` delivers this same `Vec<u8>` as its own argument, alongside a
  separate `Identity` argument, needing no explicit release either.

## See also

- [OFT.md](OFT.md) — the wire protocol specification.
- [CSharp.md](CSharp.md), [Java.md](Java.md), [C.md](C.md), [Rust.md](Rust.md) — per-language API
  reference and examples.
- [`AGENTS.md`](../AGENTS.md) — coding conventions and the cross-port alignment policy.
