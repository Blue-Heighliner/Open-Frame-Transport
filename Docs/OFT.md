# Open Frame Transport (OFT) — Protocol Specification

This document specifies the [Open Frame Transport (OFT)](../README.md) wire protocol in full — an
application-layer protocol that runs on top of TCP and TLS. For a summary of its features, see the
top-level [README.md](../README.md); for how the protocol's concepts map onto each language's
components and concurrency model, see [Architecture.md](Architecture.md).

## 1. Layering

```
┌─────────────────────────────┐
│   Application messages       │
├─────────────────────────────┤
│   OFT (framing, priority)    │
├─────────────────────────────┤
│   TLS                        │
├─────────────────────────────┤
│   TCP                        │
└─────────────────────────────┘
```

A connection is established in three steps:

1. A TCP connection is formed (the *initiator* dials, the *acceptor* listens).
2. A TLS session is negotiated on top of the TCP connection. TLS 1.3 is the only version OFT ever
   negotiates - there is no fallback to an older version and no option to allow one. The acceptor
   authenticates as a TLS server; the initiator authenticates as a TLS client. Mutual authentication
   is optional and is a deployment choice, not a protocol requirement.
3. Both sides exchange a **hail** (§3). Once each side has both sent and received a hail, the OFT
   connection is *established* and either side may send messages.

Because step 3 requires a round trip that only succeeds if the peer is genuinely speaking OFT over
a valid TLS session, performing a hail exchange and then closing the connection is a complete,
protocol-correct way to "ping" a remote endpoint and confirm it is reachable and OFT-capable.

## 2. Wire format

All structured data is encoded with Protocol Buffers. See [`OFT.proto`](OFT.proto).

Only two message types are ever written to the connection: `Hail` and `Packet`. There is no wrapper
message and no `oneof` — instead, position on the stream tells a reader which type comes next: **the
very first message a side ever reads on a connection is a `Hail`, and every message after it, for
the rest of the connection's life, is a `Packet`**. Because a side sends and reads exactly one hail
(§3), once per connection, both ends always agree on where the boundary is without needing a tag to
disambiguate it. This holds across a rekey (§8) too: a rekey derives fresh keys in place on the same
TLS session rather than starting a new one, so there's no second hail to exchange in the first
place.

Messages are written back-to-back with a **standard varint length prefix**, the same convention
protobuf implementations use for writing multiple delimited messages onto one stream (as in
`writeDelimitedTo` / `parseDelimitedFrom`): each message is preceded by its serialized byte length,
encoded as a protobuf-style base-128 varint, followed by exactly that many bytes of the serialized
message. A reader repeatedly reads a varint, reads that many bytes, and parses either a `Hail` or a
`Packet` from them, depending on whether it's the very first message read on the connection or not.
The one exception is a **zero-length frame** (a varint of `0` followed by no bytes) anywhere after
the initial `Hail`: a reader always treats that as a `Poll` (§10) rather than attempting to parse a
`Packet` from it — see §4 for why this can never collide with a genuine `Packet`.

## 3. Hail

```protobuf
message Hail {
  string version = 1;
  string info = 2;
}
```

- `version` is the OFT protocol version string of the sender (e.g. `"oft/1"`). It identifies the
  protocol revision being spoken, not the application.
- `info` is an opaque, application-controlled string. OFT does not interpret it; it exists so
  applications can exchange identity, capability, or routing information (node id, application
  name/version, feature flags, etc.) as part of connection setup without needing a second round
  trip.

Immediately after the TLS session is up, each side sends one `Hail` frame without waiting for the
peer. A side considers the connection established once it has *received* the peer's hail (it may
already have sent its own before, concurrently with, or after that). If the peer's `version` is
incompatible, the receiving side closes the connection instead of proceeding.

The hail is not itself acknowledged with a `Receipt` — it's a one-shot handshake that exists
exactly once, at a fixed position on the stream, so there's nothing to correlate an acknowledgement
with.

## 4. Packets

```protobuf
message Packet {
  optional uint32 control = 1;
  bytes data = 2;
}
```

`control` identifies what the packet means:

| Value | Name          | Meaning                                                              |
|------:|---------------|-----------------------------------------------------------------------|
| 0     | Receipt       | Acknowledges delivery of the previous packet. `data` is empty.       |
| 1     | Unit          | A complete message that fits in a single packet. `data` is the message. |
| 2     | Completion    | The final packet of a multi-packet message. `data` is its last chunk. |
| 3     | Cancellation  | Abandon the in-progress multi-packet message. `data` is empty.       |
| ≥ 4   | Data          | A chunk of a multi-packet message on priority channel `control − 4`. |

Rekeying (§8) has no `control` value of its own: it's a TLS 1.3 `KeyUpdate`, a record on the
underlying TLS session rather than an OFT `Packet`, so it's invisible at this layer entirely.

