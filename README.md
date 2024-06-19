# LiteSpeedLink: The .NET lightweight, compile-time, contract-first and source generated communication framework

## Introduction

This project implements a custom API communication channel using the `KcpTransport` NuGet library. The communication model is contract-first, with two primary implementation variants: Minimal and Class-first, totally source generated

### Key Concepts

- **Contract-First**: Both endpoints, host and client must define implementors, either service interfaces or delegates.
- **Hosts**: `KcpListener` implementations ready to receive any client message.
- **Clients**: `KcpConnection` ready to chat with any implemented `KcpListener`.
- **Communication Contracts**: Defined using source generation with two variants:
  - **Minimal Variant**: Methods are declared in `Program.cs` with attribute markers (`EndpointHandlerAttribute`).
  - **Class Variant**: Methods inside classes implementing interfaces inheriting `IServiceUnit`.

## Host Service side

Host service partial classes are generated when they have the `ServiceHostAttribute` together with any of the following attributes from the `Jab` library:
- `TransientAttribute<TInterface, TImplementation>`
- `TransientAttribute<TImplementation>`
- `ScopedAttribute<TInterface, TImplementation>`
- `ScopedAttribute<TImplementation>`
- `SingletonAttribute<TInterface, TImplementation>`
- `SingletonAttribute<TImplementation>`

These attributes should be applied to the class owning the `ServiceHostAttribute`.

### Example

```csharp
[ServiceHost]
[Singleton<IMySingletonService, MySingletonService>]
[Scoped<IMyScopedService, MyScopedService>]
[Transient<IMyTransientService, MyTransientService>]
public partial class MyHostService;
```

### EndpointHandlerAttribute

Used in the minimal variant to declare service methods.

#### **Example**
Every method in `Program.cs` marked with `EndpointHandlerAttribute` will serve as host endpoint (a dedicated delegate will be registered)

>Program.cs
```cs
[ServiceHost]
// other services...
public partial class MyHostService;

[EndpointHandler]
internal async ValueTask<bool> SaveConfigurationAsync(string key, string value, [Service] IConfigurationManager configManager = default!, CancellationToken token = default)
{
  return await configManager.CreateOrUpdateAsync(key, value, token);
}
```

### IServiceUnit

Interfaces inheriting this must implement the service endpoints.

#### **Example**

```csharp
public interface IProductServiceUnit : IServiceUnit
{
    async ValueTask<CreatedProduct> CreateProductAsync(NewProductMessage message);
}
```

## Host Client side

To generate clients you just must add interfaces defined in contracts or minimal variant generated delegates to service client class (owning ServiceClientAttribute), 

```csharp
[ServiceClient]
public partial class MyClientService : IMyService, IServiceHandler<CreateProductAsync>;
```

## Dependencies

This project uses the following libraries:
- `KcpTransport`: For managing the communication protocol.
- `Jab`: For dependency injection and service management.

## TODO
- Pipeline implementation for middlewares
- Binary mappers (a fork of SourceCrafter)
- Streaming processor (suitable for gaming, duplex services and real-time communication)
