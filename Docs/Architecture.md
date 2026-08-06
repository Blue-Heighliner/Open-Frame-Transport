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
  holds; rekey and disconnect operations act on all of them at once.

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
   register a callback/listener/event handler for accepted connections, and — inside that handler —
   register a callback/listener/event handler for received messages on each connection.
2. **Client side**: create a connector, call its connect method with a target host/port and options,
   and register a received-message callback/listener/event handler on the returned connection.
3. **Either side** sends messages on any connection it holds, at any point after it's established,
   with an optional priority.
4. **Peer-to-peer**: create a peer with options, optionally call its open method to also accept
   inbound connections, and call its send method with a target host/port — it transparently
   connects (and caches the connection) the first time, and reuses the cached connection afterward.
   Register a received-message callback/listener/event handler on the peer itself (not on individual
   connections) to handle messages from every connection it holds, inbound or outbound.

### Where a port's flow differs from this

- **The message-loss race and how each port avoids it.** A connection can begin receiving packets
  the instant its TLS handshake and hail exchange finish — potentially before the caller that just
  received the connection (from a connect call or a listener's accepted-connection notification) has
  had a chance to register a received-message handler on it. Each language avoids this race
  differently, and this is the one place the *shape* of the flow above (steps 1-2) genuinely differs
  per port:
  - **C#** buffers: `Received`/`Disconnected`/`Connected` are backed by a custom event
    implementation that holds onto everything raised before that event's first-ever subscriber
    attaches, then delivers the backlog to that subscriber immediately upon subscribing. This means
    a C# caller can simply `await` the connect call (or receive the connection from the `Connected`
    event) and subscribe to `Received` afterward, in any order, with no special API for it — nothing
    is ever silently lost between establishment and subscription.
  - **Java and C** use an explicit `onEstablished`/`on_established` callback instead: the connect
    method takes an extra callback parameter, invoked synchronously with the new connection *before*
    it starts processing inbound packets. A caller that needs a hard guarantee against missing a
    message sent immediately after connecting registers its received listener/callback inside that
    callback, not after the connect call returns. On the accept side, the listener's
    connected-callback is likewise invoked before the accepted connection starts processing inbound
    packets, so registering a received listener/callback inside it is always race-free the same way.
    Registering afterward (e.g. on the object the plain connect call returns) is not guaranteed
    race-free in these two ports, unlike C#.
- **Blocking vs. asynchronous calls.** C#'s API is fully `async`/`Task`-based throughout. Java's API
  is blocking (ordinary method calls, with `CompletableFuture` used only for results that complete
  in the background, like message delivery/rekey completion) — Java call sites don't need `await`,
  but a call like connect blocks the calling thread until the TCP connection and handshake finish.
  C's API is likewise blocking throughout, with completion states (delivered, cancelled, or the
  connection closed) reported via explicit `wait` calls or return codes rather than futures/promises.
- **Received-message delivery is single-subscriber in Java and C.** C#'s `Received` event supports
  any number of subscribers (ordinary multicast delegate semantics). Java's connection type has a
  single received-listener slot (`addReceivedListener`/`removeReceivedListener` register and
  unregister *the* listener) and C's connection has a single received-callback slot
  (`oft_connection_set_received_callback` sets *the* callback) — both because received-message data
  ownership passes to exactly one recipient (see memory ownership notes below), so there's no
  meaningful way to fan the same buffer out to more than one subscriber. Disconnect notification
  *does* support multiple listeners in all three languages (a `Disconnected` event in C#,
  `addDisconnectedListener`/`removeDisconnectedListener` in Java, and
  `oft_connection_add_disconnected_listener`/`_remove_disconnected_listener` in C, the latter capped
  at a small fixed count to avoid dynamic allocation on the hot path).

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

[OFT.md §9](OFT.md#9-security-modes) describes the four security modes (`Insecure`/`Secure`/
`Authentication`/`DualAuthentication`) at the protocol level; each implementation exposes the same
four modes as an enum on its connection/host/peer options (`OftSecurityMode` in C#/Java,
`enum oft_security_mode` in C), replacing what was originally a plain insecure on/off flag.

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

- **C#** delivers received data as `ReadOnlyMemory<byte>` backed by pooled memory
  (`ArrayPool`/`MemoryPool`), exposed via `IDisposable` event args — disposing them (optional, but
  recommended for prompt reuse) returns the memory to its pool. Data passed to `Send` is either
  copied (for a plain `ReadOnlyMemory<byte>` overload) or ownership-transferred (for an
  `IMemoryOwner<byte>` overload the connection disposes once the send completes), matching this
  repository's general pooled-memory conventions (see [`AGENTS.md`](../AGENTS.md)).
- **Java** delivers received data as an ordinary `byte[]`; the JVM garbage collector reclaims it once
  the received listener is done with it — no explicit release needed.
  `OftConnection.send(byte[], int)` copies its input; the caller retains ownership of its own
  buffer.
- **C** delivers received data as a `malloc()`-allocated `uint8_t *`/`length`; ownership passes to
  the received callback, which **must** `free()` it when done. `oft_connection_send()` copies the
  data it's given; the caller retains ownership of its own buffer.

## See also

- [OFT.md](OFT.md) — the wire protocol specification.
- [CSharp.md](CSharp.md), [Java.md](Java.md), [C.md](C.md) — per-language API reference and examples.
- [`AGENTS.md`](../AGENTS.md) — coding conventions and the cross-port alignment policy.
