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
2. A TLS session is negotiated on top of it. TLS 1.3 is the only version OFT ever negotiates — no
   fallback, no option to allow one. The acceptor authenticates as a TLS server, the initiator as a
   TLS client; mutual authentication is a deployment choice, not a protocol requirement.
3. Both sides exchange a **hail** (§3). Once each side has both sent and received a hail, the
   connection is *established* and either side may send messages.

Because step 3 needs a round trip that only succeeds against a genuine OFT peer over a valid TLS
session, a bare hail exchange followed by closing the connection is itself a complete,
protocol-correct way to "ping" a remote endpoint and confirm it's reachable and OFT-capable.

## 2. Wire format

All structured data is encoded with Protocol Buffers (see [`OFT.proto`](OFT.proto)). Only two
message types are ever written to the connection: `Hail` and `Packet`. There's no wrapper message
and no `oneof` — position on the stream tells a reader which one comes next, since **the first
message either side ever reads on a connection is a `Hail`, and every message after it is a
`Packet`**. A side sends and reads exactly one hail per connection (§3), so both ends always agree
on where that boundary is with no tag needed to mark it — including across a rekey (§8), which
derives fresh keys in place on the existing TLS session rather than starting a new one, so there's
never a second hail to exchange.

Messages are written back-to-back with a **standard varint length prefix** — the same convention
protobuf implementations use for delimited messages on one stream (`writeDelimitedTo` /
`parseDelimitedFrom`): each message is preceded by its serialized byte length as a base-128 varint,
followed by exactly that many bytes. A reader repeatedly reads a varint, reads that many bytes, and
parses a `Hail` or `Packet` from them depending on whether it's the first message on the connection.
The one exception is a **zero-length frame** (a varint of `0`, no bytes following) anywhere after the
initial `Hail`: a reader always treats that as a `Poll` (§10) rather than a `Packet` — see §4 for why
this can never collide with a genuine `Packet`.

## 3. Hail

```protobuf
message Hail {
  string version = 1;
  string info = 2;
}
```

- `version` is the OFT protocol version string of the sender (e.g. `"oft/1"`) — the protocol
  revision being spoken, not the application.
- `info` is an opaque, application-controlled string. OFT never interprets it; it exists so
  applications can exchange identity, capability, or routing information (node id, application
  name/version, feature flags, etc.) during connection setup without a second round trip.

Immediately after the TLS session is up, each side sends one `Hail` frame without waiting for the
peer. A side considers the connection established once it has *received* the peer's hail (it may
have sent its own before, concurrently with, or after that). If the peer's `version` is
incompatible, the receiving side closes the connection instead of proceeding.

The hail is never acknowledged with a `Receipt` — it's a one-shot handshake at a fixed position on
the stream, so there's nothing to correlate an acknowledgement with.

## 4. Packets

```protobuf
message Packet {
  uint32 control = 1;
  bytes data = 2;
}
```

`control` identifies what the packet means:

| Value | Name          | Meaning                                                              |
|------:|---------------|-----------------------------------------------------------------------|
| 0     | Completion    | The final packet of a multi-packet message. `data` is its last chunk, never empty. |
| 1     | Cancellation  | Abandon the in-progress multi-packet message. `data` is empty.       |
| 2     | Receipt       | Acknowledges delivery of the previous packet. `data` is empty.       |
| 3     | Unit          | A complete message that fits in a single packet. `data` is the message; may be empty. |
| ≥ 4   | Data          | A chunk of a multi-packet message on priority channel `control − 4`. |

Rekeying (§8) has no `control` value of its own: it's a TLS 1.3 `KeyUpdate`, a record on the
underlying TLS session rather than an OFT `Packet`, invisible at this layer entirely.

`Poll` (§10) isn't a `control` value at all — it's a bare zero-length frame (a varint length prefix
of `0`, §2, with no bytes after it). This falls out of proto3's default-value-omission rule almost
for free: a message with every field left at its default serializes to zero bytes, so framing `Poll`
this way needs no dedicated `control` value or payload, unlike encoding it as an otherwise-empty
`Packet` would.

