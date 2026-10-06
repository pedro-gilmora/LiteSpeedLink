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
    private IDisposable _memServer = null!;
    private Socket _udsServer = null!;
    private MemoryConnection _memory = null!;
    private UdsConnection _uds = null!;

    [Params(1, 8, 64)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        string name = $"BenchC-{Guid.CreateVersion7():N}";
        _memServer = Server.StartMemoryServerAsync(name, (op, ctx, _) => ValueTask.FromResult(ctx.Return(ctx.Get<int>() + 1)), () => { });
        _memory = new MemoryConnection(name, 5000);

        string path = Path.Combine(Path.GetTempPath(), name + ".sock");
        _udsServer = Server.StartUdsServer(path, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>() + 1), () => { });
        _uds = new UdsConnection(path);

        // Calentar: la primera llamada abre sesion/socket.
        await Memory(); await Uds();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _memory.Dispose(); _memServer.Dispose();
        await _uds.DisposeAsync(); _udsServer.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task Memory() => Run(i => _memory.GetAsync<int, int>(0, i));

    [Benchmark]
    public Task Uds() => Run(i => _uds.GetAsync<int, int>(0, i));

    private async Task Run(Func<int, ValueTask<int>> call)
    {
        var tasks = new Task[Concurrency];
        for (int i = 0; i < tasks.Length; i++)
        {
            int x = i;
            tasks[i] = Task.Run(async () => { if (await call(x) != x + 1) throw new InvalidOperationException("respuesta ajena"); });
        }
        await Task.WhenAll(tasks);
    }
}
