using System.Runtime.CompilerServices;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

// Una sola operacion con todo combinado, en el orden en que actua:
//   cliente: [ClientCache] -> un acierto no viaja (ni processor, ni middleware, ni handler).
//   host:    [ServerProcessor<ResolveCustomer>] (async, una vez) -> [ServerMiddleware<Timing>] (una vez)
//            -> [ServerRetry(3)] (solo el handler, ante TimeoutException) -> handler.
// El cable lleva el token (string); el handler recibe el Customer ya resuelto.
public interface IQuotes : IServiceUnit
{
    [ClientCache(60_000)]
    [ServerRetry(3)]
    [ServerMiddleware<Timing>]
    Task<string> QuoteAsync([ServerProcessor<ResolveCustomer>] Customer customer, int sku);
}

public sealed record Customer(string Name, decimal Discount);

/// <summary>Processor async de peticion: token -> Customer (consulta simulada); un token desconocido corta con Failed antes del handler.</summary>
public sealed class ResolveCustomer : IAsyncValuePipeline<string, Customer>
{
    public static int Calls;

    public async ValueTask<(ResponseStatus, Customer)> ProcessAsync(string token)
    {
        Interlocked.Increment(ref Calls);
        await Task.Delay(1).ConfigureAwait(false);
        return token == "tok-acme" ? (ResponseStatus.Success, new("acme", 0.1m)) : (ResponseStatus.Failed, null!);
    }
}

/// <summary>Middleware async del host: envuelve la llamada con su reintento dentro, asi que corre una vez por peticion.</summary>
public sealed class Timing : IAsyncMiddleware
{
    public static int Calls;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return await next(state, token).ConfigureAwait(false); }
        finally { Console.WriteLine($"  Timing: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms"); }
    }
}

public sealed class Quotes : IQuotes
{
    public static int Attempts, DownstreamTimeouts;

    public Task<string> QuoteAsync(Customer customer, int sku)
    {
        Interlocked.Increment(ref Attempts);

        // Dependencia aguas abajo que vence su plazo: [ServerRetry] repite solo esta invocacion.
        if (Volatile.Read(ref DownstreamTimeouts) > 0 && Interlocked.Decrement(ref DownstreamTimeouts) >= 0) throw new TimeoutException("pricing");

        return Task.FromResult($"{customer.Name}:{sku * 100 * (1 - customer.Discount):F2}");
    }
}

[ServiceHost]
[ServiceProvider]
[Singleton<ResolveCustomer>, Singleton<Timing>]
[Scoped<IQuotes, Quotes>]
public partial class QuoteService;

[ServiceClient]
[ServiceProvider]
[ServiceUnit<IQuotes>]
public sealed partial class QuoteServiceClient;
