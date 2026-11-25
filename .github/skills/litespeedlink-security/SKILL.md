---
name: litespeedlink-security
description: Use when securing or deploying a LiteSpeedLink host or client: TLS, mutual TLS, certificates or certificate pinning, choosing Tcp/Quic/Udp/Local/Memory, exposing a service beyond localhost, message size, connection or idle limits, or fixing SCLSL022, SCLSL024 or SCLSL025. Don't decompile the packages or web-search the API: the idioms are here.
---

# LiteSpeedLink: security and transports

Everything below is fixed at compile time on `[ServiceHost]`/`[ServiceClient]`: the generator emits the exact certificate parameter and the validation code. Host and client must declare the **same** `Tls`/`Check`.

## Choose the transport

| Transport | Use for | Security |
|---|---|---|
| `Local` | Same machine, another process | Same user only (shared memory on Windows, private Unix socket elsewhere). |
| `Memory` | Same machine, Windows | Same as `Local`. |
| `Tcp` | Network | Pick `Tls` explicitly (below). |
| `Quic` | Network, many concurrent calls | Always TLS; `Default` = mutual. `Tls = None` is **SCLSL022**. |
| `Udp` | Trusted networks only | No authentication; replies capped at 3× the request (`MaxAmplification`, below). |

## TLS (`Tcp`, `Quic`)

```csharp
[ServiceHost(ServiceConnectionType.Tcp, Tls = TlsMode.Mutual, Check = CertificateCheck.Pinned)]
[ServiceClient(ServiceConnectionType.Tcp, Tls = TlsMode.Mutual, Check = CertificateCheck.Pinned)]
```

| `Tls` | Effect |
|---|---|
| `Mutual` | Both ends present and validate a certificate. Production default for anything reachable by others. |
| `Server` | Only the host presents one; clients aren't authenticated (anyone can connect). |
| `None` | Plaintext, no certificate parameter. Only for trusted internal networks, stated on purpose. |
| `Default` | Tcp: mutual TLS if `Start` gets a certificate, **plaintext if it gets `null`** (**SCLSL024** warning). Don't ship it: state `Mutual`, `Server` or `None`. |

| `Check` | Remote certificate accepted when |
|---|---|
| `Pinned` (default) | it is exactly the configured certificate (SHA-256); self-signed works. |
| `Chain` | it chains to a CA the machine trusts (on the host side, any public CA counts: rarely what you want for client authentication). |
| `ChainOrPinned` | either. |

- Client: the `certificate` argument is the pin and, with `Mutual`, also the client certificate (it must have its private key). `Server` + `Chain` clients take no certificate.
- Host: the certificate must have its private key; a missing one fails at startup.
- **Windows**: SChannel can't use an in-memory key. Round-trip a generated certificate through PKCS#12 before using it:

```csharp
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var request = new CertificateRequest("CN=inventory", key, HashAlgorithmName.SHA256);
using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);   // usable by SChannel
```

## Built-in limits (no code needed)

| Limit | Default | Change |
|---|---|---|
| Request body per operation | 4 MB (max 16 MB) | `[MaxMessageSize(bytes)]` on the operation (**SCLSL023** out of range). Oversized requests fail on the client before sending. |
| Connections (Tcp, Uds, Quic) | 1024 total, 64 per remote address (IPv6 per /64) | Kestrel: `Limits.MaxConcurrentConnections` and `UseLiteSpeedLink<T>(..., maxConnectionsPerAddress: n)`; the generated `X.Start` uses the defaults |
| Half-received message | 30 s without bytes, or below 240 B/s after 5 s | Kestrel: `RequestHeadersTimeout`, `MinRequestBodyDataRate` |
| Idle connection (Tcp, Uds) | Closed after 130 s with nothing in flight; clients reconnect and retry unprocessed calls transparently | Kestrel: `KeepAliveTimeout` |
| Udp reply size | ≤ 3× the request on the wire; a bigger reply fails the call with "exceeds the host's UDP MaxAmplification" | `[ServiceHost(ServiceConnectionType.Udp, MaxAmplification = N)]`; `0` = no limit (trusted networks only); negative is **SCLSL025** |

## Exposure checklist

- Not reachable by untrusted peers unless `Tls = Mutual` (Tcp/Quic).
- `X.Start(port, ...)` listens on all interfaces. To restrict the address, host it with Kestrel (`UseLiteSpeedLink<T>(new IPEndPoint(IPAddress.Loopback, port))`, skill `litespeedlink-hosting`) or firewall it.
- `[ServerCache]` responses are shared by every caller: never cache caller-specific data; authorize with a `[ServerProcessor<T>]`, which runs before the cache lookup.
- Handlers run per request with their own container scope; don't keep request data in singletons.
