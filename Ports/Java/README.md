# Open Frame Transport - Java

A Java implementation of the [Open Frame Transport (OFT)](../../README.md) protocol. See
[Docs/OFT.md](../../Docs/OFT.md) for the protocol specification, [Docs/Architecture.md](../../Docs/Architecture.md)
for how this port's components relate to the other implementations, and
[Docs/Java.md](../../Docs/Java.md) for the full Java API reference with examples. This document
covers only building, testing, and coverage.

## Scope

This port implements the protocol engine — `OftConnection`, `OftHoster`/`OftListener`, and
`OftConnector` — plus the `OftPeer` connection-pooling convenience layer, with the same wire
behavior and API shape as the [C# reference implementation](../../Core).

## Building

This is a standard Maven project:

```
mvn package
```

`OFT.proto` is compiled to Java sources at build time by the `protobuf-maven-plugin`, which
downloads a matching `protoc` binary automatically (via `os-maven-plugin`) — no local `protoc`
install is required.

## Testing

Tests use JUnit 5 (`mvn test`) and a real loopback TCP/TLS connection, generating a throwaway
self-signed certificate via the JDK's own `keytool` (no third-party certificate library needed).

## Code coverage

Coverage is collected with the `jacoco-maven-plugin` (bound to `mvn test`, which also writes an
HTML report to `target/site/jacoco/index.html`), excluding generated protobuf code the same way
`coverlet.runsettings` excludes it on the C# side.
