# Open Frame Transport - Rust

A Rust implementation of the [Open Frame Transport (OFT)](../../README.md) protocol. See
[Docs/OFT.md](../../Docs/OFT.md) for the protocol specification, [Docs/Architecture.md](../../Docs/Architecture.md)
for how this port's components relate to the other implementations, and
[Docs/Rust.md](../../Docs/Rust.md) for the full Rust API reference with examples. This document
covers only building, testing, and coverage.

## Scope

This port implements the protocol engine — `Connection`, `Listener`/`host()`, and `connect()` — plus
the `Peer` connection-pooling convenience layer, with the same wire behavior and API shape as the
[C# reference implementation](../../Core). It depends on [`rustls`](https://docs.rs/rustls) for TLS
1.3 (including genuine, application-triggered rekey support via
`ConnectionCommon::refresh_traffic_keys()`) and [`rcgen`](https://docs.rs/rcgen) for ephemeral
certificate generation under `SecurityMode::Secure`.

One deliberate divergence from the other three ports' concurrency model: each `Connection` uses a
single background thread (not 2-4 separate receive/send/poll threads), since `rustls::Connection`
isn't `Sync` and can't safely be read from and written to concurrently across threads without a
lock spanning the whole I/O path. See [Docs/Rust.md](../../Docs/Rust.md#concurrency-model) for the
full design and why it still satisfies every timing/protocol requirement the other ports meet with
more threads.

## Building

Standard Cargo project:

```
cargo build
```

## Testing

Tests use real loopback TCP/TLS connections (no mocked sockets), generating throwaway self-signed
certificates via `rcgen` (`tests/support/mod.rs`) — the same testing philosophy as the other three
ports.

```
cargo test
```

## Code coverage

Coverage is measured with [`cargo-llvm-cov`](https://github.com/taiki-e/cargo-llvm-cov):

```
cargo install cargo-llvm-cov --locked
rustup component add llvm-tools-preview
cargo llvm-cov --html   # writes target/llvm-cov/html/index.html
```

The remaining gaps are almost entirely error-path branches requiring fault injection into a live
TLS session to reach deterministically (mid-stream I/O failures, malformed post-handshake state) —
the same category of hard-to-reach-deterministically paths the C port's own coverage notes call
out.
