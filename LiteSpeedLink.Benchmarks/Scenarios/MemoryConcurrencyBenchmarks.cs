using BenchmarkDotNet.Attributes;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// 6.1 Memory multihilo: N llamadas concurrentes sobre UNA <see cref="MemoryConnection"/> (alimenta PoC-A).
/// UDS como referencia de transporte local sobre el canal multiplexado.
/// </summary>
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
public class MemoryConcurrencyBenchmarks
{
    internal string ContextId { get; private set; } = null!;
    private IDisposable _memServer = null!;
    private Socket _udsServer = null!;
    private System.Net.Sockets.TcpListener _tcpServer = null!;
    private MemoryConnection _memory = null!;
    private UdsConnection _uds = null!;
    private TcpConnection _tcp = null!;

    [Params(1, 8, 64)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        string name = ContextId = $"BenchC-{Guid.CreateVersion7():N}";
        _memServer = Server.StartMemoryServerAsync(name, (op, ctx, _) => new(ctx.Return(ctx.Get<int>() + 1)), () => { });
        _memory = new MemoryConnection(name, 5000);

        string path = Path.Combine(Path.GetTempPath(), name + ".sock");
        _udsServer = Server.StartUdsServer(path, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>() + 1), () => { });
        _uds = new UdsConnection(path);

        _tcpServer = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>() + 1), () => { });
        _tcp = new TcpConnection(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)_tcpServer.LocalEndpoint).Port));

        // Calentar: la primera llamada abre sesion/socket.
        await Memory(); await Uds(); await Tcp();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _memory.Dispose(); _memServer.Dispose();
        await _uds.DisposeAsync(); _udsServer.Dispose();
        await _tcp.DisposeAsync(); _tcpServer.Stop();
    }

    [Benchmark(Baseline = true)]
    public Task Memory() => Run(i => _memory.GetAsync<int, int>(0, i));

    [Benchmark]
    public Task Uds() => Run(i => _uds.GetAsync<int, int>(0, i));

    [Benchmark]
    public Task Tcp() => Run(i => _tcp.GetAsync<int, int>(0, i));

    private async Task Run(Func<int, ValueTask<int>> call)
    {
        var tasks = new Task[Concurrency];
        for (int i = 0; i < tasks.Length; i++)
        {
            int x = i;
            tasks[i] = Task.Run(() => Verify(call, x));
        }
        await Task.WhenAll(tasks);
    }

    private static async Task Verify(Func<int, ValueTask<int>> call, int x)
    {
        if (await call(x) != x + 1) throw new InvalidOperationException("respuesta ajena");
    }
}