The one wrinkle is that `control = 0` (`Completion`) is a real, frequently-sent value, which under
plain default-value omission would serialize identically to `Poll`'s zero bytes if its `data` were
also empty — indistinguishable on the wire, which a reader can't tolerate. `Completion` is
deliberately the value placed at 0 because it's the one packet kind whose `data` can *never* be
empty: a `Completion` only exists as the last chunk of a message too large for one packet (§4.3), and
that chunk is always at least one byte (otherwise the message would have fit as a `Unit` instead).
So a real `Completion` always has a non-empty `data`, which alone forces a nonzero-length frame even
when `control`'s own zero value goes unwritten. Every other `control` value (1, 2, 3, and everything
≥ 4) is itself nonzero and is always serialized regardless of `data`.

The maximum size of `data` in a packet is a configurable connection setting (`MaxPacketDataSize`); it
bounds both how large a `Unit` message can be and how a larger message gets chunked into `Data`
packets.

### 4.1 Acknowledgement and flow control

Every packet except `Receipt` and `Poll` is acknowledged: on receiving a `Unit`, `Data`,
`Completion`, or `Cancellation` packet, a side replies with a `Receipt` as soon as possible,
independent of whatever it's waiting to send outbound — a stalled outbound queue never leaves a
peer's packets unacknowledged. Not acknowledging `Receipt` itself is what keeps the exchange from
recursing forever; `Poll` is never acknowledged either, since it exists purely to be *received*
(§10) — replying would only double the traffic without adding information.

`Unit`, `Data`, `Completion`, and `Cancellation` packets additionally observe strict turn-taking:
after sending one, a side must wait for its `Receipt` before sending its next packet of one of these
four kinds — exactly one such packet is ever in flight at a time. This keeps the protocol
unambiguous about which packet a given `Receipt` acknowledges, at the cost of not pipelining;
OFT trades raw throughput for a simple, easy-to-reason-about correctness model (concurrency instead
comes from priority and interruption, §6).

