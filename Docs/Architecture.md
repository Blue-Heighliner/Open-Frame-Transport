# Architecture

This document describes how the [OFT protocol](OFT.md) is implemented: the components every port
exposes, how they relate to each other, and the concurrency/memory model each language uses to
implement the same wire behavior. For language-specific API examples, see
[CSharp.md](CSharp.md), [Java.md](Java.md), and [C.md](C.md).

## Implementations

| | Language | Location | TLS library |
|---|---|---|---|
| Reference implementation | C# (.NET) | [`Core/`](../Core) | BouncyCastle (`Org.BouncyCastle.Tls`) |
| Port | Java | [`Ports/Java/`](../Ports/Java) | JSSE (`javax.net.ssl`) |
| Port | C | [`Ports/C/`](../Ports/C) | OpenSSL |

All three implement the same wire protocol and the same three-component API shape, verified against
each other via real loopback TCP/TLS tests (no mocked sockets). [`AGENTS.md`](../AGENTS.md) has an
explicit convention that the three ports' APIs — method names, semantics, and option shapes — stay
aligned as much as is practical, adapted only where a language's idioms genuinely require it; this
document calls out where and why each one differs.

## Components

Every implementation exposes the same three entry points, under names adapted to each language's
conventions (see the per-language docs for exact type/method names):

- **A connector** — dials out to a remote host:port, performs the TLS handshake and hail exchange,
  and returns an established connection. Stateless: creating a connector holds no resources, and it
  does not track or own the connections it creates — the caller is responsible for each one's
  lifetime.
- **A hoster** — starts listening on a local endpoint and returns a listener. Also stateless: each
  call to host a listener is a one-shot "start listening now" operation, independent of any other.
  The returned listener notifies the caller of each accepted, fully-established inbound connection.
  A listener does not track the connections it has accepted; closing it only stops accepting new
  ones, leaving already-accepted connections running. There is no way to reopen a closed listener —
  host a fresh one instead.
- **A peer** — a connection-pooling convenience layer built on top of one connector and one hoster.
  Sending a message to a `host:port` transparently reuses an existing outbound connection or creates
  and caches a new one. A peer may optionally also listen for inbound connections, folding them into
  the same pool. Idle, expired, or excess cached connections are disconnected automatically
  (configurable by time since last activity, maximum connection age, and maximum connection count,
  evicting the oldest first) — except a connection with any unacknowledged outbound or
  not-yet-reassembled inbound data is never evicted, regardless of these limits, so in-flight data is
  never silently dropped. There is no way to enumerate or individually address a connection the peer
  holds; rekey and disconnect operations act on all of them at once. A peer exposes only a received-
  message notification, covering every connection it holds and identifying which one via that
  notification's own connection argument (needed for replying) — it has no disconnected or connected
  notification of its own: connection lifecycle (establishing, reconnecting, evicting) is the peer's
  own implementation detail, deliberately not surfaced, so a caller can never observe or be notified
  about the individual connections it holds beyond what a received message's own connection argument
  reveals in passing.

A **connection**, produced by either the connector or an accepted-connection notification from a
listener, is the same type either way and exposes:

