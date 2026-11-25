---
name: litespeedlink-hosting
description: Use when running a LiteSpeedLink host inside ASP.NET Core, a Worker Service or any Microsoft.Extensions.Hosting IHost, serving it through Kestrel, or giving RPC handlers the app's services (ILogger, DbContext, singletons from builder.Services). Don't decompile the packages or web-search the API: the idioms are here.
---

# LiteSpeedLink in an app host

Read skill `litespeedlink-services` first for contracts, hosts and clients.

## Pick one

| App | Package | Call |
|---|---|---|
| Any `IHost` (Generic Host, Worker, ASP.NET Core), any transport | `SourceCrafter.LiteSpeedLink.Hosting` | `builder.Services.AddLiteSpeedLink(new NotesHost.Endpoint { Port = 5410 })` |
| ASP.NET Core, Tcp only, Kestrel owns the sockets | `SourceCrafter.LiteSpeedLink.Kestrel` | `builder.WebHost.UseLiteSpeedLink<NotesHost>(5410)` or `(new IPEndPoint(IPAddress.Loopback, 5410), cert)` |

Both make the generated host a singleton of the app, set its `HostServices` to the app's services, start it with the app and stop it on shutdown. Don't call `NotesHost.Start(...)` yourself in these cases.

- `Endpoint` is generated per host: what the transport and TLS mode need is `required` (`Port` or `Name`, `Certificate`), so a missing value is a compile error. With `[JsonSetting<RemoteOptions>("Rpc")]` on the host, `Port` becomes optional and is read from the app configuration (`Rpc:Port`).
- Kestrel: TLS uses Kestrel's HTTPS adapter (certificate from the argument or `ConfigureHttpsDefaults`) with the host's compile-time `Tls`/`Check`. Connection cap, timeouts, max body size and keep-alive come from `KestrelServerOptions.Limits`.

## App services in handlers

```csharp
[ServiceHost(ServiceConnectionType.Tcp, Tls = TlsMode.None)]   // trusted internal network: plaintext on purpose
[ServiceProvider]
[Hosted<ILogger<NotesHandler>>]            // app singleton/transient: resolved from builder.Services
[Hosted<NotesStore>]
[Hosted<RequestAudit>(Lifetime.Scoped)]    // app scoped service: one per RPC request, disposed when it ends
[Scoped<INotes, NotesHandler>]             // handler: one per request (container scope)
public partial class NotesHost;

[ServiceClient(ServiceConnectionType.Tcp, Tls = TlsMode.None)]
[ServiceUnit<INotes>]
[ServiceProvider]
public sealed partial class NotesRemote;    // not "NotesClient": that's a generated nested type (CS0542)

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<NotesStore>();
builder.Services.AddScoped<RequestAudit>();
builder.WebHost.UseLiteSpeedLink<NotesHost>(new IPEndPoint(IPAddress.Loopback, 5410));
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(5411));   // HTTP endpoints need their own listener
var app = builder.Build();
app.MapGet("/health", () => "Healthy");
await app.StartAsync();

// Client (same process or another): declared with the SAME Tls as the host; no certificate argument with Tls = None
await using var remote = new NotesRemote("localhost", 5410);
int id = await remote.Notes.AddAsync("Buy milk");
await foreach (var text in remote.Notes.List(app.Lifetime.ApplicationStopping)) Console.WriteLine(text);
await app.StopAsync();
```

`UseLiteSpeedLink` and `AddLiteSpeedLink` live in `namespace SourceCrafter.LiteSpeedLink` (same `using` as the attributes).

- `[Hosted<T>]` services are resolved from the app every time; the RPC container never builds, caches or disposes them.
- `[Hosted<T>(Lifetime.Scoped)]` (EF Core `DbContext`, per-request audit, unit of work): each RPC request opens one app scope on first use, shares it within the request (stream requests included) and disposes it when the request ends. Needs `Microsoft.Extensions.DependencyInjection.Abstractions` (part of ASP.NET Core; **SCDI25** otherwise).
- A `[Singleton<>]` of the RPC container that depends on a per-request hosted service is **SCDI24**.
- `using SourceCrafter.DependencyInjection.Constants;` for `Lifetime`.

## Common mistakes

| Symptom | Fix |
|---|---|
| `/health` (or any HTTP route) unreachable after `UseLiteSpeedLink` | It adds an explicit Kestrel listener, which replaces the default URLs: add `ConfigureKestrel(k => k.ListenLocalhost(httpPort))` or `Listen...` for HTTP. |
| `InvalidOperationException` resolving a `[Hosted<T>]` | `T` isn't registered in `builder.Services`, or (scoped) it was resolved outside a request. |
| The app's `DbContext` registered as `[Scoped<AppDb>]` in the RPC container | That builds a second instance the app doesn't configure: use `[Hosted<AppDb>(Lifetime.Scoped)]`. |
| `UseLiteSpeedLink<T>` doesn't compile for a non-Tcp host | Kestrel is Tcp only: use `AddLiteSpeedLink` for Quic/Udp/Local/Memory. |
| Port bound to every interface | `UseLiteSpeedLink<T>(port)` listens on `IPAddress.Any`; pass an `IPEndPoint` to restrict it. |
