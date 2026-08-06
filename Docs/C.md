# Open Frame Transport — C

A C implementation of [OFT](OFT.md) under [`Ports/C/`](../Ports/C). See
[Architecture.md](Architecture.md) for how its components relate to the other ports (notably: the
`on_established` callback pattern used here to avoid a connect-time message-loss race, the blocking
call style, and manual memory ownership); this document covers the C-specific API in detail, with
examples. See [`include/oft/oft.h`](../Ports/C/include/oft/oft.h) and
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
- `enum oft_security_mode` — `OFT_SECURITY_MODE_INSECURE` / `_SECURE` / `_AUTHENTICATION` /
  `_DUAL_AUTHENTICATION` (see [OFT.md §9](OFT.md#9-security-modes)).
- `oft_received_callback`, `oft_disconnected_callback`, `oft_connected_callback`,
  `oft_connection_established_callback` — function-pointer callback types.
- `oft_send_handle`-equivalent: `oft_connection_send()` writes a `uint64_t` message id out-parameter,
  used with `oft_connection_wait()`/`oft_connection_cancel()` instead of a returned handle object.

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
            "127.0.0.1", 5000, &connect_options, NULL, NULL, NULL, error_buffer, sizeof(error_buffer));
    if (!connection) {
        fprintf(stderr, "oft_connect failed: %s\n", error_buffer);
        return 1;
    }

    uint64_t message_id;
    oft_connection_send(connection, (const uint8_t *)"hello", 5, /* priority */ 0, &message_id);
    oft_connection_wait(connection, message_id);

    oft_connection_close(connection);
    oft_listener_close(listener);
    return 0;
}
```

`options` may be `NULL` on both `oft_connect()`/`oft_host()` to use defaults (`OFT_SECURITY_MODE_SECURE`,
empty `info`, 1 KiB max packet size, 1000ms/5000ms poll interval/timeout).

### Avoiding the connect-time message-loss race

The example above registers `on_received` inside `on_connected` — safe because that callback runs
before the accepted connection starts processing inbound packets. On the connect side, use
`oft_connect()`'s `on_established` parameter for the same guarantee if the peer might reply the
instant the connection is up:

```c
static void on_established(oft_connection *connection, void *user_data) {
    oft_connection_set_received_callback(connection, on_received, user_data);
}

oft_connection *connection = oft_connect(
        "127.0.0.1", 5000, &connect_options, NULL,
        on_established, NULL, error_buffer, sizeof(error_buffer));
```

## Peer-to-peer example

```c
#include "oft/oft_peer.h"

oft_peer_options options = {0};
options.info = "my-peer";
options.security_mode = OFT_SECURITY_MODE_SECURE;

oft_peer *peer = oft_peer_create(&options);
oft_peer_set_received_callback(peer, on_received, NULL);

/* Optional: also accept inbound connections into the same pool. */
char error_buffer[256];
oft_peer_open(peer, "0.0.0.0", 5001, error_buffer, sizeof(error_buffer));

/* Sending to a host:port transparently reuses a cached connection or creates and caches a new one. */
uint64_t message_id;
oft_peer_send(peer, "127.0.0.1", 5001, (const uint8_t *)"hello", 5, /* priority */ 0,
              NULL, &message_id, error_buffer, sizeof(error_buffer));

oft_peer_close(peer);
```

## Waiting for delivery and cancellation

```c
uint64_t message_id;
oft_connection_send(connection, payload, payload_length, /* priority */ 0, &message_id);

/* Blocks until fully delivered, cancelled, or the connection closes. */
int result = oft_connection_wait(connection, message_id);
if (result == OFT_ERROR_CANCELLED) {
    /* the message was cancelled */
}

/* Or cancel it (see OFT.md §7): immediately if not yet started, or by sending a Cancellation packet
 * if it has already begun. */
oft_connection_cancel(connection, message_id);
```

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
with `OFT_SECURITY_MODE_INSECURE` — there's no TLS session to rekey. `oft_peer_rekey()`/
`oft_peer_disconnect()` act on every connection the peer currently holds, both inbound and outbound,
at once.

## Security modes

```c
SSL_CTX *server_ctx = ...; /* carries your server certificate + private key */

/* Authentication (one-way TLS): the server presents a real certificate. */
oft_host_options host_options = {0};
host_options.info = "my-server";
host_options.security_mode = OFT_SECURITY_MODE_AUTHENTICATION;
oft_listener *listener = oft_host("0.0.0.0", 5000, &host_options, server_ctx, error_buffer, sizeof(error_buffer));

SSL_CTX *client_ctx = ...; /* configured to trust the server's certificate */
oft_connect_options connect_options = {0};
connect_options.info = "my-client";
connect_options.security_mode = OFT_SECURITY_MODE_AUTHENTICATION;
oft_connection *connection = oft_connect(
        "127.0.0.1", 5000, &connect_options, client_ctx, NULL, NULL, error_buffer, sizeof(error_buffer));

/* DualAuthentication (mutual TLS): the client's ssl_ctx must also carry its own certificate. */
SSL_CTX *mutual_client_ctx = ...; /* carries both a client certificate and trust configuration */
oft_connect_options mutual_options = {0};
mutual_options.info = "my-client";
mutual_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;
oft_connection *mutual_connection = oft_connect(
        "127.0.0.1", 5000, &mutual_options, mutual_client_ctx, NULL, NULL, error_buffer, sizeof(error_buffer));
```

`ssl_ctx` is required (non-`NULL`) for `OFT_SECURITY_MODE_AUTHENTICATION` and
`OFT_SECURITY_MODE_DUAL_AUTHENTICATION` on both `oft_host()` and `oft_connect()`. Under
`OFT_SECURITY_MODE_SECURE`, a caller-supplied `ssl_ctx` passed to `oft_host()` is accepted but
ignored (the listener generates its own throwaway identity once, reused for every connection it
accepts), and `oft_connect()`'s `ssl_ctx` is unused entirely — the connecting side accepts whatever
certificate it's presented with unconditionally. See [OFT.md §9](OFT.md#9-security-modes) for the
full semantics of each mode.

## Memory ownership

`oft_connection_send()` (and `oft_peer_send()`) copy the data they're given; the caller retains
ownership of its own buffer and may free or reuse it as soon as the call returns. Data delivered to
an `oft_received_callback` is heap-allocated (`malloc()`) by the library, and ownership passes to
the callback — it **must** `free()` it once done (see the examples above).

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