`Poll` is the only exception to turn-taking — it can be sent at any time, without waiting for
whatever `Unit`/`Data`/`Completion`/`Cancellation` packet is currently in flight (see §10 for why it
doesn't need to). Rekeying (§8) doesn't interact with turn-taking at all, since it has no `control`
value to take a turn with in the first place.

### 4.2 Messages that fit in one packet

A message small enough to fit under `MaxPacketDataSize` is sent as a single `Unit` packet, and the
receiver treats it as complete the instant it arrives. Because it's atomic, a `Unit` message is never
interrupted and never participates in the per-priority-channel bookkeeping below — it's in and out in
one round trip.

### 4.3 Messages that need multiple packets

A message too large for one packet is sent as a sequence of packets on the priority channel matching
its priority (`control = priority + 4`):

- All but the last chunk are sent as `Data` packets (`control = priority + 4`).
- The last chunk is sent as a `Completion` packet (`control = 0`), telling the receiver the message
  is complete and that anything after it belongs to a different message.

A sender may instead abandon a message it's already started sending by sending a `Cancellation`
packet (`control = 1`) instead of continuing with more `Data`/`Completion` packets — this tells the
receiver to discard whatever partial data it has buffered for that message.

### 4.4 Identifying which channel a Completion/Cancellation belongs to

`Completion` and `Cancellation` packets carry no priority of their own — by the time one arrives, the
receiver already knows, from the `Data` packets it's seen, which channels have a message in
progress. Because packets are strictly acknowledged one at a time (§4.1) and interruption (§6) only
ever suspends a *lower*-priority in-progress message to let a *higher*-priority one run, the message
a `Completion`/`Cancellation` belongs to is always unambiguous: it's the one on the **highest-priority
channel that currently has a pending (started, not yet finished) message**.

A receiver therefore keeps, per connection, a buffer of received bytes for each priority channel with
an in-progress message:

- On a `Data` packet (`control ≥ 4`): append `data` to the buffer for channel `control − 4`,
  creating it if needed.
- On a `Completion` packet: take the buffer for the highest-priority channel that has one, append the
  final `data`, deliver the concatenated bytes to the application, and discard the buffer.
- On a `Cancellation` packet: take the buffer for the highest-priority channel that has one and
  discard it without delivering anything.

In both cases a `Receipt` is still sent back per §4.1, regardless of channel.

## 5. Priority

A message is submitted for sending with a non-negative integer **priority** — larger is higher
priority, `0` is the default. Priority only affects **send-side scheduling**: which queued/in-progress
message gets the next outbound packet slot. It has no effect on a `Unit` message beyond when it gets
sent relative to other pending messages, since a `Unit` is a single packet with no ordering to
interrupt — but it's still one entry in the same priority-ordered scheduling as multi-packet messages
(§6): a lower-priority `Unit` waits behind an entire in-progress higher-priority message, not just
its next packet, while a higher-priority `Unit` interrupts a lower-priority multi-packet message in
progress on the very next free slot, exactly as a higher-priority multi-packet message would.

## 6. Interruption

Because only one packet may be in flight at a time (§4.1), a connection's outbound side is really
just a scheduler choosing, every time the single packet "slot" frees up (the previous packet's
`Receipt` has arrived), what to send next:

1. If any priority channel has a multi-packet message in progress or newly queued, choose the
   **highest-priority** such channel.
2. Send that message's next packet — its first `Data` packet if it hasn't started, its next
   `Data`/`Completion` packet otherwise, based on how much of `MaxPacketDataSize` has already been
   consumed.
3. If a `Unit` message is queued and no in-progress multi-packet message outranks it, send it
   instead.

The effect: if a low-priority multi-packet message is partway through and a higher-priority message
(of either kind) is submitted, the scheduler switches to it on the very next free slot. The
low-priority message's send position is retained exactly as it was on the sender side — the receiver
simply sees no more `Data` packets on that channel for a while. Once every higher-priority channel is
drained (finished, or nothing left queued), the scheduler resumes the interrupted message from where
it left off. This is what "interruption" means in OFT: entirely a property of send-side scheduling.
The wire format needs no distinct "pause" signal, since simply not sending more `Data` packets on a
channel *is* pausing it, and the receiver's per-channel buffers (§4.4) are what let an arbitrary
number of channels sit paused mid-message at once.

## 7. Cancellation from the application's perspective

An application can cancel a message it previously queued to send at any time before it completes:

- If no bytes of it have gone out yet, it's simply removed — the receiver never learns it existed.
- If it's a `Unit` message, it's already been fully delivered by the time it could be cancelled —
  there's nothing left to cancel.
- If it's a multi-packet message with at least one `Data` packet already sent, cancelling it sends a
  `Cancellation` packet (once it's next in line for the outbound slot, respecting priority like any
  other packet) in place of its remaining `Data`/`Completion` packets.

## 8. Rekeying

OFT connections can rekey their TLS session — derive fresh traffic keys for both directions —
without tearing down the underlying TCP connection or starting a new TLS session. This is useful for
long-lived connections that want to bound how much traffic is protected by one set of TLS keys,
whether for compliance or to limit the blast radius of a key compromise. A rekey can be triggered
manually by the application on either side, or automatically on a configured interval.

Rekeying is implemented directly as a TLS 1.3 `KeyUpdate` (RFC 8446 §4.6.3), a post-handshake message
that rotates the sender's write-direction traffic secret in place, on the same continuous encrypted
record stream. Because a `KeyUpdate` is just another record interleaved with whatever application
data is already flowing, it needs no coordination at the OFT protocol level at all — no `Rekey`
`control` value (§4), nothing to acknowledge, nothing that interacts with turn-taking (§4.1).
Rekeying a connection is simply asking the underlying TLS implementation to send a `KeyUpdate` and
returning once that request has been made.

Each reference implementation still has to be careful about *how* it invokes the TLS library's
`KeyUpdate` support: a locally initiated update and an inbound one requested by the peer (processed
as a side effect of reading incoming data) both touch the same connection's read and write state, and
not every TLS library's API guards that interaction on its own. All three ports handle this by only
ever triggering a `KeyUpdate` from the same thread that performs that connection's reads, which
trivially rules out the two running concurrently regardless of whether the library synchronizes them
itself — see [Architecture.md](Architecture.md) for how each language implements this.

## 9. Security modes

Each connection is established under one of four security modes — an explicit, opt-in per-connection
setting (`SecurityMode` in the reference implementation, see [Architecture.md](Architecture.md))
rather than something negotiated on the wire. Both sides must be configured compatibly, or the
exchange fails outright:

- **Trusted** — skips TLS entirely: step 2 of §1 is omitted, and the hail exchange (step 3) happens
  directly on the raw TCP connection as soon as it forms. Everything layered on top — framing (§2),
  the hail (§3), packets (§4-§7) — is unchanged; OFT simply runs on TCP rather than TLS-over-TCP.
  Intended for trusted, private networks (same-host/same-VPC deployments already gated by other
  means) or testing, where TLS's setup cost or certificate management isn't worth paying. It forfeits
  all of TLS's guarantees — no confidentiality (hails and packets are plaintext on the wire), no
  integrity protection, no authentication of either side — and, since rekeying (§8) is fundamentally
  a TLS-session operation, nothing to rekey either (a no-op).
- **Secure** (the default) — TLS provides confidentiality and integrity but no authentication of
  either side. The accepting side uses a throwaway certificate it generates internally rather than
  one the caller supplies, and the connecting side accepts whatever certificate it's presented with
  unconditionally, since there's nothing meaningful to validate an ephemeral certificate against.
- **Server authentication** — traditional one-way TLS: the accepting side must supply a real
  certificate, which the connecting side validates normally (a caller-supplied callback, or default
  certificate chain/hostname validation). Not valid for a peer component (see
  [Architecture.md](Architecture.md)): a peer makes both outbound and inbound connections
  interchangeably with no fixed client/server delineation, so it can't express a one-sided
  authentication requirement — use dual authentication instead.
- **Dual authentication** — mutual TLS: everything server authentication requires, plus the
  connecting side must also supply its own certificate(s), which the accepting side requests and
  validates. The only authenticating mode a peer supports.

If the two sides are configured with mismatched modes — say, one sends a TLS `ClientHello` while the
other expects a plaintext `Hail` first, or vice versa — neither side can detect this cleanly or
report a helpful error. Each simply sees bytes that don't parse as whatever it expected, and closes
the connection.

## 10. Liveness polling

Once a connection is established (§1 step 3), each side sends an empty `Poll` frame (a bare
zero-length frame, §4) to its peer on a fixed interval, `PollInterval` (default 1 second) —
independent of whatever application traffic is or isn't flowing. `Poll` is never acknowledged (§4.1)
and never competes with application traffic for turn-taking; it exists purely to guarantee that
*something* crosses the wire in each direction that often, giving each side a steady,
application-independent signal that its peer's process and network path are still alive.

Each side separately tracks when it last received *anything at all* from its peer — a `Poll` or any
other packet. If that ever exceeds a second interval, `PollTimeout` (default 5 seconds), that side
concludes its peer is unreachable (crashed, network-partitioned, or stuck behind a half-open TCP
connection neither the OS nor TLS noticed) and closes the connection itself, without waiting for the
peer. Both settings are configurable per-connection, mirrored across every implementation's
connector, hoster/listener, and peer components (see [Architecture.md](Architecture.md)).

Because `Poll` flows continuously regardless of application activity, a merely-idle connection (no
messages queued either direction) is never mistaken for a dead one — only a peer that's genuinely
stopped responding ever goes silent longer than `PollTimeout`. This is a connection-level liveness
check, distinct from a peer component's higher-level idle-connection cache eviction, which runs on a
much longer, independently configurable timescale (typically minutes) and is driven by actual
application traffic — `Poll` traffic is deliberately excluded from what that eviction mechanism
counts as "activity" (see the reference implementation's `LastSentAt`/`LastReceivedAt`), so a
connection that never carries an application message can still be evicted from a peer's cache on its
own schedule even while it stays alive at the transport level via polling the whole time.
