using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// POC S1-S3: handler del host que termina asincrono (como uno real con BD/HTTP). Con handlers sincronos el servidor
/// no asigna cajas; aqui se ven <c>HandleRequestsAsync</c> (S1), <c>DispatchAsync</c> (S2) y el registro
/// <c>inflight</c> + <c>ContinueWith</c> (S3). <c>--alloc srv</c>
/// </summary>
public interface IAsyncWork : IServiceUnit
{
    ValueTask<int> Work(int n);
}

public sealed class AsyncWorkImpl : IAsyncWork
{
    // Caja pooled tambien en la implementacion: lo que quede asignado es del framework, no del usuario.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<int> Work(int n)
    {
        await Task.Yield();
        return n + 1;
    }
}

[ServiceHost(ServiceConnectionType.Tcp),
 ServiceProvider,
 Scoped<IAsyncWork, AsyncWorkImpl>]
public partial class AsyncWorkService;

[ServiceClient(ServiceConnectionType.Tcp),
 ServiceUnit<IAsyncWork>,
 ServiceProvider]
public sealed partial class AsyncWorkServiceClient;

public sealed class AsyncHandlerPoc
{
    private TcpListener _host = null!;
    private AsyncWorkServiceClient.AsyncWorkClient _client = null!;

    public void Setup()
    {
        _host = AsyncWorkService.Start(0, null);
        _client = new AsyncWorkServiceClient("127.0.0.1", ((IPEndPoint)_host.LocalEndpoint).Port).AsyncWork;
    }

    public async Task Run()
    {
        if (await _client.WorkAsync(1) != 2) throw new InvalidOperationException("Work(1) != 2");
    }

    public void Cleanup() => _host.Stop();
}
