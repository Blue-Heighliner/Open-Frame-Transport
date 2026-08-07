# Open Frame Transport — Java

A Java implementation of [OFT](OFT.md) under [`Ports/Java/`](../Ports/Java). See
[Architecture.md](Architecture.md) for how its components relate to the other ports (notably the
blocking call style); this document covers the Java-specific API in detail, with examples.

## Types

- `OftConnector` / `DefaultOftConnector` — dials outbound connections. `OftConnector.create()`
  returns a stateless instance.
- `OftHoster` / `DefaultOftHoster` — hosts inbound listeners. `OftHoster.create()` returns a
  stateless instance.
- `OftListener` / `DefaultOftListener` — a listener returned by `OftHoster.host`.
- `OftConnection` / `DefaultOftConnection` — a single established connection, produced by either of
  the above.
- `OftPeer` / `DefaultOftPeer` — the peer-to-peer convenience layer. `OftPeer.create(OftPeerOptions)`.
- `OftConnectOptions`, `OftHostOptions`, `OftPeerOptions` — per-role option types, each with a
  `.builder()`.
- `OftSecurityMode` — `TRUSTED` / `SECURE` / `SERVER_AUTHENTICATION` / `DUAL_AUTHENTICATION` (see
  [OFT.md §9](OFT.md#9-security-modes)). `SERVER_AUTHENTICATION` is rejected by
  `OftPeer.create(OftPeerOptions)` — a peer has no client/server delineation, so use
  `DUAL_AUTHENTICATION` instead.
- `OftConnection.setReceivedHandler`/`.setDisconnectedHandler`, `OftListener.setConnectedHandler`,
  `OftPeer.setReceivedHandler` — single-slot `Consumer<T>`/`BiConsumer<T, U>` callback setters, one
  per notification kind, assigned directly (no handler-object interface to implement). Assigning a
  new value (including `null`) always replaces any previous one, and each notification kind is
  assigned independently of the others. `OftPeer` has no `setDisconnectedHandler`/
  `setConnectedHandler` of its own — see its own type doc comment for why.
- `OftSendHandle` — returned by `send`, exposes `completion()` (a `CompletableFuture<Void>`) and
  `cancel()`.

## Client/server example

```java
import org.blueheighliner.openframetransport.*;

import java.net.InetSocketAddress;

// --- Server side ---
OftHostOptions hostOptions = OftHostOptions.builder()
        .info("my-server")
        .securityMode(OftSecurityMode.SECURE) // no certificate needed for this example
        .build();

OftListener listener = OftHoster.create().host(new InetSocketAddress("0.0.0.0", 5000), hostOptions);
listener.setConnectedHandler(connection -> {
    connection.setReceivedHandler(data -> {
        System.out.println("Received: " + new String(data));
    });
});

// --- Client side ---
OftConnectOptions connectOptions = OftConnectOptions.builder()
        .info("my-client")
        .securityMode(OftSecurityMode.SECURE)
        .build();

OftConnection connection = OftConnector.create().connect("127.0.0.1", 5000, connectOptions);
connection.send("hello".getBytes(), /* priority */ 0);
```

`options` is optional on both `connect` and `host` — the no-options overloads
(`connect(host, port)`/`host(listenEndpoint)`) use defaults (`SECURE`, empty `info`, 1 KiB max
packet size, 1s/5s poll interval/timeout).

## Peer-to-peer example

```java
import org.blueheighliner.openframetransport.*;

import java.net.InetSocketAddress;

OftPeerOptions options = OftPeerOptions.builder()
        .info("my-peer")
        .securityMode(OftSecurityMode.SECURE)
        .build();

OftPeer peer = OftPeer.create(options);
peer.setReceivedHandler((connection, data) -> System.out.println("Received: " + new String(data)));

// Optional: also accept inbound connections into the same pool.
peer.open(new InetSocketAddress("0.0.0.0", 5001));

// Sending to a host:port transparently reuses a cached connection or creates and caches a new one.
peer.send("127.0.0.1", 5001, "hello".getBytes(), /* priority */ 0);

peer.close();
```

`OftPeer.setReceivedHandler`'s `connection` argument is only for replying on the same connection a
message arrived on — a peer deliberately exposes no other way to enumerate, look up, or be notified
about the individual connections it holds (there is no `setDisconnectedHandler`/
`setConnectedHandler`): connection lifecycle is the peer's own implementation detail, transparently
managed (reconnecting, evicting, etc.) behind `send`.

## Waiting for delivery and cancellation

`send` returns an `OftSendHandle`:

```java
OftSendHandle handle = connection.send(payload, /* priority */ 0);

// Block until fully delivered, cancelled, or the connection closes:
handle.completion().get(10, TimeUnit.SECONDS);

// Or cancel it (see OFT.md §7): immediately if not yet started, or by sending a Cancellation
// packet if it has already begun.
handle.cancel();
```

## Rekeying

```java
// Manual, on either side, at any time:
connection.rekey().get(10, TimeUnit.SECONDS);

// Or automatic, via options:
OftConnectOptions options = OftConnectOptions.builder()
        .info("my-client")
        .rekeyInterval(Duration.ofMinutes(10))
        .build();
```

`rekey()` is a no-op (returns an already-completed future) if the connection was established with
`OftSecurityMode.TRUSTED` — there's no TLS session to rekey. `OftPeer.rekey()`/`.disconnect()` act
on every connection the peer currently holds, both inbound and outbound, at once.

## Security modes

Java's `SSLContext` bundles a side's own identity (via a `KeyManager`) and its trust manager(s) into
one object, unlike the C# reference implementation's separate certificate/validation-callback
fields — so a single `sslContext()` option covers both roles here:

```java
SSLContext serverContext = ...; // carries your server certificate + private key

// SERVER_AUTHENTICATION (one-way TLS): the server presents a real certificate.
OftHostOptions hostOptions = OftHostOptions.builder()
        .info("my-server")
        .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
        .sslContext(serverContext)
        .build();

SSLContext clientContext = ...; // configured to trust the server's certificate
OftConnectOptions connectOptions = OftConnectOptions.builder()
        .info("my-client")
        .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
        .sslContext(clientContext) // if omitted, falls back to the JVM's default trust store
        .build();

// DUAL_AUTHENTICATION (mutual TLS): the client's SSLContext must also carry its own identity. The
// only authenticating mode OftPeer supports — SERVER_AUTHENTICATION above is only valid for
// OftConnector/OftHoster.
SSLContext mutualClientContext = ...; // carries both a client certificate and trust configuration
OftConnectOptions mutualOptions = OftConnectOptions.builder()
        .info("my-client")
        .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
        .sslContext(mutualClientContext)
        .build();
```

`sslContext()` is required (non-`null`) for `SERVER_AUTHENTICATION` and `DUAL_AUTHENTICATION`; under
`SECURE`, a caller-supplied context is accepted but ignored (the accepting side generates its own
throwaway identity, and the connecting side accepts whatever certificate it's presented with
unconditionally). See [OFT.md §9](OFT.md#9-security-modes) for the full semantics of each mode.

## Concurrency model

Each connection owns two daemon threads: one blocked reading packets (the receive loop) and one
draining the outbound priority queues (the send loop). A third daemon thread, on a
`ScheduledExecutorService`, sends the periodic `Poll` packet and runs the liveness watchdog check
(see [OFT.md §10](OFT.md#10-liveness-polling)); when `securityMode()` is `OftSecurityMode.TRUSTED`,
the connection skips the TLS handshake entirely and reads/writes the plain `Socket` directly.

Connections are pinned to TLS 1.3 (`SSLSocket.setEnabledProtocols(new String[] {"TLSv1.3"})`).
`rekey()` requests a TLS 1.3 `KeyUpdate` by calling `SSLSocket.startHandshake()` again on the
already-established socket, which JSSE's `SSLSocketImpl` recognizes as a post-handshake rekey rather
than a new handshake — see [Architecture.md](Architecture.md#rekeying-and-thread-safety) for why
this is only ever done from the connection's own receive thread.

## Testing and coverage

Tests use JUnit 5 (`mvn test`) against real loopback TCP/TLS connections, generating throwaway
self-signed certificates via the JDK's own `keytool` (no third-party certificate library needed) —
both in test code and, for `SECURE` mode's ephemeral server identity, in the library itself
(`OftEphemeralSslContext`). Coverage is collected with the `jacoco-maven-plugin` (bound to
`mvn test`, which also writes an HTML report to `target/site/jacoco/index.html`).

```
mvn test
```

`OFT.proto` is compiled to Java sources at build time by the `protobuf-maven-plugin`, which
downloads a matching `protoc` binary automatically (via `os-maven-plugin`) — no local `protoc`
install is required.

See [`AGENTS.md`](../AGENTS.md) for the coding conventions used throughout this project.
