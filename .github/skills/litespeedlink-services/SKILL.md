---
name: litespeedlink-services
description: Use when a C# project references SourceCrafter.LiteSpeedLink packages, or when defining an RPC contract (IServiceUnit), a generated host ([ServiceHost]) or client ([ServiceClient]), adding processors, retry, middleware, caching or streams to operations, or fixing SCLSL* diagnostics. Don't decompile the generators or web-search the API: the idioms are here.
---

# LiteSpeedLink: contracts, hosts and clients

Compile-time RPC. A C# interface is the contract; generators emit the host dispatcher and a typed client from it. Host and client must be generated from the **same contract** at compile time: there is no negotiation, versioning or discovery at runtime. Hosts and clients are SourceCrafter.DependencyInjection containers (read skill `sourcecrafter-dependency-injection`). Security and transports: skill `litespeedlink-security`. ASP.NET Core / Generic Host: skill `litespeedlink-hosting`.

## Packages per project

| Project | Packages |
|---|---|
| Contracts (shared) | `SourceCrafter.LiteSpeedLink.Abstractions` |
| Host | `...Server` + `...ServerGenerator` (+ contracts) |
| Client | `...Client` + `...ClientGenerator` (+ contracts) |

```csharp
using SourceCrafter.LiteSpeedLink;                  // IServiceUnit, ServiceHost, ServiceClient, ServiceUnit<T>, TlsMode, processors...
using SourceCrafter.DependencyInjection.Attributes; // ServiceProvider, Singleton, Scoped, Transient, Hosted
```

## Core pattern

```csharp
// Contract (shared project)
public interface IInventory : IServiceUnit
{
    int GetStock([ServerProcessor<NonEmptySku>] string sku);       // validated on the host, before the handler
    ValueTask<bool> ReserveAsync(string sku, int quantity);
    IAsyncEnumerable<string> LowStock(int threshold, CancellationToken token);
}

// Host: one container scope per request, so the handler is usually Scoped
[ServiceHost(ServiceConnectionType.Tcp, Tls = TlsMode.Mutual)]
[ServiceProvider]
[Singleton<NonEmptySku>]                       // processors live in the container of the side that runs them
[Singleton<StockStore>]
[Scoped<IInventory, InventoryService>]
public partial class InventoryHost;

// Client: the class name must NOT be "<Contract without I>Client" (that's a generated nested type: CS0542)
[ServiceClient(ServiceConnectionType.Tcp, Tls = TlsMode.Mutual)]
[ServiceUnit<IInventory>]
[ServiceProvider]
public sealed partial class InventoryRemote;

var listener = InventoryHost.Start(5400, cert, cts.Token);          // TcpListener
await using var remote = new InventoryRemote("localhost", 5400, cert);
int stock = await remote.Inventory.GetStockAsync("APPLE");           // property = contract name without 'I'
bool ok = await remote.Inventory.ReserveAsync("APPLE", 3);           // async members are called as declared
await foreach (var sku in remote.Inventory.LowStock(5, token)) { }   // same signature as the contract
cts.Cancel(); listener.Stop();                                        // shutdown: cancel open requests, stop accepting

// Processor: runs on the side named by the attribute, before the handler; anything but Success short-circuits
public sealed class NonEmptySku : IPipeline<string>
{
    public (ResponseStatus, string) Process(string sku)
        => string.IsNullOrWhiteSpace(sku) ? (ResponseStatus.Failed, sku) : (ResponseStatus.Success, sku.Trim());
}
```

## Generated surface

| Transport | Host start (returns) | Stop | Client ctor |
|---|---|---|---|
| Tcp | `X.Start(port[, cert][, token])` → `TcpListener` | `token` cancel + `Stop()` | `(host, port[, cert])` |
| Quic | `await X.StartAsync(port, cert[, token])` → `QuicListener` | `await DisposeAsync()` | `(host, port, cert)` |
| Udp | `X.Start(port[, token])` → `UdpClient` | `Dispose()` | `(host, port)` |
| Memory / Local | `X.Start(name)` | `Dispose()` / `DisposeAsync()` | `(name)` |

- The certificate parameter exists only when the TLS mode needs one (skill `litespeedlink-security`).
- Clients are `IAsyncDisposable`. Each sync contract member also gets an `*Async` overload with a `CancellationToken`; `out`/`ref` come back as a tuple.
- A handler exception reaches the client as `InvalidOperationException("Execution failed on <remote>: ...")`.

## Operation features (attributes on contract members)

| Feature | Attribute | Notes |
|---|---|---|
| Validate / transform / authorize | `[ServerProcessor<T>]` / `[ClientProcessor<T>]` on a parameter or `[return: ...]` | `T : IPipeline<TIn[, TOut]>` (or `IAsyncPipeline`, `IAsyncValuePipeline`) returning `(ResponseStatus, TOut)`; register `T` in that side's container (**SCLSL013**). Not `Success` short-circuits: server side → the client gets `InvalidOperationException` ("Execution failed on <remote>: REASON: Pipeline 'T' returned Failed."; no custom message); client side → `PipelineRejectedException` before sending. A type-changing chain changes the wire/client signature. |
| Retry on `TimeoutException` | `[ClientRetry(3, 50)]` or `[ServerRetry(3)]` | Handlers must be idempotent. Both on one op: **SCLSL015**. Not on streams (**SCLSL016**). |
| Around the call | `[ClientMiddleware<T>]` / `[ServerMiddleware<T>]` | `T : IMiddleware` (sync ops) / `IAsyncMiddleware` (async ops), registered in the container. |
| Cache | `[ClientCache(ms)]` (typed, skips the network) / `[ServerCache(ms)]` (bytes → bytes, shared by all callers: never cache caller-specific data), `[CacheKey]` on parameters | Not on streams, `void`, `out`/`ref` (**SCLSL017**). |
| Streams | `IAsyncEnumerable<T>` / `IEnumerable<T>` result, `[Stream(Batch = 16, MaxDelayMs = 1)]` | Take a `CancellationToken` and mark it `[EnumeratorCancellation]` in the implementation. Breaking out of `await foreach` stops the host producer. |
| Large messages | `[MaxMessageSize(bytes)]` | Default 4 MB per request, max 16 MB (**SCLSL023**). |
| Big responses / long streams on QUIC | `[DedicatedStream]` | Own QUIC stream instead of the shared pool. |

## Common mistakes

| Symptom | Fix |
|---|---|
| CS0542 member name equals enclosing type | Rename your client class (e.g. `InventoryRemote`, not `InventoryClient`). |
| CS0246 `ServiceProvider` / `Scoped` | `using SourceCrafter.DependencyInjection.Attributes;` |
| CS0103 `Lifetime` | `using SourceCrafter.DependencyInjection.Constants;` |
| Host and client disagree after a contract change | Rebuild both from the same contract version; ids are hashes of the signatures. |
| **SCLSL026** | Two operations hash to the same id: rename one. |
| **SCLSL024** warning | Tcp host without explicit TLS: see skill `litespeedlink-security`. |
| Validation inside the handler body | Use `[ServerProcessor<T>]` so it runs before the handler (and before any `[ServerCache]` lookup). |
