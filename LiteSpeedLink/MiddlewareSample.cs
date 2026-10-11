using System.Runtime.CompilerServices;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

// [Client|ServerMiddleware<T>]: el generador anida lambdas static alrededor de la llamada (transporte/handler).
// Add es sync: en Memory cliente y handler no esperan nada -> IMiddleware.Invoke en ambos lados.
// UpperAsync es async -> IAsyncMiddleware.InvokeAsync; Audit (el primero) es la capa externa, Throttle la interna.
public interface ICalc : IServiceUnit
{
    [ServerMiddleware<Audit>]
    int Add(int a, int b);

    [ClientMiddleware<Throttle>]
    Task<string> UpperAsync(string text);
}

public sealed class Calc : ICalc
{
    public int Add(int a, int b) => a + b;

    public Task<string> UpperAsync(string text) => Task.FromResult(text.ToUpperInvariant());
}

/// <summary>Sync y async: registra la entrada/salida de cada llamada.</summary>
public sealed class Audit : IMiddleware, IAsyncMiddleware
{
    public TRes Invoke<TState, TRes>(TState state, Func<TState, TRes> next)
    {
        Console.WriteLine("  Audit >");
        var res = next(state);
        Console.WriteLine($"  Audit < {res}");
        return res;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token)
    {
        Console.WriteLine("  Audit > (async)");
        var res = await next(state, token).ConfigureAwait(false);
        Console.WriteLine($"  Audit < (async) {res}");
        return res;
    }
}

/// <summary>Solo async: una llamada a la vez (espera de verdad, de ahi el builder pooled).</summary>
public sealed class Throttle : IAsyncMiddleware
{
    readonly SemaphoreSlim _gate = new(1);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Console.WriteLine("    Throttle >");
            return await next(state, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

[ServiceHost]
[ServiceProvider]
[Singleton<Audit>, Singleton<Throttle>]
[Scoped<ICalc, Calc>]
public partial class CalcService;

[ServiceClient]
[ServiceProvider]
[Singleton<Audit>, Singleton<Throttle>]
[ServiceUnit<ICalc>]
public sealed partial class CalcServiceClient;
