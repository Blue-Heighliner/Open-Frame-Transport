# Open Frame Transport - C

A C implementation of the [Open Frame Transport (OFT)](../../README.md) protocol. See
[Docs/OFT.md](../../Docs/OFT.md) for the protocol specification, [Docs/Architecture.md](../../Docs/Architecture.md)
for how this port's components relate to the other implementations, and [Docs/C.md](../../Docs/C.md)
for the full C API reference with examples. This document covers only building, testing, and
coverage. See [include/oft/oft.h](include/oft/oft.h) and [include/oft/oft_peer.h](include/oft/oft_peer.h)
for the full API reference in doc-comment form.

## Scope

This port implements the protocol engine — `oft_connection`, `oft_listener` (produced by
`oft_host()`), and `oft_connect()` — plus the `oft_peer` connection-pooling convenience layer, with
the same wire behavior and API shape as the [C# reference implementation](../../Core). It targets
Linux/POSIX and depends only on OpenSSL and pthreads.

One deliberate simplification versus the C#/Java ports: `oft_peer` serializes all outbound
connection establishment through a single peer-wide lock (rather than only deduplicating concurrent
connects to the *same* host/port). This trades a little parallelism for a much simpler,
still-correct implementation — see the note at the top of `oft_peer.h`.

## Building

Standard Make project; requires a C11 compiler, OpenSSL development headers/libraries, and
pthreads.

```
make lib    # builds build/liboft.a
make test   # builds and runs build/oft_tests
```

## Testing

`tests/test_main.c` is a small hand-rolled test framework (no external test library) driving real
loopback TCP/TLS connections, plus a set of tests that exercise the hand-written wire codec
(`oft_wire.c`) directly with malformed input. Self-signed certificates are generated at test time
via raw OpenSSL calls (`tests/test_certs.c`), not `keytool`/shelling out.

```
make test
```

## Code coverage

Coverage is measured with `gcov` (bundled with GCC) rather than a separate tool:

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

The library-error and live-socket-I/O-failure paths in `oft_frame.c` (mid-stream `SSL_read`/
`SSL_write` failures) are the main remaining gap: they'd need fault injection into an established
TLS session to reach deterministically, which isn't worth the complexity relative to the coverage
gained.

## License

[MIT](LICENSE)
