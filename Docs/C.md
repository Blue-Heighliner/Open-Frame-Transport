# Open Frame Transport — C

A C implementation of [OFT](OFT.md) under [`Ports/C/`](../Ports/C). See
[Architecture.md](Architecture.md) for how its components relate to the other ports (notably the
blocking call style and manual memory ownership); this document covers the C-specific API in detail,
with examples. See [`include/oft/oft.h`](../Ports/C/include/oft/oft.h) and
[`include/oft/oft_peer.h`](../Ports/C/include/oft/oft_peer.h) for the full API reference in
doc-comment form.

This port targets Linux/POSIX and depends only on OpenSSL and pthreads.

## Types

- `oft_connect()` — a stateless free function that dials outbound connections.
- `oft_host()` — a stateless free function that starts a listener.
- `oft_listener` — an opaque handle returned by `oft_host()`.
- `oft_connection` — an opaque handle for a single established connection, produced by either of the
  above.
- `oft_peer` — the peer-to-peer convenience layer. `oft_peer_create(&options)`.
- `oft_connect_options`, `oft_host_options`, `oft_peer_options` — per-role plain structs.
- `enum oft_security_mode` — `OFT_SECURITY_MODE_TRUSTED` / `_SECURE` / `_SERVER_AUTHENTICATION` /
  `_DUAL_AUTHENTICATION` (see [OFT.md §9](OFT.md#9-security-modes)). `_SERVER_AUTHENTICATION` makes
  `oft_peer_create()` return `NULL` — a peer has no client/server delineation, so use
  `_DUAL_AUTHENTICATION` instead.
- `oft_received_callback`, `oft_disconnected_callback`, `oft_connected_callback` — function-pointer
  callback types.
- `oft_connection_validation_callback` — an optional post-handshake connection-validation callback,
  settable on `oft_connect_options`/`oft_host_options`/`oft_peer_options` (see
  [Security modes](#security-modes) below).
- `oft_identity` — this connection's remote identity: `host`/`port`, `certificate` (an `X509 *`, or
  `NULL` if the remote side didn't present one), and `info` (the opaque hail data). Returned
  (borrowed, valid until the connection closes) by `oft_connection_identity()`.
- `oft_send_handle`-equivalent: `oft_connection_send()` writes a `uint64_t` message id out-parameter,
  used with `oft_connection_wait()`/`oft_connection_cancel()` instead of a returned handle object.
- `oft_connection_is_connected()` — non-zero until the connection permanently closes, for any reason
  (local or remote), after which `oft_connection_send()`/`_rekey()` return `OFT_ERROR_CLOSED`.
- `oft_peer_received_callback` — takes the sending connection's `const oft_identity *` (borrowed,
  valid only for the duration of the call) and payload (`uint8_t *`/`length`, heap-allocated, owned
  by the callee) as two separate arguments, the same shape `oft_received_callback` already uses.
- `enum oft_delivery_status` — `OFT_DELIVERY_STATUS_QUEUED` / `_SENDING` / `_INTERRUPTED` /
  `_RESUMED` / `_SENT` / `_ACKNOWLEDGED` / `_CANCELLED`, the full lifecycle a tagged send is reported
  through (see [Delivery status](#delivery-status) below).
- `oft_delivery_status_callback`/`oft_peer_delivery_status_callback`, set via
  `oft_connection_set_delivery_status_callback()`/`oft_peer_set_delivery_status_callback()` — invoked
  each time data sent with a non-`NULL` tag changes delivery status (see `oft_connection_send()`'s
  `tag` parameter below). `oft_peer_delivery_status_callback` does not identify which connection a
  send went out on, unlike `oft_peer_received_callback` - the caller already knows, since it's the
  same caller that made the `oft_peer_send()` call. Unlike every other callback setter above, this
  one is **not** buffered (see
  [Buffered notifications](Architecture.md#buffered-notifications-prevent-a-connectdisconnectreceive-message-loss-race)
  in Architecture.md): it can only ever be raised in response to the caller's own
  `oft_connection_send()`/`oft_peer_send()` call, so there's no message-loss race to guard against by
  assigning it beforehand.
- `oft_connection_send()`'s/`oft_peer_send()`'s `tag` parameter — an application-controlled `void *`
  (pass `NULL` if unused) attached to a send, referenced later via the delivery-status callback each
  time that send's `enum oft_delivery_status` changes (see [Delivery status](#delivery-status)
  below). A `NULL` tag never raises it at all.

## Client/server example

```c
#include "oft/oft.h"

#include <stdio.h>
#include <string.h>

static void on_received(oft_connection *connection, uint8_t *data, size_t length, void *user_data) {
    (void)connection;
    (void)user_data;
    printf("Received: %.*s\n", (int)length, data);
    free(data); /* ownership passes to this callback */
}

static void on_connected(oft_listener *listener, oft_connection *connection, void *user_data) {
    (void)listener;
    (void)user_data;
    oft_connection_set_received_callback(connection, on_received, NULL);
}

int main(void) {
    char error_buffer[256];

    /* --- Server side --- */
    oft_host_options host_options = {0};
    host_options.info = "my-server";
    host_options.security_mode = OFT_SECURITY_MODE_SECURE; /* no certificate needed for this example */

    oft_listener *listener = oft_host("0.0.0.0", 5000, &host_options, NULL, error_buffer, sizeof(error_buffer));
    if (!listener) {
        fprintf(stderr, "oft_host failed: %s\n", error_buffer);
        return 1;
    }
    oft_listener_set_connected_callback(listener, on_connected, NULL);

    /* --- Client side --- */
    oft_connect_options connect_options = {0};
    connect_options.info = "my-client";
    connect_options.security_mode = OFT_SECURITY_MODE_SECURE;

    oft_connection *connection = oft_connect(
            "127.0.0.1", 5000, &connect_options, NULL, error_buffer, sizeof(error_buffer));
    if (!connection) {
        fprintf(stderr, "oft_connect failed: %s\n", error_buffer);
        return 1;
    }

    uint64_t message_id;
    oft_connection_send(connection, (const uint8_t *)"hello", 5, /* priority */ 0, /* tag */ NULL, &message_id);
    oft_connection_wait(connection, message_id);

    oft_connection_close(connection);
    oft_listener_close(listener);
    return 0;
}
```

`options` may be `NULL` on both `oft_connect()`/`oft_host()` to use defaults (`OFT_SECURITY_MODE_SECURE`,
empty `info`, 1 KiB max packet size, 1000ms/5000ms poll interval/timeout).

## Remote identity

```c
const oft_identity *identity = oft_connection_identity(connection);
printf("Remote endpoint: %s:%u\n", identity->host, identity->port);
printf("Hail info: %s\n", identity->info);

if (identity->certificate) {
    char subject[256];
    X509_NAME_oneline(X509_get_subject_name(identity->certificate), subject, sizeof(subject));
    printf("Certificate subject: %s\n", subject);
}
```

`identity->certificate` is `NULL` for a connection established with `OFT_SECURITY_MODE_TRUSTED` (no
TLS at all), and also `NULL` on the accepting side of a connection established under a mode that
never requests a certificate from the connecting side (see `OFT_SECURITY_MODE_DUAL_AUTHENTICATION`).
`oft_connection_identity()`'s return value — including its `certificate` field — is owned by the
connection and only valid until it's closed; a peer's received callback hands out the same kind of
borrowed identity (see [Peer-to-peer example](#peer-to-peer-example) below), valid only for the
duration of that one call. `oft_peer_delivery_status_callback` carries no identity at all - see
[Delivery status](#delivery-status) below.

## Peer-to-peer example

```c
#include "oft/oft_peer.h"

static void on_peer_received(const oft_identity *identity, uint8_t *data, size_t length, void *user_data) {
    (void)user_data;
    printf("Received from %s:%u: %.*s\n", identity->host, identity->port, (int)length, data);
    free(data); /* ownership passes to this callback */
}

oft_peer_options options = {0};
options.info = "my-peer";
options.security_mode = OFT_SECURITY_MODE_SECURE;

oft_peer *peer = oft_peer_create(&options);
oft_peer_set_received_callback(peer, on_peer_received, NULL);

/* Optional: also accept inbound connections into the same pool. */
char error_buffer[256];
oft_peer_listen(peer, "0.0.0.0", 5001, error_buffer, sizeof(error_buffer));

/* Sending to a host:port transparently reuses a cached connection or creates and caches a new one. */
uint64_t message_id;
oft_peer_send(peer, "127.0.0.1", 5001, (const uint8_t *)"hello", 5, /* priority */ 0, /* tag */ NULL,
              NULL, &message_id, error_buffer, sizeof(error_buffer));

oft_peer_close(peer);
```

`oft_peer_received_callback`'s `identity` argument (borrowed from the underlying connection, valid
only for the duration of this call) is only for identifying which connection a message arrived on,
e.g. to decide how to respond via `oft_peer_send()`; a peer deliberately exposes no other way to
enumerate, look up, or be notified about the individual connections it holds (there is no
`oft_peer_set_disconnected_callback()`/`oft_peer_set_connected_callback()`): connection lifecycle is
the peer's own implementation detail, transparently managed (reconnecting, evicting, etc.) behind
`oft_peer_send()`.

## Waiting for delivery and cancellation

```c
uint64_t message_id;
oft_connection_send(connection, payload, payload_length, /* priority */ 0, /* tag */ NULL, &message_id);

/* Blocks until fully delivered, cancelled, or the connection closes. */
int result = oft_connection_wait(connection, message_id);
if (result == OFT_ERROR_CANCELLED) {
    /* the message was cancelled */
}

/* Or cancel it (see OFT.md §7): immediately if not yet started, or by sending a Cancellation packet
 * if it has already begun. */
oft_connection_cancel(connection, message_id);
```

## Delivery status

A tagged send is also reported through `enum oft_delivery_status`, via a callback set with
`oft_connection_set_delivery_status_callback()`, independent of `oft_connection_wait()` above -
useful for observing a send's progress without a message id in hand, or for tracking many in-flight
sends from one place:

```c
static void on_delivery_status(void *tag, enum oft_delivery_status status, void *user_data) {
    printf("%p -> %d\n", tag, status);
}

oft_connection_set_delivery_status_callback(connection, on_delivery_status, NULL);
oft_connection_send(connection, payload, payload_length, /* priority */ 0, /* tag */ my_tag, &message_id);
```

Every tagged send passes through `OFT_DELIVERY_STATUS_QUEUED` → `OFT_DELIVERY_STATUS_SENDING`, then
either `OFT_DELIVERY_STATUS_CANCELLED` or `OFT_DELIVERY_STATUS_SENT` followed by
`OFT_DELIVERY_STATUS_ACKNOWLEDGED`; `OFT_DELIVERY_STATUS_INTERRUPTED`/`OFT_DELIVERY_STATUS_RESUMED`
pairs may occur any number of times in between `OFT_DELIVERY_STATUS_SENDING` and
`OFT_DELIVERY_STATUS_SENT`, for a multi-packet send a higher-priority send preempts (see
[OFT.md §6](OFT.md#6-interruption)) - a single-packet send can never be interrupted.
`OFT_DELIVERY_STATUS_CANCELLED` can only occur before `OFT_DELIVERY_STATUS_SENT`: once a send's
final packet has actually been written, cancelling it can no longer prevent delivery. A `NULL` tag
never raises the callback at all.

## Rekeying

```c
/* Manual, on either side, at any time - blocks until requested: */
oft_connection_rekey(connection);

/* Or automatic, via options: */
oft_connect_options options = {0};
options.info = "my-client";
options.rekey_interval_ms = 10 * 60 * 1000;
```

`oft_connection_rekey()` is a no-op (returns `OFT_OK` immediately) if the connection was established
with `OFT_SECURITY_MODE_TRUSTED` — there's no TLS session to rekey. `oft_peer_rekey()`/
`oft_peer_drop()` act on every connection the peer currently holds, both inbound and outbound,
at once.

## Security modes

```c
SSL_CTX *server_ctx = ...; /* carries your server certificate + private key */

/* Server authentication (one-way TLS): the server presents a real certificate. */
oft_host_options host_options = {0};
host_options.info = "my-server";
host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;
oft_listener *listener = oft_host("0.0.0.0", 5000, &host_options, server_ctx, error_buffer, sizeof(error_buffer));

SSL_CTX *client_ctx = ...; /* configured to trust the server's certificate */
oft_connect_options connect_options = {0};
connect_options.info = "my-client";
connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;
oft_connection *connection = oft_connect(
        "127.0.0.1", 5000, &connect_options, client_ctx, error_buffer, sizeof(error_buffer));

/* Dual authentication (mutual TLS): the client's ssl_ctx must also carry its own certificate. The
 * only authenticating mode oft_peer supports - server authentication above is only valid for
 * oft_connect()/oft_host(). */
SSL_CTX *mutual_client_ctx = ...; /* carries both a client certificate and trust configuration */
oft_connect_options mutual_options = {0};
mutual_options.info = "my-client";
mutual_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;
oft_connection *mutual_connection = oft_connect(
        "127.0.0.1", 5000, &mutual_options, mutual_client_ctx, error_buffer, sizeof(error_buffer));
```

`ssl_ctx` is required (non-`NULL`) for `OFT_SECURITY_MODE_SERVER_AUTHENTICATION` and
`OFT_SECURITY_MODE_DUAL_AUTHENTICATION` on both `oft_host()` and `oft_connect()`. Under
`OFT_SECURITY_MODE_SECURE`, a caller-supplied `ssl_ctx` passed to `oft_host()` is accepted but
ignored (the listener generates its own throwaway identity once, reused for every connection it
accepts), and `oft_connect()`'s `ssl_ctx` is unused entirely — the connecting side accepts whatever
certificate it's presented with unconditionally. See [OFT.md §9](OFT.md#9-security-modes) for the
full semantics of each mode.

`oft_connect_options`/`oft_host_options`/`oft_peer_options` also each have a `connection_validation`
field (plus a `connection_validation_user_data` passed through to it): an optional
`oft_connection_validation_callback` invoked once the OFT hail exchange completes, for every security
mode (including `OFT_SECURITY_MODE_TRUSTED` and `OFT_SECURITY_MODE_SECURE`, where its `certificate`/
`chain` parameters are always `NULL`) — unlike the verification configured on the `SSL_CTX` itself,
which only runs during the TLS handshake and never sees the connection's `oft_identity`:

```c
static int validate_connection(const oft_identity *identity, X509 *certificate, STACK_OF(X509) *chain, long verify_result, void *user_data) {
    /* certificate/chain are borrowed - valid only for the duration of this call, and must not be
     * freed here. verify_result is whatever SSL_get_verify_result() reported (X509_V_OK if the
     * certificate validated cleanly). */
    return 1; /* or 0 to reject the connection */
}

oft_connect_options connect_options = {0};
connect_options.info = "my-client";
connect_options.connection_validation = validate_connection;
```

`NULL` (the default) accepts every connection; returning `0` fails `oft_connect()`/`oft_host()` with
`OFT_ERROR`. This callback runs synchronously, matching the rest of this port's blocking call style
(see [Architecture.md](Architecture.md)) — unlike the C# reference implementation's
`ConnectionValidation`, which is `async`. It also carries a `verify_result` parameter (mirroring
`SSL_get_verify_result()`) rather than C#'s `sslErrors` (`SslPolicyErrors`) — the closest OpenSSL
equivalent actually available at this point.

## Memory ownership

`oft_connection_send()` (and `oft_peer_send()`) copy the data they're given; the caller retains
ownership of its own buffer and may free or reuse it as soon as the call returns. Data delivered to
an `oft_received_callback`/`oft_peer_received_callback` is heap-allocated (`malloc()`) by the
library, and ownership passes to the callback — it **must** `free()` it once done (see the examples
above). The `oft_identity *` an `oft_peer_received_callback` is handed alongside that payload is,
unlike the payload, borrowed (owned by the underlying connection) and valid only for the duration of
that one call — do not retain the pointer or free it.

## Concurrency model

Each connection owns two background threads: one blocked reading packets (the receive loop) and one
draining the outbound priority queues (the send loop), plus a third thread that sends the periodic
`Poll` packet and runs the liveness watchdog check (see [OFT.md §10](OFT.md#10-liveness-polling)). An
`oft_listener`'s accept loop runs on its own thread, using a self-pipe (`poll()` on both the
listening socket and a wakeup pipe) so `oft_listener_close()` can reliably interrupt a thread blocked
in `accept()`. `oft_peer`'s eviction check runs on its own thread as well.

Connections are pinned to TLS 1.3 (`SSL_set_min_proto_version`/`SSL_set_max_proto_version`, both set
to `TLS1_3_VERSION`) over a single, connection-lifetime `SSL` object. `oft_connection_rekey()` queues
a request and blocks until it's processed by the receive thread (`process_pending_rekeys()`), which
calls `SSL_key_update(ssl, SSL_KEY_UPDATE_REQUESTED)` followed by `SSL_do_handshake(ssl)` to flush it
— see [Architecture.md](Architecture.md#rekeying-and-thread-safety) for why this is only ever done
from the connection's own receive thread.

Writing to a socket whose peer has already closed the connection raises `SIGPIPE`, which by default
terminates the whole process; the library ignores it process-wide (once, via `pthread_once`) the
first time a connection is created, so such writes surface as an ordinary `SSL_write` error instead.

## Building

Standard Make project; requires a C11 compiler, OpenSSL development headers/libraries, and pthreads.

```
make lib    # builds build/liboft.a
make test   # builds and runs build/oft_tests
```

## Testing and coverage

`tests/test_main.c` is a small hand-rolled test framework (no external test library) driving real
loopback TCP/TLS connections, plus a set of tests that exercise the hand-written wire codec
(`oft_wire.c`) directly with malformed input. Self-signed certificates are generated at test time
via raw OpenSSL calls (`tests/test_certs.c`), the same technique the library itself uses internally
(`oft_ephemeral_ssl_ctx.c`) for `OFT_SECURITY_MODE_SECURE`'s throwaway server identity.

```
make test
```

Coverage is measured with `gcov` (bundled with GCC):

```
mkdir -p build_cov
for f in src/*.c tests/*.c; do
  cc -Iinclude -Isrc -D_POSIX_C_SOURCE=200809L -D_DEFAULT_SOURCE \
     -std=c11 -O0 -g --coverage -c "$f" -o "build_cov/$(basename "$f" .c).o"
done
cc --coverage build_cov/*.o -o build_cov/oft_tests_cov -lssl -lcrypto -lpthread
(cd build_cov && ./oft_tests_cov)
gcov -o build_cov src/*.c   # writes annotated *.c.gcov files with per-line hit counts
```

See [`AGENTS.md`](../AGENTS.md) for the coding conventions used throughout this project.
