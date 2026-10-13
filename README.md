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
| Middleware | `[ClientMiddleware<T>]` / `[ServerMiddleware<T>]`: sync/async wrappers around the call, static `next` lambdas, no closures |
| Cache | `[ClientCache]` (typed, no transport on hit, shared in-flight requests) / `[ServerCache]` (request bytes -> response bytes), `[CacheKey]` |
| Streams | `[Stream(Batch, MaxDelayMs)]` batching policy baked into the host as constants |
| QUIC | pooled multiplexed streams for unary and stream calls; `[DedicatedStream]` for large responses, long streams or subscriptions |
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
- Works the same on operations without a result (`void`, `Task`, `ValueTask`).

## Middleware

```csharp
public interface IMiddleware      { TRes Invoke<TState, TRes>(TState state, Func<TState, TRes> next); }
public interface IAsyncMiddleware { ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token); }

[ServerMiddleware<Timing>] Task<string> QuoteAsync(string token, int sku);
```

- Registered in the DI container of the side that runs it; must implement `IMiddleware` (sync operations) and/or `IAsyncMiddleware` (async operations) implicitly and publicly, otherwise **SCLSL020**.
- Wraps **only** the call (transport on the client, handler on the host). Processors and cache stay outside; retry stays inside, so a middleware runs once per request however many attempts there are.
- Several middlewares run in declaration order, the first one outermost.
- `next` is a generated `static` lambda (cached delegate) and `state` carries everything it needs: no closures or per-layer structs. An async middleware that awaits should use `PoolingAsyncValueTaskMethodBuilder`; one that only forwards should return `next(state, token)` without `async`.
- Operations without a result are wrapped too; the generator passes a placeholder `bool` result through the middleware.
- Ignored on streams (**SCLSL021**).

## Cache

```csharp
[ClientCache(60_000)] Task<string> QuoteAsync(string token, int sku);      // typed, per argument key
[ServerCache(60_000, capacity: 256)] Task<Order> GetAsync([CacheKey] int id, string trace);
```

- `[ClientCache(durationMs, capacity)]`: the final result (after return processors) is stored by the argument key. A hit skips the transport, processors, middlewares and the host; identical concurrent requests share one in-flight call. Only `Success` is cached.
- `[ServerCache(durationMs, capacity)]`: request bytes -> processed response bytes, shared across connections. Parameter processors (e.g. authorization) run before the lookup; a hit skips deserialization, DI, middlewares, handler and serialization. Do not cache anything that depends on the caller.
- `[CacheKey]` on parameters, or with member paths (`nameof(order.Customer.Region)`), narrows the key; without it every payload parameter is part of the key.
- Ignored on streams, operations without a result and `out`/`ref` (**SCLSL017**). Details in [docs/CacheProposal.md](docs/CacheProposal.md).

## Putting it together

[`LiteSpeedLink/ComplexSample.cs`](LiteSpeedLink/ComplexSample.cs) combines everything on one contract, and `Program.cs` checks it at run time:

```csharp
public interface IQuotes : IServiceUnit
{
	[ClientCache(60_000)]
	[ServerRetry(3)]
	[ServerMiddleware<Timing>]
	Task<string> QuoteAsync([ServerProcessor<ResolveCustomer>] Customer customer, int sku);

	[ServerRetry(3)]
	[ServerMiddleware<Timing>]
	void Reserve([ServerProcessor<ResolveCustomer>] Customer customer, int sku);
}
```

Order for one call: client cache -> (miss) transport -> async server processor `ResolveCustomer` (`string` token -> `Customer`, once) -> `Timing` middleware (once) -> retry loop -> handler. The client signature is `QuoteAsync(string customer, int sku)` because the processor changes the wire type. An unknown token is rejected by the processor before the middleware and the handler run.

## Streams

```csharp
public interface IStreams : IServiceUnit
{
	IEnumerable<int> Range(int count);
	[Stream(Batch = 16, MaxDelayMs = 1)] IAsyncEnumerable<int> Ticks(int count);
}
```