`Poll` (§10) is not a `control` value at all: it is a bare zero-length frame — the varint length
prefix (§2) is `0`, with no `Packet` bytes following it. This falls out of protobuf's proto3 wire
format almost for free: a message with every field left at its default value serializes to zero
bytes, since proto3 never emits a field tag for a default value. Framing a `Poll` this way, rather
than as a dedicated `control` value carried in an otherwise-empty `Packet`, avoids sending a length
prefix *and* an empty payload for what is, on the wire, the most frequently sent packet on an idle
connection. The one wrinkle is `Receipt`: `control = 0` is a real, frequently-sent value, and a
`Receipt` (`control = 0`, `data` empty) would otherwise serialize identically to `Poll`'s zero-length
frame — indistinguishable on the wire, which is not survivable, since a reader has to know which one
it got. `control` is declared `optional` specifically to avoid this: proto3's *explicit field
presence* (distinct from plain default-value omission) means a field that has been explicitly set —
to any value, including its default — is always serialized, tag and all, while a field that was
simply never touched is not. Every `Packet` this codebase ever constructs sets `control` explicitly,
so every genuine `Packet`, including a `Receipt`, always serializes to at least a couple of bytes;
only a frame with no `Packet` fields set at all — i.e. `Poll` — serializes to zero, and a reader can
therefore always tell the two apart before even attempting to parse a nonempty frame as a `Packet`.

The maximum size of `data` in a packet is a configurable connection setting (`MaxPacketDataSize`);
it bounds both how large a `Unit` message can be and how a larger message gets chunked into `Data`
packets.

### 4.1 Acknowledgement and flow control

Every packet except `Receipt` and `Poll` is acknowledged: whenever a side receives a `Unit`, `Data`,
`Completion`, or `Cancellation` packet, it replies with a `Receipt` for it as soon as
possible, independent of whatever the local side is waiting to send outbound, so a stalled outbound
queue never causes a peer's packets to go unacknowledged. Not acknowledging `Receipt` itself is what
keeps the exchange from recursing forever; `Poll` is never acknowledged either, since it exists
purely to be *received* (§10) — replying to it would only double the traffic without adding
information.

`Unit`, `Data`, `Completion`, and `Cancellation` packets additionally observe a strict turn-taking
rule: whenever a side sends one, it must wait to receive its `Receipt` before sending its next packet
of one of these four kinds — the connection has exactly one such packet in flight at a time. This
makes the protocol trivially free of ambiguity about which packet a given `Receipt` acknowledges, at
the cost of not pipelining packets; OFT trades raw throughput for a very simple, easy-to-reason-about
correctness model (concurrency instead comes from priority and interruption, §6).

`Poll` is the only exception to turn-taking: it can be sent at any time, without waiting for whatever
`Unit`/`Data`/`Completion`/`Cancellation` packet is currently in flight to be acknowledged first —
see §10 for why it doesn't need to. Rekeying (§8) doesn't interact with turn-taking at all, since it
has no `control` value to take a turn with in the first place.

### 4.2 Messages that fit in one packet

If a message's payload is small enough to fit under `MaxPacketDataSize`, it is sent as a single
`Unit` packet. The receiver treats a `Unit` packet as a complete message the instant it arrives.
Because it is atomic, a `Unit` message is never interrupted and never participates in the
per-priority-channel bookkeeping described below — it's in and out in one round trip.

### 4.3 Messages that need multiple packets

If a message is too large for one packet, the sender picks the priority channel corresponding to
the message's priority (`control = priority + 4`) and sends the message as a sequence of packets on
that channel:

- All but the last chunk are sent as `Data` packets (`control = priority + 4`).
- The last chunk is sent as a `Completion` packet (`control = 2`), which tells the receiver the
  message is now complete and that any packets after it belong to a different message.

A sender may instead abandon a message it has already started sending by sending a `Cancellation`
packet (`control = 3`) instead of continuing with more `Data`/`Completion` packets for it. This
tells the receiver to discard whatever partial data it has buffered for that message; subsequent
packets belong to a different message.

### 4.4 Identifying which channel a Completion/Cancellation belongs to

`Completion` and `Cancellation` packets do not carry a priority — by the time one is sent, the
receiver already knows, from the `Data` packets it has seen, which channels have a message in
progress. Because packets are strictly acknowledged one at a time (§4.1) and interruption (§6) can
only ever suspend a *lower*-priority in-progress message to let a *higher*-priority one run, at any
instant when a `Completion` or `Cancellation` arrives, the message it belongs to is unambiguous: it
is the one on the **highest-priority channel that currently has a pending (started, not yet
finished) message**.

