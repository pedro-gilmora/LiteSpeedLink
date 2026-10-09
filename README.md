# LiteSpeedLink

Compile-time, contract-first RPC for .NET 10. A C# interface is the contract; Roslyn source generators emit the host dispatcher and the typed client from it. Everything known at compile time (operation id, wire shape, transport, processors, retry, stream batching) is decided by the generator, so the hot path has no reflection, no runtime negotiation and no per-call delegates.

Client and host are meant to be generated from the **same contract**. They are not general-purpose, runtime-agnostic endpoints: there is no protocol negotiation, version handshake or discovery.

## Features

| Area | What you get |
|---|---|
| Transports | `Memory` (shared memory, same machine), `Udp`, `Tcp` (optional TLS/mTLS), `Quic`, `Local` (Memory on Windows, Unix domain sockets elsewhere, picked at compile time) |
| Contracts | `interface IX : IServiceUnit`; sync, `Task`, `ValueTask`, `ref`/`out`, `CancellationToken`, `IEnumerable<T>`/`IAsyncEnumerable<T>` streams |
| Serialization | MemoryPack; primitives, `string`, enums and `[MemoryPackable]` types are written/read with per-operation readers/writers emitted by the generator |
| DI | Hosts and clients are SourceCrafter.DependencyInjection containers (`[ServiceProvider]`, `[Singleton<>]`, `[Scoped<>]`, `[Transient<>]`) |
| Processors | `[ClientProcessor<T>]` / `[ServerProcessor<T>]` on parameters and return values: validation, auth, transformation, inlined |
| Retry | `[ClientRetry]` / `[ServerRetry]`: inline loop on `TimeoutException`, no allocations |
| Streams | `[Stream(Batch, MaxDelayMs)]` batching policy baked into the host as constants |
| QUIC | pooled multiplexed unary streams; `[DedicatedStream]` for large unary responses |
| Diagnostics | `SCLSL0xx` compile-time errors/warnings for misuse (see below) |

## Quick start

### 1. Contract

```csharp
using SourceCrafter.LiteSpeedLink;

public interface IAuth : IServiceUnit
{
	ValueTask<bool> TryAuthAsync(string user, string password);

	[return: ClientProcessor<Exclaim>]
	string Greet([ServerProcessor<TrimName>] string name);

	bool TryGet(int key, out string value);

	[ClientRetry(3, 50)] int Hit(int n);
}
```

### 2. Host

```csharp
[ServiceHost(ServiceConnectionType.Tcp)]
[ServiceProvider]
[Singleton<TrimName>]
[Scoped<IAuth, AuthService>]
public partial class TcpTextService;

using var host = TcpTextService.Start(5102, null);
```

### 3. Client

```csharp
[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceUnit<IAuth>]
[ServiceProvider]
[Singleton<Exclaim>]
public sealed partial class TcpTextServiceClient;

var auth = new TcpTextServiceClient("localhost", 5102).Auth;
var greeting = await auth.GreetAsync("  pedro  ");
```

Each transport exposes its own entry points:

| Transport | Host | Client |
|---|---|---|
| Memory | `X.Start(name)` | `new XClient(name)` |
| Udp | `X.Start(port)` | `new XClient(host, port)` |
| Tcp | `X.Start(port, cert)` | `new XClient(host, port)` |
| Quic | `await X.StartAsync(port, cert)` | `new XClient(host, port)` |
| Local | `X.Start(name)` | `new XClient(name)` |

The client implements the contract (sync members over network transports are sync-over-async) and adds `*Async` overloads with an optional `CancellationToken`. Async overloads return `out`/`ref` values in a tuple.

## Processors

A processor is a service registered in the DI container of the side that runs it, implementing one of:

```csharp
IPipeline<T> / IPipeline<TIn, TOut>                   // (ResponseStatus, TOut) Process(TIn input)
IAsyncPipeline<T> / IAsyncPipeline<TIn, TOut>         // Task<(ResponseStatus, TOut)>
IAsyncValuePipeline<T> / IAsyncValuePipeline<TIn, TOut>  // ValueTask<(ResponseStatus, TOut)>
```

