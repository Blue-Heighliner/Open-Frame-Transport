# Agent & Contributor Conventions

Conventions for anyone (human or agent) writing code in this repository. See
[`README.md`](README.md) for the protocol design and [`Core/`](Core/) for the implementation it
describes.

## Code Style

- C#, targeting the latest .NET SDK available, using the latest available language features.
- Do not use `var`. Always declare the explicit variable type.
- Each project has a single `Using.cs` file containing all `global using` directives for that
  project. Do not place `using` directives in individual files.

## Documentation & Comments

- All `public` and `internal` types and members must have full XML documentation comments
  (`<summary>`, `<param>`, `<returns>`, `<exception>`, etc. as applicable).
- When a type or member implements an already-documented interface and doesn't add meaningfully
  beyond that documentation, use `<inheritdoc />` instead of duplicating it.
- Do not add inline comments within method bodies unless explaining something genuinely obscure (a
  non-obvious workaround, a subtle invariant, a surprising platform quirk). Code should otherwise be
  self-explanatory through naming and structure.

## Class & Type Design

- Mark all classes `sealed` unless they are explicitly designed for inheritance.
- Prefer `record` types with `required init` properties for DTOs and models.
- Default to `internal` visibility. Only use `public` when there's a clear reason to expose the
  type/member outside the assembly.
- One type per file, with these exceptions:
  - An interface and its implementing class are co-located in one file named after the class (e.g.
    `IThing` and `Thing` both live in `Thing.cs`).
  - Extension classes are co-located with the class they extend (e.g. `ThingExtensions` also lives
    in `Thing.cs`).
  - A standalone interface with no single implementing class in the same file is named after its
    concept without the `I` prefix (e.g. `IOther` with no co-located `Other` class lives in
    `Other.cs`).
- Use an IoC container to manage and inject dependencies. Configure automatic resolution of
  interfaces to their same-named implementation (e.g. `IThing` resolves to `Thing`) without
  requiring explicit registration.

## Async & Performance

- Use `async`/`await` instead of blocking calls.
- Do not suffix async method names with `Async` — name them as you would any other method.
- Prefer `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, and `ReadOnlyMemory<T>` over raw byte arrays
  where applicable.
- In serialization/networking code, use `ArrayPool<byte>.Shared` (or `MemoryPool<byte>.Shared` when
  an `IMemoryOwner<byte>` is more convenient) for buffers that are purely transient — rented,
  filled, consumed, and returned within the same call, e.g. a scratch buffer used only to serialize
  into or parse out of. A buffer may only cross into caller/application-owned territory (returned
  from a public API, handed out via an event, or otherwise outliving the call that produced it) if
  ownership transfers through an explicit, opt-in mechanism the recipient can act on — e.g. an
  `IMemoryOwner<byte>` the recipient disposes when done, or an API that accepts one from the caller
  and takes over disposing it. Never hand out or accept pooled memory implicitly (a plain
  `byte[]`/`Memory<byte>`/`ReadOnlyMemory<byte>` with no owner attached) where the recipient has no
  way to know it's poolable or return it — that just leaves rent-and-return discipline broken by
  construction. Where a parsed object already owns its own copy of data (e.g. a deserialized
  message) and no ownership transfer is needed, prefer a zero-copy `Span`/`Memory` view over it
  instead of re-copying, rather than pooling redundant copies.

## Other-language ports

- [`Ports/Java`](Ports/Java/) and [`Ports/C`](Ports/C/) are independent implementations of the same
  protocol. Their `OftServer`/`OftClient`/`OftPeer` APIs should align with the C# reference
  implementation (and each other) as much as is practical: same method names and semantics, same
  option/property shapes, adapted only where the target language's idioms genuinely require it
  (e.g. events vs. listener interfaces vs. callbacks, `Task`/`CompletableFuture`/blocking calls,
  `record`/Java `record`/plain `struct`). When one language's API changes, check whether the same
  change should be mirrored in the other two before considering the change complete.

## Testing

- Create and maintain unit tests using xUnit.
- Use mocks for dependencies under test.
- Code coverage is collected with `coverlet.collector` and measured with `reportgenerator` (a local
  tool; run `dotnet tool restore` once). To measure coverage:

  ```
  dotnet test Tests/OpenFrameTransport.Tests.csproj --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory Tests/TestResults
  dotnet tool run reportgenerator -reports:"Tests/TestResults/**/coverage.cobertura.xml" -targetdir:Tests/TestResults/report -reporttypes:Html
  ```

  `coverlet.runsettings` excludes generated protobuf code from coverage, since it isn't hand-written.