A receiver therefore keeps, per connection, a queue/buffer of received bytes for each priority
channel that currently has an in-progress message:

- On a `Data` packet (`control ≥ 4`): append `data` to the buffer for channel `control − 4`
  (creating it if it doesn't exist yet).
- On a `Completion` packet: take the buffer for the highest-priority channel that has one, append
  the final `data`, deliver the concatenated bytes to the application as a completed message, and
  discard the buffer.
- On a `Cancellation` packet: take the buffer for the highest-priority channel that has one, discard
  it without delivering anything to the application.

In both cases a `Receipt` is still sent back per §4.1 regardless of channel.

## 5. Priority

A message is submitted for sending with a non-negative integer **priority**. Larger values mean
higher priority; `0` is the lowest priority and the default if an application doesn't care.
Priority only affects **send-side scheduling** — it decides which queued/in-progress message gets
the next outbound packet slot. It has no effect on `Unit` messages beyond deciding when they get
sent relative to other pending messages, since a `Unit` message is a single packet with no ordering
to interrupt.

Because a `Unit` message is still just one entry in the same priority-ordered scheduling described
in §6, it is scheduled exactly like a multi-packet message would be, even though it can never be
interrupted itself once sent: if a higher-priority multi-packet message is already sending when a
lower-priority `Unit` message is submitted, the `Unit` message does not get a packet slot until that
higher-priority message (and any other higher-priority channel) has been fully drained — it waits
its turn behind the entire in-progress higher-priority message, not just its next packet.
Conversely, a `Unit` message submitted at a *higher* priority than an in-progress multi-packet
message causes that lower-priority message to be interrupted (§6) on the very next free slot, the
same as if a higher-priority multi-packet message had been submitted instead.

## 6. Interruption

Because only one packet may be in flight at a time (§4.1), a connection's outbound side is really
just a scheduler choosing, every time the single packet "slot" frees up (i.e. the previous packet's
`Receipt` has arrived), what to send next:

1. If any priority channel has a multi-packet message already in progress or newly queued, choose
   the **highest-priority** such channel.
2. Send that message's next packet: its first `Data` packet if it hasn't started yet, its next
   `Data`/`Completion` packet if it's partway through, based on how much of `MaxPacketDataSize` has
   already been consumed.
3. If a `Unit` message is queued and no in-progress multi-packet message outranks it, it may be sent
   in its own packet instead.

The effect: if a low-priority multi-packet message is partway through sending and a higher-priority
message (of either kind) is submitted, the scheduler switches to the higher-priority message on the
very next free slot. The low-priority message's send position (how many bytes of it have gone out)
is retained exactly as it was, untouched, on the sender side — the receiver simply sees no more
`Data` packets arrive on that channel for a while. Once every higher-priority channel has been
drained (finished with a `Completion`/`Cancellation`, or has nothing left queued), the scheduler
picks the interrupted message back up and continues sending its remaining chunks from where it left
off. This is what "interruption" means in OFT: it is entirely a property of send-side scheduling —
the wire format doesn't need a distinct "pause" signal, because simply not sending more `Data`
packets on a channel *is* pausing it, and the receiver's per-channel buffers (§4.4) are exactly what
let an arbitrary number of channels sit "paused" mid-message at once.

## 7. Cancellation from the application's perspective

An application can cancel a message it previously queued to send at any time before it completes:

- If no bytes of it have gone out yet (it was still waiting in the scheduler's queue), it is simply
  removed — the receiver never learns it existed.
- If it is a `Unit` message, once it has been sent it has already been fully delivered; there is
  nothing left to cancel.
- If it is a multi-packet message that has already had at least one `Data` packet sent, cancelling
  it causes a `Cancellation` packet to be sent for it (once it is next in line for the outbound
  slot, respecting priority like any other packet) instead of its remaining `Data`/`Completion`
  packets.

## 8. Rekeying

OFT connections can rekey their TLS session — derive fresh traffic keys for both directions —
without tearing down the underlying TCP connection or starting a new TLS session. This is useful for
long-lived connections that want to bound how much traffic is ever protected by one set of TLS keys,
whether for compliance or to limit the blast radius of a key compromise.

A rekey can be initiated manually by the application on either side, or automatically on a
configured time interval.

Rekeying is implemented directly as a TLS 1.3 `KeyUpdate` (RFC 8446 §4.6.3), a post-handshake
message that rotates the traffic secret for the sender's write direction in place, on the same,
continuous encrypted record stream. Because a `KeyUpdate` is just another record interleaved with
whatever application data is already flowing, it needs no coordination at the OFT protocol level at
all: there is no `Rekey` control value (§4), nothing to acknowledge, and nothing that interacts with
turn-taking (§4.1). Rekeying a connection is simply asking the underlying TLS implementation to send
a `KeyUpdate`, and returning once that request has been made.

Each reference implementation still needs to be careful about *how* it invokes the underlying TLS
library's `KeyUpdate` support, since a locally initiated update and an inbound update requested by
the peer (which the TLS library processes as a side effect of reading incoming data) both ultimately
touch the same connection's read and write state, and not every TLS library's API guards that
interaction on its own. All three ports handle this by only ever calling into a connection's TLS
layer to trigger a `KeyUpdate` from the same thread that also performs that connection's reads,
which trivially rules out the two ever running concurrently against each other, regardless of
whether the underlying library synchronizes them itself. See [Architecture.md](Architecture.md) for
more detail on how each language implements this.

## 9. Security modes

Each connection is established under one of four security modes, an explicit, opt-in per-connection
setting (`SecurityMode` in the reference implementation, see [Architecture.md](Architecture.md))
rather than something negotiated on the wire — both sides must be configured compatibly for a given
connection, or the exchange fails outright:

- **Insecure** — skips TLS entirely: step 2 of the establishment sequence in §1 (the TLS handshake)
  is omitted, and step 3 (the hail exchange) happens directly on the raw TCP connection instead, as
  soon as it's formed. Every part of the protocol layered on top — framing (§2), the hail (§3), and
  packets (§4-§7) — is unchanged; OFT simply runs directly on TCP rather than on TLS-over-TCP. This
  mode is intended for trusted, private networks (e.g. same-host or same-VPC deployments already
  gated by other means) or for testing, where TLS's setup cost or certificate management isn't worth
  paying. It forfeits all of TLS's guarantees: an insecure connection has no confidentiality (the
  whole exchange, hails and every packet, is plaintext on the wire), no integrity protection against
  tampering, and no authentication of either side's identity. Because rekeying (§8) is fundamentally
  a TLS-session operation, an insecure connection also has nothing to rekey — attempting to rekey one
  is a no-op.
- **Secure** (the default) — TLS provides confidentiality and integrity but no authentication of
  either side. The accepting side uses a throwaway certificate it generates internally rather than
  one supplied by the caller, and the connecting side accepts whatever certificate it's presented
  with unconditionally, since there's nothing meaningful to validate an ephemeral certificate
  against.
- **Authentication** — traditional one-way TLS: the accepting side must supply a real certificate,
  which the connecting side validates normally (a caller-supplied callback, or default certificate
  chain/hostname validation).
- **Dual authentication** — mutual TLS: everything `Authentication` requires, plus the connecting
  side must also supply its own certificate(s), which the accepting side requests and validates.

If the two sides are configured with mismatched modes — say, one side sends a TLS `ClientHello`
while the other is configured for `Insecure` and expects a plaintext `Hail` first, or vice versa —
there is no way for either side to detect this cleanly and report a helpful error. Each side simply
sees bytes that don't parse as whatever it was expecting, and closes the connection.

## 10. Liveness polling

Once a connection is established (§1 step 3), each side — independent of whatever application
traffic is or isn't flowing — sends an empty `Poll` frame (a bare zero-length frame, §4) to its peer
on a fixed interval, `PollInterval` (default 1 second). `Poll` is never acknowledged (§4.1) and never
competes with application traffic for turn-taking; it exists purely to guarantee that *something*
crosses the wire in each direction at least that often, so each side has a steady,
application-independent signal that its peer's process and network path are both still alive.

Each side separately tracks when it last received *anything at all* from its peer — a `Poll` packet
or any other kind. If that ever exceeds a second interval, `PollTimeout` (default 5 seconds), without
anything arriving, that side concludes its peer is unreachable (crashed, network-partitioned, or
stuck behind a half-open TCP connection neither the OS nor TLS noticed) and closes the connection
itself, without waiting for the peer to do anything. Both `PollInterval` and `PollTimeout` are
configurable per-connection settings, mirrored across every implementation's connector, hoster/listener,
and peer components (see [Architecture.md](Architecture.md)).

Because `Poll` packets flow continuously regardless of application activity, a connection that is
merely idle — no messages queued in either direction — is never mistaken for a dead one: traffic of
*some* kind is expected at least every `PollInterval` either way, and only a peer that has genuinely
stopped responding ever goes silent for longer than `PollTimeout`. This is a connection-level
liveness check, distinct from and unaffected by a peer component's higher-level idle-connection cache
eviction (which is driven by actual application traffic on a much longer, independently configurable
timescale, typically minutes) — `Poll` traffic is deliberately excluded from what that eviction
mechanism counts as "activity" (see the reference implementation's `LastSentAt`/`LastReceivedAt`),
so an application that never sends anything on a connection can still have it evicted from a peer's
cache on its own schedule, even though the connection itself stays alive at the transport level via
polling the whole time.