- Sync contracts get an extra `*Async` overload on the client that returns `IAsyncEnumerable<T>`.
- Streams are batched by default: a frame is sent at 32 KB or when the producer is about to wait, so fast producers send few frames and slow ones are never held back. The host has no batching option.
- `[Stream]` overrides that per operation as compile-time constants: `Batch` items per frame (`0` = one per frame), `MaxDelayMs` to flush an incomplete batch. Rationale and measurements: `docs/PerformanceMemoryEnhancements.md` (point 7).
- Leaving an enumeration early (`break`, cancellation) stops the host producer on every transport. TCP/UDS send an 8-byte `[len=4][corrId]` frame only when a stream is abandoned; no cost on streams that finish.
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
| SCLSL017 | Warning | Cache ignored (stream, no result, `out`/`ref`) |
| SCLSL018 | Info | Cache key falls back to bytes |
| SCLSL019 | Error | Invalid cache key |
| SCLSL020 | Error | Invalid middleware |
| SCLSL021 | Warning | Middleware ignored on streams |

## Benchmarks

`LiteSpeedLink.Benchmarks`, BenchmarkDotNet publication harness (mask 6305 = Control + Streaming + Comparison + QuicComparison + Features; P-core affinity, 10 warmup / 15 iterations, memory randomization), .NET 10.0.12, i9-14900HX, loopback. Control rows stayed grouped (0.63–0.71 ns, 2.88–2.90 ns). Mean / allocated per operation.

| Scenario | LiteSpeedLink | ASP.NET Core slim | gRPC |
|---|---|---|---|
| Unary, TCP | **39 µs / 353 B** | 49 µs / 2.8 KB | 92 µs / 7.1 KB |
| Stream 1000 int, TCP | **53 µs / 419 B** | 224 µs / 4.4 KB | 433 µs / 71 KB |
| Stream 1000 int, TCP, slim flushing per batch | **53 µs / 419 B** | 92 µs / 3 KB | — |
| Unary, QUIC (HTTP/3) | **83 µs / 1.5 KB** | 178 µs / 32 KB | — |
| Stream 1000 int, QUIC | **118 µs / 1.4 KB** | 616 µs / 31 KB | — |
| Client cache hit (slim: OutputCache, travels) | **16 ns / 72 B** | 68 µs / 4.2 KB | — |
| Server cache hit | **45 µs / 464 B** | 60 µs / 4.1 KB | — |
| Server cache hit by key (`[CacheKey]` vs `VaryByQuery`) | **44 µs / 519 B** | 58 µs / 4.3 KB | — |
| Server retry | **45 µs / 488 B** | 59 µs / 2.9 KB | — |
| 16 concurrent identical calls (single flight) | **50 µs / 5.1 KB** | 293 µs / 45 KB | — |

TCP rows remeasured with mask 129 (Control + Comparison) after stream cancellation; control grouped (0.57–0.73 ns, 2.88–2.95 ns).

Streaming by transport (1000 items, mask 33, range of 3 runs): Memory 61–63 µs / 471 B, UDS 63–64 µs / ~0.5 KB, TCP 78–80 µs / ~0.5 KB, QUIC 153–191 µs / 1.5 KB. Higher than the previous run (56 / 55 / 61 / 114 µs) on every transport, Memory included; an A/B with and without the per-stream cancellation token showed no measurable difference.

Run: `dotnet run -c Release --project LiteSpeedLink.Benchmarks -- <mask>` (`--list` shows the suites). Publishable times need the full harness and the Control bit (1) in the mask; `--fast` is only for allocations.

## Repository layout

| Project | Role |
|---|---|
| `LiteSpeedLink.Abstractions` | Contracts: `ResponseStatus`, pipelines, attributes, `ServiceConnectionType` |
| `SourceCrafter.LiteSpeedLink.ServerGenerator` / `ClientGenerator` | Source generators (SourceCrafter.DependencyInjection partials) |
| `SourceCrafter.LiteSpeedLink.Server` / `Client` | Transport runtimes |
| `Application.Contracts`, `LiteSpeedLink` | Sample contracts and demo app (all transports; `MiddlewareSample.cs`, `ComplexSample.cs`) |
| `LiteSpeedLink.Tests`, `LiteSpeedLink.Benchmarks` | xUnit suite and BenchmarkDotNet |
| `test/POC` | Proofs of concept (auth + retry, middlewares, wrappers, retry policy, shared memory channels), grouped in the `Tests/POC` solution folder |

Design notes and progress live in [PLAN.md](PLAN.md) and [docs/](docs/).

## Dependencies

- [MemoryPack](https://github.com/Cysharp/MemoryPack): binary serialization.
- [SourceCrafter.DependencyInjection](https://github.com/pedro-gilmora/SourceCrafter.DependencyInjection): compile-time DI; LiteSpeedLink generators plug into it as partial contributions.
- BouncyCastle: development certificates for TLS/QUIC.