- `[ClientProcessor<T>]` runs on the client, `[ServerProcessor<T>]` on the host. Attach both to run on both sides.
- On a **parameter** it runs before the call (outgoing); on the **return value** (`[return: ...]`) it runs after (incoming).
- Several processors on the same target run in declaration order; the generator checks that each output type feeds the next (`TIn` -> `TOut`).
- A status other than `Success` short-circuits: the host answers `Failed` without running the handler; the client throws `PipelineRejectedException`.
- Type-changing chains change the public signature: e.g. `[ServerProcessor<ParseInt>] int n` makes the wire and the client parameter a `string`.

```csharp
public sealed class TrimName : IPipeline<string>
{
	public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.Trim());
}
```

## Retry

```csharp
[ClientRetry(attempts: 3, intervalMs: 50)] int Hit(int n);   // around the transport call
[ServerRetry(3)] Task<string> FetchAsync(string key);        // around the handler invocation
```

- The generator emits an inline `for` loop around **only** the call: no structs, delegates or allocations per operation. Processors run once, outside the loop.
- Retries only on `TimeoutException` (the transport's per-call deadline), never after the caller cancelled. `intervalMs` is a fixed pause between attempts.
- Handlers must be idempotent: a lost response means the handler already ran.
- Both attributes on one operation emit **SCLSL015**: attempts (and worst-case latency) multiply.
- Positional and named arguments are both accepted.

## Streams

```csharp
public interface IStreams : IServiceUnit
{
	IEnumerable<int> Range(int count);
	[Stream(Batch = 16, MaxDelayMs = 1)] IAsyncEnumerable<int> Ticks(int count);
}
```

- Sync contracts get an extra `*Async` overload on the client that returns `IAsyncEnumerable<T>`.
- `[Stream]` sets the host batching policy as compile-time constants: `Batch` items per frame (`0` = one per frame), `MaxDelayMs` to flush an incomplete batch.
- **Retry is not applied to streams** (SCLSL016): a failure happens mid-enumeration and replaying would resend items the consumer already received. Resuming would need a checkpoint in the contract.
- **Return processors are not applied to streams** (SCLSL012): processing would mean unpacking and repacking every item. Process items inside the handler or on the consumer instead. Parameter processors are still fine.

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| SCLSL000 | Error | Unexpected generator failure |
| SCLSL010 | Error | Processor type is not a pipeline |
| SCLSL011 | Error | Pipeline chain type mismatch |
| SCLSL012 | Error | Processor not supported here (`out` parameter input, streamed result, return processor without return value) |
| SCLSL013 | Error | Pipeline is not registered in the container |
| SCLSL014 | Error | Pipeline must be implemented implicitly |
| SCLSL015 | Warning | Retry on both client and server |
| SCLSL016 | Warning | Retry ignored on streams |

## Repository layout

| Project | Role |
|---|---|
| `LiteSpeedLink.Abstractions` | Contracts: `ResponseStatus`, pipelines, attributes, `ServiceConnectionType` |
| `SourceCrafter.LiteSpeedLink.ServerGenerator` / `ClientGenerator` | Source generators (SourceCrafter.DependencyInjection partials) |
| `SourceCrafter.LiteSpeedLink.Server` / `Client` | Transport runtimes |
| `Application.Contracts`, `LiteSpeedLink` | Sample contracts and demo app (all transports) |
| `LiteSpeedLink.Tests`, `LiteSpeedLink.Benchmarks` | xUnit suite and BenchmarkDotNet |
| `test/POC` | Proofs of concept (auth + retry, wrappers, stream decoding) |

Design notes and progress live in [PLAN.md](PLAN.md) and [docs/](docs/).

## Dependencies

- [MemoryPack](https://github.com/Cysharp/MemoryPack): binary serialization.
- [SourceCrafter.DependencyInjection](https://github.com/pedro-gilmora/SourceCrafter.DependencyInjection): compile-time DI; LiteSpeedLink generators plug into it as partial contributions.
- BouncyCastle: development certificates for TLS/QUIC.