- Message send, taking a payload and a priority (see [OFT.md §5-§7](OFT.md#5-priority)).
- Manual rekey (see [OFT.md §8](OFT.md#8-rekeying)), plus an optional automatic rekey interval
  configured on the connection's options.
- Notification of every fully-received application message.
- Notification of disconnection, with the exception (if any) that caused it.
- Metadata: remote endpoint, the peer's hail `info`, and connect/last-sent/last-received timestamps.
- Manual disconnect.

## General API flow

The typical flow, independent of language:

1. **Server side**: create a hoster, call its host method with a listen endpoint and options,
   assign a callback for accepted connections, and — inside that callback — assign a callback for
   received messages on each connection.
2. **Client side**: create a connector, call its connect method with a target host/port and options,
   and assign a received-message callback on the returned connection.
3. **Either side** sends messages on any connection it holds, at any point after it's established,
   with an optional priority.
4. **Peer-to-peer**: create a peer with options, optionally call its open method to also accept
   inbound connections, and call its send method with a target host/port — it transparently
   connects (and caches the connection) the first time, and reuses the cached connection afterward.
   Assign a received-message callback on the peer itself (not on individual connections) to handle
   messages from every connection it holds, inbound or outbound.

All three ports expose one independent, single-slot callback per notification kind — there is no
bundled "handler object" implementing multiple notification methods at once. A connection has a
`ReceivedHandler` and a `DisconnectedHandler`; a listener has a `ConnectedHandler`; a peer has only
its own `ReceivedHandler`, covering every connection it holds — deliberately no
`DisconnectedHandler`/`ConnectedHandler` of its own (see [Components](#components) above for why).
Each notification kind is assigned (or reassigned) completely independently of the others —
assigning a connection's `ReceivedHandler`, for example, never disturbs its `DisconnectedHandler`.
Assigning a new callback (including `null`/`NULL`) always fully replaces whatever was assigned
before, so there is always at most one recipient of a given notification — this makes it unambiguous
who owns any data the notification carries (see [Memory ownership](#memory-ownership) below), unlike
a multicast event/listener-list design where several independent subscribers might all receive - and
all think they own - the same data.

The concrete shape is adapted per language: C#'s `Action<T>` properties (`IOftConnection.ReceivedHandler`/
`.DisconnectedHandler`, `IOftListener.ConnectedHandler`, `IOftPeer.ReceivedHandler`) are assigned a
lambda or method group directly, no interface to implement. Java mirrors this with
`Consumer<T>`/`BiConsumer<T, U>` setter methods (`OftConnection.setReceivedHandler`/
`.setDisconnectedHandler`, `OftListener.setConnectedHandler`, `OftPeer.setReceivedHandler`). C, which
has neither interfaces nor lambdas, uses a plain function pointer plus a `void *user_data`, set
independently per notification kind (`oft_connection_set_received_callback`,
`oft_connection_set_disconnected_callback`, `oft_listener_set_connected_callback`,
`oft_peer_set_received_callback`). A plain callback per notification kind needs no language feature C
lacks, so all three ports converge on this same shape despite their differing idioms.

### Buffered notifications prevent a connect/disconnect/receive message-loss race

A connection can begin receiving packets the instant its TLS handshake and hail exchange finish —
potentially before the caller that just received the connection (from a connect call or a listener's
accepted-connection notification) has had a chance to assign a received-message handler/callback on
it. Without precaution, a peer that replies (or disconnects) immediately upon connecting could have
its first message — or its disconnection — silently lost: delivered to nothing, before the caller
ever gets a chance to assign a handler/callback.

All three ports avoid this race the same way, and it applies uniformly to every notification kind
(received, disconnected, and a listener's accepted-connection "connected" notification): each
notification kind is backed by its own buffering slot that holds onto everything raised before a
callback is first assigned to it, then delivers that backlog to it, in order, before it becomes the
live target for anything raised afterward — C#'s custom `OftBufferedHandlerSlot<TDelegate>` type
(`Core/src/Internal/OftBufferedHandlerSlot.cs`), Java's `BufferedHandlerSlot`
(`Ports/Java/.../BufferedHandlerSlot.java`), and C's `oft_event_buffer`
(`Ports/C/src/oft_event_buffer.{h,c}`) all share the same core guarantee, adapted to each language's
callback shape. In every case, only the very first non-`null`/`NULL` assignment to a given slot ever
triggers a flush — reassigning that slot's callback afterward (including to `null`/`NULL`, which
causes future notifications to simply be dropped) never re-triggers one; every other slot on the
same connection/listener/peer buffers and flushes completely independently.

This means a caller in any of the three languages can simply get the connection back from the
connect call (or receive it via the connected notification) and assign a received or disconnected
callback afterward, in any order, with no special API for it — nothing is ever silently lost between
establishment and assignment. The same guarantee applies symmetrically to a listener's connected
notification: a caller can assign a connected callback at any point after a hoster returns the
listener, even after connections have already been accepted, and still receive the backlog of any
accepted before it did.

Because the backlog is only ever flushed to the *first-ever* callback assigned to a given slot, this
is not a general replay mechanism: assigning a second callback to the same slot later sees only live
notifications from that point on, not the earlier history. This matters for tests and application
code alike — assigning a callback after an earlier, unrelated send has already happened means seeing
that earlier message (now delivered via the backlog) before whatever the caller actually intended to
observe next.

### Where a port's flow differs from this

- **Blocking vs. asynchronous calls.** C#'s API is fully `async`/`Task`-based throughout. Java's API
  is blocking (ordinary method calls, with `CompletableFuture` used only for results that complete
  in the background, like message delivery/rekey completion) — Java call sites don't need `await`,
  but a call like connect blocks the calling thread until the TCP connection and handshake finish.
  C's API is likewise blocking throughout, with completion states (delivered, cancelled, or the
  connection closed) reported via explicit `wait` calls or return codes rather than futures/promises.

## Concurrency model

Every connection owns two background threads/tasks for its whole lifetime:

- A **receive loop**, continuously reading and dispatching inbound frames.
- A **send loop**, draining the outbound priority queues (see [OFT.md §5-§6](OFT.md#5-priority)) and
  writing packets, waiting for each one's `Receipt` before sending the next (see
  [OFT.md §4.1](OFT.md#41-acknowledgement-and-flow-control)).

A third background task per connection sends the periodic `Poll` frame and runs the liveness
watchdog check (see [OFT.md §10](OFT.md#10-liveness-polling)); a fourth, optional one drives an
automatic rekey interval if configured. A listener runs its own accept loop on a background
thread/task, dispatching each newly accepted connection's handshake so a slow one never delays
accepting the next; a peer runs its own eviction-check loop on a background thread/task as well.

C# implements these as `Task`s on the thread pool; Java as daemon `Thread`s (plus
`ScheduledExecutorService`s for the timers); C as POSIX `pthread`s.

### Rekeying and thread safety

All three implementations trigger a TLS `KeyUpdate` (manual or automatic) **only from the
connection's own receive thread/task**, never from an arbitrary caller thread, funneling rekey
requests through a queue that the receive loop drains. This is required for correctness, not just
convenience: a locally initiated `KeyUpdate` and an inbound one requested by the peer (processed as
a side effect of an ordinary read, which may itself need to write a reciprocal `KeyUpdate`) both
touch the same connection's read and write state, and none of the three TLS libraries used
(BouncyCastle, JSSE, OpenSSL) guard that interaction against concurrent access on their own —
observed directly in Java as a `bad_record_mac` alert when both peers happened to rekey at nearly
the same moment during development. Running the update on the receive thread/task guarantees it
never runs concurrently with the read path, since one thread can't do both at once; the update is
additionally wrapped in the same write-serialization primitive used for ordinary application packet
writes, so it can't interleave with a send-loop write either.

## Security modes

[OFT.md §9](OFT.md#9-security-modes) describes the four security modes (`Trusted`/`Secure`/
`ServerAuthentication`/`DualAuthentication`) at the protocol level; each implementation exposes the
same four modes as an enum on its connection/host/peer options (`OftSecurityMode` in C#/Java,
`enum oft_security_mode` in C). `ServerAuthentication` is rejected outright by every peer component (`OftPeerFactory.Create()` in
C#, `OftPeer.create()` in Java, `oft_peer_create()` in C) — a peer has no fixed client/server
delineation, so it cannot express a one-sided authentication requirement; use
`DualAuthentication` instead.

The one implementation detail worth calling out: under `Secure` mode, the accepting side (a listener
or a listening peer) generates its own throwaway certificate rather than using one supplied by the
caller. That certificate/context is **resolved once per listener, not once per accepted
connection** — generating a fresh keypair is expensive enough (RSA-2048 keygen) that doing it per
connection would meaningfully slow down or destabilize connection establishment under load. All
three implementations resolve it at host-time and reuse it for every connection accepted by that
listener afterward.

## Memory ownership

How a received message's payload is owned differs by language, following each one's own memory
model:

- **C#** delivers received data directly as an `IMemoryOwner<byte>` backed by pooled memory
  (`MemoryPool<byte>.Shared`) — the callback owns it, and disposing it (optional, but recommended
  for prompt reuse) returns the memory to its pool. Data passed to `Send` is either copied (for a
  plain `ReadOnlyMemory<byte>` overload) or ownership-transferred (for an `IMemoryOwner<byte>`
  overload the connection disposes once the send completes), matching this repository's general
  pooled-memory conventions (see [`AGENTS.md`](../AGENTS.md)).
- **Java** delivers received data as an ordinary `byte[]`; the JVM garbage collector reclaims it once
  the received callback is done with it — no explicit release needed.
  `OftConnection.send(byte[], int)` copies its input; the caller retains ownership of its own
  buffer.
- **C** delivers received data as a `malloc()`-allocated `uint8_t *`/`length`; ownership passes to
  the received callback, which **must** `free()` it when done. `oft_connection_send()` copies the
  data it's given; the caller retains ownership of its own buffer.

## See also

- [OFT.md](OFT.md) — the wire protocol specification.
- [CSharp.md](CSharp.md), [Java.md](Java.md), [C.md](C.md) — per-language API reference and examples.
- [`AGENTS.md`](../AGENTS.md) — coding conventions and the cross-port alignment policy.
